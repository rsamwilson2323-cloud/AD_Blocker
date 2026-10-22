using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AdBlocker.Core;

namespace AdBlocker.App;

public partial class MainWindow : Window
{
    private const string ServiceName = "AdBlocker";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://127.0.0.1:47653/"), Timeout = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;
    private bool _serviceRunning;
    private long _uptime;

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshStatus();
        Loaded += async (_, _) => await RefreshStatus();
        Closed += (_, _) => { _timer.Stop(); _http.Dispose(); };
        _timer.Start();
    }

    private async Task RefreshStatus()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            string serviceState = "Not installed";
            try { using var sc = new ServiceController(ServiceName); serviceState = sc.Status.ToString(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            _serviceRunning = serviceState == "Running";
            ToggleBtn.Content = _serviceRunning ? "Stop protection" : "Start protection";
            ToggleBtn.IsEnabled = serviceState != "Not installed" && serviceState != "StartPending" && serviceState != "StopPending";

            StatusDto? status = null;
            try { status = await _http.GetFromJsonAsync<StatusDto>("api/status", JsonOptions); } catch { }
            if (!_serviceRunning || status is null)
            {
                SetState(_serviceRunning ? "Engine not responding" : serviceState,
                    _serviceRunning ? "The service is running, but its local status endpoint did not answer." :
                    serviceState == "Not installed" ? "Run Run.bat once as administrator to install protection." : "Protection is currently stopped.",
                    _serviceRunning ? "#F4B942" : "#8B94B0");
                TimerText.Text = "00:00:00";
                if (!_serviceRunning) { BlockedText.Text = "0"; DnsText.Text = "System DNS: not routed through AdBlocker"; }
                BrowserText.Text = "Chrome extension status is available while the engine is running.";
                return;
            }

            _uptime = Math.Max(0, status.UptimeSeconds);
            TimerText.Text = TimeSpan.FromSeconds(_uptime).ToString(_uptime >= 86400 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss");
            BlockedText.Text = (status.Stats.BlockedTotal + status.Stats.BrowserRuleMatches).ToString("N0");
            SetState(status.State == "Active" ? "Protection is on" : status.State,
                status.State == "Active" ? "Background engine is running" : status.Error ?? "Engine is not active",
                status.State == "Active" ? "#36D399" : "#F4B942");
            DnsText.Text = status.Dns.SystemDnsApplied
                ? "System DNS: protected · Windows DNS requests pass through AdBlocker"
                : "System DNS: not enabled · only browser rules and configured DNS filtering apply";
            BrowserText.Text = status.Browser.Connected
                ? $"Chrome extension connected · version {status.Browser.ExtensionVersion ?? "unknown"}"
                : "Chrome extension not detected. Load the extension folder in chrome://extensions.";
        }
        finally { _busy = false; }
    }

    private void SetState(string title, string detail, string color)
    {
        StateText.Text = title;
        StateDetail.Text = detail;
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        ToggleBtn.IsEnabled = false;
        try
        {
            using var service = new ServiceController(ServiceName);
            if (_serviceRunning)
            {
                // Restore DNS BEFORE stopping the local resolver, otherwise internet lookups can fail.
                var networkScript = Path.Combine(AppContext.BaseDirectory, "scripts", "Ad-Network.ps1");
                if (!File.Exists(networkScript))
                {
                    MessageBox.Show("The DNS restore helper is missing. Protection was left running so your internet connection is not put at risk. Re-extract the full ZIP and run Run.bat again.", "AdBlocker", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var restoreCode = await RunPowerShell(networkScript, "Restore");
                if (restoreCode != 0)
                {
                    MessageBox.Show("AdBlocker could not restore the original DNS settings, so protection was left running to avoid interrupting internet access.\n\nCheck permissions and try again.", "AdBlocker", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Running) service.Stop();
                await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)));
            }
            else
            {
                service.Start();
                await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)));
                // Reapply DNS only if the user previously enabled system-wide routing in settings.
                var settingsPath = Path.Combine(SettingsStore.DataDir, "settings.json");
                var shouldApplyDns = false;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                    if (doc.RootElement.TryGetProperty("applySystemDns", out var v)) shouldApplyDns = v.GetBoolean();
                }
                catch { }
                if (shouldApplyDns)
                {
                    var networkScript = Path.Combine(AppContext.BaseDirectory, "scripts", "Ad-Network.ps1");
                    var applyCode = await RunPowerShell(networkScript, "Apply");
                    if (applyCode != 0)
                        MessageBox.Show("Protection started, but Windows DNS could not be routed through AdBlocker. Browser extension filtering may still work.", "AdBlocker", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not change protection state.\n\n" + ex.Message, "AdBlocker", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ToggleBtn.IsEnabled = true;
            await RefreshStatus();
        }
    }

    private static async Task<int> RunPowerShell(string script, string action)
    {
        if (!File.Exists(script)) return 0; // no DNS routing helper bundled means no system DNS change to undo
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", action }
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PowerShell.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private void OnOpenExtensions(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("chrome.exe", "chrome://extensions") { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo("https://chrome.google.com/webstore") { UseShellExecute = true }); }
    }
}
