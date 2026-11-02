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

    private sealed record SchedFlight(string Airline, string Callsign, string Other, bool Departure);

    // Fallback when no AIG models were found: common North American airline types.
    private static readonly CatalogEntry[] Fallback =
    {
        new("DAL", "B739", 3), new("DAL", "A321", 3), new("DAL", "A320", 2), new("UAL", "B738", 3), new("UAL", "A320", 2), new("AAL", "A321", 3),
        new("AAL", "B738", 3), new("SWA", "B737", 3), new("SWA", "B38M", 2), new("JBU", "A320", 2), new("ACA", "BCS3", 2), new("ASA", "B739", 2),
        new("NKS", "A320", 2), new("SKW", "E75L", 2), new("RPA", "E75L", 2), new("UAL", "B772", 1), new("DAL", "A333", 1),
    };

    private static readonly string[] Destinations =
    {
        "KATL", "KORD", "KDFW", "KDEN", "KLAX", "KJFK", "KLGA", "KBOS", "KMCO", "KMIA", "KSEA", "KSFO", "KLAS", "KPHX", "KIAH", "KMSP", "KDTW",
        "KPHL", "KCLT", "KBWI", "KDCA", "KSLC", "KSAN", "KTPA", "KFLL", "KBNA", "KAUS", "KMSY", "KSTL", "KRDU", "CYYZ", "CYUL", "CYVR", "EGLL", "LFPG", "EHAM", "MMMX",
    };

    public void SetCatalog(TrafficCatalog c)
    {
        lock (_gate) _catalog = c.Entries.Where(e => e.Airline.Length is 2 or 3 && e.Type.Length >= 3).ToList();
        log.LogInformation("AI model catalog from LAIA Sim: {n} airline/type combinations", _catalog.Count);
    }

    private List<CatalogEntry> Catalog()
    {
        lock (_gate) return _catalog.Count > 0 ? _catalog : Fallback.ToList();
    }

    public AircraftIdentity NewAircraft(string airport, int maxGateSize, Random rng)
    {
        var cat = Catalog().Where(e => Perf.For(e.Type).GateSize <= maxGateSize).ToList();
        if (cat.Count == 0) cat = Fallback.Where(e => Perf.For(e.Type).GateSize <= maxGateSize).ToList();
        CatalogEntry pick;
        var sched = Schedule(airport);
        var airlines = sched.Select(s => s.Airline).Where(a => cat.Any(c => c.Airline == a)).ToList();
        if (airlines.Count > 0)
        {
            var airline = airlines[rng.Next(airlines.Count)];
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
        var (cs, other) = NewFlight(airport, pick.Airline, true, rng);
        return new AircraftIdentity(pick.Airline, pick.Type, cs, other);
    }

    private static CatalogEntry WeightedPick(List<CatalogEntry> list, Random rng)
    {
        var total = list.Sum(e => Math.Max(1, e.Count));
        var r = rng.Next(total);
        foreach (var e in list) { r -= Math.Max(1, e.Count); if (r < 0) return e; }
        return list[^1];
    }

    public (string callsign, string other) NewFlight(string airport, string airline, bool departure, Random rng)
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
        var dests = Destinations.Where(d => d != airport).ToArray();
        return ($"{airline}{number}", dests[rng.Next(dests.Length)]);
    }

    private List<SchedFlight> Schedule(string airport)
    {
        lock (_gate)
        {
            if (_sched.TryGetValue(airport, out var list)) return list;
        }
        if (settings.ScheduleSource == "airlabs" && !string.IsNullOrEmpty(settings.Get("airlabs_key"))) _ = FetchAsync(airport);
        return new List<SchedFlight>();
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
                    var airline = S("airline_icao");
                    var cs = S("flight_icao");
                    var other = dep ? S("arr_icao") : S("dep_icao");
                    if (airline.Length != 3 || cs.Length < 4 || S("cs_flight_iata").Length > 0) continue; // skip codeshares
                    list.Add(new SchedFlight(airline, cs, other, dep));
                }
            }
            lock (_gate) _sched[airport] = list;
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
