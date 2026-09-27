namespace Reach.Agent.Net;

/// <summary>A message-oriented, binary connection (a WebSocket in production, in-memory in tests).</summary>
public interface IFrameChannel
{
    /// <summary>The next whole binary message, or null when the peer closed the connection.</summary>
    /// <exception cref="InvalidDataException">The peer sent a text frame or a message over 65,535 bytes.</exception>
    Task<byte[]?> ReceiveAsync(CancellationToken ct);

    Task SendAsync(byte[] frame, CancellationToken ct);

    /// <summary>Drops the connection without a goodbye (every handshake failure, spec §3.3/§3.4).</summary>
    void Abort();

    /// <summary>
    /// Starts a polite close while a receive may be pending: nothing more is sent, and the peer's
    /// answering close then ends <see cref="ReceiveAsync"/> with null. Never throws.
    /// </summary>
    Task CloseOutputAsync();

    /// <summary>
    /// Closes the connection politely, waiting briefly for the peer to answer, so frames already
    /// sent (a final busy or bye) are read before the connection goes away; never throws.
    /// </summary>
    Task CloseAsync();
}
