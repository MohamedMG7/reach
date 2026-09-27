using System.Threading.Channels;
using Reach.Agent.Net;

namespace Reach.Agent.Tests;

/// <summary>One end of an in-memory frame connection. <see cref="Pair"/> returns both ends.</summary>
public sealed class MemoryChannel : IFrameChannel
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;

    private MemoryChannel(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public bool Aborted { get; private set; }
    public bool Closed { get; private set; }

    public static (MemoryChannel Pc, MemoryChannel Phone) Pair()
    {
        var toPc = Channel.CreateUnbounded<byte[]>();
        var toPhone = Channel.CreateUnbounded<byte[]>();
        return (new MemoryChannel(toPc, toPhone), new MemoryChannel(toPhone, toPc));
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public Task SendAsync(byte[] frame, CancellationToken ct)
    {
        if (!_outgoing.Writer.TryWrite(frame)) throw new IOException("connection closed");
        return Task.CompletedTask;
    }

    public void Abort()
    {
        Aborted = true;
        Shutdown();
    }

    /// <summary>Like a peer that answers a close at once: both directions end.</summary>
    public Task CloseOutputAsync()
    {
        Shutdown();
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        Closed = true;
        Shutdown();
        return Task.CompletedTask;
    }

    private void Shutdown()
    {
        _outgoing.Writer.TryComplete();
        _incoming.Writer.TryComplete();
    }
}
