using System.Net;
using AdBlocker.Core;
using Xunit;

namespace AdBlocker.Tests;

public class DnsMessageTests
{
    [Fact]
    public void Parses_a_query()
    {
        Assert.True(DnsMessage.TryParseQuestion(T.Query("WWW.Example.com", 28, 0xBEEF), out var q));
        Assert.Equal("www.example.com", q.Name); Assert.Equal(28, q.QType); Assert.Equal(0xBEEF, q.Id);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Rejects_garbage(byte[] p) => Assert.False(DnsMessage.TryParseQuestion(p, out _));

    [Fact]
    public void Rejects_responses_truncated_names_and_compression_in_question()
    {
        var resp = T.Query("a.example.com"); resp[2] |= 0x80;
        Assert.False(DnsMessage.TryParseQuestion(resp, out _));
        var q = T.Query("a.example.com"); Assert.False(DnsMessage.TryParseQuestion(q.Take(q.Length - 3).ToArray(), out _));
        var c = T.Query("a.example.com"); c[12] = 0xC0; Assert.False(DnsMessage.TryParseQuestion(c, out _));
    }

    [Fact]
    public void Blocked_response_is_NXDOMAIN_with_same_id_and_no_answers()
    {
        var qb = T.Query("ads.example.com", 1, 0x4242); DnsMessage.TryParseQuestion(qb, out var q);
        var r = DnsMessage.BuildBlocked(qb, q, nullIpMode: false);
        Assert.Equal(0x42, r[0]); Assert.Equal(0x42, r[1]);
        Assert.True((r[2] & 0x80) != 0); Assert.Equal(3, DnsMessage.Rcode(r)); Assert.Equal(0, r[6] | r[7]);
    }

    [Fact]
    public void NullIp_mode_answers_A_with_zeroes_and_AAAA_with_16_zeroes()
    {
        var qa = T.Query("ads.example.com", 1); DnsMessage.TryParseQuestion(qa, out var q1);
        var ra = DnsMessage.BuildBlocked(qa, q1, true);
        Assert.Equal(0, DnsMessage.Rcode(ra)); Assert.Equal(1, ra[7]); Assert.Equal(q1.QuestionEnd + 12 + 4, ra.Length);
        Assert.All(ra.Skip(q1.QuestionEnd + 12), b => Assert.Equal(0, b));
        var q6 = T.Query("ads.example.com", 28); DnsMessage.TryParseQuestion(q6, out var q2);
        Assert.Equal(q2.QuestionEnd + 12 + 16, DnsMessage.BuildBlocked(q6, q2, true).Length);
        var qt = T.Query("ads.example.com", 16); DnsMessage.TryParseQuestion(qt, out var q3);
        var rt = DnsMessage.BuildBlocked(qt, q3, true); Assert.Equal(0, rt[7]); Assert.Equal(0, DnsMessage.Rcode(rt));   // NODATA
    }

    [Fact]
    public void ServFail_has_rcode_2()
    {
        var qb = T.Query("a.example.com"); DnsMessage.TryParseQuestion(qb, out var q);
        Assert.Equal(2, DnsMessage.Rcode(DnsMessage.BuildServFail(qb, q)));
    }

    [Fact]
    public void Reads_min_ttl_and_ignores_garbage()
    {
        Assert.True(DnsMessage.TryGetMinTtl(T.AnswerFor(T.Query("a.example.com"), 300), out var ttl)); Assert.Equal(300u, ttl);
        Assert.False(DnsMessage.TryGetMinTtl(new byte[] { 1, 2, 3 }, out _));
        var bad = T.AnswerFor(T.Query("a.example.com")); Assert.False(DnsMessage.TryGetMinTtl(bad.Take(bad.Length - 6).ToArray(), out _));
    }
}

public class DnsCacheTests
{
    [Fact]
    public void Hit_rewrites_txn_id_and_expires_after_ttl()
    {
        var clock = new T.ManualTime(); var cache = new DnsCache(100, clock);
        var qb = T.Query("a.example.com", 1, 0x1111); DnsMessage.TryParseQuestion(qb, out var q);
        cache.Store(q, T.AnswerFor(qb, 60));
        Assert.True(cache.TryGet(q, 0x2222, out var hit)); Assert.Equal(0x22, hit[0]); Assert.Equal(0x22, hit[1]);
        clock.Now = clock.Now.AddSeconds(61);
        Assert.False(cache.TryGet(q, 0x3333, out _));
    }

    [Fact]
    public void Does_not_cache_servfail_truncated_or_zero_ttl()
    {
        var cache = new DnsCache(); var qb = T.Query("a.example.com"); DnsMessage.TryParseQuestion(qb, out var q);
        cache.Store(q, DnsMessage.BuildServFail(qb, q));
        var tc = T.AnswerFor(qb); tc[2] |= 0x02; cache.Store(q, tc);
        cache.Store(q, T.AnswerFor(qb, 0));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Is_bounded()
    {
        var cache = new DnsCache(5);
        for (int i = 0; i < 20; i++) { var qb = T.Query($"h{i}.example.com"); DnsMessage.TryParseQuestion(qb, out var q); cache.Store(q, T.AnswerFor(qb)); }
        Assert.True(cache.Count <= 5);
    }
}

public class DnsProxyIntegrationTests
{
    private static DnsProxy Make(RuleEngine rules, Stats stats, IPEndPoint up, Func<bool>? on = null, int timeoutMs = 300)
        => new(rules, new DnsCache(), stats, new DnsProxyOptions { Port = 0, Upstreams = new[] { up }, UpstreamTimeout = TimeSpan.FromMilliseconds(timeoutMs), FilteringEnabled = on ?? (() => true) });

