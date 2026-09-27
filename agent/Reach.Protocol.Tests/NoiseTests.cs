using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reach.Protocol;

namespace Reach.Protocol.Tests;

public class NoiseVectorTests
{
    public static TheoryData<string> VectorFiles => new() { "noise-ik.json", "reach-ik.json" };

    [Theory]
    [MemberData(nameof(VectorFiles))]
    public void ProducesTheExpectedBytes(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", file)));
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            byte[] Hex(string name) => Convert.FromHexString(v.GetProperty(name).GetString()!);
            var initiator = new IKHandshake(true, Hex("init_prologue"), KeyPair.FromSecret(Hex("init_static")),
                rs: Hex("init_remote_static"), e: KeyPair.FromSecret(Hex("init_ephemeral")));
            var responder = new IKHandshake(false, Hex("resp_prologue"), KeyPair.FromSecret(Hex("resp_static")),
                e: KeyPair.FromSecret(Hex("resp_ephemeral")));
            var messages = v.GetProperty("messages").EnumerateArray()
                .Select(m => (Payload: Convert.FromHexString(m.GetProperty("payload").GetString()!),
                              Ciphertext: Convert.FromHexString(m.GetProperty("ciphertext").GetString()!)))
                .ToList();

            var c0 = initiator.WriteMessage(messages[0].Payload);
            Assert.Equal(messages[0].Ciphertext, c0);
            Assert.Equal(messages[0].Payload, responder.ReadMessage(c0));
            Assert.Equal(KeyPair.FromSecret(Hex("init_static")).PublicKey, responder.RemoteStaticKey);

            var c1 = responder.WriteMessage(messages[1].Payload);
            Assert.Equal(messages[1].Ciphertext, c1);
            Assert.Equal(messages[1].Payload, initiator.ReadMessage(c1));

            Assert.Equal(Hex("handshake_hash"), initiator.HandshakeHash);
            Assert.Equal(Hex("handshake_hash"), responder.HandshakeHash);

            var it = initiator.Split();
            var rt = responder.Split();
            for (var i = 2; i < messages.Count; i++)
            {
                var (sender, receiver) = i % 2 == 0 ? (it, rt) : (rt, it);
                var c = sender.Encrypt(messages[i].Payload);
                Assert.Equal(messages[i].Ciphertext, c);
                Assert.Equal(messages[i].Payload, receiver.Decrypt(c));
            }
        }
    }
}

public class NoiseFailureTests
{
    private static readonly byte[] Prologue = Encoding.ASCII.GetBytes("reach/1");

    private static (Transport Initiator, Transport Responder) Pair()
    {
        var pc = KeyPair.Generate();
        var initiator = new IKHandshake(true, Prologue, KeyPair.Generate(), rs: pc.PublicKey);
        var responder = new IKHandshake(false, Prologue, pc);
        responder.ReadMessage(initiator.WriteMessage([]));
        initiator.ReadMessage(responder.WriteMessage([]));
        return (initiator.Split(), responder.Split());
    }

    [Fact]
    public void RejectsATamperedTransportMessage()
    {
        var (initiator, responder) = Pair();
        var c = initiator.Encrypt("hi"u8.ToArray());
        c[0] ^= 1;
        Assert.Throws<NoiseException>(() => responder.Decrypt(c));
    }

    [Fact]
    public void RejectsAReplayedTransportMessage()
    {
        var (initiator, responder) = Pair();
        var c = initiator.Encrypt("hi"u8.ToArray());
        responder.Decrypt(c);
        Assert.Throws<NoiseException>(() => responder.Decrypt(c));
    }

    [Fact]
    public void FailsWhenTheInitiatorHasTheWrongResponderKey()
    {
        var initiator = new IKHandshake(true, Prologue, KeyPair.Generate(), rs: KeyPair.Generate().PublicKey);
        var responder = new IKHandshake(false, Prologue, KeyPair.Generate());
        Assert.Throws<NoiseException>(() => responder.ReadMessage(initiator.WriteMessage([])));
    }

