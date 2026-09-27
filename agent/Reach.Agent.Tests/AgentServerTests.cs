using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Reach.Agent.Net;
using Reach.Protocol;

namespace Reach.Agent.Tests;

/// <summary>Real Kestrel on a random localhost port, real ClientWebSocket.</summary>
public sealed class AgentServerTests : IAsyncLifetime
{
    private readonly TestAgent _agent = new();
    private AgentServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = new AgentServer(_agent.Context);
        await _server.StartAsync(IPAddress.Loopback, 0);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _agent.Dispose();
    }

    private Uri Url => new($"ws://127.0.0.1:{_server.Port}/");

    private async Task<(ClientWebSocket Socket, TestPhone Phone)> OpenAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(Url, default);
        return (socket, new TestPhone(new WebSocketFrameChannel(socket)));
    }

    [Fact]
    public async Task PairsAndTalksOverARealWebSocket()
    {
        var (socket, phone) = await OpenAsync();
        using (socket)
        {
            await phone.ConnectAsync(_agent.Context.Identity.PublicKey, _agent.Context.Pairing.Start().Code);
            await phone.SendAsync(new Hello("Bedroom iPhone", "1.0.0"));

            Assert.Equal(new Welcome("TEST-PC", "1.0.0"), await phone.ReceiveAsync<Welcome>());
            await phone.SendAsync(new KeyText("مرحبا 👋"));
            await phone.SendAsync(new Ping());
            await phone.ReceiveAsync<Pong>();
            Assert.Equal(8, _agent.Sink.Calls.Count(c => c.StartsWith("unicode")));
        }
    }

    [Fact]
    public async Task DisconnectReachesThePhoneOverARealWebSocketBeforeItCloses()
    {
        for (var round = 0; round < 20; round++) // the loss was a race: try it many times
        {
            var (socket, phone) = await OpenAsync();
            using (socket)
            {
                await phone.ConnectAsync(_agent.Context.Identity.PublicKey, _agent.Context.Pairing.Start().Code);
                await phone.SendAsync(new Hello("Bedroom iPhone", "1.0.0"));
                await phone.ReceiveAsync<Welcome>();

                _agent.Context.Sessions.DisconnectActive();

                Assert.Equal(new Bye(), await phone.ReceiveAsync<Bye>());
                Assert.Null(await phone.ReceiveAsync()); // then a clean WebSocket close, not a reset
            }
            await WaitUntilAsync(() => _agent.Context.Sessions.Active is null);
        }
    }

    [Fact]
    public async Task ASecondPhoneReceivesBusyOverARealWebSocketBeforeItCloses()
    {
        var (firstSocket, first) = await OpenAsync();
        using (firstSocket)
        {
            await first.ConnectAsync(_agent.Context.Identity.PublicKey, _agent.Context.Pairing.Start().Code);
            await first.SendAsync(new Hello("Bedroom iPhone", "1.0.0"));
            await first.ReceiveAsync<Welcome>();

            for (var round = 0; round < 10; round++)
            {
                var (socket, second) = await OpenAsync();
                using (socket)
                {
                    await second.ConnectAsync(_agent.Context.Identity.PublicKey, _agent.Context.Pairing.Start().Code);
                    await second.SendAsync(new Hello("Kitchen Pixel", "1.0.0"));

                    Assert.Equal(new Busy("Bedroom iPhone"), await second.ReceiveAsync<Busy>());
                    Assert.Null(await second.ReceiveAsync());
                }
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task PlainHttpGetsA404()
    {
        using var http = new HttpClient();
        var response = await http.GetAsync($"http://127.0.0.1:{_server.Port}/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnOversizedFrameClosesTheConnection()
    {
        var (socket, phone) = await OpenAsync();
        using (socket)
        {
            _agent.Devices.Add(phone.Key.PublicKey);
            await phone.ConnectAsync(_agent.Context.Identity.PublicKey);

            await socket.SendAsync(new byte[Noise.MaxMessageLength + 1], WebSocketMessageType.Binary, true, default);

            Assert.Null(await ReceiveUntilClosedAsync(phone));
        }
    }

    [Fact]
    public async Task ATextFrameClosesTheConnection()
    {
        var (socket, phone) = await OpenAsync();
        using (socket)
        {
            _agent.Devices.Add(phone.Key.PublicKey);
            await phone.ConnectAsync(_agent.Context.Identity.PublicKey);

            await socket.SendAsync("{\"type\":\"ping\"}"u8.ToArray(), WebSocketMessageType.Text, true, default);

            Assert.Null(await ReceiveUntilClosedAsync(phone));
        }
    }

    [Fact]
    public async Task ABlockedIpIsDroppedBeforeTheWebSocketUpgrade()
    {
        for (var i = 0; i < 5; i++) _agent.Context.Limiter.RecordFailure(IPAddress.Loopback);

        using var socket = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => socket.ConnectAsync(Url, default));
    }

    [Fact]
    public async Task SilentConnectionsAreClosedAndCountAgainstTheIp()
    {
        for (var i = 0; i < RateLimiter.MaxFailures; i++)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
            Assert.True(await ClosedByServerAsync(tcp, TimeSpan.FromSeconds(5)), $"silent connection {i + 1} was left open");
        }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!_agent.Handler.IsBlocked(IPAddress.Loopback) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(_agent.Handler.IsBlocked(IPAddress.Loopback));
    }

    [Fact]
    public async Task ClosedSessionsDoNotCountAgainstTheIp()
    {
        for (var i = 0; i < RateLimiter.MaxFailures; i++)
        {
            var (socket, phone) = await OpenAsync();
            using (socket)
            {
                _agent.Devices.Add(phone.Key.PublicKey);
                await phone.ConnectAsync(_agent.Context.Identity.PublicKey);
                await phone.SendAsync(new Hello("Bedroom iPhone", "1.0.0"));
                await phone.ReceiveAsync<Welcome>();
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default);
            }
        }
        await Task.Delay(200); // let the server finish tearing the last connection down

        Assert.False(_agent.Handler.IsBlocked(IPAddress.Loopback));
    }

    [Fact]
    public async Task OneIpCannotHoldMoreThanAFewConnectionsOpen()
    {
        using var agent = new TestAgent(new SessionOptions()); // the real 10 s handshake timeout
        await using var server = new AgentServer(agent.Context);
        await server.StartAsync(IPAddress.Loopback, 0);
        var held = new List<TcpClient>();
        try
        {
            for (var i = 0; i < AgentServer.MaxConnectionsPerIp; i++)
            {
                var tcp = new TcpClient();
                held.Add(tcp);
                await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            }
            using var extra = new TcpClient();
            await extra.ConnectAsync(IPAddress.Loopback, server.Port);

            Assert.True(await ClosedByServerAsync(extra, TimeSpan.FromSeconds(2)));
            Assert.False(await ClosedByServerAsync(held[0], TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            foreach (var tcp in held) tcp.Dispose();
        }
    }

    [Fact]
    public async Task StartingOnABusyPortThrows()
    {
        await using var second = new AgentServer(_agent.Context);
        await Assert.ThrowsAnyAsync<IOException>(() => second.StartAsync(IPAddress.Loopback, _server.Port));
    }

    /// <summary>True if the server closed or reset the connection within <paramref name="wait"/>.</summary>
    private static async Task<bool> ClosedByServerAsync(TcpClient tcp, TimeSpan wait)
    {
        using var timeout = new CancellationTokenSource(wait);
        try
        {
            return await tcp.GetStream().ReadAsync(new byte[1], timeout.Token) == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Skips pings; returns null once the PC has closed the connection.</summary>
    private static async Task<Message?> ReceiveUntilClosedAsync(TestPhone phone)
    {
        while (true)
        {
            Message? message;
            try
            {
                message = await phone.ReceiveAsync();
            }
            catch (WebSocketException)
            {
                return null;
            }
            if (message is not Ping) return message;
        }
    }
}
