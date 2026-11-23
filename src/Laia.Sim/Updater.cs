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
    public event Action? ExitRequested;

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
        if (server is not null && DateTime.UtcNow - _lastLearnedCopy > TimeSpan.FromMinutes(15)) { _lastLearnedCopy = DateTime.UtcNow; await CopyLearnedAsync(server); }
        if (server is not null && DateTime.UtcNow - _lastTracksCopy > TimeSpan.FromMinutes(5)) { _lastTracksCopy = DateTime.UtcNow; await CopyTracksAsync(server); }
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
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LAIA", "updates",
                    $"LAIA-Sim-Setup-{_latest.Version}-{DateTime.Now:HHmmss}.exe");
                Notify?.Invoke("Updating LAIA Sim", $"Downloading {_latest.Version}…");
                await Updates.DownloadAsync(_github, _latest.SimUrl!, file, _latest.SimSize);
                Log.Write($"Starting the LAIA Sim {_latest.Version} installer");
                // The installer closes this app, installs, and starts the new version. A freshly downloaded file can be
                // held briefly by the virus scanner, so try for a few seconds.
                for (var i = 0; ; i++)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                        StartAgainLater();
                        Log.Write("Closing LAIA Sim so the installer can replace it");
                        ExitRequested?.Invoke();
                        break;
                    }
                    catch (Exception) when (i < 10) { await Task.Delay(1000); }
                }
            }
            catch (Exception ex)
            {
                Log.Write("LAIA Sim update failed: " + ex);
                Notify?.Invoke("LAIA", "Couldn't install the LAIA Sim update: " + ex.Message + " Opening the download page instead.");
                try { Process.Start(new ProcessStartInfo("https://github.com/dlunior/LAIA/releases") { UseShellExecute = true }); } catch { }
            }
        }
        else if (!ServerUpdate) Notify?.Invoke("LAIA", $"Both PCs are up to date ({_latest.Version}).");
    }

    /// <summary>
    /// If the installer doesn't finish (a Windows prompt left unanswered, or cancelled), LAIA Sim would stay closed.
    /// A hidden Windows PowerShell waits five minutes and starts LAIA Sim again if it isn't running by then.
    /// </summary>
    private static void StartAgainLater()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var cmd = $"Start-Sleep -Seconds 300; if (-not (Get-Process LaiaSim -ErrorAction SilentlyContinue)) {{ Start-Process -FilePath '{exe.Replace("'", "''")}' }}";
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{cmd}\"")
                { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex) { Log.Write("Couldn't set up the restart after the update: " + ex.Message); }
    }

    private DateTime _lastLearnedCopy = DateTime.MinValue;
    private string? _lastLogLine;

    private async Task CopyLogAsync(ServerInfo server, string name, string fileName)
    {
        try
        {
            var text = await _local.GetStringAsync($"{server.BaseUrl}/api/logs/{name}");
            if (text.Length == 0) return;
            Directory.CreateDirectory(Log.Dir);
            await File.WriteAllTextAsync(Path.Combine(Log.Dir, fileName), text);
            if (name == "server") ArchiveServerLog(text);
        }
        catch { /* older server without the log endpoint, or briefly unreachable */ }
    }

    /// <summary>
    /// The server log copy only holds its last 3 MB, so an overnight run's start was lost. New lines are also appended to
    /// a daily archive (logs\server-YYYY-MM-DD.log, kept 7 days), so a whole session can be read afterwards.
    /// </summary>
    private void ArchiveServerLog(string text)
    {
        try
        {
            var lines = text.Split('\n');
            var start = 0;
            if (_lastLogLine is not null)
            {
                var i = Array.LastIndexOf(lines, _lastLogLine);
                start = i >= 0 ? i + 1 : 0;
            }
            else
            {
                // First copy since LAIA Sim started: carry on after the last line already archived today, if any.
                var today = Path.Combine(Log.Dir, "logs", $"server-{DateTime.Now:yyyy-MM-dd}.log");
                if (File.Exists(today))
                {
                    var last = File.ReadLines(today).LastOrDefault(l => l.Length > 0);
                    var i = last is null ? -1 : Array.LastIndexOf(lines, last);
                    start = i >= 0 ? i + 1 : 0;
                }
            }
            var fresh = lines.Skip(start).Where(l => l.Length > 0).ToList();
            if (lines.LastOrDefault(l => l.Length > 0) is { } lastLine) _lastLogLine = lastLine;
            if (fresh.Count == 0) return;
            var dir = Path.Combine(Log.Dir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllLines(Path.Combine(dir, $"server-{DateTime.Now:yyyy-MM-dd}.log"), fresh);
            foreach (var old in Directory.GetFiles(dir, "server-*.log").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7))) File.Delete(old);
        }
        catch { }
    }

    private DateTime _lastTracksCopy = DateTime.MinValue;

    /// <summary>
    /// Copies LAIA's own track recording (an hour a file, the hours gone gzipped) to tracks\ beside the logs, so the
    /// log reviews can look at what each aircraft really flew; the current hour's file again every 5 minutes. Kept 7 days.
    /// </summary>
    private async Task CopyTracksAsync(ServerInfo server)
    {
        try
        {
            var list = await _local.GetStringAsync($"{server.BaseUrl}/api/tracks");
            using var doc = System.Text.Json.JsonDocument.Parse(list);
            var dir = Path.Combine(Log.Dir, "tracks");
            Directory.CreateDirectory(dir);
            var names = new HashSet<string>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var name = e.GetProperty("name").GetString();
                if (string.IsNullOrEmpty(name) || !name.StartsWith("tracks-")) continue;
                names.Add(name);
                var mod = e.GetProperty("modified").GetDateTime();
                var path = Path.Combine(dir, name);
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= mod) continue;
                var bytes = await _local.GetByteArrayAsync($"{server.BaseUrl}/api/tracks/file/{name}");
                await File.WriteAllBytesAsync(path, bytes);
                File.SetLastWriteTimeUtc(path, mod);
                // The hour's plain copy goes once its gzipped one is here.
                if (name.EndsWith(".gz")) { var plain = Path.Combine(dir, name[..^3]); if (File.Exists(plain)) File.Delete(plain); }
            }
            foreach (var f in Directory.GetFiles(dir, "tracks-*"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
        }
        catch { /* older server, or briefly unreachable */ }
    }

    /// <summary>Copies the learned real-traffic profiles of the airports in use (when changed) to learned\ICAO.json.</summary>
    private async Task CopyLearnedAsync(ServerInfo server)
    {
        try
        {
            var list = await _local.GetStringAsync($"{server.BaseUrl}/api/learned");
            using var doc = System.Text.Json.JsonDocument.Parse(list);
            var dir = Path.Combine(Log.Dir, "learned");
            Directory.CreateDirectory(dir);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var icao = e.GetProperty("icao").GetString();
                if (string.IsNullOrEmpty(icao)) continue;
                var mod = e.GetProperty("modified").GetDateTime();
                var path = Path.Combine(dir, icao + ".json");
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= mod) continue;
                var bytes = await _local.GetByteArrayAsync($"{server.BaseUrl}/api/learned/{icao}");
                await File.WriteAllBytesAsync(path, bytes);
            }
        }
        catch { /* older server, or briefly unreachable */ }
    }
}
