using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Laia.Server;

public enum FacKind { Clearance, Ground, Tower, ApproachDeparture, Approach, Departure, Center, Ramp, Atis, Ctaf }

/// <summary>One published frequency: who answers on it and where.</summary>
public sealed record FacFreq(string Airport, string City, string BaseName, FacKind Kind, double Freq, double Lat, double Lon, bool Primary)
{
    public string KindWord => Kind switch
    {
        FacKind.ApproachDeparture or FacKind.Approach => "Approach",
        FacKind.Departure => "Departure",
        FacKind.Ctaf => "Traffic",
        FacKind.Atis => "ATIS",
        _ => Kind.ToString(),
    };
    public string Name => $"{BaseName} {KindWord}";
    public double RangeNm => Kind switch
    {
        FacKind.Center => 250,
        FacKind.ApproachDeparture or FacKind.Approach or FacKind.Departure => 70,
        _ => 30,
    };
    public string FreqText => Freq.ToString("0.0##", CultureInfo.InvariantCulture);
}

/// <summary>
/// US ATC frequencies from the FAA's free NASR data (FRQ file, 28-day cycle). Downloaded in the
/// background on the ATC PC and refreshed every cycle. Canada and the Caribbean come later.
/// </summary>
public sealed class FrequencyDb
{
    private static readonly DateTime Anchor = new(2026, 10, 29); // a known NASR cycle start
    private readonly ILogger<FrequencyDb> _log;
    private List<FacFreq> _all = new();
    private Dictionary<string, List<FacFreq>> _byAirport = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _city = new(StringComparer.OrdinalIgnoreCase);
    private List<(string ident, double lat, double lon)> _airports = new();
    public string Status { get; private set; } = "not loaded";
    public string? Cycle { get; private set; }
    public int Count => _all.Count;
    public bool Loaded => _all.Count > 0;

    public FrequencyDb(ILogger<FrequencyDb> log) => _log = log;

    private static string DataDir => Path.Combine(Paths.DataDir, "data");

    public static DateTime CurrentCycle(DateTime today)
    {
        var days = (today.Date - Anchor).Days;
        var n = (int)Math.Floor(days / 28.0);
        return Anchor.AddDays(n * 28);
    }

    /// <summary>Loads the newest cached file, then downloads the current cycle if it's newer.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(DataDir);
        var cached = Directory.GetFiles(DataDir, "FRQ-*.csv").OrderDescending().FirstOrDefault();
        if (cached is not null && !Loaded) Load(cached);

