using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// The FAA Preferred Routes database (www.fly.faa.gov/rmt/data_file/prefroutes_db.csv, about 1.4 MB, free),
/// downloaded at most once a week and kept, so each flight gets the STAR and SID it would really file.
/// </summary>
public sealed class RoutesDb(ILogger<RoutesDb> log)
{
    private const string Url = "https://www.fly.faa.gov/rmt/data_file/prefroutes_db.csv";
    public PreferredRoutes? Routes { get; private set; }
    public string Status { get; private set; } = "not loaded";
    private static string File_ => Path.Combine(Paths.DataDir, "data", "prefroutes_db.csv");

    public async Task RefreshAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        if (Routes is null && File.Exists(File_)) Load();
        if (File.Exists(File_) && DateTime.UtcNow - File.GetLastWriteTimeUtc(File_) < TimeSpan.FromDays(7)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
            log.LogInformation("Downloading the FAA preferred routes {url}", Url);
            var bytes = await http.GetByteArrayAsync(Url, ct);
            await File.WriteAllBytesAsync(File_ + ".tmp", bytes, ct);
            File.Move(File_ + ".tmp", File_, true);
            Load();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Preferred routes download failed: {msg}", ex.Message);
            if (Routes is null) Status = "couldn't download the FAA preferred routes: " + ex.Message;
        }
    }

    private void Load()
    {
        try
        {
            Routes = PreferredRoutes.Parse(File.ReadLines(File_));
            Status = $"{Routes.Count:N0} FAA preferred routes";
            log.LogInformation("FAA preferred routes: {n}", Routes.Count);
        }
        catch (Exception ex) { Status = "couldn't read the FAA preferred routes: " + ex.Message; }
    }
}

public sealed class RoutesUpdater(RoutesDb db) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try { await db.RefreshAsync(ct); } catch (OperationCanceledException) { break; } catch { }
            try { await Task.Delay(db.Routes is null ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(12), ct); } catch { break; }
        }
    }
}
