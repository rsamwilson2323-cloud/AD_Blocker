using System.Text.Json;

namespace AdBlocker.Core;

public sealed class Settings
{
    public bool DnsFilteringEnabled { get; set; } = true;          // blocking on/off (proxy keeps forwarding when off)
    public bool ApplySystemDns { get; set; } = false;             // route Windows DNS through AdBlocker. Off until the user consents.
    public string[] UpstreamDns { get; set; } = { "1.1.1.1", "9.9.9.9" };
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 53;
    public int ControlPort { get; set; } = 47653;
    public int UpstreamTimeoutMs { get; set; } = 2000;
    public bool NullIpMode { get; set; } = false;                  // false = NXDOMAIN, true = 0.0.0.0 / ::
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AdBlocker");

    public static Settings Load(string dir)
    {
        var path = Path.Combine(dir, "settings.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Opts) ?? new() : new(); }
        catch (JsonException) { return new(); }   // corrupt file -> safe defaults rather than a crash loop
    }

    public static void Save(string dir, Settings s)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json"); var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
        File.Move(tmp, path, overwrite: true);   // atomic replace: never leaves a half-written file
    }
}

/// <summary>A user-editable domain list (one per file) used for the personal allowlist and blocklist.</summary>
public sealed class UserList
{
    private readonly string _path; private readonly object _gate = new();
    public UserList(string path) => _path = path;

    public IReadOnlyList<string> Read()
    {
        lock (_gate) return File.Exists(_path) ? File.ReadAllLines(_path).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').ToList() : new();
    }
    public bool Add(string domain)
    {
        var d = RuleParser.NormalizeDomain(domain); if (d == null) return false;
        lock (_gate) { var all = Read().ToList(); if (!all.Contains(d)) { all.Add(d); Write(all); } }
        return true;
    }
    public void Remove(string domain)
    {
        var d = domain.Trim().ToLowerInvariant();
        lock (_gate) Write(Read().Where(x => x != d).ToList());
    }
    private void Write(List<string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp"; File.WriteAllLines(tmp, all); File.Move(tmp, _path, overwrite: true);
    }
}

public sealed record DnsStatusDto(bool Listening, bool FilteringEnabled, bool SystemDnsApplied, string[] Upstreams, string Listen);
public sealed record RulesDto(int Blocked, int Allowed, int RejectedLines);
public sealed record BrowserDto(bool Connected, DateTime? LastHeartbeatUtc, string? ExtensionVersion);
public sealed record StatusDto(string State, string? Error, long UptimeSeconds, string Version, DnsStatusDto Dns, StatsSnapshot Stats, RulesDto Rules, BrowserDto Browser);
public sealed record ListsDto(IReadOnlyList<string> Allow, IReadOnlyList<string> Block);
