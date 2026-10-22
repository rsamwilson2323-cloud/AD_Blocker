using System.Diagnostics;
using System.Net;
using AdBlocker.Core;

namespace AdBlocker.Service;

/// <summary>Composition root: loads settings and rules, runs the DNS proxy and the local control API, tracks state.</summary>
public sealed class AdEngine
{
    public const string Version = "0.2.3";
    private readonly ILogger<AdEngine> _log;
    private readonly DateTime _started = DateTime.UtcNow;
    private DnsProxy? _proxy; private ControlApi? _api;
    private readonly SemaphoreSlim _netGate = new(1, 1);

    public string DataDir { get; } = SettingsStore.DataDir;
    public Settings Settings { get; private set; } = new();
    public RuleEngine Rules { get; } = new();
    public Stats Stats { get; } = new();
    public DnsCache Cache { get; } = new();
    public UserList UserAllow { get; }
    public UserList UserBlock { get; }
    public string State { get; private set; } = "Disabled";
    public string? Error { get; private set; }
    public int RejectedRuleLines { get; private set; }
    public bool SystemDnsApplied { get; private set; }
    public DateTime? LastExtHeartbeat { get; set; }
    public string? ExtVersion { get; set; }
    public long UptimeSeconds => (long)(DateTime.UtcNow - _started).TotalSeconds;
    public bool ProxyListening => _proxy?.Listening == true;

    public AdEngine(ILogger<AdEngine> log)
    {
        _log = log;
        UserAllow = new UserList(Path.Combine(DataDir, "user-allow.txt"));
        UserBlock = new UserList(Path.Combine(DataDir, "custom-block.txt"));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(DataDir);
        Settings = SettingsStore.Load(DataDir);
        ReloadRules();

        _api = new ControlApi(this, _log);
        try { _api.Start(); }
        catch (Exception ex) { _log.LogError(ex, "Control API failed to start on port {Port}", Settings.ControlPort); }

        try
        {
            var upstreams = Settings.UpstreamDns.Select(a => new IPEndPoint(IPAddress.Parse(a), 53)).ToList();
            _proxy = new DnsProxy(Rules, Cache, Stats, new DnsProxyOptions
            {
                ListenAddress = IPAddress.Parse(Settings.ListenAddress), Port = Settings.ListenPort, Upstreams = upstreams,
                UpstreamTimeout = TimeSpan.FromMilliseconds(Settings.UpstreamTimeoutMs), NullIpMode = Settings.NullIpMode,
                FilteringEnabled = () => Settings.DnsFilteringEnabled
            });
            _proxy.Faulted += OnProxyFaulted;
            _proxy.Start();
            State = "Active";
            _log.LogInformation("DNS proxy listening on {Ep}", _proxy.LocalEndPoint);

            SystemDnsApplied = await NetworkConfig.IsAppliedAsync(DataDir, ct);
            if (Settings.ApplySystemDns && !SystemDnsApplied) await SetSystemDnsAsync(true, ct);
        }
        catch (Exception ex)
        {
            // Never claim protection if the engine could not start, and never touch system DNS in that state.
            State = "Error"; Error = $"DNS proxy could not start: {ex.Message}";
            _log.LogError(ex, "DNS proxy failed to start");
        }
    }

    /// <summary>Stop does NOT restore DNS by itself (user request). the UI handles the requested stop/restore flow; see README.</summary>
    public Task StopAsync() { _proxy?.Dispose(); _api?.Dispose(); State = "Disabled"; return Task.CompletedTask; }

    private void OnProxyFaulted(Exception ex)
    {
        // Fail-safe: if the proxy dies unexpectedly while system DNS points at it, restore DNS so the machine isn't left offline.
        State = "Error"; Error = "DNS proxy stopped unexpectedly: " + ex.Message;
        _log.LogError(ex, "DNS proxy faulted");
        _ = SetSystemDnsAsync(false, CancellationToken.None);
    }