        var cycle = CurrentCycle(DateTime.UtcNow);
        for (var back = 0; back < 2; back++)
        {
            var c = cycle.AddDays(-28 * back);
            var target = Path.Combine(DataDir, $"FRQ-{c:yyyy-MM-dd}.csv");
            if (File.Exists(target)) { if (Cycle != c.ToString("yyyy-MM-dd")) Load(target); return; }
            var url = $"https://nfdc.faa.gov/webContent/28DaySub/extra/{c.ToString("dd_MMM_yyyy", CultureInfo.InvariantCulture)}_FRQ_CSV.zip";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
                _log.LogInformation("Downloading FAA frequencies {url}", url);
                using var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) { _log.LogWarning("FAA download {code}", (int)resp.StatusCode); continue; }
                await using var zipStream = await resp.Content.ReadAsStreamAsync(ct);
                using var ms = new MemoryStream();
                await zipStream.CopyToAsync(ms, ct);
                ms.Position = 0;
                using var zip = new ZipArchive(ms);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("FRQ.csv", StringComparison.OrdinalIgnoreCase));
                if (entry is null) { _log.LogWarning("FRQ.csv missing in {url}", url); continue; }
                entry.ExtractToFile(target + ".tmp", true);
                File.Move(target + ".tmp", target, true);
                foreach (var old in Directory.GetFiles(DataDir, "FRQ-*.csv").Where(f => f != target)) try { File.Delete(old); } catch { }
                Load(target);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("FAA frequency download failed: {msg}", ex.Message);
                Status = Loaded ? Status : "download failed, will retry";
            }
        }
    }

    private void Load(string path)
    {
        try
        {
            var rows = ReadCsv(path).ToList();
            var header = rows[0];
            int Col(string n) => Array.IndexOf(header, n);
            int iFac = Col("FACILITY"), iType = Col("FACILITY_TYPE"), iArtcc = Col("ARTCC_OR_FSS_ID"), iSvc = Col("SERVICED_FACILITY"),
                iSvcName = Col("SERVICED_FAC_NAME"), iSite = Col("SERVICED_SITE_TYPE"), iLat = Col("LAT_DECIMAL"), iLon = Col("LONG_DECIMAL"),
                iCity = Col("SERVICED_CITY"), iTwr = Col("TOWER_OR_COMM_CALL"), iApp = Col("PRIMARY_APPROACH_RADIO_CALL"), iFreq = Col("FREQ"),
                iUse = Col("FREQ_USE");
            var list = new List<FacFreq>();
            var city = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var airportPos = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
            var traconPending = new List<(string fac, string baseName, FacKind kind, double freq, bool primary)>();
            var traconPos = new Dictionary<string, List<(double, double)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in rows.Skip(1))
            {
                if (r.Length < header.Length) continue;
                if (!double.TryParse(r[iFreq], NumberStyles.Float, CultureInfo.InvariantCulture, out var freq) || freq < 118 || freq > 137) continue;
                double.TryParse(r[iLat], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat);
                double.TryParse(r[iLon], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon);
                var type = r[iType];
                var use = r[iUse].ToUpperInvariant();
                var svc = r[iSvc];
                var site = r[iSite];
                if (site == "AIRPORT" && lat != 0)
                {
                    airportPos[svc] = (lat, lon);
                    if (!string.IsNullOrWhiteSpace(r[iCity])) city[svc] = Pretty(r[iCity]);
                }

                if (type == "RCAG")
                {
                    if (lat == 0) continue;
                    list.Add(new FacFreq(r[iArtcc], "", ArtccName(r[iArtcc]), FacKind.Center, freq, lat, lon, true));
                    continue;
                }

                var primary = !use.Contains("/S");
                FacKind? kind = null;
                var isApp = use.Contains("APCH") || use.Contains("DEP") || use.Contains("CLASS B") || use.Contains("CLASS C");
                if (use.Contains("LCL")) kind = FacKind.Tower;
                else if (use.Contains("GND")) kind = FacKind.Ground;
                else if (use.StartsWith("CD")) kind = FacKind.Clearance;
                else if (use.Contains("RAMP")) kind = FacKind.Ramp;
                else if (use.Contains("ATIS")) kind = FacKind.Atis;
                else if (use.Contains("CTAF") || use.Contains("UNICOM")) kind = FacKind.Ctaf;
                else if (isApp)
                    kind = use.Contains("APCH") && use.Contains("DEP") ? FacKind.ApproachDeparture
                         : use.Contains("DEP") ? FacKind.Departure : FacKind.Approach;
                if (kind is null) continue;

                string baseName = kind switch
                {
                    FacKind.ApproachDeparture or FacKind.Approach or FacKind.Departure => Pretty(r[iApp]),
                    FacKind.Ctaf => Pretty(r[iSvcName]),
                    _ => Pretty(string.IsNullOrWhiteSpace(r[iTwr]) ? r[iSvcName] : r[iTwr]),
                };
                if (string.IsNullOrWhiteSpace(baseName)) continue;

                if (type.Contains("TRACON") && lat == 0 && svc == r[iFac])
                {
                    traconPending.Add((r[iFac], baseName, kind.Value, freq, primary)); // position filled from its airports below
                    continue;
                }
                if (lat == 0) continue;
                if (type.Contains("TRACON") || type.Contains("RAPCON") || type.Contains("RATCF"))
                {
                    if (!traconPos.TryGetValue(r[iFac], out var pts)) traconPos[r[iFac]] = pts = new();
                    pts.Add((lat, lon));
                }
                list.Add(new FacFreq(svc, "", baseName, kind.Value, freq, lat, lon, primary));
            }

            foreach (var (fac, baseName, kind, freq, primary) in traconPending)
            {
                if (!traconPos.TryGetValue(fac, out var pts) || pts.Count == 0) continue;
                list.Add(new FacFreq(fac, "", baseName, kind, freq, pts.Average(p => p.Item1), pts.Average(p => p.Item2), primary));
            }

            list = list.Select(f => f with { City = city.GetValueOrDefault(f.Airport, "") }).ToList();
            _all = list;
            _byAirport = list.GroupBy(f => f.Airport, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            _city = city;
            _airports = airportPos.Select(kv => (kv.Key, kv.Value.Item1, kv.Value.Item2)).ToList();
            Cycle = Path.GetFileNameWithoutExtension(path)[4..];
            Status = $"FAA cycle {Cycle}: {list.Count:N0} frequencies";
            _log.LogInformation("Loaded {status}", Status);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Couldn't read {path}", path);
            Status = "couldn't read the frequency file";
        }
    }

    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 3440.065;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Who answers on this frequency at this position (nearest match within that facility's range).</summary>
    public FacFreq? Lookup(double freq, double lat, double lon)
    {
        FacFreq? best = null;
        var bestScore = double.MaxValue;
        foreach (var f in _all)
        {
            if (Math.Abs(f.Freq - freq) > 0.004) continue;
            var d = DistanceNm(lat, lon, f.Lat, f.Lon);
            if (d > f.RangeNm) continue;
            var score = d / f.RangeNm + (f.Primary ? 0 : 0.2);
            if (score < bestScore) { best = f; bestScore = score; }
        }
        return best;
    }

    public static string FaaIdent(string icao) =>
        icao.Length == 4 && (icao[0] == 'K') ? icao[1..] : icao;

    public string? City(string icaoOrIdent) =>
        _city.GetValueOrDefault(FaaIdent(icaoOrIdent)) ?? _city.GetValueOrDefault(icaoOrIdent);

    /// <summary>Primary frequency of each position at an airport (for "contact Ground on 121.7").</summary>
    public Dictionary<string, FacFreq> AirportPositions(string icaoOrIdent)
    {
        var ident = FaaIdent(icaoOrIdent);
        var result = new Dictionary<string, FacFreq>();
        if (!_byAirport.TryGetValue(ident, out var list) && !_byAirport.TryGetValue(icaoOrIdent, out list)) return result;
        foreach (var f in list.OrderByDescending(f => f.Primary))
        {
            var key = f.Kind switch
            {
                FacKind.Clearance => "Clearance", FacKind.Ground => "Ground", FacKind.Tower => "Tower", FacKind.Ramp => "Ramp",
                FacKind.Departure or FacKind.ApproachDeparture => "Departure", _ => null,
            };
            if (key is null || result.ContainsKey(key)) continue;
            result[key] = key == "Departure" ? f with { Kind = FacKind.Departure } : f;
        }
        return result;
    }

    public List<FacFreq> AirportFrequencies(string icaoOrIdent)
    {
        var ident = FaaIdent(icaoOrIdent);
        return _byAirport.TryGetValue(ident, out var list) ? list : _byAirport.GetValueOrDefault(icaoOrIdent) ?? new();
    }

    /// <summary>Nearest airport with a control tower (small private strips are ignored).</summary>
    public (string ident, double nm)? NearestAirport(double lat, double lon)
    {
        (string, double)? best = null;
        foreach (var (ident, alat, alon) in _airports)
        {
            if (!_byAirport.TryGetValue(ident, out var fl) || !fl.Any(f => f.Kind == FacKind.Tower)) continue;
            var d = DistanceNm(lat, lon, alat, alon);
            if (best is null || d < best.Value.Item2) best = (ident, d);
        }
        return best;
    }

    private static readonly Dictionary<string, string> Fixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["La Guardia"] = "LaGuardia", ["Laguardia"] = "LaGuardia", ["Mc Carran"] = "McCarran", ["Dfw"] = "DFW",
        ["O'hare"] = "O'Hare", ["Ohare"] = "O'Hare",
    };

    public static string Pretty(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Trim().ToLowerInvariant());
        foreach (var (k, v) in Fixes) t = t.Replace(k, v);
        return t;
    }

    public static string ArtccName(string id) => id.ToUpperInvariant() switch
    {
        "ZNY" => "New York", "ZDC" => "Washington", "ZBW" => "Boston", "ZOB" => "Cleveland", "ZID" => "Indianapolis",
        "ZTL" => "Atlanta", "ZJX" => "Jacksonville", "ZMA" => "Miami", "ZAU" => "Chicago", "ZME" => "Memphis",
        "ZHU" => "Houston", "ZFW" => "Fort Worth", "ZKC" => "Kansas City", "ZMP" => "Minneapolis", "ZDV" => "Denver",
        "ZAB" => "Albuquerque", "ZLC" => "Salt Lake", "ZLA" => "Los Angeles", "ZOA" => "Oakland", "ZSE" => "Seattle",
        "ZAN" => "Anchorage", "ZHN" => "Honolulu", "ZSU" => "San Juan", "ZUA" => "Guam",
        _ => id,
    };

    private static IEnumerable<string[]> ReadCsv(string path)
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

/// <summary>Keeps the FAA frequency data current (checks at start-up and every 12 hours).</summary>
public sealed class FrequencyUpdater(FrequencyDb db) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await db.RefreshAsync(ct); } catch (OperationCanceledException) { break; } catch { }
            try { await Task.Delay(db.Loaded ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(10), ct); } catch { break; }
        }
    }
}
