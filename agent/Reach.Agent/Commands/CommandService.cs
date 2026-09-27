using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Reach.Protocol;

namespace Reach.Agent.Commands;

/// <param name="Run">A PowerShell script.</param>
public sealed record CommandDefinition(string Id, string Label, string Icon, bool Confirm, string Run);

public interface ICommandService
{
    IReadOnlyList<CommandInfo> List();

    Task<CmdResult> RunAsync(string id, CancellationToken ct);
}

/// <summary>
/// Saved one-tap commands from commands.json (spec §5.3). The file is created with defaults on
/// first run and reloaded when it changes; an invalid file keeps the last valid list.
/// </summary>
public sealed class CommandService : ICommandService, IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public const string DefaultIcon = "flash";

    /// <summary>
    /// Windows PowerShell reports a failed native command (e.g. "cmd /c exit 5") as exit code 1;
    /// this passes the real code through, and still reports 1 for failed cmdlets.
    /// </summary>
    private const string ExitCodeSuffix = "\nif (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE } else { exit 1 } }";

    public static readonly CommandDefinition[] Defaults =
    [
        new("sleep", "Sleep", "moon", true,
            "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Application]::SetSuspendState('Suspend', $false, $false)"),
        new("lock", "Lock", "lock-closed", true, "rundll32.exe user32.dll,LockWorkStation"),
        new("mute", "Mute / Unmute", "volume-mute", false, "(New-Object -ComObject WScript.Shell).SendKeys([char]173)"),
        new("vol-up", "Volume +", "volume-high", false, "(New-Object -ComObject WScript.Shell).SendKeys([char]175)"),
        new("vol-down", "Volume −", "volume-low", false, "(New-Object -ComObject WScript.Shell).SendKeys([char]174)"),
        new("shutdown-30", "Shut down in 30 min", "timer", true, "shutdown /s /t 1800"),
        new("shutdown-cancel", "Cancel shutdown", "close-circle", false, "shutdown /a"),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly TimeSpan _timeout;
    private readonly ILogger<CommandService> _log;
    private readonly FileSystemWatcher _watcher;
    private readonly Lock _lock = new();
    private IReadOnlyList<CommandDefinition> _commands = [];
    private Timer? _debounce;

    public CommandService(string path, TimeSpan timeout, ILogger<CommandService> log)
    {
        _path = path;
        _timeout = timeout;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, JsonSerializer.Serialize(Defaults, Json));
        Reload();
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Why the current file can't be used, or null. Set during construction too, before anyone can
    /// subscribe to <see cref="ConfigError"/>, so the tray reads it at startup.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>Raised with a human-readable reason when commands.json can't be used.</summary>
    public event Action<string>? ConfigError;

    /// <summary>Raised after a valid file has been loaded.</summary>
    public event Action? Reloaded;

    public IReadOnlyList<CommandInfo> List()
    {
        lock (_lock)
            return _commands.Select(c => new CommandInfo(c.Id, c.Label, string.IsNullOrWhiteSpace(c.Icon) ? DefaultIcon : c.Icon, c.Confirm)).ToList();
    }

    public async Task<CmdResult> RunAsync(string id, CancellationToken ct)
    {
        CommandDefinition? command;
        lock (_lock) command = _commands.FirstOrDefault(c => c.Id == id);
        if (command is null) return new CmdResult(id, false, -1, "unknown command");

        _log.LogInformation("Running saved command {Id}", id);
        var start = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(command.Run + ExitCodeSuffix)),
            },
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception e)
        {
            return new CmdResult(id, false, -1, e.Message);
        }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                _log.LogWarning("Saved command {Id} timed out", id);
                return new CmdResult(id, false, -1, "timed out");
            }
            var code = process.ExitCode;
            return code == 0 ? new CmdResult(id, true, 0) : new CmdResult(id, false, code, $"exit code {code}");
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce?.Dispose();
    }

    /// <summary>Editors save in several writes; wait until they settle.</summary>
    private void ScheduleReload()
    {
        lock (_lock)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Reload(), null, 300, Timeout.Infinite);
        }
    }

    private void Reload()
    {
        string? error;
        List<CommandDefinition>? loaded = null;
        try
        {
            loaded = JsonSerializer.Deserialize<List<CommandDefinition>>(ReadShared(_path), Json);
            error = Validate(loaded);
        }
        catch (Exception e)
        {
            // Runs on a timer thread, where anything unhandled would end the whole agent
            // (e.g. UnauthorizedAccessException from an ACL or a file pending delete).
            error = e.Message;
        }
        LastError = error;
        if (error is not null)
        {
            _log.LogWarning("commands.json ignored: {Error}", error);
            ConfigError?.Invoke(error);
            return;
        }
        lock (_lock) _commands = loaded!;
        Reloaded?.Invoke();
    }

    private static string? Validate(List<CommandDefinition>? commands)
    {
        if (commands is null) return "the file must contain a JSON array";
        var ids = new HashSet<string>();
        foreach (var c in commands)
        {
            if (c is null) return "null entry";
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Label) || string.IsNullOrWhiteSpace(c.Run))
                return "every command needs an id, a label and a run script";
            if (!ids.Add(c.Id)) return $"duplicate id: {c.Id}";
        }
        return null;
    }

    /// <summary>Reads while an editor may still hold the file open.</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
