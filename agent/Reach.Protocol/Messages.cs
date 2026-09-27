using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reach.Protocol;

/// <summary>Application messages carried inside the Noise transport (spec §4).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(Busy), "busy")]
[JsonDerivedType(typeof(Bye), "bye")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(Pong), "pong")]
[JsonDerivedType(typeof(MouseMove), "mouse.move")]
[JsonDerivedType(typeof(MouseButton), "mouse.button")]
[JsonDerivedType(typeof(MouseScroll), "mouse.scroll")]
[JsonDerivedType(typeof(KeyText), "key.text")]
[JsonDerivedType(typeof(KeyCombo), "key.combo")]
[JsonDerivedType(typeof(TermOpen), "term.open")]
[JsonDerivedType(typeof(TermOpened), "term.opened")]
[JsonDerivedType(typeof(TermInput), "term.input")]
[JsonDerivedType(typeof(TermOutput), "term.output")]
[JsonDerivedType(typeof(TermResize), "term.resize")]
[JsonDerivedType(typeof(TermExited), "term.exited")]
[JsonDerivedType(typeof(CmdList), "cmd.list")]
[JsonDerivedType(typeof(CmdListed), "cmd.listed")]
[JsonDerivedType(typeof(CmdRun), "cmd.run")]
[JsonDerivedType(typeof(CmdResult), "cmd.result")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record Message;

public sealed record Hello(string DeviceName, string AppVersion) : Message;
public sealed record Welcome(string PcName, string AgentVersion) : Message;
/// <summary>Instead of <see cref="Welcome"/>: another phone is connected; the PC then closes the connection.</summary>
public sealed record Busy(string DeviceName) : Message;
/// <summary>The PC disconnected this phone on purpose; the phone should not reconnect by itself.</summary>
public sealed record Bye : Message;
public sealed record Ping : Message;
public sealed record Pong : Message;
public sealed record MouseMove(int Dx, int Dy) : Message;
public sealed record MouseButton(string Button, bool Down) : Message;
public sealed record MouseScroll(int Dx, int Dy) : Message;
public sealed record KeyText(string Text) : Message;
// RespectNullableAnnotations does not reach array elements, so null elements are rejected here.
public sealed record KeyCombo(string[] Keys) : Message, IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => MessageCodec.RejectNullElements(Keys, "keys");
}
public sealed record TermOpen(int Cols, int Rows) : Message;
public sealed record TermOpened(bool Resumed) : Message;
public sealed record TermInput(string Data) : Message;
public sealed record TermOutput(string Data) : Message;
public sealed record TermResize(int Cols, int Rows) : Message;
public sealed record TermExited(int Code) : Message;
public sealed record CmdList : Message;
public sealed record CommandInfo(string Id, string Label, string Icon, bool Confirm);
public sealed record CmdListed(CommandInfo[] Commands) : Message, IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => MessageCodec.RejectNullElements(Commands, "commands");
}
public sealed record CmdRun(string Id) : Message;
/// <param name="Error">Optional: may be absent, but an explicit JSON null is rejected (as in TS).</param>
public sealed record CmdResult(string Id, bool Ok, int ExitCode, [property: JsonConverter(typeof(AbsentOrStringConverter))] string? Error = null) : Message;
public sealed record ErrorMessage(string Code, string Message) : Message;

/// <summary>Payload of the first handshake message: <see cref="PairCode"/> is present only when pairing.</summary>
public sealed record ConnectRequest(string? PairCode = null);

public sealed class ProtocolException(string message) : Exception(message);

/// <summary>For optional string fields: the field may be omitted, but a present value must be a string, not null.</summary>
internal sealed class AbsentOrStringConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new JsonException("expected a string");

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

public static class MessageCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static byte[] Encode(Message message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    /// <summary>Parses and validates a message. Throws <see cref="ProtocolException"/> for malformed JSON, unknown types, or wrong field types.</summary>
    public static Message Decode(byte[] bytes) => Parse<Message>(bytes);

    public static byte[] EncodeConnectRequest(ConnectRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, Options);

    public static ConnectRequest DecodeConnectRequest(byte[] bytes) => Parse<ConnectRequest>(bytes);

    internal static void RejectNullElements<T>(T[] items, string field) where T : class
    {
        if (Array.IndexOf(items, null) >= 0) throw new JsonException($"{field} must not contain null");
    }

    private static T Parse<T>(byte[] bytes) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new ProtocolException("message must be an object");
        }
        catch (JsonException e)
        {
            throw new ProtocolException(e.Message);
        }
        catch (NotSupportedException e)
        {
            throw new ProtocolException(e.Message);
        }
    }
}
