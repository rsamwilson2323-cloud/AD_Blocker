using System.Globalization;
using System.Text.RegularExpressions;

namespace AdBlocker.Core;

public enum RuleCategory { Advertising, Tracking, Custom }
public enum Verdict { Allow, Block }
public readonly record struct MatchResult(Verdict Verdict, RuleCategory? Category, string? MatchedRule);

public sealed record ParseIssue(string Source, int Line, string Text, string Reason);

public sealed class ParseReport
{
    public int Accepted { get; internal set; }
    public int Rejected { get; internal set; }
    public List<ParseIssue> Issues { get; } = new();
    internal void Reject(string src, int line, string text, string reason)
    {
        Rejected++;
        if (Issues.Count < 50) Issues.Add(new ParseIssue(src, line, text, reason));
    }
}

public enum ParseKind { Skip, Domain, Invalid }
public readonly record struct ParsedLine(ParseKind Kind, string? Domain, string? Reason);

/// <summary>Parses one line of a rule file. Accepts: plain domain, hosts format, and ||domain^ (domain-only form).</summary>
public static class RuleParser
{
    private static readonly Regex Label = new(@"^[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?$", RegexOptions.Compiled);
    private static readonly char[] UnsupportedChars = { '/', '*', '$', '^', '|', ':', '?', '@', '#', '=' };

    public static ParsedLine Parse(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#' || line[0] == '!') return new(ParseKind.Skip, null, null);
        int hash = line.IndexOf(" #", StringComparison.Ordinal);
        if (hash < 0) hash = line.IndexOf("\t#", StringComparison.Ordinal);
        if (hash >= 0) line = line[..hash].TrimEnd();

        if (line.StartsWith("||", StringComparison.Ordinal))
        {
            var body = line[2..];
            if (body.EndsWith('^')) body = body[..^1];
            if (body.IndexOfAny(UnsupportedChars) >= 0)
                return new(ParseKind.Invalid, null, "unsupported adblock syntax (DNS filtering can only use ||domain^)");
            line = body;
        }
        else
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && (parts[0] is "0.0.0.0" or "127.0.0.1" or "::1" or "::")) line = parts[1];
            else if (parts.Length != 1) return new(ParseKind.Invalid, null, "expected one domain or a hosts-format line");
            else line = parts[0];
        }
        var d = NormalizeDomain(line);
        return d == null ? new(ParseKind.Invalid, null, "not a valid domain name (need at least two labels)")
                         : new(ParseKind.Domain, d, null);
    }

    /// <summary>Lower-cases, punycode-encodes, validates. Requires >= 2 labels so a TLD can never be blocked by accident.</summary>
    public static string? NormalizeDomain(string s)
    {
        s = s.Trim().TrimEnd('.').ToLowerInvariant();
        if (s.Length == 0 || s.Length > 253) return null;
        try { s = new IdnMapping().GetAscii(s); } catch (ArgumentException) { return null; }
        var labels = s.Split('.');
        if (labels.Length < 2) return null;
        foreach (var l in labels) if (!Label.IsMatch(l)) return null;
        return s;
    }
}

/// <summary>
/// Domain rule matcher. Walks from the most specific label to the least specific. The first label that
/// is in either list decides; if a label is in both lists, ALLOW wins. Consequences:
///   allow example.com + block ads.example.com  -> ads.example.com is blocked (more specific rule wins)
///   block ads.com     + allow good.ads.com     -> good.ads.com is allowed
/// The rule set is swapped atomically so lookups never see a half-loaded list.
/// </summary>
public sealed class RuleEngine
{
    private sealed record Snapshot(Dictionary<string, RuleCategory> Block, HashSet<string> Allow);
    private volatile Snapshot _s = new(new(), new());

    public int BlockCount => _s.Block.Count;
    public int AllowCount => _s.Allow.Count;

    public ParseReport Load(IEnumerable<(string Source, RuleCategory Category, IEnumerable<string> Lines)> blockSources,
                            IEnumerable<(string Source, IEnumerable<string> Lines)> allowSources)
    {
        var report = new ParseReport();
        var block = new Dictionary<string, RuleCategory>(StringComparer.Ordinal);
        var allow = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (src, cat, lines) in blockSources)
        {
            int n = 0;
            foreach (var l in lines)
            {
                n++;
                var p = RuleParser.Parse(l);
                if (p.Kind == ParseKind.Invalid) report.Reject(src, n, l, p.Reason!);
                else if (p.Kind == ParseKind.Domain) { if (block.TryAdd(p.Domain!, cat)) report.Accepted++; }
            }
        }
        foreach (var (src, lines) in allowSources)
        {
            int n = 0;
            foreach (var l in lines)
            {
                n++;
                var p = RuleParser.Parse(l);
                if (p.Kind == ParseKind.Invalid) report.Reject(src, n, l, p.Reason!);
                else if (p.Kind == ParseKind.Domain) { if (allow.Add(p.Domain!)) report.Accepted++; }
            }
        }
        _s = new Snapshot(block, allow);
        return report;
    }

    public MatchResult Evaluate(string host)
    {
        var s = _s;
        var cur = host.TrimEnd('.').ToLowerInvariant();
        while (cur.Length > 0)
        {
            if (s.Allow.Contains(cur)) return new(Verdict.Allow, null, cur);
            if (s.Block.TryGetValue(cur, out var cat)) return new(Verdict.Block, cat, cur);
            int dot = cur.IndexOf('.');
            if (dot < 0) break;
            cur = cur[(dot + 1)..];
        }
        return new(Verdict.Allow, null, null);
    }
}
