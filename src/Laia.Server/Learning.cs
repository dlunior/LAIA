using System.IO.Compression;
using System.Text.Json;
using Laia.Common.Airports;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Learning from real traffic (Step 6, user 27 Nov: "The whole point of running LAIA on this other PC is to utilize the
/// GPU... so it can learn"; "there are 86 hubs / focus cities it can pre-learn that we identified in routegen"). This
/// first part, in the background on the LAIA PC:
/// - the training data: each hub's days from the LAIA Collector, every track kept every 10 s, the real arrivals read
///   from them (where each came in, the runway, its threshold time, its callsign and type from the Collector's flight
///   list) and the surface wind each hour (Open-Meteo's archive of forecasts) — one file per airport and day;
/// - the scoreboard: for each hub, the busiest hour of its two newest held-back days (every seventh day of the year is
///   held back and never learned from) flown again by this version of LAIA from where and when each real arrival came
///   in, and scored against what really happened — landing order, landing times, track miles, paths, and go-arounds,
///   break-offs, vectors, refused routes and separation warnings per 10 landings. Once per version, logged and kept, so
///   each new version (and later each learned model) is measured against the last.
/// </summary>
public sealed class LearningService(ILogger<LearningService> log, AirportStore airports, SettingsStore settings) : BackgroundService
{
    /// <summary>
    /// routegen's hubs and focus cities (WIKI_HUBS, 86 airports) that the Collector watches — the US, Canada and Puerto
    /// Rico; the overseas cargo hubs are left out until there's data for them.
    /// </summary>
    public static readonly string[] Hubs =
    {
        "KATL","KAUS","KBDL","KBNA","KBOI","KBOS","KBWI","KCAE","KCLE","KCLT","KCMH","KCVG","KDAL","KDCA","KDEN","KDFW","KDTW","KEWR",
        "KFLL","KGSO","KHOU","KIAD","KIAH","KIND","KJFK","KLAS","KLAX","KLGA","KMCI","KMCO","KMDW","KMEM","KMIA","KMKE","KMSP","KOAK",
        "KONT","KORD","KPDX","KPHL","KPHX","KRDU","KRFD","KSAN","KSAT","KSDF","KSEA","KSFO","KSJC","KSLC","KSMF","KSTL","KTPA","KAFW",
        "PANC","PHNL","PHKO","PHLI","PHOG","TJSJ","CYEG","CYHM","CYHZ","CYOW","CYUL","CYVR","CYWG","CYYC","CYYZ",
    };

    private static string Root => Path.Combine(Paths.DataDir, "data", "learning");
    private static string DataDir => Path.Combine(Root, "dataset");
    private static string ScoreDir => Path.Combine(Root, "scores");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string Status { get; private set; } = "starting";
    /// <summary>Hubs whose layout hasn't been read from the simulator yet (LAIA Sim is asked for a few at a time).</summary>
    public static List<string> MissingLayouts { get; private set; } = new();

    public static bool HeldBack(string day) =>
        DateTime.TryParseExact(day, "yyyy.MM.dd", null, System.Globalization.DateTimeStyles.None, out var d) && d.DayOfYear % 7 == 0;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await Pass(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogInformation("Learning: stopped this round ({msg}); trying again in an hour", ex.Message); Status = "waiting after an error"; }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch { return; }
        }
    }

    private string Source
    {
        get
        {
            var s = (settings.Get("track_source") ?? "").Trim();
            return (s.Length == 0 || s.Equals("off", StringComparison.OrdinalIgnoreCase) ? AdsbHistory.DefaultSource : s).TrimEnd('/');
        }
    }

    private async Task Pass(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
        var src = Source;
        List<string> days;
        using (var doc = JsonDocument.Parse(await http.GetStringAsync($"{src}/api/status", ct)))
            days = doc.RootElement.TryGetProperty("daysStored", out var ds) ? ds.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new();
        if (days.Count == 0) { Status = "the Collector has no days yet"; return; }
        var have = Hubs.Where(h => airports.Data(h) is not null).ToList();
        MissingLayouts = Hubs.Except(have).ToList();
        if (MissingLayouts.Count > 0)
            log.LogInformation("Learning: {n} of {all} hubs have their layout from MSFS; LAIA Sim is asked for the rest a few at a time while MSFS runs ({miss})",
                have.Count, Hubs.Length, string.Join(" ", MissingLayouts));

        // 1. The training data, a day at a time (the day's flight list is fetched once for every hub).
        var built = 0;
        foreach (var day in days.OrderBy(d => d, StringComparer.Ordinal))
        {
            var todo = have.Where(h => !File.Exists(DayFile(h, day))).ToList();
            if (todo.Count == 0) continue;
            List<RealLeg> legs = new();
            try
            {
                var lb = Gunzip(await http.GetByteArrayAsync($"{src}/api/legs/{day}", ct));
                legs = TraceLegs.Read(lb).Select(l => new RealLeg(l.Callsign, l.Type, l.Dep, l.Arr, double.IsNaN(l.DepT) ? null : l.DepT, double.IsNaN(l.ArrT) ? null : l.ArrT)).ToList();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogInformation("Learning: no flight list for {day} ({msg}) — tracks kept without callsigns", day, ex.Message); }
            foreach (var icao in todo)
            {
                ct.ThrowIfCancellationRequested();
                Status = $"training data: {icao} {day}";
                try
                {
                    var bytes = await http.GetByteArrayAsync($"{src}/api/tracks/{icao}/{day}", ct);
                    var tracks = TrackStore.Read(Gunzip(bytes));
                    var apt = new AirportModel(airports.Data(icao)!);
                    var arr = RealReplay.Arrivals(apt, tracks, legs);
                    var wind = await Winds(http, apt, day, ct);
                    await WriteDay(icao, day, tracks, arr, wind, ct);
                    built++;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { await WriteDay(icao, day, new(), new(), new(), ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogInformation("Learning: {icao} {day} not kept ({msg})", icao, day, ex.Message); }
            }
        }
        if (built > 0) log.LogInformation("Learning: training data — {n} airport-days added ({dir})", built, DataDir);

        // 2. The scoreboard for this version.
        var ver = typeof(LearningService).Assembly.GetName().Version?.ToString(3) ?? "?";
        var results = new List<ScoreRec>();
        foreach (var icao in have)
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(ScoreDir, icao + ".json");
            var past = File.Exists(file) ? JsonSerializer.Deserialize<List<ScoreRec>>(await File.ReadAllTextAsync(file, ct)) ?? new() : new();
            if (past.Any(r => r.Version == ver)) { results.AddRange(past.Where(r => r.Version == ver)); continue; }
            var mine = await ScoreAirport(icao, ver, ct);
            if (mine.Count == 0) continue;
            past.AddRange(mine);
            Directory.CreateDirectory(ScoreDir);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(past, Json), ct);
            results.AddRange(mine);
            var before = past.Where(r => r.Version != ver).GroupBy(r => r.Version).OrderByDescending(g => g.Max(r => r.At)).FirstOrDefault()?.ToList();
            foreach (var r in mine) log.LogInformation("Scoreboard {v}: {line}", ver, r.Line);
            if (before is { Count: > 0 })
                log.LogInformation("Scoreboard {v}: {icao} against {old}: {cmp}", ver, icao, before[0].Version, Compare(before, mine));
        }
        if (results.Count > 0)
        {
            var allPast = Directory.Exists(ScoreDir)
                ? Directory.GetFiles(ScoreDir, "*.json").SelectMany(f => { try { return JsonSerializer.Deserialize<List<ScoreRec>>(File.ReadAllText(f)) ?? new(); } catch { return new(); } }).ToList()
                : new();
            var prevVer = allPast.Where(r => r.Version != ver).GroupBy(r => r.Version).OrderByDescending(g => g.Max(r => r.At)).FirstOrDefault()?.ToList();
            log.LogInformation("Scoreboard {v}, all hubs ({n} airport-hours): {sum}{cmp}", ver, results.Count, Summary(results),
                prevVer is { Count: > 0 } ? $" | {prevVer[0].Version}: {Summary(prevVer)}" : "");
        }
        Status = $"up to date ({days[0]}); scoreboard {ver}: {results.Count} airport-hours";
    }

    private static string DayFile(string icao, string day) => Path.Combine(DataDir, icao, day + ".json.gz");

    private static byte[] Gunzip(byte[] b)
    {
        if (b.Length < 2 || b[0] != 0x1f || b[1] != 0x8b) return b;
        using var gz = new GZipStream(new MemoryStream(b), CompressionMode.Decompress);
        using var o = new MemoryStream();
        gz.CopyTo(o);
        return o.ToArray();
    }

    // ------------------------------------------------------------------ the day files

    private sealed class DayData
    {
        public string Icao { get; set; } = "";
        public string Day { get; set; } = "";
        public bool HeldBack { get; set; }
        /// <summary>Surface wind each hour: [Unix hour start, from°, kt].</summary>
        public List<double[]> Wind { get; set; } = new();
        public List<ArrRec> Arrivals { get; set; } = new();
        /// <summary>Every track near the airport (45 nm, under 18,000 ft above it), a point every 10 s: [t, lat, lon, alt (or -9999), track°].</summary>
        public List<List<double[]>> Tracks { get; set; } = new();
    }

    private sealed class ArrRec
    {
        public string Cs { get; set; } = "";
        public string Type { get; set; } = "";
        public string Rwy { get; set; } = "";
        public double EntryT { get; set; }
        public double ThrT { get; set; }
        public double Nm { get; set; }
        /// <summary>The track from where it came in to the threshold, every 10 s: [t, lat, lon, alt, track°].</summary>
        public List<double[]> Pts { get; set; } = new();
    }

    private static double[] P(TrackPoint p) => new[] { Math.Round(p.T, 1), Math.Round(p.Lat, 5), Math.Round(p.Lon, 5), Math.Round(p.AltFt ?? -9999), Math.Round(p.Track) };
    private static TrackPoint Tp(double[] a) => new(a[0], a[1], a[2], a[3] <= -9000 ? null : a[3], a[4]);

    private static async Task WriteDay(string icao, string day, List<IReadOnlyList<TrackPoint>> tracks, List<RealArrival> arr, List<double[]> wind, CancellationToken ct)
    {
        var d = new DayData { Icao = icao, Day = day, HeldBack = HeldBack(day), Wind = wind };
        foreach (var a in arr)
            d.Arrivals.Add(new ArrRec { Cs = a.Callsign, Type = a.Type, Rwy = a.Runway, EntryT = a.EntryT, ThrT = Math.Round(a.ThrT, 1), Nm = Math.Round(a.TrackNm, 2), Pts = a.Pts.Select(P).ToList() });
        foreach (var t in tracks)
        {
            var l = new List<double[]>(); var last = double.NegativeInfinity;
            foreach (var p in t) if (p.T - last >= 9.5) { l.Add(P(p)); last = p.T; }
            if (l.Count >= 3) d.Tracks.Add(l);
        }
        var path = DayFile(icao, day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var fs = File.Create(path + ".tmp"))
        await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            await JsonSerializer.SerializeAsync(gz, d, Json, ct);
        File.Move(path + ".tmp", path, true);
    }

    private static DayData? ReadDay(string icao, string day)
    {
        var path = DayFile(icao, day);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<DayData>(gz, Json);
        }
        catch { return null; }
    }

    /// <summary>The surface wind each hour of a day at the airport (Open-Meteo's archive of past forecasts).</summary>
    private async Task<List<double[]>> Winds(HttpClient http, AirportModel apt, string day, CancellationToken ct)
    {
        try
        {
            var iso = day.Replace('.', '-');
            var url = $"https://historical-forecast-api.open-meteo.com/v1/forecast?latitude={apt.Map.Lat0:0.###}&longitude={apt.Map.Lon0:0.###}" +
                      $"&start_date={iso}&end_date={iso}&hourly=wind_speed_10m,wind_direction_10m&wind_speed_unit=kn&timezone=GMT&timeformat=unixtime";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
            var h = doc.RootElement.GetProperty("hourly");
            var t = h.GetProperty("time").EnumerateArray().Select(x => x.GetDouble()).ToList();
            var sp = h.GetProperty("wind_speed_10m").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToList();
            var dir = h.GetProperty("wind_direction_10m").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToList();
            var res = new List<double[]>();
            for (var i = 0; i < t.Count && i < sp.Count && i < dir.Count; i++)
                if (!double.IsNaN(sp[i]) && !double.IsNaN(dir[i])) res.Add(new[] { t[i], Math.Round(dir[i]), Math.Round(sp[i]) });
            return res;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogInformation("Learning: no wind for {icao} {day} ({msg})", apt.Icao, day, ex.Message);
            return new();
        }
    }

    // ------------------------------------------------------------------ the scoreboard

    public sealed class ScoreRec
    {
        public string Version { get; set; } = "";
        public string Airport { get; set; } = "";
        public string Day { get; set; } = "";
        public string Window { get; set; } = "";
        public int Scored { get; set; }
        public int Landed { get; set; }
        public double Order { get; set; } = double.NaN;
        public double TimeErr { get; set; } = double.NaN;
        public double TimeBias { get; set; } = double.NaN;
        public double Miles { get; set; } = double.NaN;
        public double PathNm { get; set; } = double.NaN;
        public Dictionary<string, double> Per10 { get; set; } = new();
        public string Line { get; set; } = "";
        public DateTime At { get; set; }
    }

    private async Task<List<ScoreRec>> ScoreAirport(string icao, string ver, CancellationToken ct)
    {
        var dayFiles = Directory.Exists(Path.Combine(DataDir, icao))
            ? Directory.GetFiles(Path.Combine(DataDir, icao), "*.json.gz").Select(f => Path.GetFileName(f)[..^".json.gz".Length]).OrderByDescending(d => d, StringComparer.Ordinal).ToList()
            : new();
        var testDays = dayFiles.Where(HeldBack).ToList();
        var trainDays = dayFiles.Where(d => !HeldBack(d)).Take(14).ToList();
        if (testDays.Count == 0 || trainDays.Count == 0) return new();
        var data = airports.Data(icao);
        if (data is null) return new();
        Status = $"scoreboard: {icao}";
        // What LAIA knows of real traffic there, from the days it may learn from only (never a held-back day).
        var apt = new AirportModel(data);
        var tracks = new List<IReadOnlyList<TrackPoint>>();
        foreach (var d in trainDays)
            if (ReadDay(icao, d) is { } dd) tracks.AddRange(dd.Tracks.Select(t => (IReadOnlyList<TrackPoint>)t.Select(Tp).ToList()));
        AirportProfile? prof = null;
        try { prof = TrackLearner.Learn(icao, apt.Map, apt.ElevationFt, apt.Ends, tracks, "LAIA Collector (training days)", $"{trainDays[^1]}–{trainDays[0]}"); }
        catch (Exception ex) { log.LogInformation("Scoreboard: {icao} not learned ({msg})", icao, ex.Message); }
        tracks.Clear();
        var res = new List<ScoreRec>();
        foreach (var day in testDays)
        {
            if (res.Count >= 2) break;
            var dd = ReadDay(icao, day);
            if (dd is null || dd.Arrivals.Count < 10) continue;
            var arr = dd.Arrivals.Select(a => new RealArrival(a.Cs, a.Type, a.Rwy, a.EntryT, a.Pts[0][1], a.Pts[0][2], a.Pts[0][3] <= -9000 ? 10000 : a.Pts[0][3], a.ThrT, a.Nm, a.Pts.Select(Tp).ToList())).ToList();
            var (start, count) = RealReplay.BusiestHour(arr);
            if (count < 6) continue;
            var w = dd.Wind.Where(x => x[0] <= start + 1800).OrderByDescending(x => x[0]).FirstOrDefault();
            var wind = w is null ? new Wind(0, 0) : new Wind(w[1], w[2]);
            var model = new AirportModel(data);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var score = await Task.Run(() =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.Lowest;
                return RealReplay.Replay(model, prof, wind, arr, start, start + 3600, new ReplaySource(), keepGoing: () => !ct.IsCancellationRequested);
            }, ct);
            ct.ThrowIfCancellationRequested();
            var rec = new ScoreRec
            {
                Version = ver, Airport = icao, Day = day, Window = score.Window, Scored = score.Scored, Landed = score.Landed, Order = score.OrderSame,
                TimeErr = score.TimeErr, TimeBias = score.TimeBias, Miles = score.MilesDiff, PathNm = score.PathNm, Per10 = score.Per10,
                Line = score.Line() + $" (wind {wind.FromDeg:000}/{wind.Kt:0}; {sw.Elapsed.TotalSeconds:0} s to run)" + (score.Worst.Count > 0 ? "; furthest out: " + string.Join("; ", score.Worst) : ""),
                At = DateTime.UtcNow,
            };
            res.Add(rec);
        }
        return res;
    }

    private static double Med(IEnumerable<double> xs)
    {
        var l = xs.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
        return l.Count == 0 ? double.NaN : l[l.Count / 2];
    }

    private static string Summary(List<ScoreRec> rs) =>
        $"landing order as real {100 * Med(rs.Select(r => r.Order)):0}%, landing time ±{Med(rs.Select(r => r.TimeErr)):0} s, track miles {Med(rs.Select(r => r.Miles)):+0.0;-0.0} nm, " +
        $"path {Med(rs.Select(r => r.PathNm)):0.0} nm from the real one, flown to the runway {100.0 * rs.Sum(r => r.Landed) / Math.Max(1, rs.Sum(r => r.Scored)):0}%; per 10 landings " +
        string.Join(", ", new[] { "go-arounds", "break-offs", "vectors", "routes refused", "separation warnings" }.Select(k => $"{k} {Med(rs.Select(r => r.Per10.GetValueOrDefault(k, double.NaN))):0.0}"));

    private static string Compare(List<ScoreRec> before, List<ScoreRec> now)
    {
        string D(string name, Func<ScoreRec, double> f, string fmt, bool lowerBetter)
        {
            var a = Med(before.Select(f)); var b = Med(now.Select(f));
            if (double.IsNaN(a) || double.IsNaN(b)) return $"{name} –";
            var better = lowerBetter ? Math.Abs(b) < Math.Abs(a) : b > a;
            return $"{name} {a.ToString(fmt)} → {b.ToString(fmt)}{(Math.Abs(a - b) < 1e-9 ? "" : better ? " (better)" : " (worse)")}";
        }
        return string.Join(", ", new[]
        {
            D("order", r => r.Order * 100, "0", false), D("time ±s", r => r.TimeErr, "0", true), D("track miles", r => r.Miles, "+0.0;-0.0", true), D("path nm", r => r.PathNm, "0.0", true),
        });
    }

    /// <summary>The replay brings in real arrivals with their own callsigns and types; nothing new is made up.</summary>
    private sealed class ReplaySource : IFlightSource
    {
        public AircraftIdentity NewAircraft(string airport, int maxGateSize, Random rng, IReadOnlyCollection<string>? airlines = null) => new("DAL", "B738", "DAL" + rng.Next(100, 3000), "KXXX");
        public (string callsign, string other) NewFlight(string airport, string airline, bool departure, Random rng, string type = "") => (airline + rng.Next(100, 3000), "KXXX");
    }
}
