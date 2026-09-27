using System.Net;

namespace Reach.Agent.Net;

/// <summary>5 failed handshakes from one IP within a minute → that IP is ignored for a minute (spec §3.5).</summary>
public sealed class RateLimiter(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<IPAddress, Entry> _entries = [];

    public bool IsBlocked(IPAddress ip)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(Normalize(ip), out var e) && time.GetUtcNow() < e.BlockedUntil;
        }
    }

    public void RecordFailure(IPAddress ip)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            Prune(now);
            ip = Normalize(ip);
            if (!_entries.TryGetValue(ip, out var e)) _entries[ip] = e = new Entry();
            e.Failures.Enqueue(now);
            if (e.Failures.Count >= MaxFailures)
            {
                e.BlockedUntil = now + BlockDuration;
                e.Failures.Clear();
            }
        }
    }

    // Kestrel reports IPv4 clients on a dual-stack socket as ::ffff:a.b.c.d.
    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private void Prune(DateTimeOffset now)
    {
        foreach (var (ip, e) in _entries)
        {
            while (e.Failures.TryPeek(out var t) && now - t >= Window) e.Failures.Dequeue();
            if (e.Failures.Count == 0 && now >= e.BlockedUntil) _entries.Remove(ip);
        }
    }

    private sealed class Entry
    {
        public readonly Queue<DateTimeOffset> Failures = new();
        public DateTimeOffset BlockedUntil = DateTimeOffset.MinValue;
    }
}
