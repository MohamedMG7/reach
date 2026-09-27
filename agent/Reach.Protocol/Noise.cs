using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Sodium;

namespace Reach.Protocol;

public sealed record KeyPair(byte[] PublicKey, byte[] SecretKey)
{
    public static KeyPair Generate() => FromSecret(RandomNumberGenerator.GetBytes(32));

    public static KeyPair FromSecret(byte[] secretKey) => new(ScalarMult.Base(secretKey), secretKey);
}

public static class Noise
{
    public const string ProtocolName = "Noise_IK_25519_ChaChaPoly_SHA256";
    /// <summary>Largest Noise message (handshake or transport), including the 16-byte tag.</summary>
    public const int MaxMessageLength = 65535;
    public const int TagLength = 16;
    public const int DhLength = 32;
    public const int HashLength = 32;

    /// <summary>X25519. Rejects low-order public keys (all-zero shared secret) so both implementations agree.</summary>
    internal static byte[] Dh(KeyPair own, byte[] remotePublic)
    {
        var shared = ScalarMult.Mult(own.SecretKey, remotePublic);
        var bits = 0;
        foreach (var b in shared) bits |= b;
        return bits == 0 ? throw new NoiseException("invalid public key") : shared;
    }

    /// <summary>Noise HKDF with two outputs: identical to RFC 5869 with salt = ck and empty info.</summary>
    internal static (byte[], byte[]) Hkdf2(byte[] ck, byte[] ikm)
    {
        var temp = HMACSHA256.HashData(ck, ikm);
        var out1 = HMACSHA256.HashData(temp, new byte[] { 1 });
        var out2 = HMACSHA256.HashData(temp, out1.Append((byte)2).ToArray());
        return (out1, out2);
    }
}

public sealed class NoiseException(string message) : Exception(message);

public sealed class CipherState
{
    private byte[]? _k;
    private ulong _n;

    public CipherState(byte[]? k = null) => _k = k;

    public void InitializeKey(byte[] k)
    {
        _k = k;
        _n = 0;
    }

    public byte[] EncryptWithAd(byte[] ad, byte[] plaintext)
    {
        if (_k is null) return plaintext;
        if (_n == ulong.MaxValue) throw new NoiseException("nonce exhausted");
        var output = new byte[plaintext.Length + Noise.TagLength];
        using var aead = new ChaCha20Poly1305(_k);
        aead.Encrypt(Nonce(_n), plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), ad);
        _n++;
        return output;
    }

    /// <summary>Throws <see cref="NoiseException"/> if authentication fails; the nonce only advances on success.</summary>
    public byte[] DecryptWithAd(byte[] ad, byte[] ciphertext)
    {
        if (_k is null) return ciphertext;
        if (_n == ulong.MaxValue) throw new NoiseException("nonce exhausted");
        if (ciphertext.Length < Noise.TagLength) throw new NoiseException("message too short");
        var plaintextLength = ciphertext.Length - Noise.TagLength;
        var plaintext = new byte[plaintextLength];
        using var aead = new ChaCha20Poly1305(_k);
        try
        {
            aead.Decrypt(Nonce(_n), ciphertext.AsSpan(0, plaintextLength), ciphertext.AsSpan(plaintextLength), plaintext, ad);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new NoiseException("decryption failed");
        }
        _n++;
        return plaintext;
    }

    private static byte[] Nonce(ulong n)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), n);
        return nonce;
    }
}

internal sealed class SymmetricState
{
    public byte[] Ck;
    public byte[] H;
    public readonly CipherState Cipher = new();

    public SymmetricState(string protocolName)
    {
        var name = Encoding.ASCII.GetBytes(protocolName);
        if (name.Length <= Noise.HashLength)
        {
            H = new byte[Noise.HashLength];
            name.CopyTo(H, 0);
        }
        else
        {
            H = SHA256.HashData(name);
        }
        Ck = (byte[])H.Clone();
    }

    public void MixKey(byte[] ikm)
    {
        (Ck, var k) = Noise.Hkdf2(Ck, ikm);
        Cipher.InitializeKey(k);
    }

    public void MixHash(byte[] data) => H = SHA256.HashData(H.Concat(data).ToArray());

    public byte[] EncryptAndHash(byte[] plaintext)
    {
        var ciphertext = Cipher.EncryptWithAd(H, plaintext);
        MixHash(ciphertext);
        return ciphertext;
    }

    public byte[] DecryptAndHash(byte[] ciphertext)
    {
        var plaintext = Cipher.DecryptWithAd(H, ciphertext);
        MixHash(ciphertext);
        return plaintext;
    }

    public (CipherState, CipherState) Split()
    {
        var (k1, k2) = Noise.Hkdf2(Ck, Array.Empty<byte>());
        return (new CipherState(k1), new CipherState(k2));
    }
}

