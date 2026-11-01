using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Laia.Server;

/// <summary>One runway end (the threshold you land on / take off from).</summary>
public sealed record RunwayEnd(string Airport, string Id, double TrueHeading, double Lat, double Lon, double ElevationFt, double LengthFt,
    double OppLat, double OppLon);

/// <summary>US runway ends from the FAA's NASR airport file, refreshed each 28-day cycle like the frequencies.</summary>
public sealed class RunwayDb(ILogger<RunwayDb> log)
{
    private Dictionary<string, List<RunwayEnd>> _byAirport = new(StringComparer.OrdinalIgnoreCase);
    public string Status { get; private set; } = "not loaded";
    public bool Loaded => _byAirport.Count > 0;
    private static string DataDir => Path.Combine(Paths.DataDir, "data");

    public async Task RefreshAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(DataDir);
        var cached = Directory.GetFiles(DataDir, "RWY-*.csv").OrderDescending().FirstOrDefault();
        if (cached is not null && !Loaded) Load(cached);
        var cycle = FrequencyDb.CurrentCycle(DateTime.UtcNow);
        for (var back = 0; back < 2; back++)
        {
            var c = cycle.AddDays(-28 * back);
            var target = Path.Combine(DataDir, $"RWY-{c:yyyy-MM-dd}.csv");
            if (File.Exists(target)) { if (!Loaded || cached != target) Load(target); return; }
            var url = $"https://nfdc.faa.gov/webContent/28DaySub/extra/{c.ToString("dd_MMM_yyyy", CultureInfo.InvariantCulture)}_APT_CSV.zip";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
                log.LogInformation("Downloading FAA runways {url}", url);
                using var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                using var ms = new MemoryStream();
                await (await resp.Content.ReadAsStreamAsync(ct)).CopyToAsync(ms, ct);
                ms.Position = 0;
                using var zip = new ZipArchive(ms);
                var ends = zip.Entries.FirstOrDefault(e => e.Name.Equals("APT_RWY_END.csv", StringComparison.OrdinalIgnoreCase));
                var rwys = zip.Entries.FirstOrDefault(e => e.Name.Equals("APT_RWY.csv", StringComparison.OrdinalIgnoreCase));
                if (ends is null || rwys is null) continue;
                // Keep one combined file: the runway lengths are merged in while loading.
                ends.ExtractToFile(target + ".tmp", true);
                rwys.ExtractToFile(Path.Combine(DataDir, $"RWYLEN-{c:yyyy-MM-dd}.csv"), true);
                File.Move(target + ".tmp", target, true);
                foreach (var old in Directory.GetFiles(DataDir, "RWY*-*.csv").Where(f => !f.Contains(c.ToString("yyyy-MM-dd")))) try { File.Delete(old); } catch { }
                Load(target);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("FAA runway download failed: {msg}", ex.Message); }
        }
    }

    private void Load(string path)
    {
        try
        {
            var lenFile = path.Replace("RWY-", "RWYLEN-");
            var lengths = new Dictionary<(string, string), double>();
            if (File.Exists(lenFile))
            {
                var lr = Csv.Read(lenFile).ToList();
                int la = Array.IndexOf(lr[0], "ARPT_ID"), lid = Array.IndexOf(lr[0], "RWY_ID"), llen = Array.IndexOf(lr[0], "RWY_LEN");
                foreach (var r in lr.Skip(1))
                    if (r.Length > llen && double.TryParse(r[llen], NumberStyles.Float, CultureInfo.InvariantCulture, out var l)) lengths[(r[la], r[lid])] = l;
            }
            var rows = Csv.Read(path).ToList();
            var h = rows[0];
            int iA = Array.IndexOf(h, "ARPT_ID"), iR = Array.IndexOf(h, "RWY_ID"), iE = Array.IndexOf(h, "RWY_END_ID"), iT = Array.IndexOf(h, "TRUE_ALIGNMENT"),
                iLat = Array.IndexOf(h, "LAT_DECIMAL"), iLon = Array.IndexOf(h, "LONG_DECIMAL"), iEl = Array.IndexOf(h, "RWY_END_ELEV");
            var raw = new List<(string apt, string rwy, string end, double hdg, double lat, double lon, double elev)>();
            foreach (var r in rows.Skip(1))
            {
                if (r.Length <= iEl) continue;
                if (!double.TryParse(r[iLat], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                    !double.TryParse(r[iLon], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
                if (r[iR].StartsWith('H')) continue; // helipads
                double.TryParse(r[iT], NumberStyles.Float, CultureInfo.InvariantCulture, out var hdg);
                double.TryParse(r[iEl], NumberStyles.Float, CultureInfo.InvariantCulture, out var elev);
                raw.Add((r[iA], r[iR], r[iE], hdg, lat, lon, elev));
            }
            var by = new Dictionary<string, List<RunwayEnd>>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in raw.GroupBy(x => (x.apt, x.rwy)))
            {
                var ends = g.ToList();
                if (ends.Count != 2) continue;
                foreach (var (me, other) in new[] { (ends[0], ends[1]), (ends[1], ends[0]) })
                {
                    var hdg = me.hdg > 0 ? me.hdg : Geo.Bearing(me.lat, me.lon, other.lat, other.lon);
                    var len = lengths.GetValueOrDefault((me.apt, me.rwy), Geo.DistanceNm(me.lat, me.lon, other.lat, other.lon) * 6076);
                    if (!by.TryGetValue(me.apt, out var list)) by[me.apt] = list = new();
                    list.Add(new RunwayEnd(me.apt, me.end, hdg, me.lat, me.lon, me.elev, len, other.lat, other.lon));
                }
            }
            _byAirport = by;
            Status = $"{by.Count:N0} airports with runways";
            log.LogInformation("Loaded runways: {status}", Status);
        }
        catch (Exception ex) { log.LogError(ex, "Couldn't read runways"); Status = "couldn't read the runway file"; }
    }

    public List<RunwayEnd> ForAirport(string icaoOrIdent) =>
        _byAirport.GetValueOrDefault(FrequencyDb.FaaIdent(icaoOrIdent)) ?? _byAirport.GetValueOrDefault(icaoOrIdent) ?? new();

    /// <summary>Best runway for the wind (most headwind); the longest when calm.</summary>
    public RunwayEnd? Active(string airport, double windFromTrue, double windKt)
    {
        var list = ForAirport(airport).Where(r => r.LengthFt >= 5000).ToList();
        if (list.Count == 0) list = ForAirport(airport);
        if (list.Count == 0) return null;
        if (windKt < 4) return list.OrderByDescending(r => r.LengthFt).First();
        return list.OrderByDescending(r => Math.Cos((windFromTrue - r.TrueHeading) * Math.PI / 180) * windKt + r.LengthFt / 100000).First();
    }
}

public sealed class RunwayUpdater(RunwayDb db) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try { await db.RefreshAsync(ct); } catch (OperationCanceledException) { break; } catch { }
            try { await Task.Delay(db.Loaded ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(10), ct); } catch { break; }
        }
    }
}

public static class Geo
{
    public const double NmPerDegLat = 60.0;

    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2) => FrequencyDb.DistanceNm(lat1, lon1, lat2, lon2);

    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * Math.PI / 180; var p2 = lat2 * Math.PI / 180; var dl = (lon2 - lon1) * Math.PI / 180;
        var y = Math.Sin(dl) * Math.Cos(p2);
        var x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    /// <summary>Point a distance (nm) along a true bearing (flat-earth: accurate to a few metres within 30 nm).</summary>
    public static (double lat, double lon) Offset(double lat, double lon, double bearing, double nm)
    {
        var b = bearing * Math.PI / 180;
        var dLat = nm * Math.Cos(b) / NmPerDegLat;
        var dLon = nm * Math.Sin(b) / (NmPerDegLat * Math.Cos(lat * Math.PI / 180));
        return (lat + dLat, lon + dLon);
    }
}

public static class Csv
{
    public static IEnumerable<string[]> Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.Latin1);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            yield return fields.ToArray();
        }
    }
}
