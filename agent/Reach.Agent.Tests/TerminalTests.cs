using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Reach.Agent.Input;
using Reach.Agent.Terminal;
using Reach.Protocol;

namespace Reach.Agent.Tests;

/// <summary>Collects messages sent to a fake session, from any thread.</summary>
public sealed class Recorder
{
    private readonly List<Message> _messages = [];

    public Action<Message> Send => Add;

    public List<Message> Messages
    {
        get { lock (_messages) return [.. _messages]; }
    }

    public string Output => string.Concat(Messages.OfType<TermOutput>().Select(o => o.Data));

    private void Add(Message message)
    {
        lock (_messages) _messages.Add(message);
    }

    public async Task WaitForAsync(Func<Recorder, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition(this))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for {what}; got: {Output}");
            await Task.Delay(20);
        }
    }
}

public class RingTextTests
{
    [Fact]
    public void ReplaysEverythingWhileNothingWasDropped()
    {
        var ring = new RingText(100);
        ring.Append("PS> dir\r\n");
        ring.Append("a.txt");
        Assert.Equal("PS> dir\r\na.txt", ring.Replay());
    }

    [Fact]
    public void KeepsOnlyTheNewestCharactersAndReplaysFromTheFirstWholeLine()
    {
        var ring = new RingText(10);
        ring.Append("0123456789");
        ring.Append("ab\ncd");
        Assert.Equal("cd", ring.Replay());
    }

    [Fact]
    public void ReplaysNothingWhenATrimmedBufferHasNoNewline()
    {
        var ring = new RingText(4);
        ring.Append("abcdef");
        Assert.Equal("", ring.Replay());
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var ring = new RingText(4);
        ring.Append("abcdef");
        ring.Clear();
        ring.Append("x\ny");
        Assert.Equal("x\ny", ring.Replay());
    }
}

public class TerminalChunkingTests
{
    [Fact]
    public void SplitsLongTextIntoMessageSizedChunks()
    {
        var chunks = TerminalService.Chunk(new string('a', TerminalService.ChunkChars * 2 + 5)).ToList();
        Assert.Equal([TerminalService.ChunkChars, TerminalService.ChunkChars, 5], chunks.Select(c => c.Length));
    }

    [Fact]
    public void NeverCutsASurrogatePairInHalf()
    {
        var text = new string('a', TerminalService.ChunkChars - 1) + "👋" + "b";
        var chunks = TerminalService.Chunk(text).ToList();
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, c => Assert.False(char.IsHighSurrogate(c[^1])));
    }

    [Fact]
    public void AChunkOfWorstCaseEscapesFitsInOneNoiseMessage()
    {
        var chunk = new string('\u001b', TerminalService.ChunkChars);
        var plaintext = MessageCodec.Encode(new TermOutput(chunk));
        Assert.True(plaintext.Length + Noise.TagLength <= Noise.MaxMessageLength, $"{plaintext.Length} bytes");
    }

    [Fact]
    public void DecodesCharactersSplitAcrossReads()
    {
        var text = "PS> مرحبا 👋\r\n";
        var chunks = TerminalService.DecodeUtf8(new OneByteAtATimeStream(Encoding.UTF8.GetBytes(text))).ToList();
        Assert.Equal(text, string.Concat(chunks));
        Assert.DoesNotContain(chunks, c => c.Contains((char)0xFFFD)); // U+FFFD = a mangled character
        Assert.All(chunks, c => Assert.False(char.IsHighSurrogate(c[^1])));
    }

    private sealed class OneByteAtATimeStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(1, buffer.Length)]);
    }
}

