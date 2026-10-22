using AdBlocker.Core;
using Xunit;

namespace AdBlocker.Tests;

public class RuleEngineTests
{
    [Theory]
    [InlineData("ads.example.com", true)]
    [InlineData("a.b.ads.example.com", true)]
    [InlineData("ADS.Example.COM.", true)]          // case-insensitive, trailing dot
    [InlineData("example.com", false)]
    [InlineData("notads.example.com", false)]       // must match on label boundaries, not substrings
    [InlineData("localhost", false)]
    public void Blocks_domain_and_subdomains_only(string host, bool blocked)
        => Assert.Equal(blocked, T.Engine(new[] { "ads.example.com" }).Evaluate(host).Verdict == Verdict.Block);

    [Fact]
    public void More_specific_rule_wins_in_both_directions()
    {
        var e = T.Engine(new[] { "ads.example.com", "adnet.com" }, new[] { "example.com", "good.adnet.com" });
        Assert.Equal(Verdict.Block, e.Evaluate("ads.example.com").Verdict);   // specific block beats parent allow
        Assert.Equal(Verdict.Allow, e.Evaluate("www.example.com").Verdict);
        Assert.Equal(Verdict.Allow, e.Evaluate("good.adnet.com").Verdict);    // specific allow beats parent block
        Assert.Equal(Verdict.Block, e.Evaluate("x.adnet.com").Verdict);
    }

    [Fact]
    public void Same_label_in_both_lists_allow_wins()
        => Assert.Equal(Verdict.Allow, T.Engine(new[] { "both.com" }, new[] { "both.com" }).Evaluate("both.com").Verdict);

    [Fact]
    public void Parses_hosts_adblock_plain_and_reports_invalid_lines()
    {
        var e = new RuleEngine();
        var rep = e.Load(new[] { ("f", RuleCategory.Tracking, (IEnumerable<string>)new[]
        {
            "# comment", "", "0.0.0.0 hosts.example", "127.0.0.1 h2.example # trailing", "||abp.example^", "plain.example",
            "||path.example/x^", "*.wild.example", "com", "bad_domain!.example", "two words here"
        }) }, Array.Empty<(string, IEnumerable<string>)>());
        Assert.Equal(4, rep.Accepted);
        Assert.Equal(5, rep.Rejected);
        Assert.Equal(4, e.BlockCount);
        Assert.All(new[] { "hosts.example", "h2.example", "abp.example", "plain.example" }, d => Assert.Equal(Verdict.Block, e.Evaluate(d).Verdict));
    }

    [Fact]
    public void Single_label_rule_can_never_block_a_tld()
        => Assert.Equal(Verdict.Allow, T.Engine(new[] { "com" }).Evaluate("example.com").Verdict);

    [Fact]
    public void Category_is_reported()
    {
        var e = new RuleEngine();
        e.Load(new[] { ("a", RuleCategory.Advertising, (IEnumerable<string>)new[] { "ad.example" }), ("t", RuleCategory.Tracking, (IEnumerable<string>)new[] { "trk.example" }) }, Array.Empty<(string, IEnumerable<string>)>());
        Assert.Equal(RuleCategory.Tracking, e.Evaluate("x.trk.example").Category);
        Assert.Equal("ad.example", e.Evaluate("x.ad.example").MatchedRule);
    }

    [Fact]
    public void Reload_swaps_atomically_and_empty_load_blocks_nothing()
    {
        var e = T.Engine(new[] { "a.example" });
        e.Load(Array.Empty<(string, RuleCategory, IEnumerable<string>)>(), Array.Empty<(string, IEnumerable<string>)>());
        Assert.Equal(Verdict.Allow, e.Evaluate("a.example").Verdict);
    }
}
