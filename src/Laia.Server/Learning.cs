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
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

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

        try { await UpgradeDays(have, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogInformation("Learning: days not read again ({msg})", ex.Message); }

        // 2. (0.1.333) No learned ways in: no flight flies a copied route (user, 28 Nov). The techniques are learned
        // instead — first the pattern's shape, across the hubs, below; then sequencing, spacing and speed control.

        // 3. The scoreboard for this version — the rules, and the learned ways in when there's a model — and which
        // airports the model is used at: only where it beats the rules on the held-back days.
        var ver = typeof(LearningService).Assembly.GetName().Version?.ToString(3) ?? "?";
        var results = new List<ScoreRec>();
        RouteModel? model = null;
        var variants = new List<(string name, RouteModel? m)> { ("rules", null) };
        if (model is not null) variants.Add(($"model {model.Id}", model));
        foreach (var icao in have)
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(ScoreDir, icao + ".json");
            var past = File.Exists(file) ? JsonSerializer.Deserialize<List<ScoreRec>>(await File.ReadAllTextAsync(file, ct), Json) ?? new() : new();
            foreach (var r in past) if (r.Variant.Length == 0) r.Variant = "rules";
            foreach (var (vname, vm) in variants)
            {
                if (past.Any(r => r.Version == ver && r.Variant == vname)) { if (vname == "rules") results.AddRange(past.Where(r => r.Version == ver && r.Variant == vname)); continue; }
                var mine = await ScoreAirport(icao, ver, vname, vm, ct);
                if (mine.Count == 0) continue;
                past.AddRange(mine);
                Directory.CreateDirectory(ScoreDir);
                await File.WriteAllTextAsync(file, JsonSerializer.Serialize(past, Json), ct);
                if (vname == "rules") results.AddRange(mine);
                foreach (var r in mine) log.LogInformation("Scoreboard {v} ({variant}): {line}", ver, vname, r.Line);
                var before = past.Where(r => r.Variant == "rules" && r.Version != ver).GroupBy(r => r.Version).OrderByDescending(g => g.Max(r => r.At)).FirstOrDefault()?.ToList();
                if (vname == "rules" && before is { Count: > 0 })
                    log.LogInformation("Scoreboard {v}: {icao} against {old}: {cmp}", ver, icao, before[0].Version, Compare(before, mine));
            }
            if (model is not null) Decide(icao, past.Where(r => r.Version == ver && r.Variant == "rules").ToList(), past.Where(r => r.Version == ver && r.Variant == $"model {model.Id}").ToList(), model);
        }
        if (model is not null)
        {
            var use = _approved.Where(kv => kv.Value.ok).Select(kv => kv.Key).OrderBy(x => x).ToList();
            log.LogInformation("Learned ways in ({id}): used at {n} airport(s) where they beat the rules{list}", model.Id, use.Count, use.Count > 0 ? ": " + string.Join(" ", use) : "");
        }
        if (results.Count > 0)
        {
            var allPast = Directory.Exists(ScoreDir)
                ? Directory.GetFiles(ScoreDir, "*.json").SelectMany(f => { try { return JsonSerializer.Deserialize<List<ScoreRec>>(File.ReadAllText(f), Json) ?? new List<ScoreRec>(); } catch { return new List<ScoreRec>(); } }).ToList()
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
        /// <summary>2 from 0.1.332: arrivals read per visit (a track split where the aircraft wasn't seen for 5 minutes).</summary>
        public int Format { get; set; }
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
        var d = new DayData { Icao = icao, Day = day, Format = DayFormat, HeldBack = HeldBack(day), Wind = wind };
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

    private const int DayFormat = 2;

    /// <summary>
    /// Day files written before the arrivals were read per visit (0.1.330–0.1.331): the arrivals read again from the
    /// tracks kept in them — callsigns and types kept from the old reading where the threshold time matches.
    /// </summary>
    private async Task UpgradeDays(List<string> hubs, CancellationToken ct)
    {
        var n = 0;
        foreach (var icao in hubs)
        {
            if (airports.Data(icao) is not { } data || !Directory.Exists(Path.Combine(DataDir, icao))) continue;
            AirportModel? apt = null;
            foreach (var f in Directory.GetFiles(Path.Combine(DataDir, icao), "*.json.gz"))
            {
                ct.ThrowIfCancellationRequested();
                var day = Path.GetFileName(f)[..^".json.gz".Length];
                if (ReadDay(icao, day) is not { } dd || dd.Format >= DayFormat) continue;
                apt ??= new AirportModel(data);
                var tracks = dd.Tracks.Select(t => (IReadOnlyList<TrackPoint>)t.Select(Tp).ToList()).ToList();
                var legs = dd.Arrivals.Where(a => a.Cs.Length > 0).Select(a => new RealLeg(a.Cs, a.Type, "", icao, null, a.ThrT)).ToList();
                var arr = RealReplay.Arrivals(apt, tracks, legs);
                await WriteDay(icao, day, tracks, arr, dd.Wind, ct);
                n++;
            }
        }
        if (n > 0) log.LogInformation("Learning: {n} airport-days read again (arrivals per visit, types from final-approach speed where there was no flight list)", n);
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

    // ------------------------------------------------------------------ the pattern learned across the hubs

    /// <summary>One runway's pattern as real controllers flew it (from the training days only), and how many arrivals.</summary>
    public sealed record ShapeRec(string Runway, int Count, double JoinNm, double BaseNm, double DownwindNm, double InterceptDeg);

    /// <summary>
    /// Keeps each hub's learned patterns (where real controllers join the final, turn base, how far out the downwind is,
    /// the intercept angle) and works out the pattern across all the hubs — each runway weighted by its arrivals — that
    /// LAIA flies at airports with no real traffic of their own learned (user, 28 Nov: "learn the techniques from those
    /// busy airports and apply that to all airports").
    /// </summary>
    private void SaveShapes(string icao, AirportProfile prof)
    {
        try
        {
            var dir = Path.Combine(ModelDir, "shapes");
            Directory.CreateDirectory(dir);
            var recs = prof.Arrivals.Where(kv => kv.Value.Count >= 20 && !double.IsNaN(kv.Value.JoinP50))
                .Select(kv => new ShapeRec(kv.Key, kv.Value.Count, kv.Value.JoinP50, kv.Value.BaseNm, kv.Value.DownwindNm, kv.Value.InterceptDeg)).ToList();
            File.WriteAllText(Path.Combine(dir, icao + ".json"), JsonSerializer.Serialize(recs, Json));
            var all = Directory.GetFiles(dir, "*.json").SelectMany(f => { try { return JsonSerializer.Deserialize<List<ShapeRec>>(File.ReadAllText(f), Json) ?? new List<ShapeRec>(); } catch { return new List<ShapeRec>(); } }).ToList();
            if (all.Count < 5) return;
            double W(Func<ShapeRec, double> f)
            {
                var l = all.Where(r => !double.IsNaN(f(r))).OrderBy(f).ToList();
                if (l.Count == 0) return double.NaN;
                var half = l.Sum(r => r.Count) / 2.0; double acc = 0;
                foreach (var r in l) { acc += r.Count; if (acc >= half) return f(r); }
                return f(l[^1]);
            }
            var g = new VectorShape(Math.Round(W(r => r.JoinNm), 1), Math.Round(W(r => r.BaseNm), 1), Math.Round(W(r => r.DownwindNm), 1), Math.Round(W(r => r.InterceptDeg)));
            if (double.IsNaN(g.JoinNm) || double.IsNaN(g.BaseNm)) return;
            var changed = TrafficWorld.GeneralShape != g;
            TrafficWorld.GeneralShape = g;
            File.WriteAllText(Path.Combine(ModelDir, "general-shape.json"), JsonSerializer.Serialize(g, Json));
            if (changed) log.LogInformation("Pattern learned across {n} hub runways: final joined {j:0.0} nm out, base {b:0.0} nm out, downwind {d:0.0} nm off the final, {i:0}° intercept — flown at airports with no real traffic of their own learned",
                all.Count, g.JoinNm, g.BaseNm, g.DownwindNm, g.InterceptDeg);
        }
        catch (Exception ex) { log.LogInformation("Pattern across the hubs not worked out ({msg})", ex.Message); }
    }

    // ------------------------------------------------------------------ the learned ways in (0.1.331–0.1.332; not used since)

    private static string TrainDir => Path.Combine(Root, "train");
    private static string ModelDir => Path.Combine(Root, "models");
    private static string PyLib => Path.Combine(Root, "pylib");
    private static RouteModel? _model;
    private static Dictionary<string, (bool ok, string why)> _approved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _mgate = new();

    /// <summary>The learned ways in for this airport, when the model beat the rules on its scoreboard; null otherwise.</summary>
    public static RouteModel? ModelFor(string icao) => null;   // 0.1.333: ways in are built, never copied (user, 28 Nov)

    public override Task StartAsync(CancellationToken ct)
    {
        try
        {
            var gs = Path.Combine(ModelDir, "general-shape.json");
            if (File.Exists(gs) && JsonSerializer.Deserialize<VectorShape>(File.ReadAllText(gs), Json) is { } shape)
            {
                TrafficWorld.GeneralShape = shape;
                log.LogInformation("Pattern learned across the hubs: final joined {j:0.0} nm out, base {b:0.0} nm out, downwind {d:0.0} nm off the final, {i:0}° intercept", shape.JoinNm, shape.BaseNm, shape.DownwindNm, shape.InterceptDeg);
            }
        }
        catch (Exception ex) { log.LogInformation("General pattern not loaded ({msg})", ex.Message); }
        return base.StartAsync(ct);
    }

    public sealed class Approval { public bool Ok { get; set; } public string Why { get; set; } = ""; public string Model { get; set; } = ""; }

    /// <summary>
    /// The model is used at an airport only where it beats the rules on the same held-back hours: its paths nearer the
    /// real ones, landing times no worse (10 % or 5 s), landing order no worse (5 points), and no more go-arounds and
    /// break-offs (half a one per 10 landings).
    /// </summary>
    private void Decide(string icao, List<ScoreRec> rules, List<ScoreRec> mod, RouteModel model)
    {
        if (rules.Count == 0 || mod.Count == 0) return;
        double M(List<ScoreRec> l, Func<ScoreRec, double> f) => Med(l.Select(f));
        double Ev(List<ScoreRec> l) => M(l, r => r.Per10.GetValueOrDefault("go-arounds") + r.Per10.GetValueOrDefault("break-offs"));
        var why = new List<string>();
        var pr = M(rules, r => r.PathNm); var pm = M(mod, r => r.PathNm);
        var tr = M(rules, r => r.TimeErr); var tm = M(mod, r => r.TimeErr);
        var oR = M(rules, r => r.Order); var oM = M(mod, r => r.Order);
        if (!(pm < pr)) why.Add($"paths {pm:0.0} nm from the real ones against {pr:0.0}");
        if (tm > tr * 1.1 + 5) why.Add($"landing times ±{tm:0} s against ±{tr:0}");
        if (!double.IsNaN(oR) && !double.IsNaN(oM) && oM < oR - 0.05) why.Add($"landing order {100 * oM:0}% against {100 * oR:0}%");
        if (Ev(mod) > Ev(rules) + 0.5) why.Add($"go-arounds and break-offs {Ev(mod):0.0} per 10 against {Ev(rules):0.0}");
        var ok = why.Count == 0;
        var text = ok ? $"paths {pm:0.0} nm from the real ones against {pr:0.0} by the rules, landing times ±{tm:0} s against ±{tr:0}" : string.Join("; ", why);
        lock (_mgate) _approved[icao] = (ok, text);
        log.LogInformation("Learned ways in at {icao}: {verdict} — {why}", icao, ok ? "used (better than the rules)" : "not used", text);
        try
        {
            Directory.CreateDirectory(ModelDir);
            Dictionary<string, Approval> all;
            lock (_mgate) all = _approved.ToDictionary(kv => kv.Key, kv => new Approval { Ok = kv.Value.ok, Why = kv.Value.why, Model = model.Id });
            File.WriteAllText(Path.Combine(ModelDir, "approved.json"), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// The examples the model learns from: every real arrival in the training data with where it came in, the wind, how
    /// busy it was, and the way it flew — as rows of numbers (examples.bin) with what they mean (meta.json). Written
    /// again only when there are new days.
    /// </summary>
    private async Task ExportExamples(List<string> hubs, CancellationToken ct)
    {
        var files = hubs.SelectMany(h => Directory.Exists(Path.Combine(DataDir, h)) ? Directory.GetFiles(Path.Combine(DataDir, h), "*.json.gz") : Array.Empty<string>()).ToList();
        var sig = $"{files.Count}:{files.Sum(f => new FileInfo(f).Length)}";
        var metaPath = Path.Combine(TrainDir, "meta.json");
        if (File.Exists(metaPath))
        {
            using var md = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath, ct));
            if (md.RootElement.TryGetProperty("signature", out var sg) && sg.GetString() == sig) return;
        }
        Status = "learned ways in: writing the examples";
        Directory.CreateDirectory(TrainDir);
        var airportsIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Hubs.Length; i++) airportsIdx[Hubs[i]] = i + 1;
        var runways = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long rows = 0, heldRows = 0;
        var bin = Path.Combine(TrainDir, "examples.bin");
        await using (var fs = File.Create(bin + ".tmp"))
        await using (var bw = new BinaryWriter(fs))
        {
            foreach (var icao in hubs)
            {
                ct.ThrowIfCancellationRequested();
                if (airports.Data(icao) is not { } data || !Directory.Exists(Path.Combine(DataDir, icao))) continue;
                var apt = new AirportModel(data);
                foreach (var f in Directory.GetFiles(Path.Combine(DataDir, icao), "*.json.gz"))
                {
                    var day = Path.GetFileName(f)[..^".json.gz".Length];
                    if (ReadDay(icao, day) is not { } dd) continue;
                    foreach (var a in dd.Arrivals)
                    {
                        var rwy = apt.Ends.FirstOrDefault(e => e.Ident == a.Rwy);
                        if (rwy is null || a.Pts.Count < 3 || a.Pts[0][3] <= -9000) continue;
                        var pts = a.Pts.Select(q => (apt.Map.ToXY(q[1], q[2]), q[3] <= -9000 ? double.NaN : q[3])).ToList();
                        for (var i = 0; i < pts.Count; i++) if (double.IsNaN(pts[i].Item2)) pts[i] = (pts[i].Item1, i > 0 ? pts[i - 1].Item2 : a.Pts[0][3]);
                        pts.Add((rwy.Threshold, rwy.ElevationFt));
                        if (RouteModel.Target(rwy, pts) is not { } target) continue;
                        var w = dd.Wind.Where(x => x[0] <= a.EntryT).OrderByDescending(x => x[0]).FirstOrDefault() ?? dd.Wind.FirstOrDefault();
                        var wind = w is null ? new Wind(0, 0) : new Wind(w[1], w[2]);
                        var loadR = dd.Arrivals.Count(o => o != a && o.Rwy == a.Rwy && Math.Abs(o.ThrT - a.ThrT) < 1800);
                        var loadA = dd.Arrivals.Count(o => o != a && Math.Abs(o.ThrT - a.ThrT) < 1800);
                        var feats = RouteModel.Features(rwy, pts[0].Item1, a.Pts[0][3], a.Pts[0][4], wind, loadR, loadA);
                        var key = $"{icao}/{a.Rwy}";
                        if (!runways.TryGetValue(key, out var ri)) runways[key] = ri = runways.Count + 1;
                        bw.Write((float)airportsIdx.GetValueOrDefault(icao, 0)); bw.Write((float)ri); bw.Write(dd.HeldBack ? 1f : 0f);
                        foreach (var v in feats) bw.Write(v);
                        foreach (var v in target) bw.Write(v);
                        rows++; if (dd.HeldBack) heldRows++;
                    }
                }
            }
        }
        File.Move(bin + ".tmp", bin, true);
        var meta = new Dictionary<string, object>
        {
            ["f"] = RouteModel.F, ["p"] = RouteModel.P, ["rows"] = rows, ["held_back"] = heldRows, ["signature"] = sig,
            ["airports"] = airportsIdx, ["runways"] = runways, ["written"] = DateTime.UtcNow.ToString("u"),
        };
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta), ct);
        log.LogInformation("Learned ways in: {rows} real arrivals at {n} hubs written for training ({held} on held-back days)", rows, hubs.Count, heldRows);
    }

    /// <summary>Trains the model on the graphics card when the examples are newer than it (installing the training
    /// software the first time). The new model replaces the old one; the scoreboard then decides where it's used.</summary>
    private async Task TrainIfNeeded(CancellationToken ct)
    {
        var meta = Path.Combine(TrainDir, "meta.json");
        var outPath = Path.Combine(ModelDir, "routes.json");
        if (!File.Exists(meta)) return;
        if (File.Exists(outPath) && File.GetLastWriteTimeUtc(outPath) > File.GetLastWriteTimeUtc(meta)) return;
        var script = Path.Combine(Paths.InstallDir, "learning", "train_routes.py");
        if (!File.Exists(Paths.Python) || !File.Exists(script)) { log.LogInformation("Learned ways in: Python or the training script isn't installed ({py})", Paths.Python); return; }
        if (!await EnsureTorch(ct)) return;
        Status = "learned ways in: training on the graphics card";
        Directory.CreateDirectory(ModelDir);
        var tmpOut = Path.Combine(ModelDir, "routes-new.json");
        log.LogInformation("Learned ways in: training started");
        var (code, last) = await RunPython($"-u \"{script}\" --data \"{TrainDir}\" --out \"{tmpOut}\" --pylib \"{PyLib}\"",
            line => { if (line.StartsWith("learning from") || line.Contains("held-back days") || line.StartsWith("written") || line.StartsWith("only") || line.Contains("Error")) log.LogInformation("Learned ways in: {line}", line); },
            TimeSpan.FromHours(8), ct);
        if (code != 0 || RouteModel.Load(tmpOut) is not { } m) { log.LogInformation("Learned ways in: training didn't finish (code {code}: {last})", code, last); return; }
        File.Move(tmpOut, outPath, true);
        lock (_mgate) { _model = m; _approved.Clear(); }
        log.LogInformation("Learned ways in: model {id} ready; the scoreboard decides where it's used", m.Id);
    }

    /// <summary>
    /// PyTorch with NVIDIA's CUDA libraries, in LAIA's data folder (about 3 GB, once). The first install (0.1.331) took
    /// PyTorch's processor-only build from the general package index (torch 2.15.0+cpu — trained on the processor, not
    /// the 2080 Ti): the CUDA builds come only from PyTorch's own index, newest CUDA first, each checked by asking it for
    /// the graphics card. A processor-only install is tried again for the card once a day.
    /// </summary>
    private async Task<bool> EnsureTorch(CancellationToken ct)
    {
        var marker = Path.Combine(PyLib, "laia-torch.txt");
        var have = File.Exists(marker) ? (await File.ReadAllTextAsync(marker, ct)).Trim() : "";
        if (have.EndsWith(" cuda")) return true;
        if (have == "cpu" && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(1)) return true;
        Status = "learned ways in: installing the training software for the graphics card";
        foreach (var cu in new[] { "cu130", "cu128", "cu126", "cu124" })
        {
            log.LogInformation("Learned ways in: installing PyTorch for the graphics card (CUDA build {cu}, about 3 GB) in {dir}", cu, PyLib);
            DeleteDir(PyLib);
            Directory.CreateDirectory(PyLib);
            var (code, last) = await RunPython($"-m pip install --no-warn-script-location --disable-pip-version-check --progress-bar off --target \"{PyLib}\" " +
                $"torch --index-url https://download.pytorch.org/whl/{cu}",
                line => { if (line.StartsWith("Successfully") || line.StartsWith("ERROR")) log.LogInformation("Learned ways in: {line}", line); },
                TimeSpan.FromHours(2), ct);
            if (code != 0) { log.LogInformation("Learned ways in: {cu} didn't install ({last})", cu, last); continue; }
            var (c2, ver) = await RunPython($"-c \"import sys; sys.path.insert(0, r'{PyLib}'); import torch; ok = torch.cuda.is_available(); print(torch.__version__, torch.cuda.get_device_name(0) if ok else '', 'cuda' if ok else 'cpu')\"", _ => { }, TimeSpan.FromMinutes(5), ct);
            if (c2 == 0 && ver.EndsWith(" cuda"))
            {
                await File.WriteAllTextAsync(marker, ver, ct);
                log.LogInformation("Learned ways in: training software ready for the graphics card (PyTorch {v})", ver);
                return true;
            }
            log.LogInformation("Learned ways in: {cu} installed but doesn't see the graphics card ({ver})", cu, ver);
        }
        // No CUDA build works here: the processor-only build (training still runs, slower).
        DeleteDir(PyLib);
        Directory.CreateDirectory(PyLib);
        var (c3, l3) = await RunPython($"-m pip install --no-warn-script-location --disable-pip-version-check --progress-bar off --target \"{PyLib}\" torch --index-url https://download.pytorch.org/whl/cpu", _ => { }, TimeSpan.FromHours(1), ct);
        if (c3 != 0) { log.LogInformation("Learned ways in: the training software didn't install ({last})", l3); return false; }
        await File.WriteAllTextAsync(marker, "cpu", ct);
        log.LogInformation("Learned ways in: training on the processor (no CUDA build of PyTorch sees the graphics card)");
        return true;
    }

    private static void DeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }

    private static async Task<(int code, string last)> RunPython(string args, Action<string> onLine, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Paths.Python, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Root,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PIP_CACHE_DIR"] = Path.Combine(Path.GetTempPath(), "laia-pip-cache");
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        var last = "";
        void Line(string? l) { if (string.IsNullOrWhiteSpace(l)) return; last = l.Trim(); try { onLine(last); } catch { } }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.Start();
        try { p.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "stopped: " + (ct.IsCancellationRequested ? "LAIA closing" : "took too long")); }
        return (p.ExitCode, last);
    }

    // ------------------------------------------------------------------ the scoreboard

    public sealed class ScoreRec
    {
        public string Version { get; set; } = "";
        /// <summary>"rules", or "model &lt;id&gt;" for the learned ways in.</summary>
        public string Variant { get; set; } = "";
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

    private async Task<List<ScoreRec>> ScoreAirport(string icao, string ver, string variant, RouteModel? model, CancellationToken ct)
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
        if (prof is not null) SaveShapes(icao, prof);
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
            var apt2 = new AirportModel(data);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var score = await Task.Run(() =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.Lowest;
                return RealReplay.Replay(apt2, prof, wind, arr, start, start + 3600, new ReplaySource(), keepGoing: () => !ct.IsCancellationRequested, model: model);
            }, ct);
            ct.ThrowIfCancellationRequested();
            var rec = new ScoreRec
            {
                Version = ver, Variant = variant, Airport = icao, Day = day, Window = score.Window, Scored = score.Scored, Landed = score.Landed, Order = score.OrderSame,
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
        $"landing order as real {100 * Med(rs.Select(r => r.Order)):0}%, landing time ±{Med(rs.Select(r => r.TimeErr)):0} s, track miles {RealReplay.Score.Sg(Med(rs.Select(r => r.Miles)), "0.0")} nm, " +
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
            D("order", r => r.Order * 100, "0", false), D("time ±s", r => r.TimeErr, "0", true), D("track miles", r => r.Miles, "0.0", true), D("path nm", r => r.PathNm, "0.0", true),
        });
    }

    /// <summary>The replay brings in real arrivals with their own callsigns and types; nothing new is made up.</summary>
    private sealed class ReplaySource : IFlightSource
    {
        public AircraftIdentity NewAircraft(string airport, int maxGateSize, Random rng, IReadOnlyCollection<string>? airlines = null) => new("DAL", "B738", "DAL" + rng.Next(100, 3000), "KXXX");
        public (string callsign, string other) NewFlight(string airport, string airline, bool departure, Random rng, string type = "") => (airline + rng.Next(100, 3000), "KXXX");
    }
}
