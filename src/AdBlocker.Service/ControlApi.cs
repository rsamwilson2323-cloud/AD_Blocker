using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AdBlocker.Core;

namespace AdBlocker.Service;

/// <summary>
/// Local control API on 127.0.0.1 only.
///  - GET /api/status is unauthenticated (counters only, no browsing data) so Run.bat/diagnostics can read it.
///  - Everything that reads the activity log or changes state requires the X-Ad-Token header. The token file is
///    created with an ACL granting access only to SYSTEM and Administrators (so a normal web page or user-level
///    process cannot read it). No CORS headers are ever sent, so web pages cannot read responses cross-origin.
///  - POST /api/ext/heartbeat is accepted only with an Origin of chrome-extension://... and can only add counters.
/// </summary>
public sealed class ControlApi : IDisposable
{
    private readonly AdEngine _e; private readonly ILogger _log; private readonly HttpListener _l = new();
    private readonly string _tokenPath; private byte[] _token = Array.Empty<byte>();
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public ControlApi(AdEngine e, ILogger log) { _e = e; _log = log; _tokenPath = Path.Combine(e.DataDir, "api.token"); }

    public void Start()
    {
        CreateToken();
        _l.Prefixes.Add($"http://127.0.0.1:{_e.Settings.ControlPort}/");
        _l.Start();
        _ = Task.Run(Loop);
    }

    private void CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var text = Convert.ToHexString(bytes);
        var fs = new FileSecurity();
        fs.SetAccessRuleProtection(true, false);   // drop inherited permissions (which include "Users: read")
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            fs.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        if (File.Exists(_tokenPath)) File.Delete(_tokenPath);
        using (var s = FileSystemAclExtensions.Create(new FileInfo(_tokenPath), FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, fs))
            s.Write(Encoding.ASCII.GetBytes(text));
        _token = Encoding.ASCII.GetBytes(text);
    }

    private async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch { break; }
            _ = Task.Run(() => Handle(c));
        }
    }

    private bool Authed(HttpListenerRequest r)
    {
        var h = r.Headers["X-Ad-Token"];
        return h != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(h), _token);
    }

    private static async Task<JsonElement> Body(HttpListenerRequest r)
    {
        if (r.ContentLength64 > 4096) throw new InvalidDataException("body too large");
        using var sr = new StreamReader(r.InputStream, Encoding.UTF8);
        var t = await sr.ReadToEndAsync();
        return t.Length == 0 ? default : JsonDocument.Parse(t).RootElement.Clone();
    }

    private async Task Handle(HttpListenerContext c)
    {
        var r = c.Request; var path = r.Url!.AbsolutePath; object? result = null; int code = 200;
        try
        {
            if (r.Url.Host is not ("127.0.0.1" or "localhost")) { code = 403; result = new { error = "bad host" }; }   // DNS-rebinding guard
            else if (path == "/api/status" && r.HttpMethod == "GET") result = _e.BuildStatus();
            else if (path == "/api/ext/heartbeat" && r.HttpMethod == "POST")
            {
                if (r.Headers["Origin"]?.StartsWith("chrome-extension://", StringComparison.Ordinal) != true) { code = 403; result = new { error = "origin" }; }
                else
                {
                    var b = await Body(r);
                    _e.Stats.AddBrowserReport(Num(b, "ruleMatches"), Num(b, "ytPruned"), Num(b, "ytAccelerated"));
                    _e.LastExtHeartbeat = DateTime.UtcNow;
                    _e.ExtVersion = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("version", out var v) ? v.GetString()?[..Math.Min(16, v.GetString()!.Length)] : null;
                    result = new { ok = true };
                }
            }
            else if (!Authed(r)) { code = 401; result = new { error = "missing or invalid token" }; }
            else if (path == "/api/log" && r.HttpMethod == "GET") result = _e.Stats.Recent(int.TryParse(r.QueryString["n"], out var n) ? n : 200);
            else if (path == "/api/lists" && r.HttpMethod == "GET") result = new ListsDto(_e.UserAllow.Read(), _e.UserBlock.Read());
            else if (path == "/api/reload" && r.HttpMethod == "POST") { _e.ReloadRules(); result = new { ok = true }; }
            else if (path is "/api/allow" or "/api/block")
            {
                var list = path == "/api/allow" ? _e.UserAllow : _e.UserBlock;
                var b = await Body(r); var d = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("domain", out var dv) ? dv.GetString() ?? "" : "";
                if (r.HttpMethod == "POST") { if (!list.Add(d)) { code = 400; result = new { error = "invalid domain" }; } else { _e.ReloadRules(); result = new { ok = true }; } }
                else if (r.HttpMethod == "DELETE") { list.Remove(d); _e.ReloadRules(); result = new { ok = true }; }
                else code = 405;
            }
            else if (path == "/api/dns/filtering" && r.HttpMethod == "POST")
            { var b = await Body(r); _e.SetFiltering(b.TryGetProperty("enabled", out var en) && en.GetBoolean()); result = new { ok = true }; }
            else if (path == "/api/system-dns" && r.HttpMethod == "POST")
            { var b = await Body(r); var (ok, msg) = await _e.SetSystemDnsAsync(b.TryGetProperty("apply", out var ap) && ap.GetBoolean(), CancellationToken.None); code = ok ? 200 : 500; result = new { ok, message = msg }; }
            else { code = 404; result = new { error = "not found" }; }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        { code = 400; result = new { error = "bad request" }; }
        catch (Exception ex) { _log.LogError(ex, "API error"); code = 500; result = new { error = "internal error" }; }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(result ?? new { }, J);
        c.Response.StatusCode = code; c.Response.ContentType = "application/json"; c.Response.ContentLength64 = bytes.Length;
        await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
    }

    private static long Num(JsonElement b, string k) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : 0;
    public void Dispose() { try { _l.Close(); } catch { } }
}
