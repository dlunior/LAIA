using System.Text.Json;
using Laia.Common.Airports;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Picks airlines, aircraft types and flight numbers for AI traffic: only types the user has models for
/// (from LAIA Sim's catalog of installed AIG aircraft), and, when AirLabs is chosen as the schedule source,
/// the airlines and destinations really flying from that airport today.
/// </summary>
public sealed class FlightSource(SettingsStore settings, ILogger<FlightSource> log) : IFlightSource
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), DefaultRequestHeaders = { { "User-Agent", "LAIA-Server" } } };
    private readonly object _gate = new();
    private List<CatalogEntry> _catalog = new();
    private readonly Dictionary<string, List<SchedFlight>> _sched = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fetching = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _cursor = new();
    public int AirLabsCalls { get; private set; }
    public bool HasCatalog { get { lock (_gate) return _catalog.Count > 0; } }
    public string ScheduleStatus { get; private set; } = "";

    private sealed record SchedFlight(string Airline, string Callsign, string Other, bool Departure, string When = "");

    // Fallback when no AIG models were found: common North American airline types.
    private static readonly CatalogEntry[] Fallback =
    {
        new("DAL", "B739", 3), new("DAL", "A321", 3), new("DAL", "A320", 2), new("UAL", "B738", 3), new("UAL", "A320", 2), new("AAL", "A321", 3),
        new("AAL", "B738", 3), new("SWA", "B737", 3), new("SWA", "B38M", 2), new("JBU", "A320", 2), new("ACA", "BCS3", 2), new("ASA", "B739", 2),
        new("NKS", "A320", 2), new("SKW", "E75L", 2), new("RPA", "E75L", 2), new("UAL", "B772", 1), new("DAL", "A333", 1),
    };

    // Airports used for made-up flights (when there's no real schedule), with positions for realistic distances.
    private static readonly Dictionary<string, (double lat, double lon)> Airports = new()
    {
        ["KATL"] = (33.64, -84.43), ["KORD"] = (41.98, -87.90), ["KDFW"] = (32.90, -97.04), ["KDEN"] = (39.86, -104.67), ["KLAX"] = (33.94, -118.41),
        ["KJFK"] = (40.64, -73.78), ["KLGA"] = (40.78, -73.87), ["KEWR"] = (40.69, -74.17), ["KBOS"] = (42.36, -71.01), ["KMCO"] = (28.43, -81.31),
        ["KMIA"] = (25.79, -80.29), ["KSEA"] = (47.45, -122.31), ["KSFO"] = (37.62, -122.38), ["KLAS"] = (36.08, -115.15), ["KPHX"] = (33.43, -112.01),
        ["KIAH"] = (29.98, -95.34), ["KMSP"] = (44.88, -93.22), ["KDTW"] = (42.21, -83.35), ["KPHL"] = (39.87, -75.24), ["KCLT"] = (35.21, -80.94),
        ["KBWI"] = (39.18, -76.67), ["KDCA"] = (38.85, -77.04), ["KIAD"] = (38.95, -77.46), ["KSLC"] = (40.79, -111.98), ["KSAN"] = (32.73, -117.19),
        ["KTPA"] = (27.98, -82.53), ["KFLL"] = (26.07, -80.15), ["KBNA"] = (36.12, -86.68), ["KAUS"] = (30.19, -97.67), ["KMSY"] = (29.99, -90.26),
        ["KSTL"] = (38.75, -90.37), ["KRDU"] = (35.88, -78.79), ["KPIT"] = (40.49, -80.23), ["KCLE"] = (41.41, -81.85), ["KCMH"] = (39.998, -82.89),
        ["KIND"] = (39.72, -86.29), ["KMCI"] = (39.30, -94.71), ["KMKE"] = (42.95, -87.90), ["KRIC"] = (37.51, -77.32), ["KORF"] = (36.89, -76.20),
        ["KBUF"] = (42.94, -78.73), ["KROC"] = (43.12, -77.67), ["KSYR"] = (43.11, -76.11), ["KALB"] = (42.75, -73.80), ["KBTV"] = (44.47, -73.15),
        ["KPWM"] = (43.65, -70.31), ["KCHS"] = (32.90, -80.04), ["KSAV"] = (32.13, -81.20), ["KJAX"] = (30.49, -81.69), ["KRSW"] = (26.54, -81.76),
        ["KPBI"] = (26.68, -80.10), ["KMEM"] = (35.04, -89.98), ["KCVG"] = (39.05, -84.67), ["KSDF"] = (38.17, -85.74), ["KGSO"] = (36.10, -79.94),
        ["KPDX"] = (45.59, -122.60), ["KHOU"] = (29.65, -95.28), ["KDAL"] = (32.85, -96.85), ["KMDW"] = (41.79, -87.75), ["KSJC"] = (37.36, -121.93),
        ["CYYZ"] = (43.68, -79.63), ["CYUL"] = (45.47, -73.74), ["CYVR"] = (49.19, -123.18), ["CYYC"] = (51.13, -114.01), ["CYOW"] = (45.32, -75.67),
        ["CYHZ"] = (44.88, -63.51), ["CYWG"] = (49.91, -97.24), ["CYEG"] = (53.31, -113.58), ["CYQB"] = (46.79, -71.39), ["CYTZ"] = (43.63, -79.40),
    };

    // Where the big airlines mostly fly from and to (their hubs).
    private static readonly Dictionary<string, string[]> Hubs = new()
    {
        ["DAL"] = new[] { "KATL", "KDTW", "KMSP", "KSLC", "KSEA", "KBOS", "KJFK", "KLAX", "KLGA" },
        ["EDV"] = new[] { "KATL", "KDTW", "KMSP", "KJFK", "KLGA", "KCVG" },
        ["AAL"] = new[] { "KDFW", "KCLT", "KORD", "KMIA", "KPHL", "KPHX", "KDCA", "KJFK", "KLAX", "KLGA" },
        ["ENY"] = new[] { "KDFW", "KORD", "KMIA", "KLGA", "KDCA", "KJFK" },
        ["PDT"] = new[] { "KCLT", "KPHL", "KDCA" }, ["JIA"] = new[] { "KCLT", "KPHL", "KDCA" },
        ["UAL"] = new[] { "KORD", "KDEN", "KIAH", "KEWR", "KSFO", "KIAD", "KLAX" },
        ["ACA"] = new[] { "CYYZ", "CYUL", "CYVR", "CYYC" }, ["JZA"] = new[] { "CYYZ", "CYUL", "CYOW", "CYHZ", "CYQB" },
        ["ASA"] = new[] { "KSEA", "KPDX", "KSFO", "KLAX", "KSAN" }, ["JBU"] = new[] { "KBOS", "KJFK", "KFLL", "KMCO" },
        ["WJA"] = new[] { "CYYC", "CYYZ", "CYVR" }, ["POE"] = new[] { "CYTZ" },
    };

    /// <summary>The other end of a made-up flight: a distance the type can fly, often the airline's hub.</summary>
    private static string PickOther(string airport, string airline, string type, Random rng)
    {
        var maxNm = Perf.For(type).Family switch
        {
            "turboprop" => 450, "regional jet" => 1100, "large regional" => 1700, "narrowbody" => 2600, "757" => 3300, _ => 6000,
        };
        var minNm = Perf.For(type).Family is "widebody" or "heavy" ? 900 : 120;
        var lgaPerimeter = airport == "KLGA"; // LaGuardia's 1,500-mile perimeter rule (Denver excepted)
        var here = Airports.TryGetValue(airport, out var h) ? h : ((double, double)?)null;
        bool Fits(string icao)
        {
            if (icao == airport || !Airports.TryGetValue(icao, out var p)) return false;
            if (here is null) return true;
            var nm = Geo2.DistanceNm(here.Value.Item1, here.Value.Item2, p.lat, p.lon);
            if (lgaPerimeter && icao != "KDEN" && nm > 1300) return false;
            return nm >= minNm && nm <= maxNm;
        }
        // Regional partners fly to their mainline partner's hubs.
        if (Hubs.TryGetValue(airline, out var hubs) && rng.NextDouble() < 0.7)
        {
            var ok = hubs.Where(Fits).ToList();
            if (ok.Count > 0) return ok[rng.Next(ok.Count)];
        }
        var all = Airports.Keys.Where(Fits).ToList();
        // Mostly domestic: Canadian airlines to Canada (and some US), US airlines to the US.
        var canadian = airline is "ACA" or "JZA" or "WJA" or "POE" or "TSC" or "SWG" or "FLE" or "ROU" or "SKV";
        var pref = all.Where(a => canadian ? a.StartsWith('C') || rng.NextDouble() < 0.3 : a.StartsWith('K') || rng.NextDouble() < 0.15).ToList();
        if (pref.Count > 0) return pref[rng.Next(pref.Count)];
        return all.Count > 0 ? all[rng.Next(all.Count)] : "KATL";
    }

    private static string CatalogFile => Path.Combine(Paths.DataDir, "catalog.json");
    private bool _catalogLoaded;

    public void SetCatalog(TrafficCatalog c)
    {
        lock (_gate) _catalog = c.Entries.Where(e => e.Airline.Length is 2 or 3 && e.Type.Length >= 3).ToList();
        log.LogInformation("AI model catalog from LAIA Sim: {n} airline/type combinations", _catalog.Count);
        // Kept on disk, so after a restart the traffic uses the installed models from the start.
        try { Directory.CreateDirectory(Paths.DataDir); File.WriteAllText(CatalogFile, JsonSerializer.Serialize(c)); } catch { }
    }

    /// <summary>The catalog from the last session, until LAIA Sim sends the current one.</summary>
    private void LoadCatalog()
    {
        if (_catalogLoaded) return;
        _catalogLoaded = true;
        try
        {
            if (!File.Exists(CatalogFile)) return;
            var c = JsonSerializer.Deserialize<TrafficCatalog>(File.ReadAllText(CatalogFile));
            if (c is not null && _catalog.Count == 0) _catalog = c.Entries.Where(e => e.Airline.Length is 2 or 3 && e.Type.Length >= 3).ToList();
        }
        catch { }
    }

    /// <summary>Installed models are known (from LAIA Sim, or from the last session).</summary>
    public bool CatalogKnown { get { lock (_gate) { LoadCatalog(); return _catalog.Count > 0; } } }

    private List<CatalogEntry> Catalog()
    {
        lock (_gate) { LoadCatalog(); return _catalog.Count > 0 ? _catalog : Fallback.ToList(); }
    }

    // Airlines that typically fly at airports in each region (used when there's no real schedule).
    private static readonly HashSet<string> NorthAmerica = new()
    {
        "DAL", "UAL", "AAL", "SWA", "JBU", "ASA", "NKS", "FFT", "SKW", "RPA", "ENY", "EDV", "JIA", "PDT", "AAY", "SCX", "HAL", "MXY", "BRE", "QXE",
        "GJS", "ASH", "CPZ", "UCA", "ACA", "WJA", "JZA", "POE", "TSC", "SWG", "ROU", "FLE", "WSW", "VXP", "SKV", "AWI",
    };
    private static readonly HashSet<string> Europe = new()
    {
        "BAW", "EZY", "RYR", "DLH", "AFR", "KLM", "IBE", "VLG", "SAS", "FIN", "AUA", "SWR", "TAP", "ITY", "BEL", "LOT", "WZZ", "EIN", "EWG", "TRA",
        "NAX", "NOZ", "TOM", "EXS", "VIR", "SHT", "BEE", "LOG", "CFE", "AEE", "THY", "PGT", "CSA", "AEA", "VOE", "TVS", "DLA", "CLH", "HOP", "BCS",
    };

    private static HashSet<string>? RegionFor(string airport) => airport.Length == 4 ? airport[0] switch
    {
        'K' or 'C' or 'P' => NorthAmerica,
        'E' or 'L' => Europe,
        _ => null,
    } : null;

    public AircraftIdentity NewAircraft(string airport, int maxGateSize, Random rng, IReadOnlyCollection<string>? airlines = null)
    {
        var cat = Catalog().Where(e => Perf.For(e.Type).GateSize <= maxGateSize).ToList();
        // A stand that belongs to particular airlines gets one of theirs (if a model is installed).
        if (airlines is { Count: > 0 })
        {
            var region0 = RegionFor(airport);
            var mine = cat.Where(e => airlines.Contains(e.Airline) && (region0 is null || region0.Contains(e.Airline))).ToList();
            if (mine.Count > 0)
            {
                var g = mine.GroupBy(e => e.Airline).ToList();
                var pickOwn = WeightedPick(g[rng.Next(g.Count)].ToList(), rng);
                var (ocs, oother) = NewFlight(airport, pickOwn.Airline, true, rng, pickOwn.Type);
                return new AircraftIdentity(pickOwn.Airline, pickOwn.Type, ocs, oother);
            }
        }
        var region = RegionFor(airport);
        if (region is not null)
        {
            var local = cat.Where(e => region.Contains(e.Airline)).ToList();
            if (local.Count > 0) cat = local;
            else
            {
                // No local airline has a type this small: use the region's smallest types rather than a foreign airline.
                var regional = Catalog().Where(e => region.Contains(e.Airline)).ToList();
                if (regional.Count > 0)
                {
                    var smallest = regional.Min(e => Perf.For(e.Type).GateSize);
                    cat = regional.Where(e => Perf.For(e.Type).GateSize == smallest).ToList();
                }
            }
        }
        // Nothing small enough for this gate: take the smallest types available rather than nothing.
        if (cat.Count == 0)
        {
            var all = Catalog();
            var regional = region is null ? all : all.Where(e => region.Contains(e.Airline)).ToList();
            if (regional.Count == 0) regional = all;
            var smallest = regional.Min(e => Perf.For(e.Type).GateSize);
            cat = regional.Where(e => Perf.For(e.Type).GateSize == smallest).ToList();
        }
        if (cat.Count == 0) cat = Fallback.ToList();
        CatalogEntry pick;
        var sched = Schedule(airport);
        var schedAirlines = sched.Select(s => s.Airline).Where(a => cat.Any(c => c.Airline == a) && (region is null || region.Contains(a))).ToList();
        if (schedAirlines.Count > 0)
        {
            var airline = schedAirlines[rng.Next(schedAirlines.Count)];
            var types = cat.Where(c => c.Airline == airline).ToList();
            pick = WeightedPick(types, rng);
        }
        else
        {
            // Weight each airline equally (not by how many liveries it has), then pick a type.
            var byAirline = cat.GroupBy(c => c.Airline).ToList();
            var g = byAirline[rng.Next(byAirline.Count)].ToList();
            pick = WeightedPick(g, rng);
        }
        var (cs, other) = NewFlight(airport, pick.Airline, true, rng, pick.Type);
        return new AircraftIdentity(pick.Airline, pick.Type, cs, other);
    }

    private static CatalogEntry WeightedPick(List<CatalogEntry> list, Random rng)
    {
        var total = list.Sum(e => Math.Max(1, e.Count));
        var r = rng.Next(total);
        foreach (var e in list) { r -= Math.Max(1, e.Count); if (r < 0) return e; }
        return list[^1];
    }

    public (string callsign, string other) NewFlight(string airport, string airline, bool departure, Random rng, string type = "")
    {
        var sched = Schedule(airport).Where(s => s.Airline == airline && s.Departure == departure).ToList();
        if (sched.Count > 0)
        {
            var key = $"{airport}/{airline}/{departure}";
            int i;
            lock (_gate) { i = _cursor.GetValueOrDefault(key); _cursor[key] = i + 1; }
            var s = sched[i % sched.Count];
            return (s.Callsign, s.Other);
        }
        var number = airline switch { "SKW" or "RPA" or "ENY" or "EDV" or "JIA" or "PDT" => rng.Next(3000, 6000), _ => rng.Next(100, 2999) };
        return ($"{airline}{number}", PickOther(airport, airline, type, rng));
    }

    private List<SchedFlight> Schedule(string airport)
    {
        lock (_gate)
        {
            if (_sched.TryGetValue(airport, out var list)) return list;
        }
        if (!string.IsNullOrEmpty(settings.Get("airlabs_key"))) _ = FetchAsync(airport);
        return new List<SchedFlight>();
    }

    // ------------------------------------------------------------------ real gates

    private readonly Dictionary<string, RealGates> _gates = new(StringComparer.OrdinalIgnoreCase);
    private static string GatesFile(string airport) => Path.Combine(Paths.DataDir, "gates", airport.ToUpperInvariant() + ".json");

    /// <summary>
    /// The gates the airlines really use at the airport: learned from AirLabs flight data (each flight's terminal
    /// and gate) and kept on disk, adding to it every time. Fetches when due (the same two calls as the schedule).
    /// </summary>
    public RealGates? Gates(string airport)
    {
        Schedule(airport); // fetches (once per 6 hours) when there's an AirLabs key
        _ = PoolAsync();   // the SimRouteGen gate pool (free; checked every 6 hours)
        lock (_gate)
        {
            var g = GatesFor(airport);
            var pool = _pool?.GetValueOrDefault(PoolCode(airport));
            if (g.ByAirline.Count == 0 && (pool is null || pool.Count == 0)) return null;
            // A copy for the traffic to use (AirLabs + the pool), made again only when something new came in.
            var version = g.Version * 100000 + _poolVersion;
            if (!_gateCopies.TryGetValue(airport, out var c) || c.Version != version)
            {
                c = JsonSerializer.Deserialize<RealGates>(JsonSerializer.Serialize(g))!;
                c.ByAirline = new(c.ByAirline, StringComparer.OrdinalIgnoreCase);
                c.ByFlight = new(c.ByFlight, StringComparer.OrdinalIgnoreCase);
                var sources = new List<string>();
                if (g.ByAirline.Count > 0) sources.Add("AirLabs");
                if (pool is { Count: > 0 })
                {
                    sources.Add("SimRouteGen");
                    foreach (var e in pool)
                    {
                        if (!c.ByAirline.TryGetValue(e.Airline, out var gates)) c.ByAirline[e.Airline] = gates = new(StringComparer.OrdinalIgnoreCase);
                        var key = RealGates.Key(e.Terminal, e.Gate);
                        gates[key] = gates.GetValueOrDefault(key) + Math.Max(1, e.Count);
                    }
                }
                c.Source = string.Join(" + ", sources);
                c.Version = version;
                _gateCopies[airport] = c;
            }
            return c;
        }
    }

    // ------------------------------------------------------------------ SimRouteGen's shared gate pool

    /// <summary>
    /// The gates every copy of SimRouteGen has seen in real flight data (AeroAPI), shared on its Cloudflare Worker:
    /// airline, airport, gate and how often it was seen, with each airline's terminal. Free to download; the
    /// version is checked every 6 hours and the pool downloaded again only when it changed.
    /// </summary>
    private sealed record PoolGate(string Airline, string Gate, string Terminal, int Count);
    private Dictionary<string, List<PoolGate>>? _pool;   // IATA airport → gates
    private int _poolVersion;
    private DateTime _poolChecked = DateTime.MinValue;
    private int _poolBusy;
    private string PoolBase => (settings.Get("routegen_pool_url") ?? "https://dfg.dlunior10.workers.dev").TrimEnd('/');
    private static string PoolFile => Path.Combine(Paths.DataDir, "cache", "simroutegen-pool.json");

    /// <summary>The pool's airport code (IATA): KLGA → LGA, CYUL → YUL.</summary>
    private static string PoolCode(string icao)
    {
        icao = icao.ToUpperInvariant();
        if (icao.Length == 4 && (icao[0] == 'K' || (icao[0] == 'C' && icao[1] == 'Y'))) return icao[1..];
        return icao;
    }

    // SimRouteGen's airline keys → ICAO.
    private static readonly Dictionary<string, string> PoolAirlines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DL"] = "DAL", ["DLNA"] = "DAL", ["AA"] = "AAL", ["UA"] = "UAL", ["WN"] = "SWA", ["AS"] = "ASA", ["HA"] = "HAL",
        ["B6"] = "JBU", ["AC"] = "ACA", ["WS"] = "WJA", ["F9"] = "FFT", ["NK"] = "NKS",
    };

    private async Task PoolAsync()
    {
        if (DateTime.UtcNow - _poolChecked < TimeSpan.FromHours(6) || Interlocked.CompareExchange(ref _poolBusy, 1, 0) != 0) return;
        try
        {
            _poolChecked = DateTime.UtcNow;
            string? body = null, version = null;
            if (_pool is null && File.Exists(PoolFile)) body = await File.ReadAllTextAsync(PoolFile); // the last copy, straight away
            if (body is not null) LoadPool(body, "on file");
            try
            {
                using var meta = JsonDocument.Parse(await Http.GetStringAsync(PoolBase + "/shared?v=1"));
                version = meta.RootElement.TryGetProperty("version", out var v) ? v.ToString() : null;
            }
            catch (Exception ex)
            {
                log.LogInformation("SimRouteGen gate pool not reachable: {msg}", ex.Message);
                _poolChecked = DateTime.UtcNow - TimeSpan.FromHours(5.5); // try again in half an hour
                return;
            }
            if (version is null || version == _poolVersionText) return;
            var snap = await Http.GetStringAsync(PoolBase + "/shared?ver=" + Uri.EscapeDataString(version));
            Directory.CreateDirectory(Path.GetDirectoryName(PoolFile)!);
            await File.WriteAllTextAsync(PoolFile, snap);
            LoadPool(snap, "downloaded");
        }
        catch (Exception ex) { log.LogWarning("SimRouteGen gate pool failed: {msg}", ex.Message); }
        finally { Interlocked.Exchange(ref _poolBusy, 0); }
    }

    private string? _poolVersionText;

    private void LoadPool(string json, string how)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cut = DateTimeOffset.UtcNow.AddDays(-183).ToUnixTimeMilliseconds(); // the pool's own six-month life
        static string Str(JsonElement a, int i) => a.GetArrayLength() > i ? (a[i].ValueKind == JsonValueKind.String ? a[i].GetString() ?? "" : a[i].ValueKind == JsonValueKind.Number ? a[i].ToString() : "") : "";
        static double Num(JsonElement a, int i) => a.GetArrayLength() > i && a[i].ValueKind == JsonValueKind.Number ? a[i].GetDouble() : double.TryParse(Str(a, i), out var d) ? d : 0;
        // Terminals: [airline, airport, terminal, time] — the newest per airline and airport.
        var terminals = new Dictionary<string, (string term, double t)>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("m", out var m) && m.ValueKind == JsonValueKind.Array)
            foreach (var r in m.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Array) continue;
                var k = Str(r, 0) + "|" + Str(r, 1);
                var t = Num(r, 3);
                if (!terminals.TryGetValue(k, out var old) || t >= old.t) terminals[k] = (Str(r, 2), t);
            }
        // Gates: [airline, airport, gate, times seen, last seen].
        var pool = new Dictionary<string, List<PoolGate>>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        if (root.TryGetProperty("g", out var g) && g.ValueKind == JsonValueKind.Array)
            foreach (var r in g.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Array) continue;
                if (Num(r, 4) > 0 && Num(r, 4) < cut) continue;
                var al = Str(r, 0);
                var airline = PoolAirlines.TryGetValue(al, out var ic) ? ic : AirlineCodes.ToIcao(al);
                var ap = Str(r, 1).ToUpperInvariant();
                var gate = Str(r, 2).Trim();
                if (airline.Length != 3 || ap.Length == 0 || gate.Length == 0) continue;
                var term = terminals.TryGetValue(al + "|" + ap, out var tt) ? tt.term : "";
                if (!pool.TryGetValue(ap, out var list)) pool[ap] = list = new();
                list.Add(new PoolGate(airline, gate, term, (int)Math.Max(1, Num(r, 3))));
                n++;
            }
        lock (_gate)
        {
            _pool = pool;
            _poolVersion++;
            _poolVersionText = root.TryGetProperty("version", out var v) ? v.ToString() : _poolVersionText;
        }
        log.LogInformation("SimRouteGen gate pool ({how}): {n} gates at {a} airports", how, n, pool.Count);
    }

    private readonly Dictionary<string, RealGates> _gateCopies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call with _gate held.</summary>
    private RealGates GatesFor(string airport)
    {
        if (_gates.TryGetValue(airport, out var g)) return g;
        try { if (File.Exists(GatesFile(airport))) g = JsonSerializer.Deserialize<RealGates>(File.ReadAllText(GatesFile(airport))); } catch { }
        g ??= new RealGates { Airport = airport.ToUpperInvariant(), Source = "AirLabs" };
        g.ByAirline = new(g.ByAirline, StringComparer.OrdinalIgnoreCase);
        g.ByFlight = new(g.ByFlight, StringComparer.OrdinalIgnoreCase);
        return _gates[airport] = g;
    }

    // Regional airlines (IATA) that fly for a mainline brand: their gates count for the brand too.
    private static readonly HashSet<string> RegionalIata = new() { "OO", "YX", "9E", "MQ", "OH", "PT", "ZW", "YV", "G7", "C5", "QX", "QK", "CP", "9K" };

    private void LearnGates(string airport, bool dep, JsonElement f, Func<string, string> S)
    {
        var gate = S(dep ? "dep_gate" : "arr_gate");
        if (gate.Length == 0) return;
        var terminal = S(dep ? "dep_terminal" : "arr_terminal");
        var date = S(dep ? "dep_time" : "arr_time");
        date = date.Length >= 10 ? date[..10] : date;
        var airline = S("airline_icao");
        var cs = S("flight_icao");
        var csIata = S("cs_airline_iata");
        lock (_gate)
        {
            var g = GatesFor(airport);
            bool added;
            if (csIata.Length > 0)
            {
                // A codeshare: count the gate for the selling airline only when a regional flies it for them
                // (a Delta Connection flight at Delta's gates), not for partner codeshares (AA on an Alaska flight).
                if (!RegionalIata.Contains(csIata)) return;
                added = g.Add(airline, "", date + cs, terminal, gate);
            }
            else added = g.Add(airline, cs, date, terminal, gate);
            if (added) g.Version++;
        }
    }

    private void SaveGates(string airport)
    {
        try
        {
            RealGates? g;
            string json;
            lock (_gate)
            {
                g = _gates.GetValueOrDefault(airport);
                if (g is null || g.ByAirline.Count == 0) return;
                g.UpdatedUtc = DateTime.UtcNow;
                json = JsonSerializer.Serialize(g);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(GatesFile(airport))!);
            File.WriteAllText(GatesFile(airport), json);
            var n = g.ByAirline.Sum(a => a.Value.Values.Sum());
            log.LogInformation("Real gates at {apt}: {n} flights with a gate so far, {a} airlines ({list})", airport, n, g.ByAirline.Count,
                string.Join(", ", g.ByAirline.OrderByDescending(a => a.Value.Values.Sum()).Take(10)
                    .Select(a => $"{a.Key} {string.Join("/", a.Value.OrderByDescending(x => x.Value).Take(4).Select(x => x.Key.Replace("|", " ").Trim()))}")));
        }
        catch (Exception ex) { log.LogWarning("Couldn't save real gates for {apt}: {msg}", airport, ex.Message); }
    }

    /// <summary>Two AirLabs calls per airport (departures and arrivals), cached on disk for 6 hours.</summary>
    private async Task FetchAsync(string airport)
    {
        lock (_gate) { if (!_fetching.Add(airport)) return; }
        try
        {
            var key = settings.Get("airlabs_key");
            if (string.IsNullOrEmpty(key)) return;
            var list = new List<SchedFlight>();
            foreach (var dep in new[] { true, false })
            {
                var cache = Path.Combine(Paths.DataDir, "cache", $"airlabs-{airport}-{(dep ? "dep" : "arr")}.json");
                string? body = null;
                if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromHours(6)) body = await File.ReadAllTextAsync(cache);
                else
                {
                    var url = $"https://airlabs.co/api/v9/schedules?{(dep ? "dep_icao" : "arr_icao")}={airport}&api_key={Uri.EscapeDataString(key)}";
                    AirLabsCalls++;
                    log.LogInformation("AirLabs schedule call {n} this session: {dir} for {apt}", AirLabsCalls, dep ? "departures" : "arrivals", airport);
                    using var resp = await Http.GetAsync(url);
                    body = await resp.Content.ReadAsStringAsync();
                    if (resp.IsSuccessStatusCode)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                        await File.WriteAllTextAsync(cache, body);
                    }
                }
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("response", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var f in arr.EnumerateArray())
                {
                    string S(string n) => f.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    LearnGates(airport, dep, f, S);
                    var airline = S("airline_icao");
                    var cs = S("flight_icao");
                    var other = dep ? S("arr_icao") : S("dep_icao");
                    // Skip codeshares (the same flight sold under another airline's number).
                    if (airline.Length != 3 || cs.Length < 4 || S("cs_flight_iata").Length > 0 || S("cs_airline_iata").Length > 0 || S("cs_flight_number").Length > 0) continue;
                    // AirLabs lists codeshares as separate flights at the same minute: keep one per departure time
                    // and destination, preferring an airline that actually flies in this region.
                    var when = S(dep ? "dep_time" : "arr_time");
                    var dup = list.FindIndex(x => x.Departure == dep && x.Other == other && x.When == when && when.Length > 0);
                    var region = RegionFor(airport);
                    if (dup >= 0)
                    {
                        if (region is not null && !region.Contains(list[dup].Airline) && region.Contains(airline)) list[dup] = new SchedFlight(airline, cs, other, dep, when);
                        continue;
                    }
                    list.Add(new SchedFlight(airline, cs, other, dep, when));
                }
            }
            lock (_gate) _sched[airport] = list;
            SaveGates(airport);
            ScheduleStatus = $"AirLabs: {list.Count} real flights at {airport} ({AirLabsCalls} calls this session)";
            log.LogInformation("{status}", ScheduleStatus);
        }
        catch (Exception ex)
        {
            log.LogWarning("AirLabs schedule for {apt} failed: {msg}", airport, ex.Message);
            lock (_gate) _sched[airport] = new List<SchedFlight>();
        }
        finally { lock (_gate) _fetching.Remove(airport); }
    }
}
