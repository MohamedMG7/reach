using System.Net;
using Reach.Agent.Net;
using Reach.Protocol;

namespace Reach.Agent.Tests;

public sealed class ConnectionHandlerTests : IDisposable
{
    private static readonly IPAddress PhoneIp = IPAddress.Parse("192.168.1.30");
    private readonly TestAgent _agent = new();

    public void Dispose() => _agent.Dispose();

    /// <summary>Starts the PC side of a new connection and returns the phone end and the handler task.</summary>
    private (MemoryChannel Pc, MemoryChannel Phone, Task Handling) Connect(CancellationToken shutdown = default)
    {
        var (pc, phone) = MemoryChannel.Pair();
        return (pc, phone, _agent.Handler.HandleAsync(pc, PhoneIp, shutdown));
    }

    private async Task<(TestPhone Phone, MemoryChannel PcEnd, Task Handling)> ConnectPairedAsync()
    {
        var (pc, phoneEnd, handling) = Connect();
        var phone = new TestPhone(phoneEnd);
        _agent.Devices.Add(phone.Key.PublicKey);
        await phone.ConnectAsync(_agent.Context.Identity.PublicKey);
        await phone.SendAsync(new Hello("Bedroom iPhone", "1.0.0")); // completes the handshake
        await phone.ReceiveAsync<Welcome>();
        return (phone, pc, handling);
    }

    [Fact]
    public async Task APairedPhoneGetsAWorkingSession()
    {
        var (_, phoneEnd, _) = Connect();
        var phone = new TestPhone(phoneEnd);
        _agent.Devices.Add(phone.Key.PublicKey);
        await phone.ConnectAsync(_agent.Context.Identity.PublicKey);

        await phone.SendAsync(new Hello("Bedroom iPhone", "1.0.0"));
        Assert.Equal(new Welcome("TEST-PC", "1.0.0"), await phone.ReceiveAsync<Welcome>());

        await phone.SendAsync(new MouseMove(5, 5));
        await phone.SendAsync(new Ping());
        await phone.ReceiveAsync<Pong>();
        Assert.Contains("move 5 5", _agent.Sink.Calls);
        Assert.NotNull(_agent.Context.Sessions.Active);
    }

    [Fact]
    public async Task PairingOverTheSameConnectionContinuesAsANormalSession()
    {
        var (_, phoneEnd, _) = Connect();
        var phone = new TestPhone(phoneEnd);

        await phone.ConnectAsync(_agent.Context.Identity.PublicKey, _agent.Context.Pairing.Start().Code);
        await phone.SendAsync(new Hello("New iPhone", "1.0.0"));

        await phone.ReceiveAsync<Welcome>();
        Assert.Equal("New iPhone", _agent.Devices.FindByKey(phone.Key.PublicKey)!.Name);
    }

    [Fact]
    public async Task AFailedHandshakeDropsTheConnectionAndCountsAgainstTheIp()
    {
        var (pc, phoneEnd, handling) = Connect();
        await new TestPhone(phoneEnd).SendFirstMessageAsync(_agent.Context.Identity.PublicKey);

        await handling;

        Assert.True(pc.Aborted);
        for (var i = 0; i < 4; i++)
        {
            var (_, next, attempt) = Connect();
            await new TestPhone(next).SendFirstMessageAsync(_agent.Context.Identity.PublicKey);
            await attempt;
        }
        Assert.True(_agent.Handler.IsBlocked(PhoneIp));
    }

    [Fact]
    public async Task AHandshakeThatNeverArrivesTimesOut()
    {
        var (pc, _, handling) = Connect();

        await handling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(pc.Aborted);
    }

