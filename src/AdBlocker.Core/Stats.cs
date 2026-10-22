using System.Collections.Concurrent;

namespace AdBlocker.Core;

public sealed record LogEntry(DateTime TimeUtc, string Kind, string Domain, string Detail);

public sealed record StatsSnapshot(
    long TotalQueries, long BlockedTotal, long BlockedAdvertising, long BlockedTracking, long BlockedCustom,
    long DistinctBlockedDomains, long CacheHits, long Forwarded, long UpstreamFailures, long Malformed, long Dropped,
    long BrowserRuleMatches, long YouTubeAdFieldsPruned, long YouTubeAdsAccelerated);

/// <summary>
/// Real counters only. The activity log is an in-memory ring buffer (last 500 events): it is never written
/// to disk and never transmitted. Only blocks and errors are logged, not every allowed lookup.
/// </summary>
public sealed class Stats
{
    private long _total, _blocked, _ads, _trk, _cust, _hits, _fwd, _fail, _bad, _drop, _ext, _ytP, _ytA;
    private readonly ConcurrentDictionary<string, byte> _distinct = new();
    private readonly ConcurrentQueue<LogEntry> _log = new();
    private int _logCount;
    private const int MaxLog = 500, MaxDistinct = 100_000;

    public void Query() => Interlocked.Increment(ref _total);
    public void CacheHit() => Interlocked.Increment(ref _hits);
    public void Forwarded() => Interlocked.Increment(ref _fwd);
    public void UpstreamFailure() => Interlocked.Increment(ref _fail);
    public void Malformed() => Interlocked.Increment(ref _bad);
    public void Dropped() => Interlocked.Increment(ref _drop);

    public void Blocked(RuleCategory c, string domain)
    {
        Interlocked.Increment(ref _blocked);
        if (c == RuleCategory.Advertising) Interlocked.Increment(ref _ads);
        else if (c == RuleCategory.Tracking) Interlocked.Increment(ref _trk);
        else Interlocked.Increment(ref _cust);
        if (_distinct.Count < MaxDistinct) _distinct.TryAdd(domain, 0);
    }

    /// <summary>Counts reported by the browser extension. These are browser-side numbers, kept separate from DNS blocks.</summary>
    public void AddBrowserReport(long ruleMatches, long ytPruned, long ytAccelerated)
    {
        Interlocked.Add(ref _ext, Math.Clamp(ruleMatches, 0, 1_000_000));
        Interlocked.Add(ref _ytP, Math.Clamp(ytPruned, 0, 1_000_000));
        Interlocked.Add(ref _ytA, Math.Clamp(ytAccelerated, 0, 1_000_000));
    }

    public void Log(string kind, string domain, string detail = "")
    {
        _log.Enqueue(new LogEntry(DateTime.UtcNow, kind, domain, detail));
        if (Interlocked.Increment(ref _logCount) > MaxLog && _log.TryDequeue(out _)) Interlocked.Decrement(ref _logCount);
    }

    public IReadOnlyList<LogEntry> Recent(int n) => _log.Reverse().Take(Math.Clamp(n, 1, MaxLog)).ToList();

    public StatsSnapshot Snapshot() => new(
        Interlocked.Read(ref _total), Interlocked.Read(ref _blocked), Interlocked.Read(ref _ads), Interlocked.Read(ref _trk),
        Interlocked.Read(ref _cust), _distinct.Count, Interlocked.Read(ref _hits), Interlocked.Read(ref _fwd),
        Interlocked.Read(ref _fail), Interlocked.Read(ref _bad), Interlocked.Read(ref _drop),
        Interlocked.Read(ref _ext), Interlocked.Read(ref _ytP), Interlocked.Read(ref _ytA));
}