/// <summary>
/// Noise IK handshake:
///   &lt;- s
///   ...
///   -&gt; e, es, s, ss
///   &lt;- e, ee, se
/// Any exception leaves the handshake unusable; the caller must drop the connection.
/// </summary>
public sealed class IKHandshake
{
    private readonly SymmetricState _sym = new(Noise.ProtocolName);
    private readonly bool _initiator;
    private readonly KeyPair _s;
    private KeyPair? _e;
    private byte[]? _re;
    private byte[]? _rs;
    private int _step;

    /// <param name="rs">Responder's static public key. Required for the initiator.</param>
    /// <param name="e">Fixed ephemeral key pair. Only for test vectors; normally generated.</param>
    public IKHandshake(bool initiator, byte[] prologue, KeyPair s, byte[]? rs = null, KeyPair? e = null)
    {
        if (initiator && rs is null) throw new ArgumentException("initiator needs the responder static key", nameof(rs));
        _initiator = initiator;
        _s = s;
        _rs = rs;
        _e = e;
        _sym.MixHash(prologue);
        _sym.MixHash(initiator ? rs! : s.PublicKey);
    }

    /// <summary>The peer's static public key: known up front for the initiator, learned from message 1 by the responder.</summary>
    public byte[]? RemoteStaticKey => _rs;

    public byte[] HandshakeHash => _sym.H;

    public bool IsComplete => _step == 2;

    public byte[] WriteMessage(byte[] payload)
    {
        if (_step == 0 && _initiator)
        {
            var e = _e ??= KeyPair.Generate();
            _sym.MixHash(e.PublicKey);
            _sym.MixKey(Noise.Dh(e, _rs!));
            var encryptedStatic = _sym.EncryptAndHash(_s.PublicKey);
            _sym.MixKey(Noise.Dh(_s, _rs!));
            var body = _sym.EncryptAndHash(payload);
            _step = 1;
            return CheckLength([.. e.PublicKey, .. encryptedStatic, .. body]);
        }
        if (_step == 1 && !_initiator)
        {
            var e = _e ??= KeyPair.Generate();
            _sym.MixHash(e.PublicKey);
            _sym.MixKey(Noise.Dh(e, _re!));
            _sym.MixKey(Noise.Dh(e, _rs!));
            var body = _sym.EncryptAndHash(payload);
            _step = 2;
            return CheckLength([.. e.PublicKey, .. body]);
        }
        throw new NoiseException("writeMessage called out of turn");
    }

    public byte[] ReadMessage(byte[] message)
    {
        if (message.Length > Noise.MaxMessageLength) throw new NoiseException("message too long");
        const int dh = Noise.DhLength, tag = Noise.TagLength;
        if (_step == 0 && !_initiator)
        {
            if (message.Length < dh + dh + tag + tag) throw new NoiseException("message too short");
            _re = message[..dh];
            _sym.MixHash(_re);
            _sym.MixKey(Noise.Dh(_s, _re));
            _rs = _sym.DecryptAndHash(message[dh..(dh * 2 + tag)]);
            _sym.MixKey(Noise.Dh(_s, _rs));
            var payload = _sym.DecryptAndHash(message[(dh * 2 + tag)..]);
            _step = 1;
            return payload;
        }
        if (_step == 1 && _initiator)
        {
            if (message.Length < dh + tag) throw new NoiseException("message too short");
            _re = message[..dh];
            _sym.MixHash(_re);
            _sym.MixKey(Noise.Dh(_e!, _re));
            _sym.MixKey(Noise.Dh(_s, _re));
            var payload = _sym.DecryptAndHash(message[dh..]);
            _step = 2;
            return payload;
        }
        throw new NoiseException("readMessage called out of turn");
    }

    public Transport Split()
    {
        if (!IsComplete) throw new NoiseException("handshake not complete");
        var (c1, c2) = _sym.Split();
        return _initiator ? new Transport(c1, c2) : new Transport(c2, c1);
    }

    private static byte[] CheckLength(byte[] message) =>
        message.Length > Noise.MaxMessageLength ? throw new NoiseException("message too long") : message;
}

/// <summary>Post-handshake channel. Messages must be decrypted in the order they were encrypted.</summary>
public sealed class Transport(CipherState sender, CipherState receiver)
{
    public byte[] Encrypt(byte[] plaintext)
    {
        if (plaintext.Length + Noise.TagLength > Noise.MaxMessageLength) throw new NoiseException("message too long");
        return sender.EncryptWithAd([], plaintext);
    }

    public byte[] Decrypt(byte[] ciphertext)
    {
        if (ciphertext.Length > Noise.MaxMessageLength) throw new NoiseException("message too long");
        return receiver.DecryptWithAd([], ciphertext);
    }
}
