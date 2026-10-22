using System.Net;
using System.Net.Sockets;

namespace AdBlocker.Core;

public sealed class DnsProxyOptions
{
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;
    public int Port { get; init; } = 53;
    public IReadOnlyList<IPEndPoint> Upstreams { get; init; } = Array.Empty<IPEndPoint>();
    public TimeSpan UpstreamTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public bool NullIpMode { get; init; }
    /// <summary>Evaluated per query so the UI toggle takes effect immediately. When false the proxy is a plain caching forwarder.</summary>
    public Func<bool> FilteringEnabled { get; init; } = () => true;
}

/// <summary>
/// UDP DNS forwarder with blocking. Known limitation: UDP only (no TCP listener), so answers that need
/// TCP fallback (very large records) will fail through this proxy. Ordinary web browsing is unaffected.
/// </summary>
public sealed class DnsProxy : IDisposable
{
    private readonly RuleEngine _rules; private readonly DnsCache _cache; private readonly Stats _stats; private readonly DnsProxyOptions _o;
    private UdpClient? _udp; private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _inflight = new(256);   // load shedding: never queue unbounded work

    public event Action<Exception>? Faulted;
    public IPEndPoint? LocalEndPoint => _udp?.Client.LocalEndPoint as IPEndPoint;
    public bool Listening => _udp != null && _cts is { IsCancellationRequested: false };

    public DnsProxy(RuleEngine rules, DnsCache cache, Stats stats, DnsProxyOptions options)
    { _rules = rules; _cache = cache; _stats = stats; _o = options; }

    /// <summary>Binds the socket. Throws SocketException (e.g. port 53 already in use) so the caller can report an Error state.</summary>
    public void Start()
    {
        if (_o.Upstreams.Count == 0) throw new InvalidOperationException("No upstream DNS servers configured.");
        _udp = new UdpClient(new IPEndPoint(_o.ListenAddress, _o.Port));
        if (OperatingSystem.IsWindows())
        {
            // Windows delivers ICMP "port unreachable" as a socket error on UDP receive; disable so one bad client can't kill the loop.
            const int SIO_UDP_CONNRESET = -1744830452;
            _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => ReceiveLoop(ct));
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp!.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
            catch (Exception ex) { Faulted?.Invoke(ex); break; }

            if (!_inflight.Wait(0)) { _stats.Dropped(); continue; }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(r, ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _stats.Log("ERROR", "-", ex.GetType().Name); }
                finally { _inflight.Release(); }
            });
        }
    }

    public async Task<byte[]?> HandleForTestAsync(byte[] packet, CancellationToken ct) => await BuildReplyAsync(packet, ct);

    private async Task HandleAsync(UdpReceiveResult r, CancellationToken ct)
    {
        var reply = await BuildReplyAsync(r.Buffer, ct);
        if (reply != null) await _udp!.SendAsync(reply, r.RemoteEndPoint, ct);
    }

    private async Task<byte[]?> BuildReplyAsync(byte[] pkt, CancellationToken ct)
    {
        if (!DnsMessage.TryParseQuestion(pkt, out var q)) { _stats.Malformed(); return null; }   // drop garbage silently
        _stats.Query();

        var m = _rules.Evaluate(q.Name);
        if (m.Verdict == Verdict.Block && _o.FilteringEnabled())
        {
            _stats.Blocked(m.Category ?? RuleCategory.Custom, q.Name);
            _stats.Log("BLOCK", q.Name, $"{m.Category} (rule {m.MatchedRule})");
            return DnsMessage.BuildBlocked(pkt, q, _o.NullIpMode);
        }
        if (_cache.TryGet(q, q.Id, out var cached)) { _stats.CacheHit(); return cached; }

        var resp = await ForwardAsync(pkt, ct);
        if (resp == null)
        {
            _stats.UpstreamFailure();
            _stats.Log("ERROR", q.Name, "all upstream DNS servers failed or timed out");
            return DnsMessage.BuildServFail(pkt, q);      // fail closed with a clear error, never hang the client
        }
        _cache.Store(q, resp);
        return resp;
    }

    private async Task<byte[]?> ForwardAsync(byte[] pkt, CancellationToken ct)
    {
        foreach (var up in _o.Upstreams)
        {
            try
            {
                // A fresh socket per query gives a random ephemeral source port each time (spoofing resistance).
                using var c = new UdpClient(up.AddressFamily);
                c.Connect(up);                                 // connected socket: kernel drops datagrams from other sources
                await c.SendAsync(pkt, ct);
                using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
                to.CancelAfter(_o.UpstreamTimeout);
                while (true)
                {
                    var resp = await c.ReceiveAsync(to.Token);
                    if (resp.Buffer.Length >= 12 && resp.Buffer[0] == pkt[0] && resp.Buffer[1] == pkt[1])   // txn id must match
                    { _stats.Forwarded(); return resp.Buffer; }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* timeout -> next upstream */ }
            catch (SocketException) { /* unreachable network -> next upstream */ }
        }
        return null;
    }

    public void Dispose() { _cts?.Cancel(); _udp?.Dispose(); _cts?.Dispose(); }
}