    [Fact]
    public async Task GarbageAfterTheHandshakeEndsTheSession()
    {
        var (phone, pc, handling) = await ConnectPairedAsync();

        await phone.SendRawAsync(new byte[64]);

        await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pc.Closed);
        Assert.Null(_agent.Context.Sessions.Active);
    }

    [Fact]
    public async Task AReplayedFrameEndsTheSession()
    {
        var (pc, phoneEnd) = MemoryChannel.Pair();
        var tap = new TappedChannel(phoneEnd);
        var phone = new TestPhone(tap);
        _agent.Devices.Add(phone.Key.PublicKey);
        var handling = _agent.Handler.HandleAsync(pc, PhoneIp, default);
        await phone.ConnectAsync(_agent.Context.Identity.PublicKey);
        await phone.SendAsync(new Ping());
        await phone.ReceiveAsync<Pong>();

        await phone.SendRawAsync(tap.LastSent!);

        await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pc.Closed);
    }

    [Fact]
    public async Task AReplayedHandshakeCannotReplaceTheLiveSession()
    {
        using var agent = new TestAgent(new SessionOptions
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(500),
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            IdleTimeout = TimeSpan.FromSeconds(10),
        });
        var (pc, phoneEnd) = MemoryChannel.Pair();
        var tap = new TappedChannel(phoneEnd);
        var phone = new TestPhone(tap);
        agent.Devices.Add(phone.Key.PublicKey);
        _ = agent.Handler.HandleAsync(pc, PhoneIp, default);
        await phone.ConnectAsync(agent.Context.Identity.PublicKey);
        await phone.SendAsync(new Hello("Bedroom iPhone", "1.0.0"));
        await phone.ReceiveAsync<Welcome>();
        var live = agent.Context.Sessions.Active;

        var attackerIp = IPAddress.Parse("192.168.1.66");
        var (attackerPc, attackerEnd) = MemoryChannel.Pair();
        var attack = agent.Handler.HandleAsync(attackerPc, attackerIp, default);
        await attackerEnd.SendAsync(tap.FirstSent!, default);
        await Task.WhenAny(attack, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(live, agent.Context.Sessions.Active);
        Assert.False(pc.Closed);
        Assert.True(attackerPc.Aborted);
        for (var i = 1; i < RateLimiter.MaxFailures; i++) agent.Context.Limiter.RecordFailure(attackerIp);
        Assert.True(agent.Handler.IsBlocked(attackerIp)); // the replay counted as a failure
    }

    [Fact]
    public async Task AMalformedMessageGetsAnErrorAndTheSessionStaysOpen()
    {
        var (phone, _, _) = await ConnectPairedAsync();

        await phone.SendPlaintextAsync("{\"type\":\"mouse.teleport\"}"u8.ToArray());

        Assert.Equal("bad_message", (await phone.ReceiveAsync<ErrorMessage>()).Code);
        await phone.SendAsync(new Ping());
        await phone.ReceiveAsync<Pong>();
    }

    [Fact]
    public async Task SendsHeartbeatPingsAndClosesASilentSession()
    {
        var (phone, pc, handling) = await ConnectPairedAsync();

        Assert.Equal(new Ping(), await phone.ReceiveAsync());

        await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pc.Closed);
    }

    [Fact]
    public async Task ASessionEndingMidDragReleasesTheButton()
    {
        var (phone, _, handling) = await ConnectPairedAsync();
        await phone.SendAsync(new MouseButton("left", true));
        await phone.SendAsync(new Ping());
        await phone.ReceiveAsync<Pong>();

        await handling.WaitAsync(TimeSpan.FromSeconds(5)); // heartbeat timeout: the phone went silent

        Assert.Equal("button Left up", _agent.Sink.Calls[^1]);
    }

    [Fact]
    public async Task TheSamePhoneReconnectingReplacesItsOldSession()
    {
        var (first, firstPc, firstHandling) = await ConnectPairedAsync();

        // The phone lost Wi-Fi and comes back before the PC noticed the old connection died.
        var (pc, phoneEnd, _) = Connect();
        var again = new TestPhone(phoneEnd, first.Key);
        await again.ConnectAsync(_agent.Context.Identity.PublicKey);
        await again.SendAsync(new Hello("Bedroom iPhone", "1.0.0"));
        await again.ReceiveAsync<Welcome>();

        await firstHandling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(firstPc.Closed);
        await again.SendAsync(new Ping());
        await again.ReceiveAsync<Pong>();
    }

    [Fact]
    public async Task ASecondPhoneIsToldThePcIsBusyAndTheFirstKeepsItsSession()
    {
        var (first, firstPc, _) = await ConnectPairedAsync();

        var (pc, phoneEnd, handling) = Connect();
        var second = new TestPhone(phoneEnd);
        _agent.Devices.Add(second.Key.PublicKey);
        await second.ConnectAsync(_agent.Context.Identity.PublicKey);
        await second.SendAsync(new Hello("Kitchen Pixel", "1.0.0"));

        Assert.Equal(new Busy("Bedroom iPhone"), await second.ReceiveAsync<Busy>());
        await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pc.Closed);
        Assert.False(firstPc.Closed);
        await first.SendAsync(new Ping());
        await first.ReceiveAsync<Pong>();
    }

    [Fact]
    public async Task DisconnectingThePhoneFromThePcTellsItAndEndsTheSession()
    {
        var (phone, pc, handling) = await ConnectPairedAsync();

        _agent.Context.Sessions.DisconnectActive();

        Assert.Equal(new Bye(), await phone.ReceiveAsync<Bye>());
        await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pc.Closed);
        Assert.Null(_agent.Context.Sessions.Active);
    }

    [Fact]
    public async Task TheActiveSessionKnowsWhenItStarted()
    {
        var before = _agent.Context.Time.GetUtcNow();
        await ConnectPairedAsync();

        Assert.InRange(_agent.Context.Sessions.Active!.StartedAt, before, _agent.Context.Time.GetUtcNow());
    }

    [Fact]
    public async Task RevokingADeviceClosesItsSession()
    {
        var (phone, pc, handling) = await ConnectPairedAsync();
        var device = _agent.Devices.FindByKey(phone.Key.PublicKey)!;

        _agent.Devices.Remove(device.Id);
        _agent.Context.Sessions.CloseDevice(device.Id);

        await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pc.Closed);
    }

    [Fact]
    public async Task ShutdownEndsTheSession()
    {
        using var shutdown = new CancellationTokenSource();
        var (pc, phoneEnd, handling) = Connect(shutdown.Token);
        var phone = new TestPhone(phoneEnd);
        _agent.Devices.Add(phone.Key.PublicKey);
        await phone.ConnectAsync(_agent.Context.Identity.PublicKey);
        await phone.SendAsync(new Hello("Bedroom iPhone", "1.0.0"));
        await phone.ReceiveAsync<Welcome>();

        shutdown.Cancel();

        await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pc.Closed);
    }

    /// <summary>Remembers the first and last frames the phone sent, so a test can replay them.</summary>
    private sealed class TappedChannel(IFrameChannel inner) : IFrameChannel
    {
        public byte[]? FirstSent { get; private set; }
        public byte[]? LastSent { get; private set; }
        public Task<byte[]?> ReceiveAsync(CancellationToken ct) => inner.ReceiveAsync(ct);

        public Task SendAsync(byte[] frame, CancellationToken ct)
        {
            FirstSent ??= frame;
            LastSent = frame;
            return inner.SendAsync(frame, ct);
        }

        public void Abort() => inner.Abort();
        public Task CloseOutputAsync() => inner.CloseOutputAsync();
        public Task CloseAsync() => inner.CloseAsync();
    }
}
