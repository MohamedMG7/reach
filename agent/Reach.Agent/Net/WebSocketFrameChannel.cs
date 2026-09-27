using System.Net.WebSockets;
using Reach.Protocol;

namespace Reach.Agent.Net;

/// <summary>
/// One Noise message per WebSocket binary message (spec §3.4). Works for server-side sockets
/// and for <see cref="ClientWebSocket"/> (used by tests).
/// </summary>
public sealed class WebSocketFrameChannel(WebSocket socket) : IFrameChannel
{
    private readonly byte[] _buffer = new byte[Noise.MaxMessageLength];

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var count = 0;
        while (true)
        {
            if (count == _buffer.Length)
                throw new InvalidDataException($"message larger than {Noise.MaxMessageLength} bytes");
            var result = await socket.ReceiveAsync(_buffer.AsMemory(count), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("text frames are not allowed");
            count += result.Count;
            if (result.EndOfMessage) return _buffer[..count];
        }
    }

    public Task SendAsync(byte[] frame, CancellationToken ct) =>
        socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, endOfMessage: true, ct).AsTask();

    public void Abort() => socket.Abort();

    /// <summary>How long a polite close waits for the peer's answer before dropping the connection.</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    public async Task CloseOutputAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public async Task CloseAsync()
    {
        await CloseOutputAsync();
        // Aborting right away resets the connection, and a reset makes the peer discard what it
        // hasn't read yet (a final busy or bye). So wait for its answering close first.
        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            while (socket.State == WebSocketState.CloseSent)
            {
                var result = await socket.ReceiveAsync(_buffer.AsMemory(), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        socket.Abort();
    }
}
