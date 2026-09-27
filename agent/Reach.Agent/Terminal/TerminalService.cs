using System.Text;
using Microsoft.Extensions.Logging;
using Reach.Agent.Input;
using Reach.Protocol;

namespace Reach.Agent.Terminal;

/// <summary>What a session needs from the terminal. The send callback identifies the attached session.</summary>
public interface ITerminalService
{
    /// <summary>Attaches <paramref name="send"/>, starting a shell if none is running (spec §4 term.open).</summary>
    void Open(int cols, int rows, Action<Message> send);

    void Input(string data);

    void Resize(int cols, int rows);

    /// <summary>Detaches if <paramref name="send"/> is the attached session; starts the idle countdown.</summary>
    void Detach(Action<Message> send);
}

/// <summary>
/// One PowerShell in a ConPTY that outlives phone disconnects (spec §5.2): output is kept in a
/// 64 KB ring buffer for re-attach, and the shell is killed after 30 minutes with no session.
/// </summary>
public sealed class TerminalService(
    string commandLine, string workingDirectory, TimeProvider time, ILogger<TerminalService> log) : ITerminalService, IDisposable
{
    public const int BufferChars = 64 * 1024;
    public const int MaxColsOrRows = 1000;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Characters per term.output message. Even if every character JSON-escapes to 6 bytes
    /// ("\u001b"), 8192 of them stay well under the 65,519-byte Noise plaintext limit.
    /// </summary>
    public const int ChunkChars = 8192;

    private readonly Lock _lock = new();
    private readonly RingText _buffer = new(BufferChars);
    private Shell? _shell;
    private Action<Message>? _client;
    private ITimer? _idleTimer;

    public static TerminalService CreateDefault(TimeProvider time, ILogger<TerminalService> log) =>
        new($"\"{ShellLocator.Find()}\" -NoLogo", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), time, log);

    public bool IsRunning
    {
        get { lock (_lock) return _shell is not null; }
    }

    public void Open(int cols, int rows, Action<Message> send)
    {
        CheckSize(cols, rows);
        lock (_lock)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            _client = send;
            var resumed = _shell is not null;
            if (resumed)
            {
                _shell!.Console.Resize(cols, rows);
            }
            else
            {
                _buffer.Clear();
                _shell = StartShell(cols, rows);
            }
            send(new TermOpened(resumed));
            if (resumed)
            {
                foreach (var chunk in Chunk(_buffer.Replay())) send(new TermOutput(chunk));
            }
        }
    }

    public void Input(string data)
    {
        Shell? shell;
        lock (_lock) shell = _shell;
        if (shell is null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            shell.Console.Input.Write(bytes);
            shell.Console.Input.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The shell is exiting; its exit handler reports term.exited.
        }
    }

    public void Resize(int cols, int rows)
    {
        CheckSize(cols, rows);
        lock (_lock) _shell?.Console.Resize(cols, rows);
    }

    public void Detach(Action<Message> send)
    {
        lock (_lock)
        {
            if (_client != send) return;
            _client = null;
            if (_shell is not null)
                _idleTimer = time.CreateTimer(_ => KillIdleShell(), null, IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _idleTimer?.Dispose();
            _shell?.Console.Kill();
        }
    }

    /// <summary>Splits text into term.output-sized pieces without cutting a surrogate pair in half.</summary>
    public static IEnumerable<string> Chunk(string text)
    {
        for (var start = 0; start < text.Length;)
        {
            var length = Math.Min(ChunkChars, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1])) length--;
            yield return text.Substring(start, length);
            start += length;
        }
    }

    private static void CheckSize(int cols, int rows)
    {
        if (cols is < 1 or > MaxColsOrRows || rows is < 1 or > MaxColsOrRows)
            throw new BadArgsException($"terminal size must be 1..{MaxColsOrRows}");
    }

    private Shell StartShell(int cols, int rows)
    {
        var console = PseudoConsole.Start(commandLine, workingDirectory, cols, rows);
        var shell = new Shell(console);
        var reader = Task.Run(() => ReadOutput(shell));
        _ = WatchExit(shell, reader);
        log.LogInformation("Terminal started");
        return shell;
    }

    /// <summary>
    /// Reads UTF-8 until end of stream, yielding text as it arrives. A character split across two
    /// reads is held back until it is complete, so no chunk ends in half a character.
    /// </summary>
    public static IEnumerable<string> DecodeUtf8(Stream stream)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[bytes.Length + 4]; // + room for a sequence carried over from the previous read
        while (true)
        {
            int read;
            try
            {
                read = stream.Read(bytes);
            }
            catch (IOException)
            {
                yield break; // Pipe broken: the console was closed.
            }
            if (read == 0) yield break;
            var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            if (count > 0) yield return new string(chars, 0, count);
        }
    }

    private void ReadOutput(Shell shell)
    {
        foreach (var text in DecodeUtf8(shell.Console.Output))
        {
            lock (_lock)
            {
                if (_shell != shell) return;
                _buffer.Append(text);
                _client?.Invoke(new TermOutput(text));
            }
        }
    }

    private async Task WatchExit(Shell shell, Task reader)
    {
        var code = await shell.Console.WaitForExitAsync();
        shell.Console.CloseConsole();
        await reader;
        lock (_lock)
        {
            if (_shell == shell)
            {
                _shell = null;
                _idleTimer?.Dispose();
                _idleTimer = null;
                _client?.Invoke(new TermExited(code));
            }
        }
        shell.Console.Dispose();
        log.LogInformation("Terminal exited with code {Code}", code);
    }

    private void KillIdleShell()
    {
        lock (_lock)
        {
            if (_client is not null || _shell is null) return;
            log.LogInformation("Killing terminal after {Minutes} idle minutes", IdleTimeout.TotalMinutes);
            _shell.Console.Kill();
        }
    }

    private sealed record Shell(PseudoConsole Console);
}

public static class ShellLocator
{
    /// <summary>pwsh.exe if it is on PATH, otherwise Windows PowerShell (spec §5.2).</summary>
    public static string Find()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    }
}
