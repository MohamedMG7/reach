using System.Text;
using System.Text.Json.Nodes;
using Reach.Protocol;

namespace Reach.Protocol.Tests;

public class MessageCodecTests
{
    private static readonly JsonObject Samples =
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "messages.json")))!.AsObject();

    public static TheoryData<string> Valid() => [.. Samples["valid"]!.AsArray().Select(n => n!.ToJsonString())];

    public static TheoryData<string> Invalid() => [.. Samples["invalid"]!.AsArray().Select(n => n!["json"]!.GetValue<string>())];

    [Theory]
    [MemberData(nameof(Valid))]
    public void RoundTripsEverySample(string json)
    {
        var message = MessageCodec.Decode(Encoding.UTF8.GetBytes(json));
        var reencoded = JsonNode.Parse(MessageCodec.Encode(message));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), reencoded), $"{json} became {reencoded!.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void RejectsEveryInvalidSample(string json)
    {
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void DecodesToTheRightRecordType()
    {
        var message = MessageCodec.Decode("""{"type":"mouse.move","dx":-12,"dy":7}"""u8.ToArray());
        Assert.Equal(new MouseMove(-12, 7), message);
    }

    [Fact]
    public void AcceptsTheTypeFieldInAnyPosition()
    {
        var message = MessageCodec.Decode("""{"dx":1,"dy":2,"type":"mouse.scroll"}"""u8.ToArray());
        Assert.Equal(new MouseScroll(1, 2), message);
    }

    [Fact]
    public void IgnoresUnknownExtraFieldsSoNewerPeersStayCompatible()
    {
        Assert.Equal(new Ping(), MessageCodec.Decode("""{"type":"ping","future":1}"""u8.ToArray()));
    }

    [Fact]
    public void RoundTripsConnectRequests()
    {
        Assert.Equal("{}", Encoding.UTF8.GetString(MessageCodec.EncodeConnectRequest(new ConnectRequest())));
        Assert.Equal(new ConnectRequest("abc"), MessageCodec.DecodeConnectRequest("""{"pairCode":"abc"}"""u8.ToArray()));
        Assert.Equal(new ConnectRequest(), MessageCodec.DecodeConnectRequest("{}"u8.ToArray()));
    }
}
