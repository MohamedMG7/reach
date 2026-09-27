using Microsoft.Extensions.Logging;
using Reach.Agent.Commands;
using Reach.Agent.Input;
using Reach.Agent.Pairing;
using Reach.Agent.Storage;
using Reach.Agent.Terminal;
using Reach.Protocol;

namespace Reach.Agent.Net;

public sealed class SessionOptions
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>Everything a connection needs, built once at startup (and with fakes in tests).</summary>
public sealed class AgentContext
{
    public required KeyPair Identity { get; init; }
    public required DeviceStore Devices { get; init; }
    public required PairingService Pairing { get; init; }
    public required RateLimiter Limiter { get; init; }
    public required InputService Input { get; init; }
    public required ITerminalService Terminal { get; init; }
    public required ICommandService Commands { get; init; }
    public required SessionManager Sessions { get; init; }
    public required ILoggerFactory Logs { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public SessionOptions Options { get; init; } = new();
    public string PcName { get; init; } = Environment.MachineName;
    public string AgentVersion { get; init; } = typeof(AgentContext).Assembly.GetName().Version!.ToString(3);
}
