using Microsoft.Extensions.Logging;
using Reach.Agent.Net;
using Reach.Agent.Storage;
using Reach.Protocol;

namespace Reach.Agent.Tests;

public sealed class MessageRouterTests : IDisposable
{
    private readonly TestAgent _agent = new();
    private readonly Recorder _phone = new();
    private readonly Device _device;
    private readonly MessageRouter _router;

    public MessageRouterTests()
    {
        _device = _agent.Devices.Add(KeyPair.Generate().PublicKey);
        _router = new MessageRouter(_device, _agent.Context, _phone.Send, _agent.Context.Logs.CreateLogger<MessageRouter>());
    }

    public void Dispose() => _agent.Dispose();

    [Fact]
    public void HelloStoresTheDeviceNameRepliesWelcomeAndAnnouncesTheConnection()
    {
        string? announced = null;
        _agent.Context.Sessions.DeviceConnected += name => announced = name;

        _router.Handle(new Hello("Bedroom iPhone", "1.0.0"));

        Assert.Equal([new Welcome("TEST-PC", "1.0.0")], _phone.Messages);
        Assert.Equal("Bedroom iPhone", _agent.Devices.All.Single().Name);
        Assert.Equal("Bedroom iPhone", announced);
    }

    [Fact]
    public void AnswersPingWithPongAndIgnoresPong()
    {
        _router.Handle(new Ping());
        _router.Handle(new Pong());
        Assert.Equal([new Pong()], _phone.Messages);
    }

    [Fact]
    public void RoutesInputToTheInputService()
    {
        _router.Handle(new MouseMove(3, -4));
        _router.Handle(new MouseButton("left", true));
        _router.Handle(new MouseScroll(0, 120));
        _router.Handle(new KeyText("a"));
        _router.Handle(new KeyCombo(["alt", "tab"]));

        Assert.Equal(
            ["move 3 -4", "button Left down", "scroll 0 120", "unicode 0061", "key 12 down", "key 09 down", "key 09 up", "key 12 up"],
            _agent.Sink.Calls);
        Assert.Empty(_phone.Messages);
    }

    [Fact]
    public void RoutesTerminalMessages()
    {
        _router.Handle(new TermOpen(80, 24));
        _router.Handle(new TermInput("dir\r"));
        _router.Handle(new TermResize(100, 30));

        Assert.Equal(["open 80x24", "input dir\r", "resize 100x30"], _agent.Terminal.Calls);
        Assert.Equal([new TermOpened(false)], _phone.Messages);
    }

    [Fact]
    public async Task ListsAndRunsSavedCommands()
    {
        _router.Handle(new CmdList());
        _router.Handle(new CmdRun("sleep"));

        await _phone.WaitForAsync(p => p.Messages.Count == 2, "cmd.result");
        Assert.Equal([new CommandInfo("sleep", "Sleep", "moon", true)], Assert.IsType<CmdListed>(_phone.Messages[0]).Commands);
        Assert.Equal(new CmdResult("sleep", true, 0), _phone.Messages[1]);
    }

    [Theory]
    [MemberData(nameof(PcToPhoneMessages))]
    public void RejectsMessagesThatOnlyThePcSends(Message message)
    {
        _router.Handle(message);
        Assert.Equal("unknown_message", Assert.IsType<ErrorMessage>(Assert.Single(_phone.Messages)).Code);
    }

    public static TheoryData<Message> PcToPhoneMessages() =>
    [
        new Welcome("x", "1"), new TermOpened(true), new TermOutput("x"), new TermExited(0),
        new CmdListed([]), new CmdResult("x", true, 0), new ErrorMessage("x", "y"),
    ];

    [Fact]
    public void BadArgumentsGetAnErrorAndTheSessionCarriesOn()
    {
        _router.Handle(new KeyCombo(["hyper"]));
        _router.Handle(new MouseButton("thumb", true));
        _router.Handle(new Ping());

        Assert.Equal(["bad_args", "bad_args"], _phone.Messages.OfType<ErrorMessage>().Select(e => e.Code));
        Assert.Equal(new Pong(), _phone.Messages[^1]);
    }

    [Fact]
    public void SessionEndReleasesHeldInputAndDetachesTheTerminal()
    {
        _router.Handle(new TermOpen(80, 24));
        _router.Handle(new MouseButton("left", true));
        _agent.Sink.Calls.Clear();

        _router.SessionEnded();

        Assert.Equal(["button Left up"], _agent.Sink.Calls);
        Assert.Contains("detach", _agent.Terminal.Calls);
    }

    [Fact]
    public void NeverLogsWhatWasTyped()
    {
        _router.Handle(new KeyText("secret-password"));
        _router.Handle(new TermInput("secret-command"));
        _router.Handle(new KeyCombo(["secret-key"]));

        Assert.DoesNotContain(_agent.Logs.Lines, line => line.Contains("secret"));
    }
}
