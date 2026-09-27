using System.Drawing;
using Reach.Agent.Input;

namespace Reach.Agent.Tests;

/// <summary>Records every sink call as a short string, e.g. "key 11 down", "unicode 0645".</summary>
public sealed class FakeInputSink : IInputSink
{
    public List<string> Calls { get; } = [];

    public void MoveBy(int dx, int dy) => Calls.Add($"move {dx} {dy}");
    public void Button(MouseButtonKind button, bool down) => Calls.Add($"button {button} {(down ? "down" : "up")}");
    public void Scroll(int dx, int dy) => Calls.Add($"scroll {dx} {dy}");
    public void Key(ushort vk, bool down) => Calls.Add($"key {vk:X2} {(down ? "down" : "up")}");
    public void Unicode(char c) => Calls.Add($"unicode {(int)c:X4}");
}

public class InputServiceTests
{
    private readonly FakeInputSink _sink = new();
    private readonly InputService _input;

    public InputServiceTests() => _input = new InputService(_sink);

    [Fact]
    public void PassesMovesAndScrollsThrough()
    {
        _input.Move(-12, 7);
        _input.Scroll(0, -240);
        Assert.Equal(["move -12 7", "scroll 0 -240"], _sink.Calls);
    }

    [Fact]
    public void MapsButtonNames()
    {
        _input.Button("left", true);
        _input.Button("left", false);
        _input.Button("right", true);
        _input.Button("middle", true);
        Assert.Equal(["button Left down", "button Left up", "button Right down", "button Middle down"], _sink.Calls);
    }

    [Fact]
    public void RejectsUnknownButtons()
    {
        Assert.Throws<BadArgsException>(() => _input.Button("fourth", true));
        Assert.Empty(_sink.Calls);
    }

    [Fact]
    public void TypesArabicAndEmojiAsUtf16CodeUnits()
    {
        _input.Text("م👋");
        Assert.Equal(["unicode 0645", "unicode D83D", "unicode DC4B"], _sink.Calls);
    }

    [Fact]
    public void TypesNewlineAsEnterAndBackspaceAsTheBackspaceKey()
    {
        _input.Text("a\n\b");
        Assert.Equal(["unicode 0061", "key 0D down", "key 0D up", "key 08 down", "key 08 up"], _sink.Calls);
    }

    [Fact]
    public void PressesComboKeysInOrderAndReleasesInReverse()
    {
        _input.Combo(["ctrl", "shift", "esc"]);
        Assert.Equal(["key 11 down", "key 10 down", "key 1B down", "key 1B up", "key 10 up", "key 11 up"], _sink.Calls);
    }

    [Theory]
    [InlineData("win", 0x5B)]
    [InlineData("f12", 0x7B)]
    [InlineData("a", 0x41)]
    [InlineData("z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("pagedown", 0x22)]
    [InlineData("space", 0x20)]
    public void MapsEverySpecKeyName(string name, int vk)
    {
        Assert.True(KeyMap.TryGet(name, out var actual));
        Assert.Equal(vk, actual);
    }

    [Theory]
    [InlineData("hyper")]
    [InlineData("CTRL")]
    [InlineData("")]
    public void RejectsAComboWithAnUnknownKeyBeforePressingAnything(string bad)
    {
        Assert.Throws<BadArgsException>(() => _input.Combo(["ctrl", bad]));
        Assert.Empty(_sink.Calls);
    }

    [Fact]
    public void RejectsEmptyAndOverlongCombos()
    {
        Assert.Throws<BadArgsException>(() => _input.Combo([]));
        Assert.Throws<BadArgsException>(() => _input.Combo(["ctrl", "alt", "shift", "win", "a", "b", "c"]));
        Assert.Empty(_sink.Calls);
    }

    [Fact]
    public void ReleaseAllLetsGoOfHeldButtons()
    {
        _input.Button("left", true);
        _input.Button("right", true);
        _input.Button("right", false);
        _sink.Calls.Clear();

        _input.ReleaseAll();
        _input.ReleaseAll();

        Assert.Equal(["button Left up"], _sink.Calls);
    }

    [Fact]
    public void ReleaseAllAfterCompletedCombosReleasesNothing()
    {
        _input.Combo(["alt", "tab"]);
        _sink.Calls.Clear();
        _input.ReleaseAll();
        Assert.Empty(_sink.Calls);
    }
}

public class AbsoluteMouseTests
{
    // Two 1920x1080 monitors, the second to the left of the primary.
    private static readonly Rectangle Desktop = new(-1920, 0, 3840, 1080);

    [Fact]
    public void MapsTheCornersToTheEndsOfTheRange()
    {
        Assert.Equal((0, 0), AbsoluteMouse.Normalize(new Point(-1920, 0), Desktop));
        Assert.Equal((65535, 65535), AbsoluteMouse.Normalize(new Point(1919, 1079), Desktop));
    }

    [Fact]
    public void MapsThePrimaryMonitorOriginToTheMiddle()
    {
        var (x, _) = AbsoluteMouse.Normalize(new Point(0, 0), Desktop);
        Assert.Equal(32776, x);
    }

    [Fact]
    public void ClampsMovesThatLeaveTheDesktop()
    {
        Assert.Equal(new Point(-1920, 0), AbsoluteMouse.Clamp(new Point(-5000, -3), Desktop));
        Assert.Equal(new Point(1919, 1079), AbsoluteMouse.Clamp(new Point(4000, 2000), Desktop));
    }
}