    public void ReloadRules()
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "rules");
        IEnumerable<string> L(string dir, string f) => File.Exists(Path.Combine(dir, f)) ? File.ReadAllLines(Path.Combine(dir, f)) : Array.Empty<string>();
        var report = Rules.Load(
            new (string, RuleCategory, IEnumerable<string>)[]
            {
                ("ads-domains.txt", RuleCategory.Advertising, L(baseDir, "ads-domains.txt")),
                ("popup-domains.txt", RuleCategory.Advertising, L(baseDir, "popup-domains.txt")),
                ("tracking-domains.txt", RuleCategory.Tracking, L(baseDir, "tracking-domains.txt")),
                ("custom-block.txt", RuleCategory.Custom, UserBlock.Read()),
            },
            new (string, IEnumerable<string>)[] { ("allowlist.txt", L(baseDir, "allowlist.txt")), ("user-allow.txt", UserAllow.Read()) });
        RejectedRuleLines = report.Rejected;
        foreach (var i in report.Issues.Take(10)) _log.LogWarning("Rule rejected {Src}:{Line} {Reason}", i.Source, i.Line, i.Reason);
        Cache.Clear();   // cached answers may now be stale relative to the rules
    }

    public void SetFiltering(bool on)
    {
        Settings.DnsFilteringEnabled = on; SettingsStore.Save(DataDir, Settings);
        Stats.Log("INFO", "-", "DNS filtering " + (on ? "enabled" : "disabled"));
    }

    public async Task<(bool ok, string message)> SetSystemDnsAsync(bool apply, CancellationToken ct)
    {
        await _netGate.WaitAsync(ct);
        try
        {
            if (apply && State != "Active") return (false, "Refusing to point Windows DNS at AdBlocker while the engine is not Active.");
            var (code, output) = await NetworkConfig.RunAsync(apply ? "Apply" : "Restore", DataDir, ct);
            SystemDnsApplied = await NetworkConfig.IsAppliedAsync(DataDir, ct);
            if (code == 0) { Settings.ApplySystemDns = apply; SettingsStore.Save(DataDir, Settings); }
            Stats.Log(code == 0 ? "INFO" : "ERROR", "-", $"System DNS {(apply ? "apply" : "restore")}: exit {code}");
            return (code == 0, output.Trim());
        }
        finally { _netGate.Release(); }
    }

    public StatusDto BuildStatus()
    {
        bool connected = LastExtHeartbeat is { } t && DateTime.UtcNow - t < TimeSpan.FromMinutes(3);
        return new StatusDto(State, Error, UptimeSeconds, Version,
            new DnsStatusDto(ProxyListening, Settings.DnsFilteringEnabled, SystemDnsApplied, Settings.UpstreamDns, $"{Settings.ListenAddress}:{Settings.ListenPort}"),
            Stats.Snapshot(), new RulesDto(Rules.BlockCount, Rules.AllowCount, RejectedRuleLines),
            new BrowserDto(connected, LastExtHeartbeat, ExtVersion));
    }
}

public static class NetworkConfig
{
    /// <summary>Runs our own Ad-Network.ps1 (shipped next to the service). No downloaded or user-supplied script is ever executed.</summary>
    public static async Task<(int code, string output)> RunAsync(string action, string dataDir, CancellationToken ct)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "Ad-Network.ps1");
        if (!File.Exists(script)) return (-1, "Ad-Network.ps1 not found at " + script);
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", action, "-DataDir", dataDir }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct); to.CancelAfter(TimeSpan.FromSeconds(60));
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync(to.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-2, "timed out"); }
        return (p.ExitCode, (await so) + (await se));
    }

    public static async Task<bool> IsAppliedAsync(string dataDir, CancellationToken ct)
    {
        var (code, output) = await RunAsync("Status", dataDir, ct);
        return code == 0 && output.Contains("\"applied\":true", StringComparison.OrdinalIgnoreCase);
    }
}
