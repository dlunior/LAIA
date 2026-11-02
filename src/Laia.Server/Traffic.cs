using Laia.Common.SimConnect;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Runs the AI traffic: picks the airport near the user that MSFS has given us the layout of, runs the
/// traffic simulation there (TrafficWorld) four times a second, and hands LAIA Sim the aircraft to draw.
/// Without a layout (or for the test buttons at an airport not read yet) a simple runway-only world is used.
/// </summary>
public sealed class TrafficEngine(RunwayDb runways, FrequencyDb freqs, AirportStore airports, FlightSource source, SettingsStore settings,
    SimBriefService simbrief, ServerState state, ILogger<TrafficEngine> log) : BackgroundService
{
    private readonly object _gate = new();
    private TrafficWorld? _world;       // the airport near the user (with layout)
    private TrafficWorld? _testWorld;   // runway-only world for test buttons at airports without layout
    private readonly List<TrafficWorld> _retiring = new();
    private long _stampMs;

    public string Motion => "laia";
    public void SetMotion(string mode) { }

    public TrafficLevel Level
    {
        get => (settings.Get("traffic_level") ?? "normal") switch { "off" => TrafficLevel.Off, "light" => TrafficLevel.Light, "busy" => TrafficLevel.Busy, _ => TrafficLevel.Normal };
    }

    public void SetLevel(string level)
    {
        if (level is not ("off" or "light" or "normal" or "busy")) return;
        settings.Update(System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["traffic_level"] = level }));
        lock (_gate) if (_world is not null) _world.Level = Level;
        log.LogInformation("Traffic level: {level}", level);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _testWorld = null;
            _retiring.Clear();
            if (_world is not null) foreach (var f in _world.Flights) f.Done = true;
            _world = null; // restarts (and refills) on the next tick
        }
    }

    // ------------------------------------------------------------------ what LAIA Sim draws

    public List<TrafficAircraft> Snapshot()
    {
        var sim = state.Sim;
        var list = new List<TrafficAircraft>();
        lock (_gate)
        {
            foreach (var w in Worlds())
                foreach (var f in w.Flights)
                {
                    var a = ToAircraft(w, f);
                    if (sim is not null && sim.Connected && Geo2.DistanceNm(sim.Lat, sim.Lon, a.Lat, a.Lon) > 25) continue;
                    list.Add(a);
                }
        }
        return list;
    }

    private IEnumerable<TrafficWorld> Worlds()
    {
        if (_world is not null) yield return _world;
        if (_testWorld is not null) yield return _testWorld;
        foreach (var w in _retiring) yield return w;
    }

    private TrafficAircraft ToAircraft(TrafficWorld w, Flight f)
    {
        var (lat, lon) = w.Apt.Map.ToLatLon(f.Pos);
        var ground = f.Rwy is not null && f.OnRunway >= 0 ? f.Rwy.ElevationFt : w.Apt.ElevationFt;
        if (!f.OnGround && f.Rwy is not null && (f.Phase is "final" or "flare" or "climbing" or "takeoff roll")) ground = f.Rwy.ElevationFt;
        return new TrafficAircraft
        {
            Id = f.Id, Callsign = f.Callsign, Airline = f.Airline, Type = f.Type, Lat = lat, Lon = lon, AltFt = f.AltFt,
            AglFt = f.OnGround ? 0 : Math.Max(0, f.AltFt - ground), OnGround = f.OnGround, TimeMs = _stampMs, Heading = f.Heading,
            Pitch = f.Pitch, Bank = f.Bank, GroundSpeed = f.Gs, VerticalSpeed = f.Vs, GearDown = f.Gear, Phase = f.Phase, Lights = f.Lights,
        };
    }

    /// <summary>For the control page.</summary>
    public object Status()
    {
        lock (_gate)
        {
            var w = _world;
            return new
            {
                level = (settings.Get("traffic_level") ?? "normal"),
                airport = w?.Apt.Icao,
                airportName = w?.Apt.Name,
                layout = w is null ? null : $"{w.Apt.Spots.Count} parking spots, {w.Apt.Data.Departures.Count} SIDs, {w.Apt.Data.Arrivals.Count} STARs, {w.Apt.Data.Approaches.Count} approaches",
                landing = w?.ArrRwy?.Ident,
                departing = w?.DepRwy?.Ident,
                airportsOnFile = airports.Count,
                catalog = source.HasCatalog,
                schedule = source.ScheduleStatus,
                flights = Worlds().SelectMany(x => x.Flights.Select(f => new
                {
                    f.Callsign, f.Type, f.Kind, f.Phase, other = f.Other, runway = f.Rwy?.Ident, route = f.Route,
                    alt = Math.Round(f.AltFt), gs = Math.Round(f.Gs), gate = f.Spot?.Label, ground = f.OnGround,
                })).Where(f => f.Kind != "parked").ToList(),
                parked = Worlds().Sum(x => x.Flights.Count(f => f.Kind == "parked")),
            };
        }
    }

    /// <summary>Airports the server would like LAIA Sim to read: the flight plan's and the nearest towered one.</summary>
    public List<string> NeededAirports(AircraftState sim)
    {
        var want = new List<string>();
        var plan = simbrief.Plan;
        if (plan is not null) { want.Add(plan.Origin); want.Add(plan.Destination); }
        var near = freqs.NearestAirport(sim.Lat, sim.Lon);
        if (near is { } n && n.nm < 40)
            want.Add(n.ident.Length == 3 && n.ident.All(char.IsLetter) ? "K" + n.ident : n.ident);
        return airports.Missing(want, sim.Simulator ?? "");
    }

    // ------------------------------------------------------------------ test buttons

    public (bool ok, string message) StartTest(string kind)
    {
        var sim = state.Sim;
        if (sim is null || !sim.Connected || DateTime.UtcNow - state.SimUpdatedUtc > TimeSpan.FromSeconds(15))
            return (false, "Start MSFS and load a flight first, so LAIA knows where you are.");
        lock (_gate)
        {
            UpdateWorld(sim);
            var w = _world;
            if (w is null)
            {
                if (!runways.Loaded) return (false, "Runway data is still downloading. Try again in a minute.");
                var near = freqs.NearestAirport(sim.Lat, sim.Lon);
                if (near is null || near.Value.nm > 30) return (false, "You're not near an airport LAIA knows yet (a towered US airport within 30 nm, or one MSFS has given LAIA the layout of).");
                var rwy = runways.Active(near.Value.ident, sim.WindDirection, sim.WindSpeed);
                if (rwy is null) return (false, $"No runway data for {near.Value.ident}.");
                if (_testWorld is null || _testWorld.Apt.Icao != rwy.Airport)
                {
                    var model = AirportModel.RunwayOnly(rwy.Airport, rwy.Lat, rwy.Lon, rwy.ElevationFt, rwy.TrueHeading, rwy.LengthFt, rwy.Id, rwy.Lat, rwy.Lon);
                    _testWorld = new TrafficWorld(model, source) { Automatic = false, Log = s => log.LogInformation("{msg}", s) };
                }
                w = _testWorld;
            }
            w.Wind = new Wind(sim.WindDirection, sim.WindSpeed);
            var (ok, message) = kind == "departure" ? w.TestDeparture() : w.TestArrival();
            log.LogInformation("Test {kind} at {apt}: {msg}", kind, w.Apt.Icao, message);
            return (ok, message + (w == _testWorld ? "" : ""));
        }
    }

    // ------------------------------------------------------------------ loop

    private void UpdateWorld(AircraftState sim)
    {
        var near = airports.Nearest(sim.Lat, sim.Lon, 40);
        if (near is { } n && n.model.HasGround)
        {
            if (_world is null || _world.Apt != n.model)
            {
                if (_world is not null && _world.Flights.Count > 0) _retiring.Add(_world);
                _world = new TrafficWorld(n.model, source) { Level = Level, Log = s => log.LogInformation("{msg}", s) };
                _world.Wind = new Wind(sim.WindDirection, sim.WindSpeed);
                _world.SelectRunways(true);
                log.LogInformation("Traffic airport: {icao} {name} ({nm:0} nm away); taxi map {cal}; {desc}", n.model.Icao, n.model.Name, n.nm, n.model.Calibration, n.model.Describe());
                _testWorld = null;
            }
        }
        else if (_world is not null && sim.Connected)
        {
            // Left the airport's area: let the remaining flights finish, but start nothing new.
            _world.Automatic = false;
            _retiring.Add(_world);
            _world = null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(0.25));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        var lastWorldCheck = -10.0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = clock.Elapsed.TotalSeconds;
            var dt = Math.Clamp(now - last, 0.01, 1.0);
            last = now;
            try
            {
                var sim = state.Sim;
                var live = sim is not null && sim.Connected && DateTime.UtcNow - state.SimUpdatedUtc < TimeSpan.FromSeconds(30);
                lock (_gate)
                {
                    if (live && now - lastWorldCheck > 5) { UpdateWorld(sim!); lastWorldCheck = now; }
                    foreach (var w in Worlds().ToList())
                    {
                        if (live)
                        {
                            w.Wind = new Wind(sim!.WindDirection, sim.WindSpeed);
                            w.UserPos = w.Apt.Map.ToXY(sim.Lat, sim.Lon);
                            w.UserOnGround = sim.OnGround;
                        }
                        if (w == _world) w.Level = Level;
                        // Don't run traffic while nobody is flying (sim closed): it would all depart unseen.
                        if (live || w.Flights.Any(f => f.Test)) w.Step(dt);
                    }
                    _retiring.RemoveAll(w => w.Flights.Count == 0 || w.Flights.All(f => f.Kind == "parked"));
                    _stampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
            catch (Exception ex) { log.LogWarning("Traffic step failed: {msg}", ex.Message); }
        }
    }
}
