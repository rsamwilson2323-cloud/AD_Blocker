using System.Collections.Concurrent;

namespace AdBlocker.Core;

/// <summary>
/// TTL-honouring response cache. Known limitation: cached answers are returned with their ORIGINAL TTL
/// (not decremented), so a client may hold a record up to ~2x its TTL. Max lifetime is clamped to 1 hour.
/// </summary>
public sealed class DnsCache
{
    private sealed record Entry(byte[] Response, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Entry> _map = new();
    private readonly TimeProvider _time;
    private readonly int _max;
    public const uint MinTtl = 10, MaxTtl = 3600, NegativeDefaultTtl = 30;

    public DnsCache(int maxEntries = 10_000, TimeProvider? time = null) { _max = maxEntries; _time = time ?? TimeProvider.System; }
    public int Count => _map.Count;

    public bool TryGet(DnsQuestion q, ushort clientId, out byte[] response)
    {
        response = Array.Empty<byte>();
        if (!_map.TryGetValue(q.CacheKey, out var e)) return false;
        if (e.Expires <= _time.GetUtcNow()) { _map.TryRemove(q.CacheKey, out _); return false; }
        response = (byte[])e.Response.Clone();
        response[0] = (byte)(clientId >> 8); response[1] = (byte)clientId;   // answer must carry the CLIENT's txn id
        return true;
    }

    public void Store(DnsQuestion q, byte[] response)
    {
        int rcode = DnsMessage.Rcode(response);
        if (DnsMessage.IsTruncated(response) || (rcode != 0 && rcode != 3)) return;   // never cache SERVFAIL/TC
        uint ttl = DnsMessage.TryGetMinTtl(response, out var t) ? t : (rcode == 3 ? NegativeDefaultTtl : 0);
        if (ttl == 0) return;
        ttl = Math.Clamp(ttl, MinTtl, MaxTtl);
        if (_map.Count >= _max) Purge();
        _map[q.CacheKey] = new Entry(response, _time.GetUtcNow().AddSeconds(ttl));
    }

    private void Purge()
    {
        var now = _time.GetUtcNow();
        foreach (var kv in _map) if (kv.Value.Expires <= now) _map.TryRemove(kv.Key, out _);
        if (_map.Count >= _max) _map.Clear();   // simple and safe; the cache is only an optimisation
    }
    public void Clear() => _map.Clear();
}
