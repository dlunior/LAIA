using System.Diagnostics;
using Laia.Common;

namespace Laia.Server;

/// <summary>
/// Lets the sim PC update this PC: checks GitHub for a newer LAIA release, downloads the server installer
/// and runs it silently. The installer stops this service, installs the new version and starts it again.
/// </summary>
public sealed class Updater(ServerState state, ILogger<Updater> log)
{
    private readonly HttpClient _http = Updates.NewClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ReleaseInfo? _latest;
    private DateTime _checked = DateTime.MinValue;
    public string Status { get; private set; } = "";
    public bool Updating { get; private set; }

    public async Task<ReleaseInfo?> LatestAsync(bool force = false)
    {
        if (!force && _latest is not null && DateTime.UtcNow - _checked < TimeSpan.FromMinutes(20)) return _latest;
        try
        {
            _latest = await Updates.LatestAsync(_http);
            _checked = DateTime.UtcNow;
        }
        catch (Exception ex) { Status = "Couldn't check for updates: " + ex.Message; }
        return _latest;
    }

    public async Task<(bool ok, string message)> UpdateAsync()
    {
        if (!await _gate.WaitAsync(0)) return (false, "An update is already running.");
        try
        {
            var latest = await LatestAsync(force: true);
            if (latest?.ServerUrl is null) return (false, Status.Length > 0 ? Status : "No LAIA release found on GitHub.");
            if (!Updates.IsNewer(latest.Version, state.Version)) return (true, $"LAIA Server is already up to date ({state.Version}).");
            Updating = true;
            Status = $"Downloading LAIA Server {latest.Version}…";
            log.LogInformation("Update: downloading LAIA Server {v}", latest.Version);
            var dir = Path.Combine(Paths.DataDir, "updates");
            var file = Path.Combine(dir, $"LAIA-Server-Setup-{latest.Version}.exe");
            await Updates.DownloadAsync(_http, latest.ServerUrl, file, latest.ServerSize);
            foreach (var old in Directory.GetFiles(dir, "LAIA-Server-Setup-*.exe").Where(f => f != file)) { try { File.Delete(old); } catch { } }
            Status = $"Installing LAIA Server {latest.Version}… (the server restarts; back in a few minutes)";
            log.LogInformation("Update: starting the installer for {v}; this service will restart", latest.Version);
            Process.Start(new ProcessStartInfo(file, "/silent") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir });
            return (true, $"Updating the ATC PC to {latest.Version}. It restarts and is back in a few minutes.");
        }
        catch (Exception ex)
        {
            Updating = false;
            Status = "Update failed: " + ex.Message;
            log.LogWarning("Update failed: {msg}", ex.Message);
            return (false, Status);
        }
        finally { _gate.Release(); }
    }
}
