using System.Globalization;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Every airport in the world with its position (OurAirports' public data, refreshed monthly), so a flight
/// between any two airports in the AIG schedules can be flown logically. US airports fall back to the FAA
/// runway file when this isn't downloaded yet.
/// </summary>
public sealed class WorldAirports(RunwayDb runways, ILogger<WorldAirports> log)
{
    private const string Url = "https://davidmegginson.github.io/ourairports-data/airports.csv";
    private Dictionary<string, (double lat, double lon, double elevFt)> _byCode = new(StringComparer.OrdinalIgnoreCase);
    public string Status { get; private set; } = "not loaded";
    public int Count => _byCode.Count;
    private static string File_ => Path.Combine(Paths.DataDir, "data", "world-airports.csv");

    public (double lat, double lon, double elevFt)? Find(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (_byCode.TryGetValue(code, out var p)) return p;
        var ends = runways.ForAirport(code);
        if (ends is { Count: > 0 }) return (ends.Average(e => e.Lat), ends.Average(e => e.Lon), ends.Average(e => e.ElevationFt));
        return null;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        if (_byCode.Count == 0 && File.Exists(File_)) Load(File_);
        if (File.Exists(File_) && DateTime.UtcNow - File.GetLastWriteTimeUtc(File_) < TimeSpan.FromDays(30)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
            log.LogInformation("Downloading world airports {url}", Url);
            var bytes = await http.GetByteArrayAsync(Url, ct);
            await File.WriteAllBytesAsync(File_ + ".tmp", bytes, ct);
            File.Move(File_ + ".tmp", File_, true);
            Load(File_);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("World airports download failed: {msg}", ex.Message);
            if (_byCode.Count == 0) Status = "couldn't download the world airport list: " + ex.Message;
        }
    }

    private void Load(string path)
    {
        try
        {
            var rows = Csv.Read(path).ToList();
            var h = rows[0];
            int iType = Array.IndexOf(h, "type"), iLat = Array.IndexOf(h, "latitude_deg"), iLon = Array.IndexOf(h, "longitude_deg"),
                iEl = Array.IndexOf(h, "elevation_ft"), iIcao = Array.IndexOf(h, "icao_code"), iGps = Array.IndexOf(h, "gps_code"), iId = Array.IndexOf(h, "ident");
            var map = new Dictionary<string, (double, double, double)>(StringComparer.OrdinalIgnoreCase);
            var weak = new Dictionary<string, (double, double, double)>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows.Skip(1))
            {
                if (r.Length <= Math.Max(iGps, iIcao)) continue;
                if (r[iType] is "closed" or "heliport" or "balloonport") continue;
                if (!double.TryParse(r[iLat], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                    !double.TryParse(r[iLon], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
                double.TryParse(r[iEl], NumberStyles.Float, CultureInfo.InvariantCulture, out var el);
                var p = (lat, lon, el);
                // The ICAO code wins; GPS code and ident only fill gaps (several small fields can share an ident).
                if (r[iIcao].Length > 0) map[r[iIcao]] = p;
                foreach (var k in new[] { r[iGps], r[iId] })
                    if (k.Length is >= 3 and <= 4 && !weak.ContainsKey(k)) weak[k] = p;
            }
            foreach (var (k, p) in weak) map.TryAdd(k, p);
            _byCode = map;
            Status = $"{map.Count:N0} airports worldwide";
            log.LogInformation("World airports: {n}", map.Count);
        }
        catch (Exception ex) { Status = "couldn't read the world airport list: " + ex.Message; }
    }
}

/// <summary>
/// The whole-sky schedule: every flight in the AIG flight plans (installed models only), sent by LAIA Sim
/// and kept here so it survives restarts. Asked for again once a day so new AIG updates come through.
/// </summary>
public sealed class NetworkStore(WorldAirports world, ILogger<NetworkStore> log)
{
    private readonly object _gate = new();
    private List<NetLeg> _legs = new();
    private DateTime _readUtc = DateTime.MinValue, _askedUtc = DateTime.MinValue;
    private int _locatedWith = -1, _located;
    private static string File_ => Path.Combine(Paths.DataDir, "network.tsv.gz");
    public string Status { get; private set; } = "waiting for the schedules from LAIA Sim";
    public int Count { get { lock (_gate) return _legs.Count; } }

    public void LoadCache()
    {
        try
        {
            if (!File.Exists(File_)) return;
            using var f = File.OpenRead(File_);
            var (legs, read) = NetworkModel.Parse(f);
            lock (_gate) { _legs = legs; _readUtc = read == DateTime.MinValue ? File.GetLastWriteTimeUtc(File_) : read; _locatedWith = -1; }
            log.LogInformation("Network schedule from cache: {n} flights a week", legs.Count);
        }
        catch (Exception ex) { log.LogWarning("Network cache unreadable: {msg}", ex.Message); }
    }

    /// <summary>LAIA Sim's upload: the gzipped schedule file.</summary>
    public async Task<int> AddAsync(Stream body, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct);
        ms.Position = 0;
        var bytes = ms.ToArray();
        var (legs, read) = NetworkModel.Parse(new MemoryStream(bytes));
        if (legs.Count == 0) return 0;
        lock (_gate) { _legs = legs; _readUtc = read == DateTime.MinValue ? DateTime.UtcNow : read; _locatedWith = -1; }
        try { Directory.CreateDirectory(Paths.DataDir); await File.WriteAllBytesAsync(File_, bytes, ct); } catch { }
        log.LogInformation("Network schedule received: {n} flights a week ({kb:N0} kB)", legs.Count, bytes.Length / 1024);
        return legs.Count;
    }

    /// <summary>True when the server should ask LAIA Sim for the schedules (none yet, or a day old), at most every 15 minutes.</summary>
    public bool Wanted()
    {
        lock (_gate)
        {
            if (_legs.Count > 0 && DateTime.UtcNow - _readUtc < TimeSpan.FromHours(24)) return false;
            if (DateTime.UtcNow - _askedUtc < TimeSpan.FromMinutes(15)) return false;
            _askedUtc = DateTime.UtcNow;
            return true;
        }
    }

    private void EnsureLocated()
    {
        if (_locatedWith == world.Count) return;
        _located = NetworkModel.Locate(_legs, world.Find);
        _locatedWith = world.Count;
        var missing = _legs.Where(l => !l.Located).SelectMany(l => new[] { l.Dep, l.Arr }).Where(a => world.Find(a) is null).Distinct().Take(8).ToList();
        Status = $"{_legs.Count:N0} flights a week, {_located:N0} placeable" + (missing.Count > 0 ? $" (unknown airports: {string.Join(", ", missing)})" : "") + $"; {world.Status}";
    }

    /// <summary>Flights in the air now within a radius, nearest first, leaving out the airports run in detail.</summary>
    public List<NetPos> Around(double weekMin, double lat, double lon, double radiusNm, ICollection<string> excludeRegs, int max = 1500)
    {
        lock (_gate)
        {
            EnsureLocated();
            var list = new List<(NetPos p, double d)>();
            foreach (var l in _legs)
            {
                if (!l.Located || (l.Reg.Length > 0 && excludeRegs.Contains(l.Reg))) continue;
                var p = NetworkModel.At(l, weekMin);
                if (p is null) continue;
                var d = Geo2.DistanceNm(lat, lon, p.Lat, p.Lon);
                if (d <= radiusNm) list.Add((p, d));
            }
            return list.OrderBy(x => x.d).Take(max).Select(x => x.p).ToList();
        }
    }

    /// <summary>Airports within a radius with at least this many scheduled flights a week, busiest first.</summary>
    public List<string> BusyAirportsNear(double lat, double lon, double radiusNm, int minWeekly, string except)
    {
        lock (_gate)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in _legs)
            {
                counts[l.Dep] = counts.GetValueOrDefault(l.Dep) + 1;
                counts[l.Arr] = counts.GetValueOrDefault(l.Arr) + 1;
            }
            return counts.Where(kv => kv.Value >= minWeekly && !kv.Key.Equals(except, StringComparison.OrdinalIgnoreCase))
                .Select(kv => (kv.Key, kv.Value, p: world.Find(kv.Key)))
                .Where(x => x.p is { } p && Geo2.DistanceNm(lat, lon, p.lat, p.lon) <= radiusNm)
                .OrderByDescending(x => x.Value).Select(x => x.Key).ToList();
        }
    }

    /// <summary>Of these airports, those with at least this many scheduled flights a week, busiest first.</summary>
    /// <summary>Weekly flights (departures plus arrivals) at each of these airports in the installed schedules.</summary>
    public Dictionary<string, int> WeeklyCounts(IEnumerable<string> icaos)
    {
        var want = new HashSet<string>(icaos, StringComparer.OrdinalIgnoreCase);
        var counts = want.ToDictionary(i => i, _ => 0, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            foreach (var l in _legs)
            {
                if (want.Contains(l.Dep)) counts[l.Dep]++;
                if (want.Contains(l.Arr)) counts[l.Arr]++;
            }
        return counts;
    }

    public List<string> BusyAmong(IEnumerable<string> icaos, int minWeekly)
    {
        var want = new HashSet<string>(icaos, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in _legs)
            {
                if (want.Contains(l.Dep)) counts[l.Dep] = counts.GetValueOrDefault(l.Dep) + 1;
                if (want.Contains(l.Arr)) counts[l.Arr] = counts.GetValueOrDefault(l.Arr) + 1;
            }
            return counts.Where(kv => kv.Value >= minWeekly).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        }
    }

    public int AirborneNow(double weekMin)
    {
        lock (_gate)
        {
            EnsureLocated();
            var n = 0;
            foreach (var l in _legs) if (l.Located && NetworkModel.At(l, weekMin) is not null) n++;
            return n;
        }
    }

    public NetLeg? Find(string id)
    {
        lock (_gate) return _legs.FirstOrDefault(l => l.Id == id);
    }
}

public sealed class NetworkUpdater(WorldAirports world, NetworkStore net) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        net.LoadCache();
        await Task.Delay(TimeSpan.FromSeconds(30), ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try { await world.RefreshAsync(ct); } catch (OperationCanceledException) { break; } catch { }
            try { await Task.Delay(world.Count > 0 ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(10), ct); } catch { break; }
        }
    }
}