    [Fact]
    public void FailsWhenTheProloguesDiffer()
    {
        var pc = KeyPair.Generate();
        var initiator = new IKHandshake(true, Prologue, KeyPair.Generate(), rs: pc.PublicKey);
        var responder = new IKHandshake(false, Encoding.ASCII.GetBytes("reach/2"), pc);
        Assert.Throws<NoiseException>(() => responder.ReadMessage(initiator.WriteMessage([])));
    }

    [Fact]
    public void RejectsAFirstMessageWithAnAllZeroEphemeralKey()
    {
        var pc = KeyPair.Generate();
        var responder = new IKHandshake(false, Prologue, pc);
        var ex = Assert.Throws<NoiseException>(() => responder.ReadMessage(ZeroEphemeralFirstMessage(pc.PublicKey)));
        Assert.Equal("invalid public key", ex.Message);
    }

    /// <summary>
    /// A correctly encrypted message 1 whose ephemeral key is all zeros: es is then zero on the
    /// responder's side, so the sender knows it without any secret and can build a valid tag.
    /// </summary>
    private static byte[] ZeroEphemeralFirstMessage(byte[] responderStatic)
    {
        var sender = KeyPair.Generate();
        var h = new byte[Noise.HashLength];
        Encoding.ASCII.GetBytes(Noise.ProtocolName).CopyTo(h, 0);
        var ck = (byte[])h.Clone();
        var cipher = new CipherState();
        void MixHash(byte[] data) => h = SHA256.HashData(h.Concat(data).ToArray());
        void MixKey(byte[] ikm)
        {
            (ck, var k) = HkdfForTest(ck, ikm);
            cipher.InitializeKey(k);
        }
        byte[] EncryptAndHash(byte[] plaintext)
        {
            var c = cipher.EncryptWithAd(h, plaintext);
            MixHash(c);
            return c;
        }
        MixHash(Prologue);
        MixHash(responderStatic);
        var re = new byte[Noise.DhLength];
        MixHash(re);
        MixKey(new byte[Noise.DhLength]);
        var encryptedStatic = EncryptAndHash(sender.PublicKey);
        MixKey(Sodium.ScalarMult.Mult(sender.SecretKey, responderStatic));
        var body = EncryptAndHash("{}"u8.ToArray());
        return [.. re, .. encryptedStatic, .. body];
    }

    private static (byte[], byte[]) HkdfForTest(byte[] ck, byte[] ikm)
    {
        var temp = HMACSHA256.HashData(ck, ikm);
        var out1 = HMACSHA256.HashData(temp, new byte[] { 1 });
        return (out1, HMACSHA256.HashData(temp, out1.Append((byte)2).ToArray()));
    }

    [Fact]
    public void RejectsAnEmptyTransportMessage()
    {
        var (_, responder) = Pair();
        var ex = Assert.Throws<NoiseException>(() => responder.Decrypt([]));
        Assert.Equal("message too short", ex.Message);
    }

    [Fact]
    public void RejectsAnOversizedIncomingTransportMessageBeforeDecrypting()
    {
        var (_, responder) = Pair();
        var ex = Assert.Throws<NoiseException>(() => responder.Decrypt(new byte[Noise.MaxMessageLength + 1]));
        Assert.Equal("message too long", ex.Message);
    }

    [Fact]
    public void RejectsATruncatedFirstMessage()
    {
        var responder = new IKHandshake(false, [], KeyPair.Generate());
        var ex = Assert.Throws<NoiseException>(() => responder.ReadMessage(new byte[40]));
        Assert.Equal("message too short", ex.Message);
    }

    [Fact]
    public void RefusesToEncryptPastTheNoiseLimit()
    {
        var (initiator, _) = Pair();
        var ex = Assert.Throws<NoiseException>(() => initiator.Encrypt(new byte[Noise.MaxMessageLength]));
        Assert.Equal("message too long", ex.Message);
    }

    [Fact]
    public void RefusesToWriteOutOfTurn()
    {
        var responder = new IKHandshake(false, [], KeyPair.Generate());
        Assert.Throws<NoiseException>(() => responder.WriteMessage([]));
    }
}
