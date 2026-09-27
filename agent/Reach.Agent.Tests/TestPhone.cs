using Reach.Agent.Net;
using Reach.Protocol;

namespace Reach.Agent.Tests;

/// <summary>The phone side of the protocol, driven by tests over any <see cref="IFrameChannel"/>.</summary>
public sealed class TestPhone(IFrameChannel channel, KeyPair? key = null)
{
    private Transport? _transport;

    public KeyPair Key { get; } = key ?? KeyPair.Generate();

    /// <summary>Sends handshake message 1 (with an optional pairing code); returns immediately.</summary>
    public async Task<IKHandshake> SendFirstMessageAsync(byte[] pcPublicKey, string? pairCode = null)
    {
        var handshake = new IKHandshake(initiator: true, HandshakeAuthenticator.Prologue, Key, rs: pcPublicKey);
        await channel.SendAsync(handshake.WriteMessage(MessageCodec.EncodeConnectRequest(new ConnectRequest(pairCode))), default);
        return handshake;
    }

    /// <summary>Full handshake. Throws if the PC drops the connection instead of answering.</summary>
    public async Task ConnectAsync(byte[] pcPublicKey, string? pairCode = null)
    {
        var handshake = await SendFirstMessageAsync(pcPublicKey, pairCode);
        var reply = await ReceiveFrameAsync() ?? throw new IOException("PC closed the connection during the handshake");
        handshake.ReadMessage(reply);
        _transport = handshake.Split();
    }

    public Task SendAsync(Message message) => SendPlaintextAsync(MessageCodec.Encode(message));

    /// <summary>Encrypts arbitrary bytes, e.g. JSON the codec would refuse to produce.</summary>
    public Task SendPlaintextAsync(byte[] plaintext) => channel.SendAsync(_transport!.Encrypt(plaintext), default);

    public Task SendRawAsync(byte[] frame) => channel.SendAsync(frame, default);

    /// <summary>The next application message, or null if the PC closed the connection.</summary>
    public async Task<Message?> ReceiveAsync()
    {
        var frame = await ReceiveFrameAsync();
        return frame is null ? null : MessageCodec.Decode(_transport!.Decrypt(frame));
    }

    /// <summary>Skips heartbeat pings until a message of type <typeparamref name="T"/> arrives.</summary>
    public async Task<T> ReceiveAsync<T>() where T : Message
    {
        while (true)
        {
            var message = await ReceiveAsync() ?? throw new IOException($"PC closed the connection while waiting for {typeof(T).Name}");
            if (message is T wanted) return wanted;
            if (message is not Ping) throw new InvalidOperationException($"expected {typeof(T).Name}, got {message}");
        }
    }

    private async Task<byte[]?> ReceiveFrameAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await channel.ReceiveAsync(timeout.Token);
    }
}
