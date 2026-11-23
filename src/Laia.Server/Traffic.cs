using Laia.Common.SimConnect;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Runs the AI traffic: picks the airport near the user that MSFS has given us the layout of, runs the
/// traffic simulation there (TrafficWorld) four times a second, and hands LAIA Sim the aircraft to draw.
/// Without a layout (or for the test buttons at an airport not read yet) a simple runway-only world is used.
/// </summary>
public sealed class TrafficEngine(RunwayDb runways, FrequencyDb freqs, AirportStore airports, FlightSource source, SettingsStore settings,
    SimBriefService simbrief, ServerState state, ScheduleStore schedules, NetworkStore network, WorldAirports worldAirports, RoutesDb routesDb, NasrDb nasrDb, AdsbHistory history, MetarReader metar, WindsAloftReader winds, LiveRunways liveRunways, SimBriefService simBrief, PerfMonitor perf, ILogger<TrafficEngine> log) : BackgroundService
{
    private readonly object _gate = new();
    private readonly List<TrafficWorld> _neighbours = new();
    private List<string> _neighbourWant = new();
    private TrafficWorld? _neighbourFor;
    private string _neighbourWhy = "";
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
        if (LevelName != level) { log.LogWarning("Traffic level {level} wasn't saved (still {now})", level, LevelName); return; }
        lock (_gate) foreach (var w in Worlds()) w.Level = Level;   // the destination ahead and the neighbouring fields too
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
    /// Traffic (here and at the neighbouring fields) starts only when it's all done (user, 13 Nov: "then and only then";
    /// there's no "start now"). Picking a level while it's preparing shows a box on the control page with each item.
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
    private TrafficWorld? _ahead;       // the flight plan's destination, prepared and run before the user gets there
    private Prep? _aheadPrep;
    private List<string> _aheadNeighbourWant = new();

    /// <summary>
    /// Towers with runway safety logic: the 35 ASDE-X airports and the civil ASSC airports (FAA surface surveillance) —
    /// where line up and wait is allowed with an aircraft cleared to land (7110.65 3-9-4 c).
    /// </summary>
    private static readonly HashSet<string> SafetyLogicAirports = new(StringComparer.OrdinalIgnoreCase)
    {
        "KATL", "KBDL", "KBOS", "KBWI", "KCLT", "KDCA", "KDEN", "KDFW", "KDTW", "KEWR", "KFLL", "PHNL", "KHOU", "KIAD", "KIAH",
        "KJFK", "KLAS", "KLAX", "KLGA", "KMCO", "KMDW", "KMEM", "KMIA", "KMKE", "KMSP", "KORD", "KPHL", "KPHX", "KPVD", "KSAN",
        "KSDF", "KSEA", "KSLC", "KSNA", "KSTL",
        "KSFO", "KCLE", "KPDX", "KMCI", "KCVG", "KMSY", "KPIT", "PANC",
    };

    /// <summary>Each STAR's fix names (for reading its chart).</summary>
    private static Dictionary<string, HashSet<string>> StarFixes(AirportModel apt) =>
        apt.Data.Arrivals.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.SelectMany(a => a.Legs
            .Concat(a.RunwayTransitions.SelectMany(t => t.Legs)).Concat(a.EnrouteTransitions.SelectMany(t => t.Legs)))
            .Where(l => l.Fix.Length > 0).Select(l => l.Fix.ToUpperInvariant()).ToHashSet(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a field has airline passenger service: 14 or more passenger airline flights a week in its real
    /// schedule (not cargo); null until its schedule is known.</summary>
    private bool? PassengerService(string icao) =>
        schedules.Get(icao) is { } sd ? sd.Legs.Count(l => !TrafficWorld.IsCargo(l.Airline)) >= 14 : null;


    private void UpdatePrep(TrafficWorld w, Prep? p, IReadOnlyList<TrafficWorld> neighbours)
    {
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
        var expDone = !us || w.StarExpects is not null || t > 180;
        Item("STAR \"expect\" restrictions (charts)", expDone, !us ? "none (not a US airport)" : w.StarExpects is { } se ? (se.Count == 0 ? "none on the charts" : $"{se.Sum(kv => kv.Value.Count)} on {se.Count} STARs") : expDone ? "charts not read" : "reading the arrival charts…");
        var topsDone = !us || w.SidTops is not null || t > 180;
        Item("SID top altitudes (charts)", topsDone, !us ? "from the STARs (not a US airport)" : w.SidTops is { Count: > 0 } st ? $"{st.Count} SIDs" : topsDone ? "none on the charts, from the STARs" : "reading the departure charts…");
        var pax = PassengerService(icao);
        var learnDone = pax == false || (pax is null && t > 90) || history.Current(icao) || history.Gave(icao) || t > 900
                        || (!history.Learning(icao) && t > 60 && history.Profile(icao) is null && !history.Status.StartsWith("learning"));
        Item("Real traffic (adsb.lol, passenger jets)", learnDone, pax == false ? "no airline passenger service, not needed" : history.Current(icao) ? $"learned from {history.Profile(icao)!.Day}"
            : learnDone ? "not available" : history.Learning(icao) ? history.Status : "starting…");
        // The neighbouring fields' runway use, learned in the same download.
        var nbs = neighbours.Select(n => n.Apt.Icao).ToList();
        var nbDone = nbs.All(n => history.Current(n) || history.Gave(n)) || t > 900;
        Item("Neighbouring fields (adsb.lol)", nbDone, nbs.Count == 0 ? (w == _ahead ? "when you get there" : "none with airline service") : string.Join(", ", nbs.Select(n => n + (history.Current(n) ? " ✓" : history.Gave(n) ? " (not available)" : " …"))));
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
            foreach (var n in neighbours) n.Automatic = true;
            log.LogInformation("{icao} prepared in {s:0} s{forced}{ahead}: {items}", icao, t, p.Forced ? " (started early by the user)" : "",
                w == _ahead ? " (your destination, ahead of you: its traffic runs unseen until you're in range)" : "",
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
                    if (drawn is null || drawn.Contains(OutId(f)))
                    {
                        var a = ToAircraft(w, f);
                        a.Id = OutId(f);
                        if (!a.OnGround) a.AltFt += FlCorrection(a.AltFt);
                        list.Add(a);
                    }
            if (drawn is not null)
            {
                var clock = NetClock;
                foreach (var l in _enroute)
                    if (drawn.Contains(l.Id) && EnRouteAircraft(l, clock) is { } a) list.Add(a);
            }
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
                // A little give for what's already drawn (no flicker at the edges): 3 nm and 1,000 ft beyond.
                var drawn = _drawnLast.Contains(f.Id);
                if (nm > DrawRadiusNm + (drawn ? 3 : 0)) continue;
                var dz = Math.Abs(f.AltFt - UserLevelFt(sim));
                if (!f.OnGround && dz > DrawBandFt + (drawn ? 1000 : 0)) continue;
                // What matters first: moving traffic (in the air, taxiing, on the runway) before aircraft parked at a
                // gate — a parked aircraft counts as 25 nm further away (MLB's arrivals 50 nm out weren't drawn while
                // MCO's parked aircraft filled the 80).
                var parked = f.OnGround && f.Phase is "parked" or "boarding" or "at gate";
                cands.Add((OutId(f), nm + dz / 2000 + (parked ? 25 : 0)));
            }
        // The whole-sky flights passing within range of the user en route (airborne passenger jets no airport here
        // runs), drawn like the rest (user, 13 Nov: traffic seen on the way, not only at the two airports).
        var clock = NetClock;
        foreach (var l in _enroute)
        {
            if (NetworkModel.At(l, clock) is not { } p) continue;
            var nm = Geo2.DistanceNm(sim.Lat, sim.Lon, p.Lat, p.Lon);
            var drawn = _drawnLast.Contains(l.Id);
            if (nm > DrawRadiusNm + (drawn ? 3 : 0)) continue;
            var dz = Math.Abs(NetAlt(l, clock, p.AltFt) - UserLevelFt(sim));
            if (dz > DrawBandFt + (drawn ? 1000 : 0)) continue;
            cands.Add((l.Id, nm + dz / 2000));
        }
        // The nearest that matter, MaxDrawn of them; one already drawn stays while it's within the first MaxDrawn + 8
        // (it isn't swapped out the moment another comes a little closer).
        var ordered = cands.OrderBy(c => c.score).Select(c => c.id).ToList();
        var keep = ordered.Take(MaxDrawn + 8).Where(_drawnLast.Contains).ToHashSet();
        var want = ordered.Where(id => keep.Contains(id) || !_drawnLast.Contains(id)).Take(Math.Max(MaxDrawn, keep.Count)).ToList();
        foreach (var id in keep) if (!want.Contains(id)) want.Add(id);
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

    // ------------------------------------------------------------------ en-route traffic drawn

    private List<Laia.Server.TrafficCore.NetLeg> _enroute = new();
    private double _enrouteAt = -99;
    private readonly Dictionary<string, string> _alias = new();          // flight id → the id the sim knows it by
    private readonly Dictionary<string, string> _netDrawnRegs = new(StringComparer.OrdinalIgnoreCase);   // registration → drawn en-route id

    private double NetClock => _world?.ScheduleClock ?? WeekMinuteNow();

    /// <summary>
    /// Flight levels in the sim's real air (user, 15 Nov: traffic at "-07" on the TCAS, 700 ft below, at FL380 — LAIA
    /// flew its traffic at true altitudes, 37,000 ft, while the user's FL380 was 37,750 ft true that day). Above the
    /// transition (18,000 ft) LAIA's altitudes are flight levels, so they're drawn at the true altitude that flight
    /// level has where the user is: plus (true − pressure altitude) of the user's own aircraft, eased in from 15,000 ft.
    /// Below that, altitudes are on the local altimeter setting, as before. Needs LAIA Sim 0.1.194+ (pressure altitude).
    /// </summary>
    private double _trueMinusPressure;
    private double FlCorrection(double altFt) => _trueMinusPressure * Math.Clamp((altFt - 15000) / 3000, 0, 1);
    /// <summary>The user's altitude on LAIA's scale (a flight level above the transition).</summary>
    private double UserLevelFt(AircraftState sim) => sim.AltitudeFt - FlCorrection(sim.AltitudeFt);
    private void UpdateFlCorrection(AircraftState sim)
    {
        if (sim.PressureAltFt == 0) return;
        var raw = Math.Clamp(sim.AltitudeFt - sim.PressureAltFt, -3000, 3000);
        _trueMinusPressure += (raw - _trueMinusPressure) * 0.1;
    }

    /// <summary>
    /// The id an aircraft is drawn under. A whole-sky flight drawn en route that an airport then takes over (at 200 nm)
    /// keeps the id it was drawn with, so the sim moves the same aircraft on instead of removing it and adding a new one.
    /// </summary>
    private string OutId(Flight f)
    {
        if (_alias.TryGetValue(f.Id, out var a)) return a;
        if (f.Reg.Length > 0 && _netDrawnRegs.TryGetValue(f.Reg, out var nid) && _drawnLast.Contains(nid) && !f.OnGround)
        {
            _alias[f.Id] = nid;
            _netDrawnRegs.Remove(f.Reg);
            return nid;
        }
        return f.Id;
    }

    /// <summary>The whole-sky flights that may be drawn near the user: airborne passenger jets (user, 13 Nov), above
    /// 8,000 ft, not flown by an airport here (not within 30 nm of one, nor within its 200 nm take-over to or from it).</summary>
    private List<Laia.Server.TrafficCore.NetLeg> EnRoute(AircraftState sim)
    {
        // Traffic off is all traffic off (user, 15 Nov: "if the traffic is off, why do I see traffic?").
        if (Level == TrafficLevel.Off) return new();
        var worlds = Worlds().ToList();
        var exclude = new HashSet<string>(worlds.SelectMany(x => x.Flights).Select(f => f.Reg).Where(r => r.Length > 0), StringComparer.OrdinalIgnoreCase);
        var callsigns = worlds.SelectMany(x => x.Flights).Where(f => !f.Done).Select(f => f.Callsign).ToHashSet();
        var cand = new List<(Laia.Server.TrafficCore.NetLeg l, bool keep, bool near)>();
        var userFt = UserLevelFt(sim);
        foreach (var p in network.Around(NetClock, sim.Lat, sim.Lon, DrawRadiusNm + 40, exclude, 400))
        {
            var l = p.Leg;
            if (p.AltFt < 8000 || TrafficWorld.IsCargo(l.Airline) || TrafficWorld.IsCargo(l.Operator) || Perf.For(l.Type).Family == "turboprop") continue;
            var keep = TrafficWorld.KeepAtLevel(Level, l.Reg.Length > 0 ? l.Reg : l.Callsign);   // the level thins it, as at the airports
            if (callsigns.Contains(l.Callsign)) continue;
            var owned = false;
            foreach (var wd in worlds)
            {
                var d = Geo2.DistanceNm(p.Lat, p.Lon, wd.Apt.Data.Lat, wd.Apt.Data.Lon);
                var ours = l.Dep.Equals(wd.Apt.Icao, StringComparison.OrdinalIgnoreCase) || l.Arr.Equals(wd.Apt.Icao, StringComparison.OrdinalIgnoreCase);
                if (d < 30 || (ours && d < wd.HandoverNm + 10)) { owned = true; break; }
            }
            if (!owned) cand.Add((l, keep, Geo2.DistanceNm(sim.Lat, sim.Lon, p.Lat, p.Lon) <= DrawRadiusNm && Math.Abs(p.AltFt - userFt) <= DrawBandFt));
        }
        // A quiet sky isn't thinned (user, 15 Nov, MCO–ORD at FL360 on Medium: a United 1,000 ft above, beside the one
        // Southwest drawn, was left out — "if the amount of aircraft in the area of the user is less than 15, they all
        // get drawn"): the level only thins when 15 or more aircraft are around the user (within 60 nm and 10,000 ft,
        // en route and the airports' moving traffic together). Back to thinning above 18, so it doesn't flip each time
        // one comes or goes.
        var around = cand.Count(c => c.near);
        foreach (var wd in worlds)
            foreach (var f in wd.Flights)
            {
                if (f.Done || (f.OnGround && f.Phase is "parked" or "boarding" or "at gate")) continue;
                var (la, lo) = wd.Apt.Map.ToLatLon(f.Pos);
                if (Geo2.DistanceNm(sim.Lat, sim.Lon, la, lo) <= DrawRadiusNm && (f.OnGround || Math.Abs(f.AltFt - userFt) <= DrawBandFt)) around++;
            }
        if (_quietSky ? around > QuietSky + 3 : around < QuietSky) _quietSky = !_quietSky;
        _aroundUser = around;
        var res = new List<Laia.Server.TrafficCore.NetLeg>();
        var thinned = new HashSet<string>();
        foreach (var c in cand)
        {
            if (c.keep || _quietSky) res.Add(c.l);
            else thinned.Add(c.l.Id);
        }
        _thinned = thinned;
        // Remember which drawn ones are which aircraft (for the take-over); forget aliases of flights now gone.
        foreach (var l in res) if (l.Reg.Length > 0 && _drawnLast.Contains(l.Id)) _netDrawnRegs[l.Reg] = l.Id;
        if (_alias.Count > 0)
        {
            var live = worlds.SelectMany(x => x.Flights).Where(f => !f.Done).Select(f => f.Id).ToHashSet();
            foreach (var k in _alias.Keys.Where(k => !live.Contains(k)).ToList()) _alias.Remove(k);
        }
        if (_netDrawnRegs.Count > 300) _netDrawnRegs.Clear();
        SeparateEnRoute(sim, res);
        return res;
    }

    private double _windsAt = -99, _windsLogAt = -9999, _meterAt = -99, _meterLogAt = -9999, _routesAt = -99;

    /// <summary>
    /// Arrival metering for the airports LAIA runs (user, 18 Nov: Peak at KORD, the busiest real three hours, "isn't the
    /// amount of traffic but how they are spaced when injected"): the real schedule bunches arrivals (many at the same
    /// minute), and they were handed to the airport's arrival manager 200 nm out already bunched, with more delay than
    /// speed and the downwind could take. As the FAA's time-based flow does, each flight landing in the next 3 hours gets
    /// a slot in order of its scheduled time, one per landing runway about every 95 s (after the arrivals the airport
    /// already has), and runs late by the difference — a departure delay at its origin if it hasn't left yet, a slower
    /// cruise (no more than 15% slower) if it's flying. So it reaches the 200 nm hand-over at about its slot.
    /// </summary>
    // ------------------------------------------------------------------ self-test (offline Peak replay)

    /// <summary>
    /// A fast offline replay of the traffic airport (user, 19 Nov: "feels like we are playing whack a mole" — the tests
    /// in CI use a made-up one-runway airport, so problems with several runways and real tracks only showed on the
    /// user's screen). Once per server version and airport, when the airport is prepared, a second copy of it runs
    /// unseen: an hour of its busiest three (Peak), from an empty sky, with the same data (layout, schedule, procedures,
    /// learned real tracks, ATIS rules, wind). A few milliseconds of each traffic step, so it takes several minutes, and
    /// the result goes to the log as a scorecard ("Self-test …") to compare one version with the next.
    /// </summary>
    private TrafficWorld? _shadow;
    private string? _shadowDoneFor, _shadowKey;
    private readonly List<string> _shadowLog = new();
    private double _shadowStartWeek, _shadowStartedAt;
    private const double ShadowSecs = 3600;
    public bool SelfTestRunning => _shadow is not null;
    /// <summary>Removes one of LAIA's aircraft (by its map id or callsign) at the user's request.</summary>
    public (bool ok, string message) RemoveAircraft(string id)
    {
        lock (_gate)
        {
            foreach (var w in Worlds())
                foreach (var f in w.Flights)
                    if (!f.Done && (OutId(f) == id || f.Id == id || string.Equals(f.Callsign, id, StringComparison.OrdinalIgnoreCase)))
                        return w.RemoveByUser(f) is { } cs ? (true, $"{cs} removed.") : (false, "That one can't be removed.");
        }
        return (false, "Not found (it may already be gone).");
    }

    public void RestartSelfTest() { lock (_gate) { _shadowDoneFor = null; _shadow = null; } }

    private void SelfTestStep()
    {
        var w = _world;
        if (_shadow is null)
        {
            if (w is null || w.RunwayOnly || _prep is not { Ready: true } || w.Learned is null || w.Schedule is null) return;
            _shadowKey = $"{typeof(TrafficEngine).Assembly.GetName().Version?.ToString(3)}|{w.Apt.Icao}";
            if (_shadowDoneFor == _shadowKey) return;
            _shadowLog.Clear();
            TrafficWorld? s = null;
            s = new TrafficWorld(w.Apt, source, seed: 7)
            {
                Automatic = true, GenericTraffic = false, HoldRunways = false,
                Log = line => { if (_shadowLog.Count < 20000) _shadowLog.Add($"{s!.Time,6:0} {line}"); },
            };
            s.Schedule = w.Schedule; s.Gates = w.Gates; s.Atis = w.Atis; s.Rules = w.Rules; s.SidTops = w.SidTops; s.StarExpects = w.StarExpects;
            s.Routes = w.Routes; s.NavData = w.NavData; s.Learned = w.Learned; s.Locate = w.Locate; s.Wind = w.Wind; s.Aloft = w.Aloft;
            s.LowWeather = w.LowWeather; s.SafetyLogic = w.SafetyLogic; s.LiveLanding = w.LiveLanding; s.LiveDeparting = w.LiveDeparting;
            s.WeekMinute = w.WeekMinute;
            s.Level = TrafficLevel.Peak;
            _shadowStartWeek = w.WeekMinute; _shadowStartedAt = DateTime.UtcNow.Ticks;
            _shadow = s;
            log.LogInformation("Self-test started: {icao} at Peak, an hour replayed offline (version {v})", w.Apt.Icao, _shadowKey.Split('|')[0]);
            return;
        }
        var sh = _shadow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < 25 && sh.Time < ShadowSecs)
        {
            sh.WeekMinute = _shadowStartWeek + sh.Time / 60;
            sh.Step(0.5);
        }
        if (sh.Time < ShadowSecs) return;
        _shadow = null;
        _shadowDoneFor = _shadowKey;
        var mins = (DateTime.UtcNow.Ticks - _shadowStartedAt) / TimeSpan.TicksPerMinute;
        foreach (var line in Scorecard(_shadowLog, sh, mins)) log.LogInformation("{line}", line);
    }

    /// <summary>The self-test's scorecard: what happened in the hour, per 10 landings, and the worst moments.</summary>
    private static List<string> Scorecard(List<string> lines, TrafficWorld sh, double realMin)
    {
        int C(string k) => lines.Count(l => l.Contains(k));
        var landed = lines.Where(l => l.Contains(": landed runway")).ToList();
        int met = 0, tot = 0, shortGaps = 0; var extra = new List<int>();
        foreach (var l in landed)
        {
            var m = System.Text.RegularExpressions.Regex.Match(l, @"restrictions (\d+) of (\d+) met");
            if (m.Success) { met += int.Parse(m.Groups[1].Value); tot += int.Parse(m.Groups[2].Value); }
            var g = System.Text.RegularExpressions.Regex.Match(l, @"(\d+) s behind \S+ \(needed (\d+)\)");
            if (g.Success) { var e = int.Parse(g.Groups[1].Value) - int.Parse(g.Groups[2].Value); extra.Add(e); if (e < -5) shortGaps++; }
        }
        var n = Math.Max(1, landed.Count);
        string Per(int x) => (10.0 * x / n).ToString("0.0");
        var spacing = lines.Count(l => l.Contains("vectors for spacing (") && !l.Contains("delay for the sequence"));
        var delay = C("delay for the sequence),");
        var takeovers = lines.Where(l => l.Contains("taken over from the whole sky")).ToList();
        var real = takeovers.Count(l => l.Contains("real traffic flies"));
        var tooHigh = takeovers.Count(l => System.Text.RegularExpressions.Regex.Match(l, @"([\d.]+) nm out at (\d+) ft") is { Success: true } m
            && double.Parse(m.Groups[2].Value) > double.Parse(m.Groups[1].Value) * 300 + 4000 && double.Parse(m.Groups[1].Value) < 150);
        extra.Sort();
        var res = new List<string>
        {
            $"Self-test {sh.Apt.Icao} (Peak, {sh.Time / 60:0} min replayed offline in {realMin:0} min; landing {string.Join("/", sh.ArrRwys.Select(r => r.Ident))}): " +
            $"{landed.Count} landings, {C(": took off") + C(": airborne")} take-offs; per 10 landings — vectors for spacing {Per(spacing)}, delay vectors {Per(delay)}, " +
            $"break-offs {Per(C("breaking off"))}, go-arounds {Per(C("going around"))}, runway switches {Per(C("for less delay") + C("lands about"))}, real-track moves {Per(C("real track off its STAR"))}, " +
            $"trombones {Per(C("downwind extended"))}; holds {C("holding for")}, 360s {C("making a 360")}; separation warnings {C("SEPARATION")}; " +
            $"restrictions {met} of {tot} met; landings short of the gap needed {shortGaps}" + (extra.Count > 0 ? $", median {extra[extra.Count / 2]} s beyond it" : "") +
            $"; take-overs {takeovers.Count} ({(takeovers.Count > 0 ? 100 * real / takeovers.Count : 0)}% on real tracks, {C("sequenced: starts")} moved back, {tooHigh} too high); " +
            $"failures {C("failed")}, removed {C("removed after")}",
        };
        foreach (var l in lines.Where(l => l.Contains("breaking off") || l.Contains("going around") || l.Contains("SEPARATION") || l.Contains("failed")).Take(8))
            res.Add("Self-test   " + l);
        return res;
    }

    /// <summary>The metering delay the whole sky's copy of a scheduled arrival has (0 when none).</summary>
    private double MeterFor(string icao, Laia.Common.Airports.ScheduleLeg l)
    {
        var list = network.ArrivingAt(icao);
        if (!_meterIndex.TryGetValue(icao, out var ix) || !ReferenceEquals(ix.list, list))
        {
            var d = new Dictionary<(string, string, int), NetLeg>();
            foreach (var n in list) d.TryAdd((n.Dep.ToUpperInvariant(), n.Flight, n.DepMin), n);
            _meterIndex[icao] = ix = (list, d);
        }
        return ix.map.TryGetValue((l.Dep.ToUpperInvariant(), l.Flight, l.DepMin), out var leg) ? leg.MeterMin : 0;
    }
    private readonly Dictionary<string, (List<NetLeg> list, Dictionary<(string, string, int), NetLeg> map)> _meterIndex = new(StringComparer.OrdinalIgnoreCase);

    private void MeterArrivals()
    {
        var clock = NetClock;
        foreach (var w in Worlds().ToList())
        {
            if (w.RunwayOnly || w.ArrRwys.Count == 0 || w.Level == TrafficLevel.Off) continue;
            var spacing = 95.0 / 60 / w.ArrRwys.Count;
            var mine = w.Flights.Where(f => f.Kind == "arrival" && !f.OnGround && !f.Done).Select(f => f.Reg).Where(r => r.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var t = clock + w.Flights.Count(f => f.Kind == "arrival" && !f.OnGround && !f.Done) * spacing;
            var legs = new List<(NetLeg l, double eta)>();
            foreach (var l in network.ArrivingAt(w.Apt.Icao))
            {
                if (mine.Contains(l.Reg) || TrafficWorld.IsCargo(l.Airline) || TrafficWorld.IsCargo(l.Operator) || Perf.For(l.Type).Family == "turboprop") continue;
                if (!TrafficWorld.KeepAtLevel(w.Level, l.Reg.Length > 0 ? l.Reg : l.Callsign)) continue;
                var rel = ((NetworkModel.LandingMin(l) - clock) % 10080 + 10080) % 10080;
                // Landed (late as metered) a while ago: its metering is done.
                if (l.MeterMin != 0 && ((NetworkModel.LandingMin(l) + l.MeterMin - clock) % 10080 + 10080) % 10080 > 9000) { l.MeterMin = 0; l.MeterTarget = 0; }
                if (rel + l.MeterMin < 30 || rel > 180) continue;   // inside 30 minutes it's (about to be) the airport's own
                legs.Add((l, clock + rel));
            }
            double total = 0; var n = 0; var maxD = 0.0;
            foreach (var (l, eta) in legs.OrderBy(x => x.eta))
            {
                var slot = Math.Max(eta, t + spacing);
                var target = Math.Min(25, slot - eta);
                var airborne = NetworkModel.At(l, clock) is not null;
                var departed = ((clock - l.MeterMin - l.DepMin) % 10080 + 10080) % 10080 < l.BlockMin;
                if (!departed) l.MeterMin = target;
                else if (airborne) l.MeterMin += Math.Clamp(target - l.MeterMin, -0.10, 0.075);   // per 30 s: at most 15% slower
                l.MeterTarget = target;
                t = Math.Max(t, eta + l.MeterMin);
                total += l.MeterMin; n++; maxD = Math.Max(maxD, l.MeterMin);
            }
            if (_meterAt - _meterLogAt > 900 && w == _world && n > 0)
            {
                _meterLogAt = _meterAt;
                log.LogInformation("Arrival metering for {icao}: {n} flights landing in the next 3 hours, {runways} landing runways — average {avg:0.0} min late, most {max:0} min (departure delays at their origins, slower cruise once flying)",
                    w.Apt.Icao, n, w.ArrRwys.Count, total / n, maxD);
            }
        }
    }

    /// <summary>
    /// The wind MSFS gives the user goes into the winds-aloft field as they fly, and every 15 s each airport's traffic
    /// gets the wind at every level over it (user, 17 Nov: LAIA's aircraft and the user in the same air, so the same
    /// airspeed gives the same ground speed).
    /// </summary>
    private void UpdateWinds(AircraftState sim, double now)
    {
        var field = winds.Field;
        var sfc = _world?.Wind ?? new Wind(sim.WindDirection, sim.WindSpeed);
        var sfcElev = _world?.Apt.ElevationFt ?? Math.Max(0, sim.AltitudeFt - sim.AglFt);
        field.AddUserSample(DateTime.UtcNow, sim.Lat, sim.Lon, sim.AltitudeFt, sim.WindDirection, sim.WindSpeed, sfc, sfcElev);
        if (now - _windsAt < 15) return;
        _windsAt = now;
        var known = field.StationCount > 0 || field.SampleCount > 0;
        foreach (var w in Worlds().ToList())
            w.Aloft = known ? field.Profile(DateTime.UtcNow, w.Apt.Data.Lat, w.Apt.Data.Lon, w.Wind, w.Apt.ElevationFt) : null;
        if (now - _windsLogAt > 1800 && _world is { Aloft: { } a } mw)
        {
            _windsLogAt = now;
            string At(double ft) { var x = WindsAloft.FromProfile(a, ft); return $"{ft / 1000:0}k {x.FromDeg:000}/{x.Kt:0}"; }
            log.LogInformation("Winds aloft over {icao}: {w} (surface {sd:000}/{sk:0}; {what}); you: {ud:000}/{uk:0} at {ua:N0} ft",
                mw.Apt.Icao, string.Join(", ", new[] { 3000.0, 6000, 10000, 18000, 24000, 34000, 39000 }.Select(At)), mw.Wind.FromDeg, mw.Wind.Kt,
                field.Describe(DateTime.UtcNow), sim.WindDirection, sim.WindSpeed, sim.AltitudeFt);
        }
    }

    private readonly EnRouteSeparation _sep = new();
    private const int QuietSky = 15;
    private bool _quietSky;
    private int _aroundUser;
    private HashSet<string> _thinned = new();

    private static string LevelTitle(TrafficLevel l) => l switch { TrafficLevel.RealWorld => "Real world", _ => l.ToString() };

    /// <summary>Why a whole-sky flight isn't drawn in the sim (for the map's details), or null when it is.</summary>
    private string? WhyNotDrawn(Laia.Server.TrafficCore.NetLeg l, double lat, double lon, double altFt)
    {
        if (_drawnLast.Contains(l.Id)) return null;
        var sim = state.Sim;
        if (sim is null || !sim.Connected) return "MSFS isn't connected";
        if (Level == TrafficLevel.Off) return "traffic is off";
        if (TrafficWorld.IsCargo(l.Airline) || TrafficWorld.IsCargo(l.Operator)) return "cargo (only passenger jets are drawn)";
        if (Perf.For(l.Type).Family == "turboprop") return "turboprop (only passenger jets are drawn)";
        var nm = Geo2.DistanceNm(sim.Lat, sim.Lon, lat, lon);
        if (nm > DrawRadiusNm) return $"{nm:0} nm from you (drawn within {DrawRadiusNm:0} nm)";
        if (altFt < 8000) return "below 8,000 ft, near an airport whose traffic isn't run here";
        var dz = altFt - UserLevelFt(sim);
        if (Math.Abs(dz) > DrawBandFt) return $"{Math.Abs(dz):N0} ft {(dz > 0 ? "above" : "below")} you (drawn within {DrawBandFt:N0} ft)";
        if (_thinned.Contains(l.Id)) return $"thinned out by the {LevelTitle(Level)} level ({_aroundUser} aircraft around you; all are drawn below {QuietSky})";
        return $"not among the nearest {MaxDrawn}, or joining shortly (one a second)";
    }

    /// <summary>A whole-sky flight's altitude as drawn (its schedule's altitude, unless separation changed it).</summary>
    private double NetAlt(Laia.Server.TrafficCore.NetLeg l, double clock, double baseAlt) => _sep.Alt(l, clock, baseAlt);

    /// <summary>Separation for the whole-sky flights near the user (see EnRouteSeparation): against each other, the
    /// user and the airports' aircraft.</summary>
    private void SeparateEnRoute(AircraftState sim, List<Laia.Server.TrafficCore.NetLeg> legs)
    {
        var obs = new List<EnRouteSeparation.Obstacle>();
        if (!sim.OnGround)
            obs.Add(new((UserCallsignOf(sim) is { Length: > 0 } cs ? cs : "your aircraft") + " (you)", sim.Lat, sim.Lon, sim.HeadingTrue, sim.GroundSpeed, UserLevelFt(sim), sim.VerticalSpeed, 6));
        foreach (var w in Worlds())
            foreach (var f in w.Flights)
            {
                if (f.Done || f.OnGround) continue;
                var (la, lo) = w.Apt.Map.ToLatLon(f.Pos);
                if (Geo2.DistanceNm(sim.Lat, sim.Lon, la, lo) > DrawRadiusNm + 60) continue;
                obs.Add(new($"{f.Callsign} ({w.Apt.Icao})", la, lo, f.Heading, f.Gs, f.AltFt, f.Vs, 5));
            }
        foreach (var e in _sep.Update(NetClock, sim.Lat, sim.Lon, legs, obs))
            if (_drawnLast.Contains(e.Leg.Id) || e.Who.EndsWith("(you)"))
                log.LogInformation("En route: {cs} {what} for {who}", e.Leg.Callsign, e.Text, e.Who);
    }

    private string? UserCallsignOf(AircraftState sim) => _world?.UserCallsign is { Length: > 0 } c ? c : simBrief.Plan?.Callsign;

    private TrafficAircraft? EnRouteAircraft(Laia.Server.TrafficCore.NetLeg l, double clock)
    {
        if (NetworkModel.At(l, clock) is not { } p) return null;
        var vs = NetworkModel.At(l, clock + 0.25) is { } q ? (q.AltFt - p.AltFt) * 4 : 0;
        if (_sep.Rate(l) is { } r) { vs = r; p = p with { AltFt = _sep.Alt(l, clock, p.AltFt) }; }
        var gs = p.GsKt;
        return new TrafficAircraft
        {
            Id = l.Id, Callsign = l.Callsign, Airline = l.Operator.Length > 0 ? l.Operator : l.Airline, Type = l.Type, Model = l.Title,
            Lat = p.Lat, Lon = p.Lon, AltFt = p.AltFt + FlCorrection(p.AltFt), AglFt = p.AltFt, OnGround = false, TimeMs = _stampMs, Heading = p.Heading,
            Pitch = Math.Clamp(2.5 + Math.Atan2(vs / 60, Math.Max(gs, 120) * 1.688) * 180 / Math.PI, -3, 12), Bank = 0,
            GroundSpeed = gs, VerticalSpeed = vs, GearDown = false, Phase = p.Phase,
            Lights = 1 | 2 | 4 | 32 | (p.AltFt < 10000 ? 8 : 0),
        };
    }
    private double _drawBudget = 1;
    private DateTime _drawBudgetAt = DateTime.UtcNow;

    private double _othersAt = -99;

    /// <summary>
    /// Each airport's airborne traffic shown to the others (in their own frame), so they keep clear of each other:
    /// MCO's departures level under MLB's arrivals, MLB's arrivals stay above MCO's traffic, and losses between
    /// airports are logged (user, 13 Nov).
    /// </summary>
    private void ShareTraffic()
    {
        var worlds = Worlds().ToList();
        foreach (var w in worlds)
        {
            var list = new List<TrafficWorld.Foreign>();
            foreach (var o in worlds)
            {
                if (o == w) continue;
                foreach (var f in o.Flights)
                {
                    if (f.Done || f.OnGround) continue;
                    var (la, lo) = o.Apt.Map.ToLatLon(f.Pos);
                    if (Geo2.DistanceNm(la, lo, w.Apt.Data.Lat, w.Apt.Data.Lon) > 80) continue;
                    list.Add(new TrafficWorld.Foreign(f.Callsign, o.Apt.Icao, f.Kind, w.Apt.Map.ToXY(la, lo), f.AltFt, f.Vs, f.Gs, f.Heading, f.Phase, o == _world));
                }
            }
            // The whole-sky flights near the user too: the airports' departures stop their climbs below them and the
            // arrivals their descents above (user, 15 Nov: separation for all traffic, not only the user).
            var clock = NetClock;
            foreach (var l in _enroute)
            {
                if (NetworkModel.At(l, clock) is not { } p) continue;
                if (Geo2.DistanceNm(p.Lat, p.Lon, w.Apt.Data.Lat, w.Apt.Data.Lon) > 80) continue;
                var a = NetAlt(l, clock, p.AltFt);
                var vs = _sep.Rate(l) ?? (NetworkModel.At(l, clock + 0.25) is { } q ? (q.AltFt - p.AltFt) * 4 : 0);
                list.Add(new TrafficWorld.Foreign(l.Callsign, "en route", vs < -300 ? "arrival" : "departure", w.Apt.Map.ToXY(p.Lat, p.Lon), a, vs, p.GsKt, p.Heading, p.Phase, true));
            }
            w.Others = list;
        }
    }

    private readonly HashSet<TrafficWorld> _recorded = new();

    private IEnumerable<TrafficWorld> Worlds()
    {
        if (_world is not null) yield return _world;
        if (_ahead is not null) yield return _ahead;
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
                    var drawn = drawnIds is null || drawnIds.Contains(OutId(f));
                    flights.Add(new
                    {
                        id = f.Id, cs = f.Callsign, type = f.Type, airline = f.Airline, kind = f.Kind, phase = f.Phase,
                        lat = Math.Round(lat, 6), lon = Math.Round(lon, 6), alt = Math.Round(f.AltFt), gs = Math.Round(f.Gs), ias = Math.Round(f.Ias),
                        hdg = Math.Round(f.Heading), drawn, ground = f.OnGround, runway = f.Rwy?.Ident, route = f.Route, gate = f.Spot?.Label,
                        other = f.Other, reg = f.Reg, wake = f.Wake.ToString(), title = f.Title,
                        eta = f.Kind == "arrival" && !f.OnGround && !double.IsNaN(f.EtaSec) ? Math.Round(Math.Max(0, f.EtaSec - (wd.Time - f.EtaAt))) : (double?)null,
                        seq = f.Kind == "arrival" && !f.OnGround ? f.Seq : 0,
                        depSeq = f.Kind == "departure" && f.OnGround ? f.DepSeq : 0,
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
                lat = Math.Round(p.Lat, 4), lon = Math.Round(p.Lon, 4), alt = Math.Round(NetAlt(p.Leg, NetClock, p.AltFt) / 100) * 100, gs = Math.Round(p.GsKt),
                hdg = Math.Round(p.Heading), phase = p.Phase, cruise = p.CruiseFt, eta = Math.Round(p.EtaMin), reg = p.Leg.Reg, title = p.Leg.Title,
                drawn = _drawnLast.Contains(p.Leg.Id),
                why = WhyNotDrawn(p.Leg, p.Lat, p.Lon, NetAlt(p.Leg, NetClock, p.AltFt)),
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
                    res.Add(new() { Id = w.Apt.Icao + ":" + n.Id, Text = n.Text, Warning = n.Warning, Clear = n.Clear });
            return res.Take(5).ToList();
        }
    }

    /// <summary>Real tracks (adsb.lol) learned for the airport, for the map: every arrival and departure for the runways in use.</summary>
    public object? RealTracks()
    {
        // The departure field and the destination prepared ahead (user, 13 Nov, CYWG–CYYC: "not showing the real
        // track or taxi flow for my arrival airport").
        // And the satellite fields (user, 19 Nov: "incorporate ADSB data for satellite fields as well … different
        // colors on the map"), marked so the map draws them in their own colours.
        List<(TrafficWorld w, bool sat)> ws;
        lock (_gate) ws = MapAirports().Select(w => (w, false)).Concat(_neighbours.Select(n => (n, true))).DistinctBy(x => x.Item1.Apt.Icao).ToList();
        var parts = new List<(TrafficWorld w, AirportProfile p, string key, bool sat)>();
        foreach (var (w, sat) in ws)
        {
            if (history.Profile(w.Apt.Icao) is not { } p) continue;
            var key = $"{p.Icao}@{p.LearnedUtc:O}:{string.Join("/", w.ArrRwys.Select(e => e.Ident))}:{string.Join("/", w.DepRwys.Select(e => e.Ident))}";
            parts.Add((w, p, key, sat));
        }
        if (parts.Count == 0) return null;
        var all = string.Join("|", parts.Select(x => x.key));
        lock (_realGate)
        {
            // Worked out once per airport, profile and runways in use, not on every request.
            if (_realCache is { } c && c.key == all) return c.result;
        }
        var tracks = new List<object>();
        var icaos = new List<string>();
        foreach (var (w, p, _, sat) in parts)
        {
            // Every real arrival to the landing runways and departure from the departure runways, the part below FL200
            // (user, 15 Nov: "all arrival and departure tracks lower than FL200 should be shown" — no longer an equal
            // number of each).
            var arrRw = w.ArrRwys.Select(e => e.Ident).ToHashSet();
            var depRw = w.DepRwys.Select(e => e.Ident).ToHashSet();
            static List<double[]> Below(List<double[]> pts, bool arrival)
            {
                // The contiguous part below FL200 next to the runway: the end of an arrival, the start of a departure.
                var res = new List<double[]>();
                if (arrival) { for (var i = pts.Count - 1; i >= 0 && (pts[i].Length < 3 || pts[i][2] < 20000); i--) res.Add(pts[i]); res.Reverse(); }
                else for (var i = 0; i < pts.Count && (pts[i].Length < 3 || pts[i][2] < 20000); i++) res.Add(pts[i]);
                return res;
            }
            foreach (var t in p.Samples)
            {
                var arrival = t.Kind == "arrival";
                if (!(arrival ? arrRw.Count == 0 || arrRw.Contains(t.Runway) : depRw.Count == 0 || depRw.Contains(t.Runway))) continue;
                var pts = Thin(Below(t.Points, arrival));
                if (pts.Count >= 2) tracks.Add(new { kind = t.Kind, sat, points = pts });
            }
            icaos.Add($"{p.Icao} {p.Day}");
        }
        var result = new { key = all, icao = string.Join(", ", icaos), tracks };
        lock (_realGate) _realCache = (all, result);
        return result;
    }
    private readonly object _realGate = new();
    private (string key, object result)? _realCache;

    /// <summary>
    /// A track with only the points the map needs: a point is dropped when the line from the last kept point to the
    /// next one passes within about 40 m of it (straight stretches become one segment, turns keep their shape).
    /// Positions only, rounded to about a metre.
    /// </summary>
    private static List<double[]> Thin(List<double[]> pts)
    {
        var res = new List<double[]>();
        if (pts.Count == 0) return res;
        const double Tol = 0.00036;   // degrees of latitude, about 40 m
        double[] P(double[] q) => new[] { Math.Round(q[0], 5), Math.Round(q[1], 5) };
        var anchor = pts[0];
        res.Add(P(anchor));
        for (var i = 1; i < pts.Count - 1; i++)
        {
            var next = pts[i + 1];
            var cos = Math.Cos(anchor[0] * Math.PI / 180);
            double ax = anchor[1] * cos, ay = anchor[0], bx = next[1] * cos, by = next[0], px = pts[i][1] * cos, py = pts[i][0];
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            var u = len2 > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1) : 0;
            var off = Math.Sqrt(Math.Pow(ax + u * dx - px, 2) + Math.Pow(ay + u * dy - py, 2));
            if (off > Tol) { anchor = pts[i]; res.Add(P(anchor)); }
        }
        if (pts.Count > 1) res.Add(P(pts[^1]));
        return res;
    }

    /// <summary>The airports the map shows learned data for: the traffic airport and the destination prepared ahead.</summary>
    /// <summary>The traffic airport, the destination prepared ahead and the satellite fields.</summary>
    public List<string> AirportsInUse()
    {
        lock (_gate) return MapAirports().Concat(_neighbours).Select(w => w.Apt.Icao).Distinct().ToList();
    }

    private IEnumerable<TrafficWorld> MapAirports()
    {
        if (_world is not null) yield return _world;
        if (_ahead is not null && _ahead.Apt != _world?.Apt) yield return _ahead;
    }

    /// <summary>The taxi flow for the map (the traffic airport's and the destination's): one-way stretches with their direction.</summary>
    public object? TaxiFlowMap()
    {
        lock (_gate)
        {
            var stretches = new List<object>();
            var keys = new List<string>();
            var sums = new List<string>();
            foreach (var w in MapAirports())
            {
                if (w.Flow is not { } fl) continue;
                keys.Add(w.Apt.Icao + fl.Key);
                sums.Add($"{w.Apt.Icao}: {fl.Summary}");
                foreach (var st in fl.Stretches)
                    stretches.Add(new
                    {
                        name = st.name, use = st.use, real = st.real, icao = w.Apt.Icao,
                        points = st.pts.Select(q => { var (la, lo) = w.Apt.Map.ToLatLon(q); return new[] { Math.Round(la, 6), Math.Round(lo, 6) }; }),
                    });
            }
            if (keys.Count == 0) return null;
            return new { key = string.Join("|", keys), summary = string.Join(" · ", sums), stretches };
        }
    }

    public object? MapNetPath(string id)
    {
        var l = network.Find(id);
        if (l is null || !l.Located) return null;
        double clock;
        lock (_gate) clock = _world?.ScheduleClock ?? WeekMinuteNow();
        var p = NetworkModel.At(l, clock, includeGround: true);
        var f = p is null ? 0 : p.FlownNm / l.PathNm;
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
                // The traffic airport's checklist, or (en route, no traffic airport) the destination's being prepared ahead.
                preparing = _prep is { Ready: false } pp && w is not null && pp.Icao == w.Apt.Icao
                    ? new { icao = pp.Icao, ahead = false, seconds = (int)pp.Secs, items = pp.Items.Select(i => new { i.item, i.done, i.note }).ToList() }
                    : w is null && _aheadPrep is { Ready: false } ap0
                    ? new { icao = ap0.Icao, ahead = true, seconds = (int)ap0.Secs, items = ap0.Items.Select(i => new { i.item, i.done, i.note }).ToList() } : null,
                destinationPlanned = simBrief.Plan?.Destination,
                airport = w?.Apt.Icao,
                destination = _ahead is { } ah ? $"{ah.Apt.Icao} {ah.Apt.Name}: " + (_aheadPrep is { Ready: true } ? $"ready — landing {string.Join(", ", ah.ArrRwys.Select(e => e.Ident).DefaultIfEmpty(ah.ArrRwy?.Ident ?? "?"))}{(ah.ApproachInUse is { Length: > 0 } aa ? $" ({aa})" : "")}{(ah.ExpectedRunway is { } er ? $"; expect runway {er}, {ah.ExpectedApproach}" : "")}; {ah.Flights.Count(f => !f.Done && f.Kind != "parked")} flights running ahead of you"
                    : _aheadPrep is { } ap ? "preparing (" + string.Join("; ", ap.Items.Where(i => !i.done).Select(i => i.item)) + ")" : "preparing") : null,
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
                windsAloft = "Winds aloft: " + winds.Status,
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
        want.AddRange(_aheadNeighbourWant);
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
            want.AddRange(_aheadNeighbourWant);
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
            foreach (var n in _neighbours) Retire(n);
            _neighbours.Clear();
            return;
        }
        // At once when the traffic airport changes (16 Nov: after O'Hare, Midway was set up as Orlando's neighbour for two
        // minutes, until the list was worked out again).
        if (DateTime.UtcNow - _neighbourAt > TimeSpan.FromMinutes(2) || _neighbourFor != main)
        {
            _neighbourAt = DateTime.UtcNow;
            _neighbourFor = main;
            // The airports worked by the same approach control (the FAA's records: MCO's F11 also works SFB, ORL, MLB,
            // ISM, TIX, LEE) that have airline traffic in the schedules; a 40 nm circle where the FAA data has none.
            var family = Family(main.Apt);
            _neighbourWant = NeighbourWant(main.Apt, family);
            _neighbourHow = (family.Count > 0 ? $"{nasrDb.ApproachFacility(main.Apt.Icao)} approach control" : "within 40 nm") + "; airline fields within 30 nm first";
            // Say which fields were considered and why each is or isn't a neighbour (user, 13 Nov: "I don't see SFB").
            if (family.Count > 0)
            {
                var counts = network.WeeklyCounts(family);
                var why = family.Where(i => !i.Equals(main.Apt.Icao, StringComparison.OrdinalIgnoreCase)).Select(i =>
                {
                    var c = counts.GetValueOrDefault(i);
                    var used = _neighbourWant.Contains(i, StringComparer.OrdinalIgnoreCase);
                    var reason = used ? (airports.Model(i) is null ? "waiting for its layout from LAIA Sim" : "neighbour")
                        : c < 14 ? "fewer than 14 airline flights a week in your installed AIG schedules" : "more than 6 busier fields";
                    return $"{i} {c} a week ({reason})";
                });
                var sig = string.Join("; ", why);
                if (sig != _neighbourWhy) { _neighbourWhy = sig; log.LogInformation("Neighbouring fields considered ({how}): {why}", _neighbourHow, sig); }
            }
            // Learn the airport and its neighbours from real tracks (M6), once, in the background.
            // Only fields with airline passenger service (user, 13 Nov); the neighbours are chosen that way already.
            var learn = new List<AirportModel>();
            if (PassengerService(main.Apt.Icao) == true) learn.Add(main.Apt);
            foreach (var icao in _neighbourWant) if (airports.Model(icao) is { } m) learn.Add(m);
            if (_ahead is { } ah && PassengerService(ah.Apt.Icao) != false) learn.Add(ah.Apt);
            foreach (var icao in _aheadNeighbourWant) if (airports.Model(icao) is { } m) learn.Add(m);
            if (learn.Count > 0) history.Want(learn);
        }
        foreach (var n in _neighbours.Where(n => !_neighbourWant.Contains(n.Apt.Icao) || n.ClockOwner != main).ToList())
        {
            Retire(n); _neighbours.Remove(n);
        }
        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var icao in _neighbourWant)
        {
            var model = airports.Model(icao);
            if (model is null) continue;
            foreach (var a in model.Data.Arrivals) shared.Add(AirportRules.Base(a.Name));
            if (_neighbours.Any(n => n.Apt.Icao == icao)) continue;
            // Never a second copy of an airport LAIA already runs in full (the destination prepared ahead).
            if (_ahead?.Apt.Icao == icao) continue;
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

    /// <summary>The airports worked by the same approach control as this one (within 120 nm).</summary>
    private List<string> Family(AirportModel apt) => nasrDb.SameApproach(apt.Icao)
        .Where(i => worldAirports.Find(i) is { } p && Geo2.DistanceNm(apt.Data.Lat, apt.Data.Lon, p.lat, p.lon) < 120).ToList();

    /// <summary>The neighbouring fields of an airport: the busiest airline fields of its approach control (or within 40 nm), up to six.
    /// Airline fields within 30 nm always come first, whoever staffs them (user, 22 Nov: Newark's approach control
    /// moved to Philadelphia's staffing but its airspace is still New York's — the FAA records give "PHL", so Newark
    /// got Philadelphia, 70 nm away, and not LaGuardia (14 nm) or JFK (18 nm)); then the approach control's fields.</summary>
    private List<string> NeighbourWant(AirportModel apt, List<string> family)
    {
        var close = network.BusyAirportsNear(apt.Data.Lat, apt.Data.Lon, 30, 14, apt.Icao);
        var rest = family.Count > 0 ? network.BusyAmong(family, 14) : network.BusyAirportsNear(apt.Data.Lat, apt.Data.Lon, 40, 14, apt.Icao);
        return close.Concat(rest).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(i => !i.Equals(apt.Icao, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
    }

    private void UpdateWorld(AircraftState sim)
    {
        try { UpdateWorldCore(sim); UpdateAhead(sim); } finally { UpdateNeighbours(); }
    }

    /// <summary>
    /// The destination, ready before the user gets there (user, 13 Nov: everything prepared first, quality over speed):
    /// once the user is on the way and within 300 nm of the flight plan's destination (250 until 16 Nov; user: "to
    /// allow the user to configure arriving runway and approach sooner"), its world is created and
    /// prepared exactly like the departure field (layout, schedule, ATIS, METAR, AAUP, charts, real tracks, runways,
    /// taxi flow, pushbacks, routes) and its traffic runs unseen; it's drawn as the user comes within range, and it
    /// becomes the traffic airport at 40 nm with its traffic already flowing (not started from nothing then).
    /// Its neighbouring fields' layouts, schedules and real tracks are asked for at the same time.
    /// </summary>
    private void UpdateAhead(AircraftState sim)
    {
        var dest = simBrief.Plan?.Destination;
        var model = dest is { Length: > 0 } ? airports.Model(dest) : null;
        var want = Level != TrafficLevel.Off && model is { HasGround: true } && !sim.OnGround && _world?.Apt != model
                   && Geo2.DistanceNm(sim.Lat, sim.Lon, model.Data.Lat, model.Data.Lon) < 300;
        if (_ahead is not null && (!want || _ahead.Apt != model))
        {
            log.LogInformation("Destination {icao} no longer ahead (flight plan changed or turned back): its traffic is dropped", _ahead.Apt.Icao);
            foreach (var f in _ahead.Flights) f.Done = true;
            _ahead = null; _aheadPrep = null; _aheadNeighbourWant = new();
        }
        if (!want || _ahead is not null || model is null) return;
        if (!source.CatalogKnown) return;
        _ahead = new TrafficWorld(model, source) { Level = Level, Automatic = false, HoldRunways = true, GenericTraffic = false, Log = s => log.LogInformation("{msg}", s) };
        _aheadPrep = new Prep(model.Icao);
        if (metar.Wind(model.Icao) is { } mw) _ahead.Wind = new Wind(mw.dir ?? 0, mw.kt);
        _aheadNeighbourWant = NeighbourWant(model, Family(model));
        log.LogInformation("Destination {icao} {name}: preparing it ahead of you ({nm:0} nm out); neighbouring fields asked for: {nbs}", model.Icao, model.Name,
            Geo2.DistanceNm(sim.Lat, sim.Lon, model.Data.Lat, model.Data.Lon), _aheadNeighbourWant.Count == 0 ? "none" : string.Join(", ", _aheadNeighbourWant));
        var w = _ahead;
        _aheadPrep.Survey = Task.Run(() =>
        {
            try { var r = w.PushbackSurvey(); log.LogInformation("{icao}: {survey}", w.Apt.Icao, r); }
            catch (Exception ex) { log.LogWarning("Pushback survey failed: {msg}", ex.Message); }
        });
        var learn = new List<AirportModel>();
        if (PassengerService(model.Icao) != false) learn.Add(model);
        foreach (var icao in _aheadNeighbourWant) if (airports.Model(icao) is { } m) learn.Add(m);
        if (learn.Count > 0) history.Want(learn);
    }

    /// <summary>
    /// The airports LAIA runs with ground traffic (user, 12 Nov: "as far as ground traffic we only need the departing
    /// and arriving fields of the user"; 15 Nov, KORD: airliners landing at KPWK). With a flight plan: its departure
    /// and destination fields only — flying past or near another airport (KBIS, KRST, KDBQ, KJVL, then KPWK 19 nm
    /// from O'Hare) no longer makes it the traffic airport, and the destination takes over as soon as the user is
    /// within 40 nm of it. On the ground somewhere else (a different start), that airport. With no flight plan: the
    /// nearest airport with airline service (14+ passenger flights a week in the schedules).
    /// </summary>
    private bool MayRun(string icao, AircraftState sim)
    {
        if (simBrief.Plan is { } plan && (plan.Origin.Length > 0 || plan.Destination.Length > 0))
        {
            if (icao.Equals(plan.Origin, StringComparison.OrdinalIgnoreCase) || icao.Equals(plan.Destination, StringComparison.OrdinalIgnoreCase)) return true;
            if (!sim.OnGround) return false;
            return airports.Nearest(sim.Lat, sim.Lon, 3) is { } here && here.model.Icao == icao;
        }
        // No flight plan yet: in the air, give it a minute after the server starts before going by the nearest
        // airport (KMLI, 15 Nov: the server restarted after an update in cruise ORD–MCI and picked Quad City 23 nm
        // away, a moment before the SimBrief plan was read). And only an airport known to have airline service.
        if (!sim.OnGround && _started.Elapsed < TimeSpan.FromSeconds(60)) return false;
        return PassengerService(icao) == true;
    }

    private void UpdateWorldCore(AircraftState sim)
    {
        // A traffic airport that's no longer one LAIA may run (the flight plan arrived after it was picked, or
        // changed): in the air, it's let go — its flights finish, nothing new starts.
        if (_world is not null && !sim.OnGround && !MayRun(_world.Apt.Icao, sim))
        {
            log.LogInformation("Traffic airport {icao} let go: not your departure or destination", _world.Apt.Icao);
            _world.Automatic = false;
            if (_world.Flights.Count > 0) Retire(_world);
            _world = null; _prep = null;
        }
        var near = airports.Nearest(sim.Lat, sim.Lon, 40, icao => MayRun(icao, sim));
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
                if (_world is not null && _world.Flights.Count > 0) Retire(_world);
                if (_ahead is { } a && a.Apt == n.model)
                {
                    // The destination prepared ahead: it carries on as the traffic airport, its traffic already flowing.
                    _world = a; _prep = _aheadPrep; _ahead = null; _aheadPrep = null;
                    _neighbourAt = DateTime.MinValue;   // its neighbours now
                    log.LogInformation("Traffic airport: {icao} {name} ({nm:0} nm away), prepared ahead ({ready}); {n} flights already running", a.Apt.Icao, a.Apt.Name, n.nm,
                        _prep?.Ready == true ? "ready" : "still preparing", a.Flights.Count(f => !f.Done));
                    _testWorld = null;
                    return;
                }
                _world = new TrafficWorld(n.model, source) { Level = Level, Automatic = false, HoldRunways = true, GenericTraffic = false, Log = s => log.LogInformation("{msg}", s) };
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
        else if (_world is not null && sim.Connected
                 && Geo2.DistanceNm(sim.Lat, sim.Lon, _world.Apt.Data.Lat, _world.Apt.Data.Lon) > 80)
        {
            // The departure field keeps its traffic (departures still going, arrivals still coming) until the user is
            // well away (80 nm: beyond the 60 nm drawn), so it doesn't go quiet while still in sight.
            // Left the airport's area: let the remaining flights finish, but start nothing new.
            Retire(_world);
            _world = null;
        }
    }

    /// <summary>
    /// An airport the user has left: nothing new starts, the aircraft on its ground and close in finish, and the rest
    /// go back to the whole sky at once (user, 19 Nov: "TM seems still to work on the departure field's traffic way after
    /// departure" — with the 400 nm hand-over it kept O'Hare's arrivals for 45 minutes after the user had gone).
    /// </summary>
    private void Retire(TrafficWorld w)
    {
        w.Automatic = false;
        var nm = Geo2.MPerNm;
        var gone = w.Flights.RemoveAll(f => !f.OnGround && f != w.UserArr
            && (f.Kind == "arrival" && f.Pos.Length > 40 * nm || f.Kind == "departure" && f.Pos.Length > 20 * nm));
        if (gone > 0) log.LogInformation("{icao}: {n} aircraft far from it handed back to the whole sky (you've left)", w.Apt.Icao, gone);
        if (!_retiring.Contains(w)) _retiring.Add(w);
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
            // Timing for the performance line (PerfMonitor): the whole update, the wait for the lock, and its parts.
            var tickSw = System.Diagnostics.Stopwatch.StartNew();
            var parts = new List<(string, double)>();
            double lockMs = 0;
            var partSw = new System.Diagnostics.Stopwatch();
            void Part(string name) { parts.Add((name, partSw.Elapsed.TotalMilliseconds)); partSw.Restart(); }
            try
            {
                var sim = state.Sim;
                var live = sim is not null && sim.Connected && DateTime.UtcNow - state.SimUpdatedUtc < TimeSpan.FromSeconds(30);
                var lockSw = System.Diagnostics.Stopwatch.StartNew();
                lock (_gate)
                {
                    lockMs = lockSw.Elapsed.TotalMilliseconds;
                    tickSw.Restart();
                    partSw.Restart();
                    if (live && now - lastWorldCheck > 5) { UpdateWorld(sim!); lastWorldCheck = now; Part("airport check"); }
                    if (now - _othersAt > 1) { _othersAt = now; ShareTraffic(); Part("sharing between airports"); }
                    if (live) UpdateFlCorrection(sim!);
                    if (live && now - _enrouteAt > 2) { _enrouteAt = now; _enroute = EnRoute(sim!); Part("en route"); }
                    if (live) UpdateWinds(sim!, now);
                    if (now - _routesAt >= 60 && routesDb.Routes is { } prr && nasrDb.Nav is { } navr) { _routesAt = now; try { network.ApplyRoutes(prr, navr); } catch (Exception ex) { log.LogInformation("Whole-sky routes failed: {msg}", ex.Message); } }
                    if (now - _meterAt >= 30) { _meterAt = now; try { MeterArrivals(); } catch (Exception ex) { log.LogInformation("Metering failed: {msg}", ex.Message); } Part("metering"); }
                    foreach (var w in Worlds().ToList())
                    {
                        partSw.Restart();
                        if (live)
                        {
                            // The airport's METAR is the source of truth for its wind (user rule). Only with no METAR is
                            // the sim's wind used — and only while the user is at the airport (within 15 nm, below 5,000 ft
                            // above it), since it's the wind where the user is; otherwise the last wind stays.
                            var dNm = Geo2.DistanceNm(sim!.Lat, sim.Lon, w.Apt.Data.Lat, w.Apt.Data.Lon);
                            if (metar.Wind(w.Apt.Icao) is { } mw) w.Wind = new Wind(mw.dir ?? w.Wind.FromDeg, mw.kt);
                            else if (dNm < 15 && sim.AltitudeFt < w.Apt.ElevationFt + 5000) w.Wind = new Wind(sim.WindDirection, sim.WindSpeed);
                            w.LowWeather = metar.LowWeather(w.Apt.Icao);
                            w.SafetyLogic = SafetyLogicAirports.Contains(w.Apt.Icao);
                            w.UserPos = w.Apt.Map.ToXY(sim.Lat, sim.Lon);
                            w.UserOnGround = sim.OnGround;
                            w.UserAltFt = UserLevelFt(sim); w.UserGs = sim.GroundSpeed; w.UserTrack = sim.HeadingTrue; w.UserVs = sim.VerticalSpeed;
                            var plan = simBrief.Plan;
                            w.UserCallsign = plan?.Callsign is { Length: > 0 } pc ? pc : (sim.AtcAirline + " " + sim.AtcFlightNumber).Trim() is { Length: > 0 } ac ? ac : sim.AtcId;
                            w.UserType = plan?.Aircraft ?? "";
                            w.UserDestination = plan?.Destination;
                            w.UserOrigin = plan?.Origin;
                            w.UserStar = plan?.Star;
                            w.UserStarFrom = plan?.Star is { Length: > 0 } pst && plan.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } toks
                                && Array.FindLastIndex(toks, x => x.Equals(pst, StringComparison.OrdinalIgnoreCase)) is > 0 and var si ? toks[si - 1] : null;
                        }
                        if (w == _world || w == _ahead || _neighbours.Contains(w))
                        {
                            w.Level = Level;
                            // The installed AIG schedule is the real one to follow (exact models and registrations).
                            if (w.Schedule is null) w.Schedule = schedules.Get(w.Apt.Icao);
                            if (w == _world || w == _ahead) w.Gates = source.Gates(w.Apt.Icao);
                            w.Atis = Atis.Get(w.Apt.Icao);
                            if (w.Rules is null && Aaup.Get(w.Apt.Icao) is { } aaup)
                            {
                                w.Rules = AaupReader.Rules(aaup.text, aaup.charts, w.Apt.Data);
                                if (aaup.charts.Length > 0) log.LogInformation("{icao}: {rules}", w.Apt.Icao, w.Rules.Describe());
                            }
                            if (w.SidTops is not { Count: > 0 }) w.SidTops = Aaup.SidTops(w.Apt.Icao);
                            if (w.StarExpects is null && Aaup.StarExpects(w.Apt.Icao, StarFixes(w.Apt)) is { } ex)
                                w.StarExpects = ex.ToDictionary(kv => kv.Key, kv => kv.Value.Select(e => (e.Fix, e.MinFt, e.MaxFt, e.Kt, e.When, e.How)).ToList(), StringComparer.OrdinalIgnoreCase);
                            // A neighbour shares the main airport's STARs: BITHO7 is MCO's and MLB's. Their charts' "expects"
                            // are read for each airport itself, since a chart lists which airports each one is for (BITHO7:
                            // LAMMA at 15,000 landing Melbourne, 12,000 landing Orlando).
                            if (_neighbours.Contains(w) && _world is { } mw) w.SharedArrivals = mw.Apt.Data.Arrivals;
                            w.Routes = routesDb.Routes;
                            w.NavData = nasrDb.Nav;
                            w.Learned = history.Profile(w.Apt.Icao);
                            // No ATIS here: the runways real traffic is using now (live adsb.lol positions).
                            if (w.AtisLanding.Count == 0) { var lr = liveRunways.Get(w.Apt); w.LiveLanding = lr.landing; w.LiveDeparting = lr.departing; }
                            else { w.LiveLanding = new(); w.LiveDeparting = new(); }
                            w.Locate ??= code => worldAirports.Find(code) is { } p ? (p.lat, p.lon) : null;
                            var icaoM = w.Apt.Icao;
                            w.MeterOf ??= l => MeterFor(icaoM, l);
                            var self = w;
                            w.CallsignInUse ??= cs => Worlds().Any(o => o != self && o.Flights.Any(f => !f.Done && f.Kind != "parked" && f.Callsign == cs));
                            if (w == _world) UpdatePrep(w, _prep, _neighbours);
                            else if (w == _ahead) UpdatePrep(w, _aheadPrep, Array.Empty<TrafficWorld>());
                        }
                        w.WeekMinute = live && sim!.ZuluWeekMinute >= 0 ? sim.ZuluWeekMinute : WeekMinuteNow();
                        // Don't run traffic while nobody is flying (sim closed): it would all depart unseen.
                        if (live || w.Flights.Any(f => f.Test)) w.Step(dt);
                        Part(w.Apt.Icao);
                    }
                    _retiring.RemoveAll(w => w.Flights.Count == 0 || w.Flights.All(f => f.Kind == "parked"));
                    // LAIA's own track recording (map replay, and the log reviews): each world's log lines about an
                    // aircraft become markers where it was.
                    if (live)
                    {
                        foreach (var w in Worlds())
                        {
                            if (w == _testWorld || !_recorded.Add(w)) continue;
                            var inner = w.Log; var ww = w;
                            w.Log = s => { inner?.Invoke(s); TrackRecorder.Instance.Event(ww, s); };
                        }
                        TrackRecorder.Instance.Frame(Worlds().Where(w => w != _testWorld && !_retiring.Contains(w)));
                    }
                    try { SelfTestStep(); } catch (Exception ex) { log.LogInformation("Self-test stopped: {msg}", ex.Message); _shadow = null; _shadowDoneFor = _shadowKey; }
                    _stampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
            catch (Exception ex) { log.LogWarning("Traffic step failed: {msg}", ex.Message); }
            perf.Tick(tickSw.Elapsed.TotalMilliseconds, lockMs, parts);
        }
    }
}
