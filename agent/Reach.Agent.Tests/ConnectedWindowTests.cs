using Reach.Agent.Tray;

namespace Reach.Agent.Tests;

public sealed class ConnectedWindowTests
{
    private static readonly DateTimeOffset Since = new DateTimeOffset(2026, 9, 27, 17, 42, 0, TimeSpan.Zero).ToLocalTime();

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(42, "0:42")]
    [InlineData(12 * 60 + 4, "12:04")]
    [InlineData(3600 + 2 * 60 + 45, "1:02:45")]
    [InlineData(26 * 3600, "26:00:00")]
    public void CountsTheConnectionTimeUpToTheSecond(int seconds, string expected)
    {
        Assert.Equal(expected, ConnectedWindow.Elapsed(Since, Since.AddSeconds(seconds)));
    }

    [Fact]
    public void NeverShowsANegativeTimeWhenTheClockIsBehind()
    {
        Assert.Equal("0:00", ConnectedWindow.Elapsed(Since, Since.AddSeconds(-3)));
    }

    [Fact]
    public void SaysSinceWhenInLocalTime()
    {
        Assert.Equal($"since {Since:HH:mm}", ConnectedWindow.SinceText(Since));
    }
}
