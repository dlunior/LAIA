using Laia.Common.SimConnect;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Runs the AI traffic: picks the airport near the user that MSFS has given us the layout of, runs the
/// traffic simulation there (TrafficWorld) four times a second, and hands LAIA Sim the aircraft to draw.
/// Without a layout (or for the test buttons at an airport not read yet) a simple runway-only world is used.
/// </summary>
public sealed class TrafficEngine(RunwayDb runways, FrequencyDb freqs, AirportStore airports, FlightSource source, SettingsStore settings,
    SimBriefService simbrief, ServerState state, ScheduleStore schedules, NetworkStore network, WorldAirports worldAirports, RoutesDb routesDb, NasrDb nasrDb, AdsbHistory history, MetarReader metar, LiveRunways liveRunways, SimBriefService simBrief, ILogger<TrafficEngine> log) : BackgroundService
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
        get => (settings.Get("traffic_level") ?? "medium") switch { "off" => TrafficLevel.Off, "light" => TrafficLevel.Light, "busy" => TrafficLevel.Busy, "real" => TrafficLevel.RealWorld, "peak" => TrafficLevel.Peak, _ => TrafficLevel.Medium };   // "normal" (older) = Medium
    }

    /// <summary>The level's setting name ("normal" from older versions reads as "medium").</summary>
    private string LevelName => (settings.Get("traffic_level") ?? "medium") is "normal" ? "medium" : settings.Get("traffic_level") ?? "medium";

    public void SetLevel(string level)
    {
        if (level == "normal") level = "medium";
        if (level is not ("off" or "light" or "medium" or "busy" or "real" or "peak")) return;
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

    // ------------------------------------------------------------------ preparation (quality over speed)

    /// <summary>
    /// Before any traffic at an airport, everything is gathered and worked out first (user, 12 Nov: "quality over
    /// speed"): the layout, the installed models, the real schedule, the live ATIS and METAR, the FAA AAUP rules and
    /// SID charts, the real-traffic learning (adsb.lol), then the runways in use (settled once, from all of that), the
    /// taxi flow for them and the pushback plans. Each item waits only so long; what doesn't come is noted and skipped.
    /// Traffic (here and at the neighbouring fields) starts only when it's all done, or when the user says start now.
    /// </summary>
    private sealed class Prep(string icao)
    {
        public readonly string Icao = icao;
        public readonly DateTime Started = DateTime.UtcNow;
        public Task? Survey;
        public Task<(string summary, List<string> lines)>? RouteCheck;
        public bool Ready, Forced, RunwaysSettled, RouteLogged;
        public DateTime? SettledAt;
        public readonly List<(string item, bool done, string note)> Items = new();
        public double Secs => (DateTime.UtcNow - Started).TotalSeconds;
    }
    private Prep? _prep;

    /// <summary>Whether a field has airline passenger service: 14 or more passenger airline flights a week in its real
    /// schedule (not cargo); null until its schedule is known.</summary>
    private bool? PassengerService(string icao) =>
        schedules.Get(icao) is { } sd ? sd.Legs.Count(l => !TrafficWorld.IsCargo(l.Airline)) >= 14 : null;

    /// <summary>Skip the rest of the preparation and start the traffic now.</summary>
    public void StartNow() { lock (_gate) if (_prep is { Ready: false } p) p.Forced = true; }

    private void UpdatePrep(TrafficWorld w)
    {
        var p = _prep;
        if (p is null || p.Ready || p.Icao != w.Apt.Icao) return;
        var icao = w.Apt.Icao;
        var us = icao.Length == 4 && icao[0] == 'K';
        var t = p.Secs;
        p.Items.Clear();
        void Item(string name, bool done, string note) => p.Items.Add((name, done, note));
        Item("Airport layout", true, $"{w.Apt.Spots.Count} parking spots, {w.Apt.Data.Departures.Count} SIDs, {w.Apt.Data.Arrivals.Count} STARs");
        Item("Real schedule", w.Schedule is not null || t > 90, w.Schedule is { } sd ? $"{sd.Legs.Count} flights a week ({sd.Source})" : t > 90 ? "none found, made-up flights" : "reading the AIG schedule…");
        var atisDone = Atis.Known(icao) || t > 45;
        Item("Live ATIS", atisDone, !us ? "none (not a US airport)" : Atis.Known(icao) ? (w.Atis.CodeArr is { } c ? $"info {c}" : "this airport publishes none") : atisDone ? "didn't answer" : "asking…");
        var metarDone = metar.Known(icao) || t > 45;
        Item("METAR wind", metarDone, metar.Wind(icao) is { } mw ? $"{(mw.dir is { } d ? $"{d:000}°" : "variable")} at {mw.kt:0} kt" : metarDone ? "not available, the sim's wind" : "asking…");
        var aaupDone = !us || Aaup.Get(icao) is not null || t > 180;
        Item("FAA runway rules (AAUP)", aaupDone, !us ? "none (not a US airport)" : w.Rules is { Charts.Length: > 0 } ru ? ru.Describe() : aaupDone ? "none published" : "downloading the charts…");
        var topsDone = !us || w.SidTops is not null || t > 180;
        Item("SID top altitudes (charts)", topsDone, !us ? "from the STARs (not a US airport)" : w.SidTops is { Count: > 0 } st ? $"{st.Count} SIDs" : topsDone ? "none on the charts, from the STARs" : "reading the departure charts…");
        var pax = PassengerService(icao);
        var learnDone = pax == false || (pax is null && t > 90) || history.Current(icao) || history.Gave(icao) || t > 900
                        || (!history.Learning(icao) && t > 60 && history.Profile(icao) is null && !history.Status.StartsWith("learning"));
        Item("Real traffic (adsb.lol, passenger jets)", learnDone, pax == false ? "no airline passenger service, not needed" : history.Current(icao) ? $"learned from {history.Profile(icao)!.Day}"
            : learnDone ? "not available" : history.Learning(icao) ? history.Status : "starting…");
        // The neighbouring fields' runway use, learned in the same download.
        var nbs = _neighbours.Select(n => n.Apt.Icao).ToList();
        var nbDone = nbs.All(n => history.Current(n) || history.Gave(n)) || t > 900;
        Item("Neighbouring fields (adsb.lol)", nbDone, nbs.Count == 0 ? "none with airline service" : string.Join(", ", nbs.Select(n => n + (history.Current(n) ? " ✓" : history.Gave(n) ? " (not available)" : " …"))));
        // The runways: once everything they're decided from is in, settled once.
        // No ATIS here: the runways real traffic is using right now (adsb.lol live positions) rank above the wind — wait
        // for them too (until some are seen, or two looks have come back).
        var noAtis = atisDone && w.AtisLanding.Count == 0;
        var lr = noAtis ? liveRunways.Get(w.Apt) : (new List<string>(), new List<string>());
        var liveDone = !noAtis || lr.Item1.Count > 0 || liveRunways.Polls(icao) >= 2 || t > 300;
        Item("Live runway use (adsb.lol)", liveDone, !noAtis ? "not needed (the ATIS says)" : lr.Item1.Count > 0 ? $"landing {string.Join("/", lr.Item1)}{(lr.Item2.Count > 0 ? $", departing {string.Join("/", lr.Item2)}" : "")}"
            : liveDone ? "none seen, from the wind and what was learned" : $"watching live traffic ({liveRunways.Polls(icao)} looks)…");
        if (noAtis) { w.LiveLanding = lr.Item1; w.LiveDeparting = lr.Item2; }
        var inputs = atisDone && metarDone && aaupDone && learnDone && liveDone;
        if (inputs && !p.RunwaysSettled) { w.HoldRunways = false; w.SelectRunways(true); p.RunwaysSettled = true; p.SettledAt = DateTime.UtcNow; }
        Item("Runways in use", p.RunwaysSettled, p.RunwaysSettled ? $"landing {string.Join("/", w.ArrRwys.Select(e => e.Ident))}, departing {string.Join("/", w.DepRwys.Select(e => e.Ident))}" : "waiting for the ATIS, wind and rules…");
        var flowDone = p.RunwaysSettled && (w.FlowReady || (DateTime.UtcNow - p.SettledAt!.Value).TotalSeconds > 60);
        Item("Taxi flow", flowDone, w.Flow is { Full: true } fl ? $"{fl.OneWay} one-way stretches{(fl.RealStretches > 0 ? $", {fl.RealStretches} from real taxi tracks" : "")}" : flowDone ? "not worked out" : p.RunwaysSettled ? "rehearsing the taxi routes…" : "after the runways");
        var pushDone = p.Survey is null || p.Survey.IsCompleted || t > 120;
        Item("Pushback plans", pushDone, pushDone ? "planned" : "planning every stand…");
        // Every SID and STAR as LAIA will fly it, compared with the real tracks (the check that found the FATHE4 35L
        // intercept 0.9 nm off), once the runways are settled and the real tracks are in.
        if (p.RunwaysSettled && learnDone && p.RouteCheck is null)
        {
            // Once, here on the traffic thread (it reads the runways in use; well under a second).
            try { p.RouteCheck = Task.FromResult(w.RouteCheck()); }
            catch (Exception ex) { p.RouteCheck = Task.FromResult(("couldn't compare: " + ex.Message, new List<string>())); }
        }
        var rcDone = p.RouteCheck is { IsCompleted: true } || (p.RouteCheck is not null && (DateTime.UtcNow - p.SettledAt!.Value).TotalSeconds > 60) || (learnDone && w.Learned is null && p.RunwaysSettled);
        if (p.RouteCheck is { IsCompletedSuccessfully: true } rc && !p.RouteLogged)
        {
            p.RouteLogged = true;
            foreach (var line in rc.Result.lines) log.LogInformation("{icao} route check: {line}", icao, line);
        }
        Item("Routes vs real tracks", rcDone, p.RouteCheck is { IsCompletedSuccessfully: true } rc2 ? rc2.Result.summary : rcDone ? "no real tracks to compare" : p.RunwaysSettled ? "comparing…" : "after the runways");
        if (p.Items.All(i => i.done) || p.Forced)
        {
            if (!p.RunwaysSettled) { w.HoldRunways = false; w.SelectRunways(true); }
            p.Ready = true;
            w.Automatic = true;
            foreach (var n in _neighbours) n.Automatic = true;
            log.LogInformation("{icao} prepared in {s:0} s{forced}: {items}", icao, t, p.Forced ? " (started early by the user)" : "",
                string.Join("; ", p.Items.Select(i => $"{i.item}: {(i.done ? "" : "skipped, ")}{i.note}")));
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
    /// <summary>Real world and Peak allow more, as there's more traffic close by worth seeing.</summary>
    private int MaxDrawn => Level >= TrafficLevel.RealWorld ? 80 : 50;
    private const double DrawRadiusNm = 60, DrawBandFt = 10000;   // nearest 50 (80 at Real world and Peak) within 60 nm (a TRACON's satellites, e.g. MLB 55 nm from MCO)

    /// <summary>
    /// Which simulated flights are drawn in the sim (null = all, when MSFS isn't connected): airborne traffic within
    /// 40 nm and 10,000 ft above or below the user; ground traffic at the airports within 40 nm (the user's field
    /// with its gates and taxiing, neighbours on their runways). At most 50, nearest first — ground traffic close to
    /// the user and aircraft near the user's level win over distant ones.
    /// </summary>
    private HashSet<string>? DrawnIds(bool forMap = false)
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
        var want = cands.OrderBy(c => c.score).Take(MaxDrawn).Select(c => c.id).ToList();
        if (forMap) return want.Where(_drawnLast.Contains).ToHashSet();   // the map shows what the sim has; it doesn't add
        // Brought in gradually (user: no "big bang"; 4 a second still looked like all at once): aircraft already drawn
        // stay; new ones join at one a second, nearest first, so a busy airport fills over about a minute.
        var now = DateTime.UtcNow;
        _drawBudget = Math.Min(1, _drawBudget + (now - _drawBudgetAt).TotalSeconds * 1);
        _drawBudgetAt = now;
        var res = new HashSet<string>();
        foreach (var id in want)
        {
            if (_drawnLast.Contains(id)) { res.Add(id); continue; }
            if (_drawBudget < 1) continue;
            _drawBudget -= 1;
            res.Add(id);
        }
        _drawnLast = res;
        return res;
    }
    private HashSet<string> _drawnLast = new();
    private double _drawBudget = 1;
    private DateTime _drawBudgetAt = DateTime.UtcNow;

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
            var drawnIds = DrawnIds(forMap: true);
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
                level = LevelName,
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
    /// <summary>Smart traffic mode notices for the user (M8), from the traffic airport (and a neighbour the user is at).</summary>
    public List<Laia.Common.SimConnect.NoticeDto> UserNotices()
    {
        lock (_gate)
        {
            var res = new List<Laia.Common.SimConnect.NoticeDto>();
            foreach (var w in Worlds())
                foreach (var n in w.Notices)
                    res.Add(new() { Id = w.Apt.Icao + ":" + n.Id, Text = n.Text, Warning = n.Warning });
            return res.Take(5).ToList();
        }
    }

    /// <summary>Real tracks (adsb.lol) learned for the airport, for the map: a sample of arrivals and departures per runway.</summary>
    public object? RealTracks()
    {
        var w = _world;
        if (w is null || history.Profile(w.Apt.Icao) is not { } p) return null;
        // Arrivals to the landing runways and departures from the departure runways (not either kind on any runway in
        // use), and as many of each, spread over the day(s), so neither looks busier than it is.
        var arrRw = w.ArrRwys.Select(e => e.Ident).ToHashSet();
        var depRw = w.DepRwys.Select(e => e.Ident).ToHashSet();
        var arr = p.Samples.Where(t => t.Kind == "arrival" && (arrRw.Count == 0 || arrRw.Contains(t.Runway))).ToList();
        var dep = p.Samples.Where(t => t.Kind == "departure" && (depRw.Count == 0 || depRw.Contains(t.Runway))).ToList();
        var n = Math.Min(arr.Count, dep.Count);
        static List<T> Spread<T>(List<T> l, int n) => l.Count <= n || n <= 0 ? l : Enumerable.Range(0, n).Select(i => l[(int)((long)i * l.Count / n)]).ToList();
        return new
        {
            icao = p.Icao, day = p.Day, source = p.Source,
            tracks = Spread(arr, n).Concat(Spread(dep, n)).Select(t => new { kind = t.Kind, runway = t.Runway, points = t.Points }),
        };
    }

    /// <summary>The main airport's taxi flow for the map: one-way stretches with their direction.</summary>
    public object? TaxiFlowMap()
    {
        lock (_gate)
        {
            var w = _world;
            if (w?.Flow is not { } fl) return null;
            return new
            {
                key = fl.Key, summary = fl.Summary,
                stretches = fl.Stretches.Select(st => new
                {
                    name = st.name, use = st.use, real = st.real,
                    points = st.pts.Select(q => { var (la, lo) = w.Apt.Map.ToLatLon(q); return new[] { Math.Round(la, 6), Math.Round(lo, 6) }; }),
                }),
            };
        }
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
                level = (LevelName),
                preparing = _prep is { Ready: false } pp && w is not null && pp.Icao == w.Apt.Icao
                    ? new { seconds = (int)pp.Secs, items = pp.Items.Select(i => new { i.item, i.done, i.note }).ToList() } : null,
                airport = w?.Apt.Icao,
                airportName = w?.Apt.Name,
                layout = w is null ? null : $"{w.Apt.Spots.Count} parking spots, {w.Apt.Data.Departures.Count} SIDs, {w.Apt.Data.Arrivals.Count} STARs, {w.Apt.Data.Approaches.Count} approaches",
                landing = w is null ? null : string.Join(", ", w.ArrRwys.Select(e => e.Ident).DefaultIfEmpty(w.ArrRwy?.Ident ?? "")),
                approach = w?.ApproachInUse,
                prediction = w?.PredictionSummary,
                restrictions = w?.RestrictionSummary,
                separation = w?.SeparationSummary,
                topAltitude = w?.TopAltitudeSummary,
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
            // Only fields with airline passenger service (user, 13 Nov); the neighbours are chosen that way already.
            var learn = new List<AirportModel>();
            if (PassengerService(main.Apt.Icao) == true) learn.Add(main.Apt);
            foreach (var icao in _neighbourWant) if (airports.Model(icao) is { } m) learn.Add(m);
            if (learn.Count > 0) history.Want(learn);
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
                RunwayOnly = true, Level = Level, ClockFrom = () => main.ScheduleClock, ClockOwner = main, Automatic = _prep?.Ready ?? true,
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
                _world = new TrafficWorld(n.model, source) { Level = Level, Automatic = false, HoldRunways = true, Log = s => log.LogInformation("{msg}", s) };
                _prep = new Prep(n.model.Icao);
                _world.Wind = new Wind(sim.WindDirection, sim.WindSpeed);
                log.LogInformation("Traffic airport: {icao} {name} ({nm:0} nm away); taxi map {cal}; {desc}", n.model.Icao, n.model.Name, n.nm, n.model.Calibration, n.model.Describe());
                _testWorld = null;
                var w = _world;
                _prep.Survey = Task.Run(() =>
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
                            // The airport's METAR is the source of truth for its wind (user rule). Only with no METAR is
                            // the sim's wind used — and only while the user is at the airport (within 15 nm, below 5,000 ft
                            // above it), since it's the wind where the user is; otherwise the last wind stays.
                            var dNm = Geo2.DistanceNm(sim!.Lat, sim.Lon, w.Apt.Data.Lat, w.Apt.Data.Lon);
                            if (metar.Wind(w.Apt.Icao) is { } mw) w.Wind = new Wind(mw.dir ?? w.Wind.FromDeg, mw.kt);
                            else if (dNm < 15 && sim.AltitudeFt < w.Apt.ElevationFt + 5000) w.Wind = new Wind(sim.WindDirection, sim.WindSpeed);
                            w.UserPos = w.Apt.Map.ToXY(sim.Lat, sim.Lon);
                            w.UserOnGround = sim.OnGround;
                            w.UserAltFt = sim.AltitudeFt; w.UserGs = sim.GroundSpeed; w.UserTrack = sim.HeadingTrue;
                            var plan = simBrief.Plan;
                            w.UserCallsign = plan?.Callsign is { Length: > 0 } pc ? pc : (sim.AtcAirline + " " + sim.AtcFlightNumber).Trim() is { Length: > 0 } ac ? ac : sim.AtcId;
                            w.UserType = plan?.Aircraft ?? "";
                            w.UserDestination = plan?.Destination;
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
                            if (w.SidTops is not { Count: > 0 }) w.SidTops = Aaup.SidTops(w.Apt.Icao);
                            w.Routes = routesDb.Routes;
                            w.NavData = nasrDb.Nav;
                            w.Learned = history.Profile(w.Apt.Icao);
                            // No ATIS here: the runways real traffic is using now (live adsb.lol positions).
                            if (w.AtisLanding.Count == 0) { var lr = liveRunways.Get(w.Apt); w.LiveLanding = lr.landing; w.LiveDeparting = lr.departing; }
                            else { w.LiveLanding = new(); w.LiveDeparting = new(); }
                            w.Locate ??= code => worldAirports.Find(code) is { } p ? (p.lat, p.lon) : null;
                            if (w == _world) UpdatePrep(w);
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