    [Fact]
    public async Task Blocked_domain_never_reaches_upstream_and_allowed_domain_resolves()
    {
        using var up = new T.FakeUpstream(); var stats = new Stats();
        using var p = Make(T.Engine(new[] { "ads.example.com" }), stats, up.Endpoint);
        var blocked = await p.HandleForTestAsync(T.Query("x.ads.example.com"), default);
        Assert.Equal(3, DnsMessage.Rcode(blocked!)); Assert.Equal(0, up.Received);
        var ok = await p.HandleForTestAsync(T.Query("www.example.com"), default);
        Assert.Equal(0, DnsMessage.Rcode(ok!)); Assert.Equal(1, ok![7]); Assert.Equal(1, up.Received);
        var s = stats.Snapshot(); Assert.Equal(1, s.BlockedTotal); Assert.Equal(2, s.TotalQueries);
    }

    [Fact]
    public async Task Second_identical_query_is_served_from_cache()
    {
        using var up = new T.FakeUpstream(); var stats = new Stats();
        using var p = Make(T.Engine(Array.Empty<string>()), stats, up.Endpoint);
        await p.HandleForTestAsync(T.Query("a.example.com"), default);
        await p.HandleForTestAsync(T.Query("a.example.com", 1, 0x9999), default);
        Assert.Equal(1, up.Received); Assert.Equal(1, stats.Snapshot().CacheHits);
    }

    [Fact]
    public async Task Disabling_filtering_lets_blocked_domains_through()
    {
        using var up = new T.FakeUpstream(); bool on = false;
        using var p = Make(T.Engine(new[] { "ads.example.com" }), new Stats(), up.Endpoint, () => on);
        Assert.Equal(0, DnsMessage.Rcode((await p.HandleForTestAsync(T.Query("ads.example.com"), default))!));
        on = true;
        await Task.Delay(1);   // distinct cache path is not an issue: blocking is evaluated before the cache
        Assert.Equal(3, DnsMessage.Rcode((await p.HandleForTestAsync(T.Query("ads.example.com"), default))!));
    }

    [Fact]
    public async Task Dead_upstream_returns_SERVFAIL_within_timeout_and_counts_failure()
    {
        using var up = new T.FakeUpstream(respond: false); var stats = new Stats();
        using var p = Make(T.Engine(Array.Empty<string>()), stats, up.Endpoint, timeoutMs: 200);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await p.HandleForTestAsync(T.Query("a.example.com"), default);
        Assert.Equal(2, DnsMessage.Rcode(r!)); Assert.True(sw.ElapsedMilliseconds < 2000); Assert.Equal(1, stats.Snapshot().UpstreamFailures);
    }

    [Fact]
    public async Task Falls_back_to_second_upstream()
    {
        using var dead = new T.FakeUpstream(respond: false); using var live = new T.FakeUpstream(); var stats = new Stats();
        using var p = new DnsProxy(T.Engine(Array.Empty<string>()), new DnsCache(), stats,
            new DnsProxyOptions { Port = 0, Upstreams = new[] { dead.Endpoint, live.Endpoint }, UpstreamTimeout = TimeSpan.FromMilliseconds(200) });
        Assert.Equal(0, DnsMessage.Rcode((await p.HandleForTestAsync(T.Query("a.example.com"), default))!));
        Assert.Equal(1, live.Received);
    }

    [Fact]
    public async Task Malformed_packets_are_dropped_and_counted()
    {
        using var up = new T.FakeUpstream(); var stats = new Stats();
        using var p = Make(T.Engine(Array.Empty<string>()), stats, up.Endpoint);
        Assert.Null(await p.HandleForTestAsync(new byte[] { 1, 2, 3 }, default));
        Assert.Equal(1, stats.Snapshot().Malformed); Assert.Equal(0, up.Received);
    }

    [Fact]
    public async Task Live_socket_round_trip_on_loopback()
    {
        using var up = new T.FakeUpstream();
        using var p = Make(T.Engine(new[] { "ads.example.com" }), new Stats(), up.Endpoint); p.Start();
        using var c = new System.Net.Sockets.UdpClient(); c.Connect(p.LocalEndPoint!);
        await c.SendAsync(T.Query("ads.example.com", 1, 0x0A0B));
        var r = await c.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0x0A, r.Buffer[0]); Assert.Equal(3, DnsMessage.Rcode(r.Buffer));
    }

    [Fact]
    public void Binding_a_port_already_in_use_throws_so_the_engine_can_report_Error()
    {
        using var up = new T.FakeUpstream();
        using var p1 = Make(T.Engine(Array.Empty<string>()), new Stats(), up.Endpoint); p1.Start();
        var o = new DnsProxyOptions { Port = p1.LocalEndPoint!.Port, Upstreams = new[] { up.Endpoint } };
        using var p2 = new DnsProxy(T.Engine(Array.Empty<string>()), new DnsCache(), new Stats(), o);
        Assert.Throws<System.Net.Sockets.SocketException>(() => p2.Start());
    }
}

public class UserListTests
{
    [Fact]
    public void Add_normalises_dedupes_removes_and_rejects_invalid()
    {
        var path = Path.Combine(Path.GetTempPath(), "sam-" + Guid.NewGuid() + ".txt"); var l = new UserList(path);
        try
        {
            Assert.True(l.Add("Example.COM.")); Assert.True(l.Add("example.com")); Assert.False(l.Add("not a domain")); Assert.False(l.Add("localhost"));
            Assert.Equal(new[] { "example.com" }, l.Read());
            l.Remove("EXAMPLE.com"); Assert.Empty(l.Read());
        }
        finally { File.Delete(path); }
    }
}
