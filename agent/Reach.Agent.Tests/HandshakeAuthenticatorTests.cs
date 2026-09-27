using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Reach.Agent.Net;
using Reach.Agent.Pairing;
using Reach.Agent.Storage;
using Reach.Protocol;

namespace Reach.Agent.Tests;

public sealed class HandshakeAuthenticatorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new();
    private readonly KeyPair _pc = KeyPair.Generate();
    private readonly DeviceStore _devices;
    private readonly PairingService _pairing;
    private readonly HandshakeAuthenticator _auth;

    public HandshakeAuthenticatorTests()
    {
        _devices = new DeviceStore(_dir.File("devices.json"), _time);
        _pairing = new PairingService(_time);
        _auth = new HandshakeAuthenticator(_pc, _devices, _pairing, NullLogger<HandshakeAuthenticator>.Instance);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task APairedPhoneConnectsAndBothSidesShareTheTransport()
    {
        var (pcEnd, phoneEnd) = MemoryChannel.Pair();
        var phone = new TestPhone(phoneEnd);
        var device = _devices.Add(phone.Key.PublicKey);

        var connect = phone.ConnectAsync(_pc.PublicKey);
        var peer = await _auth.AuthenticateAsync(pcEnd, default);
        await connect;

        Assert.NotNull(peer);
        Assert.Equal(device, peer.Device);
        Assert.False(peer.JustPaired);
        await phone.SendAsync(new Ping());
        Assert.Equal(new Ping(), MessageCodec.Decode(peer.Transport.Decrypt((await pcEnd.ReceiveAsync(default))!)));
    }

    [Fact]
    public async Task AnUnknownPhoneIsRejectedWithoutAReply()
    {
        var (pcEnd, phoneEnd) = MemoryChannel.Pair();
        await new TestPhone(phoneEnd).SendFirstMessageAsync(_pc.PublicKey);

        Assert.Null(await _auth.AuthenticateAsync(pcEnd, default));
        await phoneEnd.CloseAsync();
        Assert.Null(await phoneEnd.ReceiveAsync(default));
    }

    [Fact]
    public async Task AValidPairingCodeAddsTheDeviceAndTheSessionContinues()
    {
        var (pcEnd, phoneEnd) = MemoryChannel.Pair();
        var phone = new TestPhone(phoneEnd);
        var code = _pairing.Start().Code;

        var connect = phone.ConnectAsync(_pc.PublicKey, code);
        var peer = await _auth.AuthenticateAsync(pcEnd, default);
        await connect;

        Assert.NotNull(peer);
        Assert.True(peer.JustPaired);
        Assert.Equal(peer.Device, _devices.FindByKey(phone.Key.PublicKey));
    }

    [Fact]
    public async Task ARepairOfAKnownPhoneKeepsItsIdentity()
    {
        var (pcEnd, phoneEnd) = MemoryChannel.Pair();
        var phone = new TestPhone(phoneEnd);
        var device = _devices.Add(phone.Key.PublicKey);

        var connect = phone.ConnectAsync(_pc.PublicKey, _pairing.Start().Code);
        var peer = await _auth.AuthenticateAsync(pcEnd, default);
        await connect;

        Assert.Equal(device, peer!.Device);
        Assert.False(peer.JustPaired);
        Assert.Single(_devices.All);
    }

    [Fact]
    public async Task AnExpiredPairingCodeIsRejected()
    {
        var (pcEnd, phoneEnd) = MemoryChannel.Pair();
        var code = _pairing.Start().Code;
        _time.Advance(TimeSpan.FromMinutes(3));
        await new TestPhone(phoneEnd).SendFirstMessageAsync(_pc.PublicKey, code);

        Assert.Null(await _auth.AuthenticateAsync(pcEnd, default));
        Assert.Empty(_devices.All);
    }

    [Fact]
    public async Task AReusedPairingCodeIsRejected()
    {
        var code = _pairing.Start().Code;
        var (pcEnd1, phoneEnd1) = MemoryChannel.Pair();
        var first = new TestPhone(phoneEnd1).ConnectAsync(_pc.PublicKey, code);
        Assert.NotNull(await _auth.AuthenticateAsync(pcEnd1, default));
        await first;

        var (pcEnd2, phoneEnd2) = MemoryChannel.Pair();
        await new TestPhone(phoneEnd2).SendFirstMessageAsync(_pc.PublicKey, code);

        Assert.Null(await _auth.AuthenticateAsync(pcEnd2, default));
        Assert.Single(_devices.All);
    }

    [Fact]
    public async Task APhoneWithTheWrongPcKeyIsRejected()
    {
        var (pcEnd, phoneEnd) = MemoryChannel.Pair();
        var phone = new TestPhone(phoneEnd);
        _devices.Add(phone.Key.PublicKey);
        await phone.SendFirstMessageAsync(KeyPair.Generate().PublicKey);

        Assert.Null(await _auth.AuthenticateAsync(pcEnd, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(96)]
    [InlineData(500)]
    public async Task GarbageFirstMessagesAreRejectedWithoutThrowing(int length)
    {
        var (pcEnd, phoneEnd) = MemoryChannel.Pair();
        await phoneEnd.SendAsync(new byte[length], default);

        Assert.Null(await _auth.AuthenticateAsync(pcEnd, default));
    }

    [Fact]
    public async Task AFirstMessageWhosePayloadIsNotAConnectRequestIsRejected()
    {
        var (pcEnd, phoneEnd) = MemoryChannel.Pair();
        var phone = new TestPhone(phoneEnd);
        _devices.Add(phone.Key.PublicKey);
        var handshake = new IKHandshake(initiator: true, HandshakeAuthenticator.Prologue, phone.Key, rs: _pc.PublicKey);
        await phoneEnd.SendAsync(handshake.WriteMessage("[1,2]"u8.ToArray()), default);

        Assert.Null(await _auth.AuthenticateAsync(pcEnd, default));
    }

    [Fact]
    public async Task APeerThatClosesBeforeSendingAnythingIsRejected()
    {
        var (pcEnd, phoneEnd) = MemoryChannel.Pair();
        await phoneEnd.CloseAsync();

        Assert.Null(await _auth.AuthenticateAsync(pcEnd, default));
    }
}
