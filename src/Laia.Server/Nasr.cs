using System.IO.Compression;
using System.Text;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// The FAA's NASR navigation data (nfdc.faa.gov 28-day subscription, CSV edition, free): fixes, navaids and airways,
/// downloaded once a cycle (the same 28-day cycle as the charts) and kept, to fly filed route strings.
/// </summary>
public sealed class NasrDb(ILogger<NasrDb> log)
{
    public NavDb? Nav { get; private set; }
    public string Status { get; private set; } = "not loaded";
    private string _loaded = "";

    private static readonly string[] Parts = { "FIX", "NAV", "AWY", "ATC" };
    /// <summary>Each towered airport's primary approach control facility (ATC_BASE: APCH_P_PROVIDER, e.g. MCO → F11), by ICAO and FAA id.</summary>
    private Dictionary<string, string> _approach = new(StringComparer.OrdinalIgnoreCase);

    public string? ApproachFacility(string icao) => _approach.TryGetValue(icao, out var f) ? f : null;

    /// <summary>The other airports worked by the same approach control as this one (its satellites and neighbours), ICAO codes.</summary>
    public List<string> SameApproach(string icao)
    {
        var f = ApproachFacility(icao);
        if (f is null) return new();
        return _approach.Where(kv => kv.Value.Equals(f, StringComparison.OrdinalIgnoreCase) && kv.Key.Length == 4 && !kv.Key.Equals(icao, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var (_, start) = AaupReader.Cycle(DateTime.UtcNow);
        var tag = start.ToString("dd_MMM_yyyy", System.Globalization.CultureInfo.InvariantCulture);
        if (_loaded == tag) return;
        var dir = Path.Combine(Paths.DataDir, "data", "nasr", tag);
        Directory.CreateDirectory(dir);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
            foreach (var part in Parts)
            {
                var file = Path.Combine(dir, part + ".zip");
                if (File.Exists(file)) continue;
                var url = $"https://nfdc.faa.gov/webContent/28DaySub/extra/{tag}_{part}_CSV.zip";
                log.LogInformation("Downloading FAA NASR {part} {url}", part, url);
                try
                {
                    var bytes = await http.GetByteArrayAsync(url, ct);
                    await File.WriteAllBytesAsync(file + ".tmp", bytes, ct);
                    File.Move(file + ".tmp", file, true);
                }
                catch (Exception ex) when (part == "ATC" && ex is not OperationCanceledException) { log.LogWarning("FAA NASR ATC data not available: {msg}", ex.Message); }
            }
            var nav = new NavDb();
            var approach = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in Parts)
            {
                if (!File.Exists(Path.Combine(dir, part + ".zip"))) continue;
                using var zip = ZipFile.OpenRead(Path.Combine(dir, part + ".zip"));
                foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
                {
                    var name = Path.GetFileName(e.FullName).ToUpperInvariant();
                    // FIX_BASE / NAV_BASE hold the positions; for airways, the base file (point string) or the segment file.
                    if (part == "FIX" && name.StartsWith("FIX_BASE")) nav.AddPoints(Rows(e), false, name);
                    else if (part == "NAV" && name.StartsWith("NAV_BASE")) nav.AddPoints(Rows(e), true, name);
                    else if (part == "AWY" && (name.StartsWith("AWY_BASE") || name.StartsWith("AWY_SEG"))) { if (nav.Airways == 0 || name.StartsWith("AWY_BASE")) nav.AddAirways(Rows(e), name); }
                    else if (part == "ATC" && name.StartsWith("ATC_BASE"))
                    {
                        string[]? head = null; int iId = -1, iIcao = -1, iApp = -1;
                        foreach (var r in Rows(e))
                        {
                            if (head is null)
                            {
                                head = r.Select(h => h.Trim().Trim('"').ToUpperInvariant()).ToArray();
                                iId = Array.IndexOf(head, "FACILITY_ID"); iIcao = Array.IndexOf(head, "ICAO_ID"); iApp = Array.IndexOf(head, "APCH_P_PROVIDER");
                                nav.Notes.Add($"{name}: id={(iId >= 0 ? "FACILITY_ID" : "?")}, icao={(iIcao >= 0 ? "ICAO_ID" : "?")}, approach={(iApp >= 0 ? "APCH_P_PROVIDER" : "?")}");
                                if (iId < 0 || iApp < 0) { nav.Notes.Add($"{name} header: {string.Join(",", head.Take(30))}"); break; }
                                continue;
                            }
                            if (r.Length <= Math.Max(iId, iApp)) continue;
                            var app = r[iApp].Trim();
                            if (app.Length is < 2 or > 5) continue;
                            var id = r[iId].Trim();
                            var icao = iIcao >= 0 && iIcao < r.Length && r[iIcao].Trim().Length == 4 ? r[iIcao].Trim() : id.Length == 3 && id.All(char.IsLetter) ? "K" + id : id;
                            approach[icao] = app;
                        }
                    }
                }
            }
            Nav = nav;
            _approach = approach;
            _loaded = tag;
            Status = $"{nav.Fixes:N0} fixes, {nav.Navaids:N0} navaids, {nav.Airways:N0} airways, approach control for {approach.Count:N0} airports (FAA NASR {tag.Replace('_', ' ')})";
            log.LogInformation("FAA NASR: {status}; columns: {notes}", Status, string.Join(" | ", nav.Notes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("FAA NASR download/read failed: {msg}", ex.Message);
            if (Nav is null) Status = "couldn't load the FAA NASR data: " + ex.Message;
        }
    }

    private static IEnumerable<string[]> Rows(ZipArchiveEntry e)
    {
        using var reader = new StreamReader(e.Open(), Encoding.Latin1);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var fields = new List<string>(); var sb = new StringBuilder(); var q = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (q) { if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (c == '"') q = false; else sb.Append(c); }
                else if (c == '"') q = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            yield return fields.ToArray();
        }
    }
}

public sealed class NasrUpdater(NasrDb db) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(40), ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try { await db.RefreshAsync(ct); } catch (OperationCanceledException) { break; } catch { }
            try { await Task.Delay(db.Nav is null ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(6), ct); } catch { break; }
        }
    }
}
