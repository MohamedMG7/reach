using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Reach.Agent.Storage;
using Reach.Protocol;

namespace Reach.Agent.Net;

/// <summary>
/// Everything that happens on one incoming connection, independent of WebSockets: rate limit,
/// handshake within the timeout, then the session until it ends (spec §3.4, §3.5).
/// </summary>
public sealed class ConnectionHandler(AgentContext agent)
{
    private readonly HandshakeAuthenticator _authenticator = new(
        agent.Identity, agent.Devices, agent.Pairing, agent.Logs.CreateLogger<HandshakeAuthenticator>());

    private readonly ILogger<ConnectionHandler> _log = agent.Logs.CreateLogger<ConnectionHandler>();

    /// <summary>True if the connection should be dropped before it is even accepted.</summary>
    public bool IsBlocked(IPAddress remote) => agent.Limiter.IsBlocked(remote);

    public async Task HandleAsync(IFrameChannel channel, IPAddress remote, CancellationToken shutdown)
    {
        AuthenticatedPeer? peer = null;
        byte[]? first = null;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        {
            timeout.CancelAfter(agent.Options.HandshakeTimeout);
            try
            {
                peer = await _authenticator.AuthenticateAsync(channel, timeout.Token);
                if (peer is not null) first = await ReceiveFirstAsync(channel, peer.Transport, timeout.Token);
            }
            catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
            {
                _log.LogInformation("Handshake from {Remote} timed out", remote);
            }
            catch (Exception e) when (e is IOException or WebSocketException or InvalidDataException or NoiseException)
            {
                _log.LogInformation("Handshake from {Remote} failed: {Reason}", remote, e.Message);
            }
        }
        if (peer is null || first is null)
        {
            if (!shutdown.IsCancellationRequested) agent.Limiter.RecordFailure(remote);
            channel.Abort();
            return;
        }

        var session = new Session(channel, peer, agent);
        if (!agent.Sessions.TryActivate(session, out var current))
        {
            // Another phone is connected: say who, then close. Not a failed attempt, so no rate limiting.
            var name = agent.Devices.All.FirstOrDefault(d => d.Id == current!.Device.Id)?.Name ?? DeviceStore.DefaultName;
            _log.LogInformation("Refused device {DeviceId}: {Name} is connected", peer.Device.Id, name);
            try
            {
                await channel.SendAsync(peer.Transport.Encrypt(MessageCodec.Encode(new Busy(name))), shutdown);
                await channel.CloseAsync();
            }
            catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException)
            {
                channel.Abort();
            }
            return;
        }
        try
        {
            await session.RunAsync(first, shutdown);
        }
        finally
        {
            agent.Sessions.Deactivate(session);
        }
    }

    /// <summary>
    /// Noise IK message 1 can be replayed, so answering it proves nothing about the peer. Only a
    /// transport message that decrypts shows it holds the keys; until then no session is activated
    /// and an existing one is left alone. Returns the plaintext, or null if the peer closed.
    /// </summary>
    private static async Task<byte[]?> ReceiveFirstAsync(IFrameChannel channel, Transport transport, CancellationToken ct)
    {
        var frame = await channel.ReceiveAsync(ct);
        return frame is null ? null : transport.Decrypt(frame);
    }
}
