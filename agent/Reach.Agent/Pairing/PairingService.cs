using System.Buffers.Text;
using System.Security.Cryptography;

namespace Reach.Agent.Pairing;

public sealed record PairingCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>One pairing code at a time: 16 random bytes, valid for 2 minutes, single use (spec §3.3).</summary>
public sealed class PairingService(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly Lock _lock = new();
    private byte[]? _code;
    private DateTimeOffset _expiresAt;

    /// <summary>Raised (outside the lock, on the connection's thread) after a code is consumed.</summary>
    public event Action? Paired;

    /// <summary>Issues a new code, replacing any previous one.</summary>
    public PairingCode Start()
    {
        lock (_lock)
        {
            _code = RandomNumberGenerator.GetBytes(16);
            _expiresAt = time.GetUtcNow() + Lifetime;
            return new PairingCode(Base64Url.EncodeToString(_code), _expiresAt);
        }
    }

    public void Cancel()
    {
        lock (_lock) _code = null;
    }

    /// <summary>True exactly once for the current, unexpired code. Constant-time compare.</summary>
    public bool TryConsume(string code)
    {
        byte[] candidate;
        try
        {
            candidate = Base64Url.DecodeFromChars(code);
        }
        catch (FormatException)
        {
            return false;
        }
        lock (_lock)
        {
            if (_code is null || time.GetUtcNow() >= _expiresAt) return false;
            if (!CryptographicOperations.FixedTimeEquals(candidate, _code)) return false;
            _code = null;
        }
        Paired?.Invoke();
        return true;
    }
}
