using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Reach.Agent.Commands;
using Reach.Agent.Input;
using Reach.Agent.Net;
using Reach.Agent.Pairing;
using Reach.Agent.Storage;
using Reach.Agent.Terminal;
using Reach.Protocol;

namespace Reach.Agent.Tests;

public sealed class FakeTerminal : ITerminalService
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public Action<Message>? Attached { get; private set; }

    public void Open(int cols, int rows, Action<Message> send)
    {
        Calls.Enqueue($"open {cols}x{rows}");
        Attached = send;
        send(new TermOpened(false));
    }

    public void Input(string data) => Calls.Enqueue($"input {data}");
    public void Resize(int cols, int rows) => Calls.Enqueue($"resize {cols}x{rows}");

    public void Detach(Action<Message> send)
    {
        if (Attached != send) return;
        Calls.Enqueue("detach");
        Attached = null;
    }
}

public sealed class FakeCommands : ICommandService
{
    public IReadOnlyList<CommandInfo> List() => [new CommandInfo("sleep", "Sleep", "moon", true)];

    public Task<CmdResult> RunAsync(string id, CancellationToken ct) =>
        Task.FromResult(id == "sleep" ? new CmdResult(id, true, 0) : new CmdResult(id, false, -1, "unknown command"));
}

/// <summary>Captures log lines so tests can check what is (not) logged.</summary>
public sealed class ListLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ListLogger(Lines);

    public void Dispose()
    {
    }

    private sealed class ListLogger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception));
    }
}

/// <summary>A fully wired agent with fake OS services, a temp data directory and short timeouts.</summary>
public sealed class TestAgent : IDisposable
{
    private readonly TempDir _dir = new();

    public TestAgent(SessionOptions? options = null)
    {
        Devices = new DeviceStore(_dir.File("devices.json"), Clock);
        Context = new AgentContext
        {
            Identity = KeyPair.Generate(),
            Devices = Devices,
            Pairing = new PairingService(Clock),
            Limiter = new RateLimiter(Clock),
            Input = new InputService(Sink),
            Terminal = Terminal,
            Commands = new FakeCommands(),
            Sessions = new SessionManager(),
            Logs = LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace)),
            Options = options ?? new SessionOptions
            {
                HandshakeTimeout = TimeSpan.FromMilliseconds(500),
                HeartbeatInterval = TimeSpan.FromMilliseconds(100),
                IdleTimeout = TimeSpan.FromMilliseconds(400),
            },
            PcName = "TEST-PC",
            AgentVersion = "1.0.0",
        };
        Handler = new ConnectionHandler(Context);
    }

    /// <summary>Drives pairing codes, device timestamps and the rate limiter (not session timers).</summary>
    public FakeTimeProvider Clock { get; } = new();
    public FakeInputSink Sink { get; } = new();
    public FakeTerminal Terminal { get; } = new();
    public ListLoggerProvider Logs { get; } = new();
    public DeviceStore Devices { get; }
    public AgentContext Context { get; }
    public ConnectionHandler Handler { get; }

    public void Dispose() => _dir.Dispose();
}
