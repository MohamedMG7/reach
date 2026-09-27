using System.Buffers.Text;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Reach.Agent.Net;
using Reach.Agent.Pairing;

namespace Reach.Agent.Tests;

public class PairingServiceTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void ACodeIsSixteenRandomBytesValidForTwoMinutes()
    {
        var pairing = new PairingService(_time);
        var code = pairing.Start();
        Assert.Equal(16, Base64Url.DecodeFromChars(code.Code).Length);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(2), code.ExpiresAt);
        Assert.NotEqual(code.Code, pairing.Start().Code);
    }

    [Fact]
    public void AValidCodeWorksExactlyOnce()
    {
        var pairing = new PairingService(_time);
        var paired = 0;
        pairing.Paired += () => paired++;
        var code = pairing.Start().Code;

        Assert.True(pairing.TryConsume(code));
        Assert.False(pairing.TryConsume(code));
        Assert.Equal(1, paired);
    }

    [Fact]
    public void AnExpiredCodeIsRejected()
    {
        var pairing = new PairingService(_time);
        var code = pairing.Start().Code;
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(pairing.TryConsume(code));
    }

    [Fact]
    public void AWrongCodeIsRejectedAndTheRightOneStillWorks()
    {
        var pairing = new PairingService(_time);
        var code = pairing.Start().Code;
        Assert.False(pairing.TryConsume(Base64Url.EncodeToString(new byte[16])));
        Assert.True(pairing.TryConsume(code));
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!!not base64!!!")]
    [InlineData("AAAA")]
    public void MalformedCodesAreRejectedWithoutThrowing(string code)
    {
        var pairing = new PairingService(_time);
        pairing.Start();
        Assert.False(pairing.TryConsume(code));
    }

    [Fact]
    public void NoCodeIsValidBeforeStartOrAfterCancel()
    {
        var pairing = new PairingService(_time);
        Assert.False(pairing.TryConsume(Base64Url.EncodeToString(new byte[16])));
        var code = pairing.Start().Code;
        pairing.Cancel();
        Assert.False(pairing.TryConsume(code));
    }

    [Fact]
    public void StartingAgainInvalidatesTheOldCode()
    {
        var pairing = new PairingService(_time);
        var old = pairing.Start().Code;
        pairing.Start();
        Assert.False(pairing.TryConsume(old));
    }
}

public class PairingUriTests
{
    [Fact]
    public void BuildsTheSpecFormat()
    {
        var uri = PairingUri.Build(
            [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("10.0.0.5")], 47800, new byte[32], "AAECAwQFBgcICQoLDA0ODw", "DESKTOP PC&1");

        Assert.Equal(
            "reach://pair?v=1&h=192.168.1.20,10.0.0.5&p=47800&k=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&c=AAECAwQFBgcICQoLDA0ODw&n=DESKTOP%20PC%261",
            uri);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.10", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("fe80::1", false)]
    public void RecognizesPrivateIPv4Addresses(string address, bool expected)
    {
        Assert.Equal(expected, PairingUri.IsPrivate(IPAddress.Parse(address)));
    }

    [Fact]
    public void LocalAddressesAreAllPrivate()
    {
        Assert.All(PairingUri.LocalPrivateAddresses(), a => Assert.True(PairingUri.IsPrivate(a)));
    }
}

public class RateLimiterTests
{
    private static readonly IPAddress Phone = IPAddress.Parse("192.168.1.30");
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void BlocksAnIpAfterFiveFailuresWithinAMinute()
    {
        var limiter = new RateLimiter(_time);
        for (var i = 0; i < 4; i++) limiter.RecordFailure(Phone);
        Assert.False(limiter.IsBlocked(Phone));

        limiter.RecordFailure(Phone);

        Assert.True(limiter.IsBlocked(Phone));
        Assert.False(limiter.IsBlocked(IPAddress.Parse("192.168.1.31")));
    }

    [Fact]
    public void UnblocksAfterOneMinute()
    {
        var limiter = new RateLimiter(_time);
        for (var i = 0; i < 5; i++) limiter.RecordFailure(Phone);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(limiter.IsBlocked(Phone));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(limiter.IsBlocked(Phone));
    }

    [Fact]
    public void FailuresSpreadOverMoreThanAMinuteDoNotBlock()
    {
        var limiter = new RateLimiter(_time);
        for (var i = 0; i < 10; i++)
        {
            limiter.RecordFailure(Phone);
            _time.Advance(TimeSpan.FromSeconds(15));
        }
        Assert.False(limiter.IsBlocked(Phone));
    }

    [Fact]
    public void TreatsIPv4MappedAddressesAsTheSameClient()
    {
        var limiter = new RateLimiter(_time);
        for (var i = 0; i < 5; i++) limiter.RecordFailure(Phone.MapToIPv6());
        Assert.True(limiter.IsBlocked(Phone));
    }
}
