using Laia.Common.SimConnect;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Runs the AI traffic: picks the airport near the user that MSFS has given us the layout of, runs the
/// traffic simulation there (TrafficWorld) four times a second, and hands LAIA Sim the aircraft to draw.
/// Without a layout (or for the test buttons at an airport not read yet) a simple runway-only world is used.
/// </summary>
public sealed class TrafficEngine(RunwayDb runways, FrequencyDb freqs, AirportStore airports, FlightSource source, SettingsStore settings,
    SimBriefService simbrief, ServerState state, ScheduleStore schedules, NetworkStore network, WorldAirports worldAirports, RoutesDb routesDb, NasrDb nasrDb, AdsbHistory history, MetarReader metar, ILogger<TrafficEngine> log) : BackgroundService
{
    private readonly object _gate = new();
    private readonly List<TrafficWorld> _neighbours = new();
    private List<string> _neighbourWant = new();
    private string _neighbourHow = "";
    private DateTime _neighbourAt = DateTime.MinValue;
    private AtisReader? _atisReader;
    private AtisReader Atis => _atisReader ??= new AtisReader(log);
    private AaupReader? _aaupReader;
    private AaupReader Aaup => _aaupReader ??= new AaupReader(log);
    private TrafficWorld? _world;       // the airport near the user (with layout)
    private TrafficWorld? _testWorld;   // runway-only world for test buttons at airports without layout
    private readonly List<TrafficWorld> _retiring = new();
    private long _stampMs;
    private readonly System.Diagnostics.Stopwatch _started = System.Diagnostics.Stopwatch.StartNew();

    public string Motion => "laia";
    public void SetMotion(string mode) { }

    public TrafficLevel Level
    {
        get => (settings.Get("traffic_level") ?? "normal") switch { "off" => TrafficLevel.Off, "light" => TrafficLevel.Light, "busy" => TrafficLevel.Busy, "peak" => TrafficLevel.Peak, _ => TrafficLevel.Normal };
    }

    public void SetLevel(string level)
    {
        if (level is not ("off" or "light" or "normal" or "busy" or "peak")) return;
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
        lock (_gate)
        {
            var drawn = DrawnIds();
            var list = new List<TrafficAircraft>();
            foreach (var w in Worlds())
                foreach (var f in w.Flights)
                    if (drawn is null || drawn.Contains(f.Id)) list.Add(ToAircraft(w, f));
            return list;
        }
    }

    /// <summary>Most aircraft drawn in the sim at once (MSFS slows down with many AI objects).</summary>
    private const int MaxDrawn = 50;
    private const double DrawRadiusNm = 60, DrawBandFt = 10000;   // nearest 50 within 60 nm (a TRACON's satellites, e.g. MLB 55 nm from MCO)

    /// <summary>
    /// Which simulated flights are drawn in the sim (null = all, when MSFS isn't connected): airborne traffic within
    /// 40 nm and 10,000 ft above or below the user; ground traffic at the airports within 40 nm (the user's field
    /// with its gates and taxiing, neighbours on their runways). At most 50, nearest first — ground traffic close to
    /// the user and aircraft near the user's level win over distant ones.
    /// </summary>
    private HashSet<string>? DrawnIds()
    {
        var sim = state.Sim;
        if (sim is null || !sim.Connected || DateTime.UtcNow - state.SimUpdatedUtc > TimeSpan.FromSeconds(30)) return null;
        var cands = new List<(string id, double score)>();
        foreach (var w in Worlds())
            foreach (var f in w.Flights)
            {
                if (f.Done) continue;
                var (lat, lon) = w.Apt.Map.ToLatLon(f.Pos);
                var nm = Geo2.DistanceNm(sim.Lat, sim.Lon, lat, lon);
                if (nm > DrawRadiusNm) continue;
                var dz = Math.Abs(f.AltFt - sim.AltitudeFt);
                if (!f.OnGround && dz > DrawBandFt) continue;
                cands.Add((f.Id, nm + dz / 2000));
            }
        return cands.OrderBy(c => c.score).Take(MaxDrawn).Select(c => c.id).ToHashSet();
    }

    private IEnumerable<TrafficWorld> Worlds()
    {
        if (_world is not null) yield return _world;
        foreach (var n in _neighbours) yield return n;
        if (_testWorld is not null) yield return _testWorld;
        foreach (var w in _retiring) yield return w;
    }

    private static double WeekMinuteNow()
    {
        var n = DateTime.UtcNow;
        return (((int)n.DayOfWeek + 6) % 7) * 1440 + n.TimeOfDay.TotalMinutes;
    }

    private TrafficAircraft ToAircraft(TrafficWorld w, Flight f)
    {
        var (lat, lon) = w.Apt.Map.ToLatLon(f.Pos);
        var ground = f.Rwy is not null && f.OnRunway >= 0 ? f.Rwy.ElevationFt : w.Apt.ElevationFt;
        if (!f.OnGround && f.Rwy is not null && (f.Phase is "final" or "flare" or "climbing" or "takeoff roll")) ground = f.Rwy.ElevationFt;
        return new TrafficAircraft
        {
            Id = f.Id, Callsign = f.Callsign, Airline = f.Airline, Type = f.Type, Model = f.Title, Lat = lat, Lon = lon, AltFt = f.AltFt,
            AglFt = f.OnGround ? 0 : Math.Max(0, f.AltFt - ground), OnGround = f.OnGround, TimeMs = _stampMs, Heading = f.Heading,
            Pitch = f.Pitch, Bank = f.Bank, GroundSpeed = f.Gs, VerticalSpeed = f.Vs, GearDown = f.Gear, Phase = f.Phase, Lights = f.Lights,
        };
    }

    // ------------------------------------------------------------------ the live map (control page)


    /// <summary>Everything the live map shows: the user, every simulated flight (drawn in the sim or not), runways in use.</summary>
    public object MapData(bool withSky = false)
    {
        var sim = state.Sim;
        var live = sim is not null && sim.Connected && DateTime.UtcNow - state.SimUpdatedUtc < TimeSpan.FromSeconds(30);
        lock (_gate)
        {
            var w = _world;
            var flights = new List<object>();
            var drawnIds = DrawnIds();
            foreach (var wd in Worlds())
                foreach (var f in wd.Flights)
                {
                    if (f.Done) continue;
                    var (lat, lon) = wd.Apt.Map.ToLatLon(f.Pos);
                    var drawn = drawnIds is null || drawnIds.Contains(f.Id);
                    flights.Add(new
                    {
                        id = f.Id, cs = f.Callsign, type = f.Type, airline = f.Airline, kind = f.Kind, phase = f.Phase,
                        lat = Math.Round(lat, 6), lon = Math.Round(lon, 6), alt = Math.Round(f.AltFt), gs = Math.Round(f.Gs), ias = Math.Round(f.Ias),
                        hdg = Math.Round(f.Heading), drawn, ground = f.OnGround, runway = f.Rwy?.Ident, route = f.Route, gate = f.Spot?.Label,
                        other = f.Other, reg = f.Reg, wake = f.Wake.ToString(), title = f.Title,
                        eta = f.Kind == "arrival" && !f.OnGround && !double.IsNaN(f.EtaSec) ? Math.Round(Math.Max(0, f.EtaSec - (wd.Time - f.EtaAt))) : (double?)null,
                        seq = f.Kind == "arrival" && !f.OnGround ? f.Seq : 0,
                        mgr = f.Kind == "arrival" && !f.OnGround && f.Mgr.Length > 0 ? f.Mgr : null,
                        gapnm = f.Kind == "arrival" && !f.OnGround && !double.IsNaN(f.GapNm) ? Math.Round(f.GapNm, 1) : (double?)null,
                        dth = f.Kind == "arrival" && !f.OnGround ? Math.Round((f.ThrS - f.S) / Geo2.MPerNm, 1) : (double?)null,
                        fixes = f.Kind == "arrival" && !f.OnGround && f.FixEtas.Count > 0
                            ? f.FixEtas.Take(4).Select(x => new { fix = x.Fix, eta = Math.Round(Math.Max(0, x.At - wd.Time)) }).ToList() : null,
                        etd = f.Kind == "departure" && f.OnGround && !double.IsNaN(f.TakeoffAt) && f.Phase != "takeoff roll" ? Math.Round(Math.Max(0, f.TakeoffAt - wd.Time)) : (double?)null,
                    });
                }
            object? runways = null;
            if (w is not null)
            {
                var ends = new List<object>();
                foreach (var e in w.Apt.Ends)
                {
                    bool L(TrafficCore.RunwayEnd x) => x == w.ArrRwy || w.ArrRwys.Contains(x);
                    bool D(TrafficCore.RunwayEnd x) => x == w.DepRwy || w.DepRwys.Contains(x);
                    var use = L(e) && D(e) ? "landing and departing" : L(e) ? "landing" : D(e) ? "departing" : null;
                    var (tla, tlo) = w.Apt.Map.ToLatLon(e.Threshold);
                    var (ela, elo) = w.Apt.Map.ToLatLon(e.FarEnd);
                    ends.Add(new { ident = e.Ident, use, thr = new[] { tla, tlo }, end = new[] { ela, elo } });
                }
                runways = ends;
            }
            return new
            {
                airport = w is null ? null : new { icao = w.Apt.Icao, name = w.Apt.Name, lat = w.Apt.Map.ToLatLon(new V2(0, 0)).lat, lon = w.Apt.Map.ToLatLon(new V2(0, 0)).lon },
                user = live ? new { lat = sim!.Lat, lon = sim.Lon, hdg = Math.Round(sim.HeadingTrue), alt = Math.Round(sim.AltitudeFt), gs = Math.Round(sim.GroundSpeed), ground = sim.OnGround, callsign = sim.AtcId } : null,
                drawRadiusNm = DrawRadiusNm,
                level = settings.Get("traffic_level") ?? "normal",
                peak = w?.PeakInfo,
                approach = w?.ApproachInUse,
                runways,
                flights,
                net = withSky ? NetFlights(w, live ? sim!.Lat : (double?)null, live ? sim!.Lon : null) : null,
                netStatus = network.Status,
            };
        }
    }

    private const double NetRadiusNm = 400;

    /// <summary>The rest of the sky: scheduled flights flying logically (not drawn in the sim), around the user or the airport.</summary>
    private List<object> NetFlights(TrafficWorld? w, double? lat, double? lon)
    {
        var list = new List<object>();
        double clat, clon;
        if (lat is not null) { clat = lat.Value; clon = lon!.Value; }
        else if (w is not null) (clat, clon) = w.Apt.Map.ToLatLon(new V2(0, 0));
        else return list;
        // Flights the detailed traffic is flying (handed over, or at and around the airport) aren't shown twice.
        var exclude = new HashSet<string>(Worlds().SelectMany(x => x.Flights).Select(f => f.Reg).Where(r => r.Length > 0), StringComparer.OrdinalIgnoreCase);
        var clock = w?.ScheduleClock ?? WeekMinuteNow();
        var worlds = Worlds().ToList();
        foreach (var p0 in network.Around(clock, clat, clon, NetRadiusNm, exclude))
        {
            // Flights to or from the airports run here fly their filed STAR or SID (the same route the hand-over uses).
            var p = p0;
            foreach (var wd in worlds) p = wd.OnRoute(p);
            list.Add(new
            {
                id = p.Leg.Id, cs = p.Leg.Callsign, type = p.Leg.Type, airline = p.Leg.Airline, dep = p.Leg.Dep, arr = p.Leg.Arr,
                lat = Math.Round(p.Lat, 4), lon = Math.Round(p.Lon, 4), alt = Math.Round(p.AltFt / 100) * 100, gs = Math.Round(p.GsKt),
                hdg = Math.Round(p.Heading), phase = p.Phase, cruise = p.CruiseFt, eta = Math.Round(p.EtaMin), reg = p.Leg.Reg, title = p.Leg.Title,
            });
        }
        return list;
    }

    /// <summary>A network flight's great-circle route: flown so far and still to go.</summary>
    /// <summary>Real tracks (adsb.lol) learned for the airport, for the map: a sample of arrivals and departures per runway.</summary>
    public object? RealTracks()
    {
        var w = _world;
        if (w is null || history.Profile(w.Apt.Icao) is not { } p) return null;
        var rw = w.ArrRwys.Select(e => e.Ident).Concat(w.DepRwys.Select(e => e.Ident)).ToHashSet();
        return new
        {
            icao = p.Icao, day = p.Day, source = p.Source,
            tracks = p.Samples.Where(t => rw.Count == 0 || rw.Contains(t.Runway)).Select(t => new { kind = t.Kind, runway = t.Runway, points = t.Points }),
        };
    }

    public object? MapNetPath(string id)
    {
        var l = network.Find(id);
        if (l is null || !l.Located) return null;
        double clock;
        lock (_gate) clock = _world?.ScheduleClock ?? WeekMinuteNow();
        var p = NetworkModel.At(l, clock, includeGround: true);
        var f = p is null ? 0 : p.FlownNm / l.DistNm;
        return new
        {
            id, dep = l.Dep, arr = l.Arr, distNm = Math.Round(l.DistNm),
            flown = NetworkModel.Line(l, 0, f), points = NetworkModel.Line(l, f, 1),
        };
    }

    /// <summary>The selected flight's path ahead (and its procedure's speed-limit fixes), for the map overlay.</summary>
    public object? MapPath(string id)
    {
        lock (_gate)
        {
            foreach (var wd in Worlds())
                foreach (var f in wd.Flights)
                {
                    if (f.Id != id || f.Path is null) continue;
                    var path = f.Path;
                    var step = f.OnGround ? 10.0 : 250.0;
                    var pts = new List<double[]>();
                    for (var s = Math.Max(0, f.S); s < path.Length; s += step)
                    {
                        var (ll, lo) = wd.Apt.Map.ToLatLon(path.At(s).p);
                        pts.Add(new[] { Math.Round(ll, 6), Math.Round(lo, 6) });
                    }
                    var (el, eo) = wd.Apt.Map.ToLatLon(path.At(path.Length).p);
                    pts.Add(new[] { el, eo });
                    var limits = f.SpeedLimits.Where(l => l.S >= f.S).Select(l =>
                    {
                        var (la, lo2) = wd.Apt.Map.ToLatLon(path.At(l.S).p);
                        return new { lat = la, lon = lo2, kt = l.Kt };
                    }).ToList();
                    // Where it has been, ending at where it is now.
                    var flown = f.Trail.Select(t => { var (la, lo) = wd.Apt.Map.ToLatLon(t.P); return new[] { Math.Round(la, 6), Math.Round(lo, 6) }; }).ToList();
                    var (nla, nlo) = wd.Apt.Map.ToLatLon(f.Pos);
                    flown.Add(new[] { Math.Round(nla, 6), Math.Round(nlo, 6) });
                    return new { id = f.Id, cs = f.Callsign, route = f.Route, ground = f.OnGround, points = pts, flown, limits };
                }
        }
        return null;
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
                landing = w is null ? null : string.Join(", ", w.ArrRwys.Select(e => e.Ident).DefaultIfEmpty(w.ArrRwy?.Ident ?? "")),
                approach = w?.ApproachInUse,
                prediction = w?.PredictionSummary,
                restrictions = w?.RestrictionSummary,
                separation = w?.SeparationSummary,
                arrivalManager = w?.ArrivalManagerSummary,
                navdata = nasrDb.Status,
                routes = $"{routesDb.Status}{(w is null ? "" : $"; {w.RoutesUsed} SIDs/STARs from them so far; {w.FiledRoutesFlown} whole-sky flights on their filed airways")}",
                rules = w?.Rules is { Charts.Length: > 0 } ru ? ru.Describe() : null,
                atis = w?.Atis.CodeArr is { } code ? $"real ATIS info {code}" : w is null ? null : "no ATIS: runways from the wind",
                departing = w is null ? null : string.Join(", ", w.DepRwys.Select(e => e.Ident).DefaultIfEmpty(w.DepRwy?.Ident ?? "")),
                airportsOnFile = airports.Count,
                catalog = source.HasCatalog,
                schedule = (w?.PeakInfo is { } pk ? pk + " · " : "") + (w?.Schedule is { } sd ? $"Real {sd.Source} schedule: {sd.Legs.Count} flights a week at {sd.Airport}" : source.ScheduleStatus)
                    + (w?.Plan is { } gp ? $" · real gates: {gp.Matched} of {gp.Gates} found in the scenery" : ""),
                flights = Worlds().SelectMany(x => x.Flights.Select(f => new
                {
                    f.Callsign, f.Type, f.Kind, f.Phase, other = f.Other, runway = f.Rwy?.Ident, route = f.Route,
                    alt = Math.Round(f.AltFt), gs = Math.Round(f.Gs), gate = f.Spot?.Label, ground = f.OnGround,
                })).Where(f => f.Kind != "parked").ToList(),
                parked = Worlds().Sum(x => x.Flights.Count(f => f.Kind == "parked")),
                learned = history.Status + (w is not null && history.Profile(w.Apt.Icao) is { } lp ? "; " + lp.Describe() : ""),
                neighbours = _neighbours.Count == 0 ? null : $"({_neighbourHow}) " + string.Join(", ", _neighbours.Select(n => $"{n.Apt.Icao} ({n.Flights.Count} flying{(n.PredictionSummary is { } ps ? "; predictions: " + ps : "")})")),
                sky = network.Count == 0 ? network.Status : $"{network.AirborneNow(w?.ScheduleClock ?? WeekMinuteNow()):N0} flights in the air worldwide · {network.Status}",
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
        want.AddRange(_neighbourWant);
        return airports.Missing(want, sim.Simulator ?? "");
    }

    /// <summary>Real schedules wanted from LAIA Sim: the traffic airport and the flight plan's airports.</summary>
    public List<string> NeededSchedules(AircraftState sim)
    {
        var want = new List<string>();
        lock (_gate)
        {
            if (_world is not null) want.Add(_world.Apt.Icao);
            want.AddRange(_neighbourWant);
        }
        var plan = simbrief.Plan;
        if (plan is not null) { want.Add(plan.Origin); want.Add(plan.Destination); }
        var list = schedules.Wanted(want);
        schedules.MarkAsked(list);
        return list;
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

    /// <summary>
    /// The neighbouring fields around the traffic airport: the busiest scheduled airports within 40 nm (up to four),
    /// flown runway-only on the main airport's schedule clock. Their STARs also tell the main airport which of its
    /// published STARs are also a neighbour's (a stopgap until real filed routes).
    /// </summary>
    private void UpdateNeighbours()
    {
        var main = _world;
        if (main is null)
        {
            foreach (var n in _neighbours) { n.Automatic = false; _retiring.Add(n); }
            _neighbours.Clear();
            return;
        }
        if (DateTime.UtcNow - _neighbourAt > TimeSpan.FromMinutes(2))
        {
            _neighbourAt = DateTime.UtcNow;
            // The airports worked by the same approach control (the FAA's records: MCO's F11 also works SFB, ORL, MLB,
            // ISM, TIX, LEE) that have airline traffic in the schedules; a 40 nm circle where the FAA data has none.
            var family = nasrDb.SameApproach(main.Apt.Icao)
                .Where(i => worldAirports.Find(i) is { } p && Geo2.DistanceNm(main.Apt.Data.Lat, main.Apt.Data.Lon, p.lat, p.lon) < 120).ToList();
            _neighbourWant = (family.Count > 0 ? network.BusyAmong(family, 14) : network.BusyAirportsNear(main.Apt.Data.Lat, main.Apt.Data.Lon, 40, 14, main.Apt.Icao))
                .Where(i => !i.Equals(main.Apt.Icao, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
            _neighbourHow = family.Count > 0 ? $"{nasrDb.ApproachFacility(main.Apt.Icao)} approach control" : "within 40 nm";
            // Learn the airport and its neighbours from real tracks (M6), once, in the background.
            var learn = new List<AirportModel> { main.Apt };
            foreach (var icao in _neighbourWant) if (airports.Model(icao) is { } m) learn.Add(m);
            history.Want(learn);
        }
        foreach (var n in _neighbours.Where(n => !_neighbourWant.Contains(n.Apt.Icao) || n.ClockOwner != main).ToList())
        {
            n.Automatic = false; _retiring.Add(n); _neighbours.Remove(n);
        }
        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var icao in _neighbourWant)
        {
            var model = airports.Model(icao);
            if (model is null) continue;
            foreach (var a in model.Data.Arrivals) shared.Add(AirportRules.Base(a.Name));
            if (_neighbours.Any(n => n.Apt.Icao == icao)) continue;
            var nb = new TrafficWorld(model, source)
            {
                RunwayOnly = true, Level = Level, ClockFrom = () => main.ScheduleClock, ClockOwner = main,
                Log = s => log.LogInformation("{msg}", s),
            };
            nb.Wind = main.Wind;
            nb.SelectRunways(true);
            _neighbours.Add(nb);
            log.LogInformation("Neighbouring field: {icao} {name} (runway traffic only)", model.Icao, model.Name);
        }
        main.NeighbourStars = shared;
    }

    private void UpdateWorld(AircraftState sim)
    {
        try { UpdateWorldCore(sim); } finally { UpdateNeighbours(); }
    }

    private void UpdateWorldCore(AircraftState sim)
    {
        var near = airports.Nearest(sim.Lat, sim.Lon, 40);
        // Keep the traffic airport while the user is still within 40 nm of it: flying out past a neighbour (Mirabel,
        // climbing west out of Montréal) doesn't make the neighbour the traffic airport — only landing or being on
        // the ground at another airport does, or leaving the area.
        if (_world is not null && near is { } n0 && n0.model != _world.Apt && !(sim.OnGround && n0.nm < 3)
            && Geo2.DistanceNm(sim.Lat, sim.Lon, _world.Apt.Data.Lat, _world.Apt.Data.Lon) < 40) return;
        if (near is { } n && n.model.HasGround)
        {
            if (_world is null || _world.Apt != n.model)
            {
                // Wait (up to a minute) for the list of installed models, so the first aircraft use real liveries.
                if (!source.CatalogKnown && _started.Elapsed < TimeSpan.FromSeconds(60)) return;
                if (_world is not null && _world.Flights.Count > 0) _retiring.Add(_world);
                _world = new TrafficWorld(n.model, source) { Level = Level, Log = s => log.LogInformation("{msg}", s) };
                _world.Wind = new Wind(sim.WindDirection, sim.WindSpeed);
                _world.SelectRunways(true);
                log.LogInformation("Traffic airport: {icao} {name} ({nm:0} nm away); taxi map {cal}; {desc}", n.model.Icao, n.model.Name, n.nm, n.model.Calibration, n.model.Describe());
                _testWorld = null;
                var w = _world;
                _ = Task.Run(() =>
                {
                    try { var r = w.PushbackSurvey(); log.LogInformation("{icao}: {survey}", w.Apt.Icao, r); }
                    catch (Exception ex) { log.LogWarning("Pushback survey failed: {msg}", ex.Message); }
                });
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
                            // The sim's wind is the wind where the user is: the airport's own only when the user is there
                            // (within 15 nm and below 5,000 ft above it); otherwise the airport's METAR, and failing
                            // that the last wind it had (never the wind at the user's cruise level).
                            var dNm = Geo2.DistanceNm(sim!.Lat, sim.Lon, w.Apt.Data.Lat, w.Apt.Data.Lon);
                            if (dNm < 15 && sim.AltitudeFt < w.Apt.ElevationFt + 5000) w.Wind = new Wind(sim.WindDirection, sim.WindSpeed);
                            else if (metar.Wind(w.Apt.Icao) is { } mw) w.Wind = new Wind(mw.dir ?? w.Wind.FromDeg, mw.kt);
                            w.UserPos = w.Apt.Map.ToXY(sim.Lat, sim.Lon);
                            w.UserOnGround = sim.OnGround;
                        }
                        if (w == _world || _neighbours.Contains(w))
                        {
                            w.Level = Level;
                            // The installed AIG schedule is the real one to follow (exact models and registrations).
                            if (w.Schedule is null) w.Schedule = schedules.Get(w.Apt.Icao);
                            if (w == _world) w.Gates = source.Gates(w.Apt.Icao);
                            w.Atis = Atis.Get(w.Apt.Icao);
                            if (w.Rules is null && Aaup.Get(w.Apt.Icao) is { } aaup)
                            {
                                w.Rules = AaupReader.Rules(aaup.text, aaup.charts, w.Apt.Data);
                                if (aaup.charts.Length > 0) log.LogInformation("{icao}: {rules}", w.Apt.Icao, w.Rules.Describe());
                            }
                            w.Routes = routesDb.Routes;
                            w.NavData = nasrDb.Nav;
                            w.Learned = history.Profile(w.Apt.Icao);
                            w.Locate ??= code => worldAirports.Find(code) is { } p ? (p.lat, p.lon) : null;
                        }
                        w.WeekMinute = live && sim!.ZuluWeekMinute >= 0 ? sim.ZuluWeekMinute : WeekMinuteNow();
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
