using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Laia.Common;
using Laia.Common.Airports;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Keeps LAIA's real timetable (RealTimetable) up to date from the LAIA Collector: the newest 28 days of real flights
/// (each day downloaded once and kept), rebuilt every 6 hours and whenever LAIA Sim's AIG timetable or model catalog
/// changes, then used at every airport where it's good — for that airport's traffic and for the whole sky's flights to
/// and from it (NetworkStore), the AIG timetable everywhere else.
/// </summary>
public sealed class RealTimetableService(SettingsStore settings, NetworkStore network, FlightSource source, WorldAirports world,
    ILogger<RealTimetableService> log) : BackgroundService
{
    private readonly object _gate = new();
    private Dictionary<string, List<ScheduleLeg>> _byAirport = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _good = new(StringComparer.OrdinalIgnoreCase);
    public string Status { get; private set; } = "real timetable: waiting for the LAIA Collector";
    private int _builtFor = -1;
    private int _catalogSig = -1;

    private sealed record LegDay([property: JsonPropertyName("day")] string Day, [property: JsonPropertyName("flights")] int Flights);

    /// <summary>The real timetable for an airport, when it's good there (null: use the AIG timetable).</summary>
    public ScheduleData? For(string icao)
    {
        lock (_gate)
        {
            if (!_good.Contains(icao) || !_byAirport.TryGetValue(icao, out var legs)) return null;
            return new ScheduleData { Airport = icao.ToUpperInvariant(), Source = "ADS-B (LAIA Collector)", Legs = legs.ToList() };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), ct).ContinueWith(_ => { });
        var last = DateTime.MinValue;
        var lastLook = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sig = source.CatalogSignature;
                // Every 15 minutes, a look at the collector's newest day: a new one (or the first ones, after an update
                // of the collector) is built in at once rather than at the 6-hour rebuild (24 Nov: it waited 6 hours).
                var newDay = false;
                if (DateTime.UtcNow - lastLook > TimeSpan.FromMinutes(15)) { lastLook = DateTime.UtcNow; newDay = await NewestDayAsync(ct) is { } nd && nd != _builtDay; }
                if (network.AigVersion != _builtFor || sig != _catalogSig || newDay || DateTime.UtcNow - last > TimeSpan.FromHours(6))
                {
                    if (network.AigCount > 0) { await RebuildAsync(ct); _builtFor = network.AigVersion; _catalogSig = sig; last = DateTime.UtcNow; }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Status = "real timetable: " + ex.Message; log.LogWarning("Real timetable failed: {msg}", ex.Message); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch { break; }
        }
    }

    private string? _builtDay;

    private string Source()
    {
        var src = (settings.Get("track_source") ?? "").Trim();
        if (src.Length == 0) src = AdsbHistory.DefaultSource;
        return src.TrimEnd('/');
    }

    /// <summary>The collector's newest day with flights (null: none, or it can't be reached); also notes when the
    /// collector is an older version without them (its /api/legdays unknown).</summary>
    private async Task<string?> NewestDayAsync(CancellationToken ct)
    {
        var src = Source();
        if (src.Equals("off", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var r = await http.GetAsync($"{src}/api/legdays", ct);
            if (r.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Status = "real timetable: the LAIA Collector on the NAS needs updating — it's an older version without the real flights (Container Manager: stop and clean the laia-collector project, delete its image, build it again)";
                return null;
            }
            if (!r.IsSuccessStatusCode) return null;
            var days = JsonSerializer.Deserialize<List<LegDay>>(await r.Content.ReadAsStringAsync(ct)) ?? new();
            return days.Where(d => d.Flights > 0).Select(d => d.Day).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private async Task RebuildAsync(CancellationToken ct)
    {
        var src = (settings.Get("track_source") ?? "").Trim();
        if (src.Length == 0) src = AdsbHistory.DefaultSource;
        if (src.Equals("off", StringComparison.OrdinalIgnoreCase)) { Status = "real timetable: off (no LAIA Collector)"; return; }
        src = src.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
        List<LegDay> days;
        try { days = JsonSerializer.Deserialize<List<LegDay>>(await http.GetStringAsync($"{src}/api/legdays", ct)) ?? new(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = ex.Message.Contains("404") ? "real timetable: the LAIA Collector on the NAS needs updating — it's an older version without the real flights (Container Manager: stop and clean the laia-collector project, delete its image, build it again)"
                : $"real timetable: the LAIA Collector can't be reached ({ex.Message})";
            return;
        }
        days = days.Where(d => d.Flights > 0).OrderByDescending(d => d.Day, StringComparer.Ordinal).Take(28).ToList();
        _builtDay = days.FirstOrDefault()?.Day;
        if (days.Count == 0) { Status = "real timetable: the LAIA Collector has no flights yet (it reads its days again for them first)"; return; }
        var dir = Path.Combine(Paths.DataDir, "timetable");
        Directory.CreateDirectory(dir);
        var all = new List<List<TraceLegs.Leg>>();
        foreach (var d in days)
        {
            var file = Path.Combine(dir, d.Day + ".json.gz");
            byte[] bytes;
            if (File.Exists(file) && new FileInfo(file).Length > 0) bytes = await File.ReadAllBytesAsync(file, ct);
            else { bytes = await http.GetByteArrayAsync($"{src}/api/legs/{d.Day}", ct); await File.WriteAllBytesAsync(file, bytes, ct); }
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            await gz.CopyToAsync(raw, ct);
            all.Add(TraceLegs.Read(raw.ToArray()));
        }
        foreach (var old in Directory.GetFiles(dir, "*.json.gz").Where(f => !days.Any(d => Path.GetFileName(f).StartsWith(d.Day)))) { try { File.Delete(old); } catch { } }
        // What the AIG timetable knows: airframes by registration (exact model, livery, brand), and each callsign's ends.
        var aig = network.AigLegs();
        var byReg = new Dictionary<string, RealTimetable.AigInfo>(StringComparer.OrdinalIgnoreCase);
        var route = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var aigWeekly = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in aig)
        {
            if (l.Reg.Length > 0 && l.Title.Length > 0) byReg[l.Reg] = new RealTimetable.AigInfo(l.Airline, l.Title, l.Operator);
            route[l.Callsign] = (l.Dep, l.Arr);
            aigWeekly[l.Dep] = aigWeekly.GetValueOrDefault(l.Dep) + 1;
            aigWeekly[l.Arr] = aigWeekly.GetValueOrDefault(l.Arr) + 1;
        }
        var res = RealTimetable.Build(all, source.HasLivery, byReg, route, c => world.Find(c) is { } p ? (p.lat, p.lon) : null);
        var good = RealTimetable.GoodAirports(res, c => aigWeekly.GetValueOrDefault(c));
        var byAirport = new Dictionary<string, List<ScheduleLeg>>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in res.Legs)
        {
            if (good.Contains(l.Dep)) (byAirport.TryGetValue(l.Dep, out var a) ? a : byAirport[l.Dep] = new()).Add(l);
            if (good.Contains(l.Arr)) (byAirport.TryGetValue(l.Arr, out var b) ? b : byAirport[l.Arr] = new()).Add(l);
        }
        lock (_gate) { _byAirport = byAirport; _good = good; }
        network.ApplyTimetable(good, res.Legs);
        Status = $"real timetable (ADS-B, {days.Count} days to {days[0].Day}): {res.Summary}; used at {good.Count} airports";
        log.LogInformation("{s}", Status);
        var top = byAirport.OrderByDescending(kv => kv.Value.Count).Take(10).Select(kv => $"{kv.Key} {kv.Value.Count} (AIG {aigWeekly.GetValueOrDefault(kv.Key)})");
        log.LogInformation("Real timetable, busiest airports it's used at (flights a week): {t}", string.Join(", ", top));
    }
}
