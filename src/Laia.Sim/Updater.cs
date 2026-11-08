using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Laia.Common;

namespace Laia.Sim;

/// <summary>
/// Keeps both PCs up to date from the sim PC: checks GitHub for a newer LAIA, offers it in the tray menu,
/// asks the ATC PC to update itself, and updates this app. Also keeps a copy of the ATC PC's log next to
/// this app's log, so nobody has to walk over to the other PC to fetch it.
/// </summary>
internal sealed class Updater
{
    private readonly HttpClient _github = Updates.NewClient();
    private readonly HttpClient _local = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Func<ServerInfo?> _server;
    private ReleaseInfo? _latest;
    private string? _serverVersion;
    private DateTime _lastCheck = DateTime.MinValue, _lastLogCopy = DateTime.MinValue, _lastSetupCopy = DateTime.MinValue;
    private bool _announced;

    public static string Current => Application.ProductVersion.Split('+')[0];
    public event Action? Changed;
    public event Action<string, string>? Notify; // title, text

    public Updater(Func<ServerInfo?> server) => _server = server;

    public string? Latest => _latest?.Version;
    public bool SimUpdate => _latest?.SimUrl is not null && Updates.IsNewer(_latest.Version, Current);
    public bool ServerUpdate => _latest?.ServerUrl is not null && _serverVersion is not null && Updates.IsNewer(_latest.Version, _serverVersion);
    public bool Available => SimUpdate || ServerUpdate;

    public string MenuText =>
        SimUpdate && ServerUpdate ? $"Update LAIA to {_latest!.Version} (both PCs)"
        : SimUpdate ? $"Update LAIA Sim to {_latest!.Version}"
        : ServerUpdate ? $"Update the ATC PC to {_latest!.Version}"
        : "LAIA is up to date";

    /// <summary>Called every few seconds by the tray heartbeat; does real work only now and then.</summary>
    private int _ticking;

    public async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try { await TickCoreAsync(); } finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private async Task TickCoreAsync()
    {
        var server = _server();
        if (server is not null) _serverVersion = server.Version;
        if (DateTime.UtcNow - _lastCheck > TimeSpan.FromHours(3)) await CheckAsync();
        if (server is not null && DateTime.UtcNow - _lastLogCopy > TimeSpan.FromSeconds(60)) { _lastLogCopy = DateTime.UtcNow; await CopyLogAsync(server, "server", "server.log"); }
        if (server is not null && DateTime.UtcNow - _lastSetupCopy > TimeSpan.FromMinutes(30)) { _lastSetupCopy = DateTime.UtcNow; await CopyLogAsync(server, "setup", "server-setup.log"); }
    }

    public async Task CheckAsync()
    {
        _lastCheck = DateTime.UtcNow;
        try
        {
            _latest = await Updates.LatestAsync(_github);
            if (Available)
            {
                Log.Write($"Update available: {_latest!.Version} (this PC {Current}, ATC PC {_serverVersion ?? "?"})");
                if (!_announced) { _announced = true; Notify?.Invoke("LAIA update available", MenuText + " — right-click the LAIA icon to install."); }
            }
        }
        catch (Exception ex) { Log.Write("Update check failed: " + ex.Message); }
        Changed?.Invoke();
    }

    /// <summary>Updates the ATC PC (if behind), then this app (if behind). This app closes while its installer runs.</summary>
    public async Task RunAsync()
    {
        if (_latest is null) await CheckAsync();
        if (_latest is null) { Notify?.Invoke("LAIA", "Couldn't reach GitHub to check for updates."); return; }
        var server = _server();
        if (ServerUpdate && server is not null)
        {
            try
            {
                using var resp = await _local.PostAsync(server.BaseUrl + "/api/update/server", null);
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                var msg = json.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                Log.Write("ATC PC update: " + msg);
                Notify?.Invoke("Updating the ATC PC", msg);
            }
            catch (Exception ex)
            {
                Log.Write("ATC PC update failed: " + ex.Message);
                Notify?.Invoke("LAIA", "Couldn't start the update on the ATC PC: " + ex.Message);
            }
        }
        if (SimUpdate)
        {
            try
            {
                var file = Path.Combine(Path.GetTempPath(), $"LAIA-Sim-Setup-{_latest.Version}.exe");
                Notify?.Invoke("Updating LAIA Sim", $"Downloading {_latest.Version}…");
                await Updates.DownloadAsync(_github, _latest.SimUrl!, file, _latest.SimSize);
                Log.Write($"Starting the LAIA Sim {_latest.Version} installer");
                // The installer closes this app, installs, and starts the new version.
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Write("LAIA Sim update failed: " + ex.Message);
                Notify?.Invoke("LAIA", "Couldn't download the update: " + ex.Message);
            }
        }
        else if (!ServerUpdate) Notify?.Invoke("LAIA", $"Both PCs are up to date ({_latest.Version}).");
    }

    private async Task CopyLogAsync(ServerInfo server, string name, string fileName)
    {
        try
        {
            var text = await _local.GetStringAsync($"{server.BaseUrl}/api/logs/{name}");
            if (text.Length == 0) return;
            Directory.CreateDirectory(Log.Dir);
            await File.WriteAllTextAsync(Path.Combine(Log.Dir, fileName), text);
        }
        catch { /* older server without the log endpoint, or briefly unreachable */ }
    }
}