/// <summary>Real ConPTY running cmd.exe (fast to start; the service is shell-agnostic).</summary>
public sealed class TerminalServiceTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly TerminalService _terminal;

    public TerminalServiceTests()
    {
        _terminal = new TerminalService("cmd.exe /Q", Path.GetTempPath(), _time, NullLogger<TerminalService>.Instance);
    }

    public void Dispose() => _terminal.Dispose();

    [Fact]
    public async Task RunsCommandsAndStreamsTheirOutput()
    {
        var phone = new Recorder();
        _terminal.Open(80, 24, phone.Send);
        Assert.Equal(new TermOpened(false), phone.Messages[0]);

        // %OS% expands to Windows_NT, so a match proves the command ran (not just the echoed input).
        _terminal.Input("echo hi-%OS%\r");

        await phone.WaitForAsync(p => p.Output.Contains("hi-Windows_NT"), "echo output");
    }

    [Fact]
    public async Task PassesUnicodeThroughBothWays()
    {
        var phone = new Recorder();
        _terminal.Open(80, 24, phone.Send);

        _terminal.Input("echo مرحبا-%OS%\r");

        await phone.WaitForAsync(p => p.Output.Contains("مرحبا-Windows_NT"), "Arabic echo");
    }

    [Fact]
    public async Task AReattachingPhoneGetsTheEarlierOutputReplayed()
    {
        var first = new Recorder();
        _terminal.Open(80, 24, first.Send);
        _terminal.Input("echo before-%OS%\r");
        await first.WaitForAsync(p => p.Output.Contains("before-Windows_NT"), "echo output");
        _terminal.Detach(first.Send);

        var second = new Recorder();
        _terminal.Open(100, 30, second.Send);

        Assert.Equal(new TermOpened(true), second.Messages[0]);
        Assert.Contains("before-Windows_NT", second.Output);
    }

    [Fact]
    public async Task ReportsWhenTheShellExitsAndStartsAFreshOneNextTime()
    {
        var phone = new Recorder();
        _terminal.Open(80, 24, phone.Send);

        _terminal.Input("exit 7\r");

        await phone.WaitForAsync(p => p.Messages.Contains(new TermExited(7)), "term.exited");
        Assert.False(_terminal.IsRunning);
        var again = new Recorder();
        _terminal.Open(80, 24, again.Send);
        Assert.Equal(new TermOpened(false), again.Messages[0]);
    }

    [Fact]
    public async Task KillsTheShellAfterThirtyIdleMinutes()
    {
        var phone = new Recorder();
        _terminal.Open(80, 24, phone.Send);
        _terminal.Detach(phone.Send);

        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.True(_terminal.IsRunning);
        _time.Advance(TimeSpan.FromMinutes(1));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_terminal.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.False(_terminal.IsRunning);
        Assert.DoesNotContain(phone.Messages, m => m is TermExited);
    }

    [Fact]
    public void ReattachingCancelsTheIdleCountdown()
    {
        var phone = new Recorder();
        _terminal.Open(80, 24, phone.Send);
        _terminal.Detach(phone.Send);
        _time.Advance(TimeSpan.FromMinutes(20));

        _terminal.Open(80, 24, phone.Send);
        _time.Advance(TimeSpan.FromMinutes(20));

        Assert.True(_terminal.IsRunning);
    }

    [Fact]
    public void DetachingAnotherSessionDoesNotDetachTheCurrentOne()
    {
        var current = new Recorder();
        var old = new Recorder();
        _terminal.Open(80, 24, current.Send);

        _terminal.Detach(old.Send);
        _time.Advance(TimeSpan.FromMinutes(31));

        Assert.True(_terminal.IsRunning);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(1001, 24)]
    [InlineData(-5, -5)]
    public void RejectsImpossibleSizesWithoutStartingAShell(int cols, int rows)
    {
        Assert.Throws<BadArgsException>(() => _terminal.Open(cols, rows, new Recorder().Send));
        Assert.False(_terminal.IsRunning);
    }

    [Fact]
    public void InputWithoutAShellIsIgnored()
    {
        _terminal.Input("dir\r");
        Assert.False(_terminal.IsRunning);
    }

    [Fact]
    public void FindsAPowerShell()
    {
        var shell = ShellLocator.Find();
        Assert.True(File.Exists(shell), shell);
        Assert.Contains(Path.GetFileName(shell), new[] { "pwsh.exe", "powershell.exe" });
    }
}
