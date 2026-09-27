using Microsoft.Extensions.Logging;
using Reach.Agent.Pairing;
using Reach.Agent.Storage;
using Reach.Protocol;

namespace Reach.Agent.Net;

public sealed record AuthenticatedPeer(Device Device, Transport Transport, bool JustPaired);

/// <summary>
/// Runs the responder side of the Noise IK handshake (spec §3.3, §3.4) and decides whether the
/// phone may in: its static key must be paired, or message 1 must carry a valid pairing code.
/// </summary>
public sealed class HandshakeAuthenticator(KeyPair identity, DeviceStore devices, PairingService pairing, ILogger<HandshakeAuthenticator> log)
{
    public static readonly byte[] Prologue = "reach/1"u8.ToArray();

    /// <summary>
    /// Null means "reject": the caller must drop the connection without replying and count a failure.
    /// Transport-level exceptions (cancellation, socket errors) propagate.
    /// </summary>
    public async Task<AuthenticatedPeer?> AuthenticateAsync(IFrameChannel channel, CancellationToken ct)
    {
        var first = await channel.ReceiveAsync(ct);
        if (first is null) return null;

        var handshake = new IKHandshake(initiator: false, Prologue, identity);
        ConnectRequest request;
        try
        {
            request = MessageCodec.DecodeConnectRequest(handshake.ReadMessage(first));
        }
        catch (Exception e) when (e is NoiseException or ProtocolException)
        {
            log.LogInformation("Handshake rejected: {Reason}", e.Message);
            return null;
        }

        var phoneKey = handshake.RemoteStaticKey!;
        var device = devices.FindByKey(phoneKey);
        var justPaired = false;
        if (request.PairCode is not null)
        {
            if (!pairing.TryConsume(request.PairCode))
            {
                log.LogWarning("Pairing rejected: invalid or expired code");
                return null;
            }
            if (device is null)
            {
                device = devices.Add(phoneKey);
                justPaired = true;
                log.LogInformation("Paired new device {DeviceId}", device.Id);
            }
        }
        if (device is null)
        {
            log.LogInformation("Handshake rejected: unknown device");
            return null;
        }

        await channel.SendAsync(handshake.WriteMessage(MessageCodec.EncodeConnectRequest(new ConnectRequest())), ct);
        return new AuthenticatedPeer(device, handshake.Split(), justPaired);
    }
}
