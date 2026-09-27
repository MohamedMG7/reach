using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Reach.Agent.Net;

/// <summary>Kestrel serving WebSockets on / (spec §5: 0.0.0.0:47800, no URL ACL needed).</summary>
public sealed class AgentServer(AgentContext agent) : IAsyncDisposable
{
    public const int DefaultPort = 47800;

    /// <summary>Open connections one IP may hold; a phone needs one, two while it reconnects.</summary>
    public const int MaxConnectionsPerIp = 4;

    /// <summary>Open connections in total, so a LAN flood can't pin unbounded memory.</summary>
    public const int MaxConnections = 64;

    private readonly ConnectionHandler _handler = new(agent);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<IPAddress, int> _open = [];
    private const string HandledKey = "reach.handled";
    private WebApplication? _app;

    /// <summary>The bound port (useful when started with port 0).</summary>
    public int Port { get; private set; }

    /// <exception cref="IOException">The port is already in use.</exception>
    public async Task StartAsync(IPAddress address, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(agent.Logs);
        builder.WebHost.ConfigureKestrel(k =>
        {
            // Spec §3.5: 10 s from TCP connect, so a silent socket must not wait out Kestrel's defaults.
            k.Limits.KeepAliveTimeout = agent.Options.HandshakeTimeout;
            k.Limits.RequestHeadersTimeout = agent.Options.HandshakeTimeout;
            k.Limits.MaxConcurrentConnections = MaxConnections;
            k.Limits.MaxConcurrentUpgradedConnections = MaxConnections;
            k.Listen(address, port, listen => listen.Use(next => connection => GateAsync(connection, next)));
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
        app.Map("/", HandleAsync);
        await app.StartAsync();
        _app = app;
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Port = new Uri(bound).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        if (_app is not null) await _app.DisposeAsync();
    }

    /// <summary>
    /// Runs at TCP accept, before any HTTP: drops blocked IPs, caps connections per IP, and counts a
    /// connection that closes before reaching the handshake (silent, slow or plain HTTP) as a failure.
    /// </summary>
    private async Task GateAsync(ConnectionContext connection, ConnectionDelegate next)
    {
        var remote = (connection.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (_handler.IsBlocked(remote))
        {
            connection.Abort();
            return;
        }
        lock (_open)
        {
            var count = _open.GetValueOrDefault(remote);
            if (count >= MaxConnectionsPerIp)
            {
                agent.Limiter.RecordFailure(remote);
                connection.Abort();
                return;
            }
            _open[remote] = count + 1;
        }
        try
        {
            await next(connection);
        }
        finally
        {
            lock (_open)
            {
                if (--_open[remote] == 0) _open.Remove(remote);
            }
            // The handler counts its own handshake failures; count only what never reached it.
            if (!connection.Items.ContainsKey(HandledKey) && !_shutdown.IsCancellationRequested)
                agent.Limiter.RecordFailure(remote);
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress ?? IPAddress.None;
        if (_handler.IsBlocked(remote))
        {
            context.Abort();
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Features.Get<IConnectionItemsFeature>()!.Items[HandledKey] = true;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await _handler.HandleAsync(new WebSocketFrameChannel(socket), remote, _shutdown.Token);
    }
}
