using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Reach.Agent.Storage;
using Reach.Protocol;

namespace Reach.Agent.Net;

/// <summary>
/// One authenticated connection after the handshake (spec §3.4): decrypts and routes incoming
/// messages, sends outgoing ones in order, and runs the heartbeat. Any decryption failure,
/// network error or 15 s of silence ends it.
/// </summary>
public sealed class Session
{
    private readonly IFrameChannel _channel;
    private readonly Transport _transport;
    private readonly AgentContext _agent;
    private readonly ILogger<Session> _log;
    private readonly Channel<Message> _outbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private long _lastReceived;

    public Session(IFrameChannel channel, AuthenticatedPeer peer, AgentContext agent)
    {
        _channel = channel;
        _transport = peer.Transport;
        _agent = agent;
        _log = agent.Logs.CreateLogger<Session>();
        Device = peer.Device;
        StartedAt = agent.Time.GetUtcNow();
    }

    public Device Device { get; }

    /// <summary>When the phone connected, shown in the tray.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Queues a message; silently dropped once the session has ended. Thread-safe.</summary>
    public void Send(Message message) => _outbox.Writer.TryWrite(message);

    /// <summary>Ends the session (replaced by a newer one, device revoked). Thread-safe.</summary>
    public void Close() => _stop.Cancel();

    /// <summary>Tells the phone <see cref="Bye"/> and ends the session once that has been sent. Thread-safe.</summary>
    public void Disconnect()
    {
        Send(new Bye());
        _outbox.Writer.TryComplete(); // the write loop ends the session after sending it
    }

    /// <param name="first">The decrypted first message, which completed the handshake (normally <c>hello</c>).</param>
    public async Task RunAsync(byte[] first, CancellationToken shutdown)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _stop.Token);
        var router = new MessageRouter(Device, _agent, Send, _agent.Logs.CreateLogger<MessageRouter>());
        Interlocked.Exchange(ref _lastReceived, _agent.Time.GetTimestamp());
        _log.LogInformation("Session started for device {DeviceId}", Device.Id);

        var writer = WriteLoopAsync(session);
        var heartbeat = HeartbeatLoopAsync(session);
        try
        {
            Dispatch(router, first);
            await ReceiveLoopAsync(router, session.Token);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or WebSocketException or InvalidDataException or NoiseException)
        {
            _log.LogInformation("Session for device {DeviceId} ended: {Reason}", Device.Id, e.Message);
        }
        finally
        {
            session.Cancel();
            router.SessionEnded();
            _outbox.Writer.TryComplete();
            await Task.WhenAll(writer, heartbeat);
            await _channel.CloseAsync();
            _log.LogInformation("Session closed for device {DeviceId}", Device.Id);
        }
    }

    private async Task ReceiveLoopAsync(MessageRouter router, CancellationToken ct)
    {
        while (true)
        {
            var frame = await _channel.ReceiveAsync(ct);
            if (frame is null) return;
            Interlocked.Exchange(ref _lastReceived, _agent.Time.GetTimestamp());
            Dispatch(router, _transport.Decrypt(frame));
        }
    }

    private void Dispatch(MessageRouter router, byte[] plaintext)
    {
        Message message;
        try
        {
            message = MessageCodec.Decode(plaintext);
        }
        catch (ProtocolException e)
        {
            Send(new ErrorMessage("bad_message", e.Message));
            return;
        }
        router.Handle(message);
    }

    private async Task WriteLoopAsync(CancellationTokenSource session)
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(session.Token))
                await _channel.SendAsync(_transport.Encrypt(MessageCodec.Encode(message)), session.Token);
            // The outbox was completed by Disconnect and everything is sent. Close politely: the
            // phone's answering close ends the receive loop. Cancelling the receive instead would
            // reset the connection, and the phone would lose the bye and reconnect.
            await _channel.CloseOutputAsync();
            session.CancelAfter(WebSocketFrameChannel.CloseTimeout); // if the phone never answers
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.LogInformation("Session for device {DeviceId} failed to send: {Reason}", Device.Id, e.Message);
            session.Cancel();
        }
    }

    private async Task HeartbeatLoopAsync(CancellationTokenSource session)
    {
        var options = _agent.Options;
        try
        {
            while (true)
            {
                await Task.Delay(options.HeartbeatInterval, _agent.Time, session.Token);
                if (_agent.Time.GetElapsedTime(Interlocked.Read(ref _lastReceived)) > options.IdleTimeout)
                {
                    _log.LogInformation("Session for device {DeviceId} timed out", Device.Id);
                    session.Cancel();
                    return;
                }
                Send(new Ping());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
