using Laia.Common.Airports;

namespace Laia.Server.TrafficCore;

/// <summary>Peak is a test setting: the airport's busiest hours from its real schedule, replayed now (not the real time).</summary>
public enum TrafficLevel { Off, Light, Normal, Busy, Peak }

public sealed record AircraftIdentity(string Airline, string Type, string Callsign, string Other);

/// <summary>Supplies believable airlines, types and flight numbers (from installed models and schedules).</summary>
public interface IFlightSource
{
    /// <summary>
    /// A new aircraft (airline + type) that fits a gate of the given size (0 small, 1 medium, 2 heavy), from one of
    /// the given airlines when possible (the airlines the stand belongs to).
    /// </summary>
    AircraftIdentity NewAircraft(string airport, int maxGateSize, Random rng, IReadOnlyCollection<string>? airlines = null);
    /// <summary>A flight number and the other airport for this airline's next departure or arrival.</summary>
    (string callsign, string other) NewFlight(string airport, string airline, bool departure, Random rng, string type = "");
}

public sealed class Stop
{
    public double S;
    public int Runway = -1;     // runway being crossed, or -1
    public double ExitS;        // distance at which the runway is clear again
    public bool Cleared;
}

public sealed class Flight
{
    public required string Id;
    public required string Callsign;
    public required string Airline;
    public required string Type;
    public string Other = "";
    public required string Kind;   // parked | departure | arrival
    public required Perf P;
    public V2 Pos;
    public double AltFt, Heading, Ias, Gs, Vs, Pitch, Bank;
    public bool OnGround = true, Gear = true;
    public string Phase = "parked";
    public int Lights;
    public PathGeom? Path;
    public double S;
    public bool Reverse;
    public double MaxKt = 20;
    public readonly List<Stop> Stops = new();
    public AirportModel.ParkingSpot? Spot;
    public RunwayEnd? Rwy;
    public double Timer, PhaseTime, Age;
    public double LiftoffTime = -1;
    public int OnRunway = -1;
    public int Reserve = -1;
    public bool Done, Test;
    public double ThrS;                     // arrival: distance along the path of the landing threshold
    public int HoldNode = -1, EntryNode = -1, StartNode = -1;
    public List<int> LineupNodes = new();   // nodes between the hold-short point and the runway
    public double BlockedFor;
    public Flight? BlockedBy, Ignore;
    public double IgnoreUntil;
    public bool LandingChecked, WentAround, BlockedByUser;
    public double StuckFor;
    public double RerouteTime = -999;
    public double LastOrbit = -999;
    public string Route = "";               // procedure names for the control page
    public string Reg = "";                 // registration (scheduled flights: the same airframe flies on)
    public string Title = "";               // exact model/livery (scheduled flights)
    public string? Brand;                   // the airline whose flight it is (DAL for a Delta Connection flight), when known
    public bool UserIsObstacle;
    public char Wake => _wake ??= Cwt.For(Type, P);
    private char? _wake;
    /// <summary>Published speed limits along the path: distance along the path of the fix, and the limit (kt).</summary>
    public List<(double S, double Kt)> SpeedLimits = new();
    /// <summary>The planned departure route (planned when lining up, so the spacing can look at where it goes).</summary>
    public List<AirPoint>? DepPlan;
    public string DepRoute = "";
    public double DepCourse = double.NaN;   // the track about 4 nm after the runway end
    public bool StableChecked;
    public double LastHold = -999;

    public double AglFt(double groundFt) => OnGround ? 0 : Math.Max(0, AltFt - groundFt);

    public void SetPhase(string phase, double now)
    {
        if (Phase == phase) return;
        Phase = phase;
        PhaseTime = now;
    }
}

/// <summary>
/// The traffic simulation for one airport: parked aircraft, pushback, taxi, runway use, takeoff and climb
/// along the real departure procedure, arrivals along the real arrival and approach, spacing on final,
/// landing, runway exit and taxi to a gate. Pure logic (no simulator or network), so it can be tested offline.
/// </summary>
public sealed class TrafficWorld
{
    public const int Nav = 1, Beacon = 2, Strobes = 4, Landing = 8, Taxi = 16, Logo = 32, Wing = 64, Cabin = 128;
    private const double G = 9.81;
    private const double Kt = Geo2.MpsPerKt;

    public AirportModel Apt { get; }
    public IFlightSource Source { get; }
    public Random Rng { get; }
    public Action<string> Log { get; set; } = _ => { };
    public List<Flight> Flights { get; } = new();
    public double Time { get; private set; }
    public Wind Wind { get; set; }
    /// <summary>The traffic level. Changing it re-picks the runways (a busy airport may use a second, crossing runway).</summary>
    public TrafficLevel Level
    {
        get => _level;
        set
        {
            if (_level == value) return;
            var wasBusy = _level is TrafficLevel.Normal or TrafficLevel.Busy or TrafficLevel.Peak;
            _level = value;
            var isBusy = value is TrafficLevel.Normal or TrafficLevel.Busy or TrafficLevel.Peak;
            if (value != TrafficLevel.Peak) { _peakSet = false; _peakOffset = 0; PeakInfo = null; }
            if (wasBusy != isBusy && ArrRwy is not null) SelectRunways(true);
        }
    }
    private TrafficLevel _level = TrafficLevel.Normal;
    public RunwayEnd? ArrRwy { get; private set; }
    public RunwayEnd? DepRwy { get; private set; }
    public V2? UserPos { get; set; }
    public bool UserOnGround { get; set; }
    public bool Automatic { get; set; } = true;
    public int Movements { get; private set; }
    /// <summary>The airport's real schedule; when set, traffic follows it instead of made-up flights.</summary>
    public ScheduleData? Schedule { get; set; }
    /// <summary>Current time as minutes into the week (Monday 00:00 UTC = 0), from the simulator's clock.</summary>
    public double WeekMinute { get; set; } = -1;
    /// <summary>The gates the airlines really use here (learned from real flight data); overrides the scenery's assignments.</summary>
    public RealGates? Gates { get; set; }
    private GatePlan? _plan;
    private RealGates? _planFrom;
    private int _planVersion = -1;

    /// <summary>The current real-gate plan for this airport's scenery (null without real gate data).</summary>
    public GatePlan? Plan
    {
        get
        {
            var g = Gates;
            if (g is null) return null;
            if (!ReferenceEquals(g, _planFrom) || g.Version != _planVersion)
            {
                _planFrom = g;
                _planVersion = g.Version;
                try { _plan = new GatePlan(Apt, g); Log($"{Apt.Icao}: real gates from {g.Source}: {_plan.Describe()}"); }
                catch (Exception ex) { _plan = null; Log($"{Apt.Icao}: real gates couldn't be used: {ex.Message}"); }
            }
            return _plan;
        }
    }

    /// <summary>
    /// Who uses a stand: the airlines really seen there; without real data for the stand, the scenery's airlines
    /// (except airlines whose real gates are known to be elsewhere).
    /// </summary>
    public IReadOnlyList<string> AirlinesAt(AirportModel.ParkingSpot s)
    {
        var plan = Plan;
        if (plan is null) return s.Airlines;
        if (plan.Owners.TryGetValue(s, out var o)) return o;
        return s.Airlines.Where(a => !plan.Knows(a)).ToList();
    }

    private int _nextId = 1;
    private double _nextDep = double.NaN, _nextArr = double.NaN, _lastRunwayCheck = -999;
    private bool _filled;

    public TrafficWorld(AirportModel apt, IFlightSource source, int seed = 0)
    {
        Apt = apt;
        Source = source;
        Rng = seed == 0 ? new Random() : new Random(seed);
    }

    // ------------------------------------------------------------------ levels

    private (int maxObjects, int maxActive, double movementsPerHour, double parkedFill) Budget => Level switch
    {
        TrafficLevel.Light => (12, 4, 10, 0.25),
        TrafficLevel.Normal => (22, 7, 20, 0.40),
        TrafficLevel.Busy => (34, 11, 32, 0.55),
        TrafficLevel.Peak => (44, 14, 40, 0.65),
        _ => (0, 0, 0, 0),
    };

    // ------------------------------------------------------------------ runways

    /// <summary>Crosswind component (kt) on a heading.</summary>
    private double Crosswind(double heading) => Math.Abs(V2.Cross(V2.FromBearing(heading), Wind.Vector));

    /// <summary>
    /// Picks the runways: the one most into the wind for landing, kept until the tailwind on it is more than
    /// 5 knots (as real airports do, so light shifting winds don't flip the airport). Departures use a parallel
    /// runway if there is one, else — when it's busy enough to matter — a crossing runway whose departures don't
    /// fly through the arrivals' final approach (LaGuardia: land 22, depart 13), else the landing runway.
    /// </summary>
    public void SelectRunways(bool force = false)
    {
        if (Apt.Ends.Count == 0) return;
        var usable = Apt.Ends.Where(e => e.LengthM >= 1400).ToList();
        if (usable.Count == 0) usable = Apt.Ends.ToList();
        bool WindOk(RunwayEnd e) => Wind.Headwind(e.Heading) >= -5 && Crosswind(e.Heading) <= 25;
        RunwayEnd best;
        if (Wind.Kt < 4 && ArrRwy is not null && !force) best = ArrRwy;
        else if (Wind.Kt < 4) best = usable.OrderByDescending(e => e.LengthM).First();
        else best = usable.OrderByDescending(e => Math.Round(Wind.Headwind(e.Heading) / 3)).ThenByDescending(e => e.LengthM).First();
        // Keep the current runways unless the tailwind on the landing runway gets above 5 knots.
        if (!force && ArrRwy is not null && Wind.Headwind(ArrRwy.Heading) > -5 && (DepRwy is null || WindOk(DepRwy) || DepRwy == ArrRwy)) return;
        var group = usable.Where(e => Math.Abs(Geo2.Diff(e.Heading, best.Heading)) < 25).OrderByDescending(e => e.LengthM).ToList();
        var arr = group[0];
        var dep = group.Skip(1).FirstOrDefault(e => e.Runway != arr.Runway && e.LengthM >= 2000);
        if (dep is null && Level is TrafficLevel.Normal or TrafficLevel.Busy or TrafficLevel.Peak)
        {
            var nm = Geo2.MPerNm;
            dep = usable.Where(e => e.Runway != arr.Runway && e.LengthM >= 1900 && WindOk(e) && Crossing(arr, e) is not null)
                .Where(e =>
                {
                    // The departure's first miles must stay clear of the final approach to the landing runway.
                    var a0 = e.FarEnd; var a1 = e.FarEnd + e.Dir * (6 * nm);
                    var f0 = arr.Threshold - arr.Dir * (12 * nm); var f1 = arr.Threshold - arr.Dir * (0.5 * nm);
                    return !SegmentsCross(a0, a1, f0, f1) && Math.Abs(Geo2.Diff(e.Heading, arr.Heading + 180)) > 30;
                })
                .OrderByDescending(e => Math.Round(Wind.Headwind(e.Heading) / 3)).ThenByDescending(e => e.LengthM).FirstOrDefault();
        }
        dep ??= arr;
        if (ArrRwy != arr || DepRwy != dep) Log($"Runways in use at {Apt.Icao}: landing {arr.Ident}, departing {dep.Ident} (wind {Wind.FromDeg:000} at {Wind.Kt:0} kt)");
        ArrRwy = arr;
        DepRwy = dep;
    }

    private static bool SegmentsCross(V2 a0, V2 a1, V2 b0, V2 b1)
    {
        double C(V2 o, V2 a, V2 b) => V2.Cross(a - o, b - o);
        var d1 = C(b0, b1, a0); var d2 = C(b0, b1, a1); var d3 = C(a0, a1, b0); var d4 = C(a0, a1, b1);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    /// <summary>Where two runways cross: the distance from each end's threshold to the intersection. Null if they don't.</summary>
    private static (double alongA, double alongB)? Crossing(RunwayEnd a, RunwayEnd b)
    {
        if (a.Runway == b.Runway || !SegmentsCross(a.Threshold, a.FarEnd, b.Threshold, b.FarEnd)) return null;
        var r = a.FarEnd - a.Threshold; var q = b.FarEnd - b.Threshold;
        var t = V2.Cross(b.Threshold - a.Threshold, q) / V2.Cross(r, q);
        var x = a.Threshold + r * t;
        return (a.Along(x), b.Along(x));
    }

    // ------------------------------------------------------------------ main step

    public void Step(double dt)
    {
        Time += dt;
        if (ArrRwy is null || Time - _lastRunwayCheck > 120) { SelectRunways(); _lastRunwayCheck = Time; }
        foreach (var f in Flights)
        {
            f.Age += dt;
            f.OnRunway = -1;
            var agl = f.AglFt(Apt.ElevationFt);
            if (f.OnGround || agl < 150)
                foreach (var e in Apt.Ends)
                    if (e.Contains(f.Pos, 3)) { f.OnRunway = e.Runway; break; }
        }
        if (Automatic && Level != TrafficLevel.Off && Apt.HasGround)
        {
            if (Schedule is { Legs.Count: > 0 } && WeekMinute >= 0) RunSchedule();
            else Schedule_();
        }
        if (Level == TrafficLevel.Off) foreach (var f in Flights) if (!f.Test) f.Done = true;
        foreach (var f in Flights.ToList())
        {
            if (f.Done) continue;
            try { Advance(f, dt); }
            catch (Exception ex) { Log($"{f.Callsign}: {ex.Message}; removed"); f.Done = true; }
        }
        foreach (var f in Flights.Where(f => f.Done && f.Spot is not null)) f.Spot = null;
        Flights.RemoveAll(f => f.Done);
    }

    // ------------------------------------------------------------------ scheduling

    private void Schedule_()
    {
        var (maxObjects, maxActive, mph, fill) = Budget;
        var gates = UsableSpots().ToList();
        if (!_filled)
        {
            _filled = true;
            _nextDep = Time + 20;
            _nextArr = Time + 3;
            var want = (int)Math.Min(gates.Count * fill, Math.Max(0, maxObjects - maxActive));
            foreach (var s in gates.OrderBy(_ => Rng.Next()).Take(want))
            {
                if (!SpotFree(s)) continue; // an overlapping alternative stand is already taken
                try { SpawnParked(s); }
                catch (Exception ex) { Log($"Couldn't park an aircraft at {s.Label}: {ex.Message}"); }
            }
            Log($"{Apt.Icao}: traffic started with {want} parked aircraft ({gates.Count} usable parking spots)");
        }
        var active = Flights.Count(f => f.Kind != "parked" && !f.Test);
        var interval = 3600 / Math.Max(1, mph / 2);
        if (Time >= _nextDep)
        {
            var queue = Flights.Count(f => f.Kind == "departure" && f.Phase is "pushback" or "engine start" or "taxi" or "holding short");
            if (active < maxActive && queue < 3 && StartDeparture(null, 60 + Rng.NextDouble() * 60) is not null) Movements++;
            _nextDep = Time + interval * (0.6 + 0.8 * Rng.NextDouble());
        }
        if (Time >= _nextArr)
        {
            var arrivalsAirborne = Flights.Count(f => f.Kind == "arrival" && !f.OnGround);
            if (active < maxActive && Flights.Count < maxObjects && arrivalsAirborne < 4)
            {
                var first = Movements == 0 && !Flights.Any(f => f.Kind == "arrival");
                if (SpawnArrival(first ? 14 : 40, false) is not null) Movements++;
            }
            var lastHeavy = Flights.Where(f => f.Kind == "arrival" && !f.OnGround).OrderBy(f => f.Age).FirstOrDefault()?.P.Heavy ?? false;
            _nextArr = Time + Math.Max(lastHeavy ? 170 : 130, interval * (0.6 + 0.8 * Rng.NextDouble()));
        }
        // Too many parked aircraft (arrivals keep coming): remove a few, far from the user first.
        var parked = Flights.Where(f => f.Kind == "parked" && f.Phase == "parked").ToList();
        if (Flights.Count > maxObjects && parked.Count > 0)
        {
            var victim = parked.OrderByDescending(f => UserPos is { } u ? (f.Pos - u).Length : 0).First();
            victim.Done = true;
        }
    }

    // ------------------------------------------------------------------ real schedule

    private readonly HashSet<string> _done = new();
    private bool _schedStarted;

    /// <summary>Signed minutes from now until a time of the week (-5040..5040).</summary>
    private double Until(int weekMin) => ((weekMin - SchedNow) % 10080 + 10080 + 5040) % 10080 - 5040;

    // ------------------------------------------------------------------ peak-hour test setting
    private double _peakOffset;
    private bool _peakSet;
    private double _peakStart, _peakLen = 180;
    /// <summary>What the peak test is replaying, for the control page (null when not in use).</summary>
    public string? PeakInfo { get; private set; }

    /// <summary>The schedule's clock: the real time, or in the peak test the replayed busiest hours.</summary>
    private double SchedNow => ((WeekMinute + _peakOffset) % 10080 + 10080) % 10080;

    /// <summary>
    /// Finds the airport's busiest three hours in the week from its real schedule (movements per 15 minutes)
    /// and shifts the schedule clock so they start now. When they're over, they are replayed again.
    /// </summary>
    private void PlanPeak(List<ScheduleLeg> legs)
    {
        var me = Apt.Icao;
        var bins = new int[672];
        foreach (var l in legs)
        {
            if (l.Dep == me) bins[(l.DepMin % 10080) / 15]++;
            if (l.Arr == me) bins[(l.ArrMin % 10080) / 15]++;
        }
        var win = (int)(_peakLen / 15);
        int best = 0, bestSum = -1;
        for (var b = 0; b < 672; b++)
        {
            var sum = 0;
            for (var k = 0; k < win; k++) sum += bins[(b + k) % 672];
            if (sum >= bestSum) { bestSum = sum; best = b; }   // equal: the later start, so the window begins where the rush does
        }
        _peakStart = best * 15;
        _peakOffset = _peakStart - WeekMinute;
        _peakSet = true;
        var day = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" }[(int)(_peakStart / 1440) % 7];
        var t = TimeSpan.FromMinutes(_peakStart % 1440);
        PeakInfo = $"Peak test: replaying {Apt.Icao}'s busiest 3 hours ({day} {t:hh\\:mm} UTC, {bestSum} movements), not the real time";
        Log($"{Apt.Icao}: {PeakInfo}");
    }

    /// <summary>The traffic level thins the real schedule (the same flights each time).</summary>
    private bool Keep(ScheduleLeg l)
    {
        var pct = Level switch { TrafficLevel.Light => 35, TrafficLevel.Normal => 65, _ => 100 };   // Busy and Peak: every flight
        // Whole airframes are kept or left out (so an aircraft that lands also leaves again), the same ones every time.
        uint h = 2166136261;
        foreach (var c in l.Reg.Length > 0 ? l.Reg : l.Callsign) h = (h ^ c) * 16777619;
        return h % 100 < pct;
    }

    private static string Key(ScheduleLeg l, char kind) => $"{kind}|{l.Reg}|{l.Flight}|{l.DepMin}";

    /// <summary>
    /// Traffic from the real schedule: aircraft on the ground now are parked at their airline's gates, departures
    /// push back at their scheduled times, arrivals are placed so they land at theirs, and an arriving airframe
    /// later leaves again on its next scheduled flight.
    /// </summary>
    /// <summary>
    /// Where a new arrival can start: the wanted distance from the runway (nm), moved further out until it's at least
    /// 5 nm from every arrival already flying to the landing runway. -1 if that would be more than 50 nm out.
    /// </summary>
    private double ArrivalSlot(double wantNm)
    {
        if (ArrRwy is null) SelectRunways(true);
        var rwy = ArrRwy;
        var taken = Flights.Where(o => !o.Done && o.Kind == "arrival" && !o.OnGround && o.Rwy is not null && rwy is not null && o.Rwy.Runway == rwy.Runway)
            .Select(o => o.Phase == "go-around" ? 12.0 : (o.ThrS - o.S) / Geo2.MPerNm).OrderBy(x => x).ToList();
        var d = wantNm;
        for (var tries = 0; tries < 20; tries++)
        {
            var clash = taken.FirstOrDefault(o => Math.Abs(o - d) < 5, double.NaN);
            if (double.IsNaN(clash)) return d <= 50 ? d : -1;
            d = clash + 5;
        }
        return -1;
    }

    private void RunSchedule()
    {
        var (maxObjects, maxActive, _, _) = Budget;
        var legs = Schedule!.Legs;
        var me = Apt.Icao;
        if (Level == TrafficLevel.Peak)
        {
            if (!_peakSet) { PlanPeak(legs); _schedStarted = false; foreach (var f in Flights.Where(f => f.Kind == "parked" && f.Phase == "parked" && f.Reg.Length > 0)) f.Done = true; }
            else if (((SchedNow - _peakStart) % 10080 + 10080) % 10080 >= _peakLen)
            {
                // The busiest hours are over: replay them (each flight may run again, but never one that's still around).
                _peakOffset -= _peakLen;
                _done.Clear();
                Log($"{me}: peak test: replaying the busiest hours again");
            }
        }
        var deps = legs.Where(l => l.Dep == me && Keep(l)).ToList();
        var arrs = legs.Where(l => l.Arr == me && Keep(l)).ToList();
        if (!_schedStarted)
        {
            _schedStarted = true;
            _filled = true;
            // Made-up aircraft from before the schedule arrived make way for the real ones.
            foreach (var f in Flights.Where(f => f.Kind == "parked" && f.Phase == "parked" && f.Reg.Length == 0)) f.Done = true;
            // Aircraft at the gates now: every airframe whose last flight landed here and whose next one leaves
            // from here (overnight too — at night the gates fill up with the morning's first departures), plus
            // aircraft leaving in the next few hours. Soonest departures first if there isn't room for all.
            var parked = Flights.Count(f => f.Kind == "parked");
            var upcoming = new List<ScheduleLeg>();
            foreach (var g in deps.Where(l => l.Reg.Length > 0 && Until(l.DepMin) is > 4 and < 1080).GroupBy(l => l.Reg))
            {
                var next = g.OrderBy(l => Until(l.DepMin)).First();
                var untilDep = Until(next.DepMin);
                var lastArr = arrs.Where(a => a.Reg == g.Key && Until(a.ArrMin) is < -2 and > -1440).Select(a => -Until(a.ArrMin)).DefaultIfEmpty(double.MaxValue).Min();
                var lastDep = deps.Where(d => d.Reg == g.Key && Until(d.DepMin) is <= 4 and > -1440).Select(d => -Until(d.DepMin)).DefaultIfEmpty(double.MaxValue).Min();
                var arrivedAndWaiting = lastArr < double.MaxValue && lastArr < lastDep;
                var flyingIn = arrs.Any(a => a.Reg == g.Key && Until(a.ArrMin) is >= -2 && Until(a.ArrMin) < untilDep);
                if (flyingIn) continue;                                  // it lands first, then leaves
                if (arrivedAndWaiting || untilDep < 240) upcoming.Add(next);
            }
            upcoming = upcoming.OrderBy(l => Until(l.DepMin)).ToList();
            var room = Math.Max(0, maxObjects - maxActive - parked);
            var placed = 0;
            foreach (var l in upcoming)
            {
                if (placed >= room) break;
                if (Flights.Any(f => f.Reg == l.Reg && f.Reg.Length > 0)) continue;
                var perf = Perf.For(l.Type);
                var gate = FindGate(perf, l.Operator, l.Airline, l.Callsign);
                if (gate is null) continue;
                try { SpawnParked(gate, l); placed++; } catch (Exception ex) { Log($"Couldn't park {l.Callsign}: {ex.Message}"); }
            }
            // Arrivals already on their way in.
            foreach (var l in arrs.Where(l => Until(l.ArrMin) is >= 1 and <= 13).OrderBy(l => Until(l.ArrMin)))
            {
                if (l.Reg.Length > 0 && Flights.Any(f => !f.Done && f.Reg == l.Reg)) continue;
                // Spread out along the approach, clear of every arrival already flying, rather than on top of one.
                var d = ArrivalSlot(Math.Clamp(Until(l.ArrMin) * 3.2, 7, 40));
                if (d < 0) break;
                _done.Add(Key(l, 'A'));
                SpawnArrival(d, false, l);
            }
            Log($"{me}: following the real {Schedule.Source} schedule ({deps.Count} departures and {arrs.Count} arrivals a week at this level); {placed} aircraft at the gates waiting for their next flight");
        }
        var active = Flights.Count(f => f.Kind != "parked" && !f.Test);
        // Departures: board a few minutes before, push back on time.
        foreach (var l in deps)
        {
            var u = Until(l.DepMin);
            if (u > 3 || u < -15) continue;
            if (!_done.Add(Key(l, 'D'))) continue;
            if (active >= maxActive) { Log($"{l.Callsign}: skipped (traffic limit)"); continue; }
            var f = Flights.FirstOrDefault(x => x.Reg == l.Reg && x.Reg.Length > 0 && x.Kind == "parked" && x.Phase == "parked" && x.Spot is not null);
            if (f is null)
            {
                var gate = FindGate(Perf.For(l.Type), l.Operator, l.Airline, l.Callsign);
                if (gate is null) continue;
                f = SpawnParked(gate, l);
            }
            if (StartDeparture(f, Math.Max(20, u * 60), l) is not null) { active++; Movements++; }
        }
        // Arrivals: appear about 40 track miles out so they land on time.
        foreach (var l in arrs)
        {
            var u = Until(l.ArrMin);
            if (u > 13 || u < 1) continue;
            if (l.Reg.Length > 0 && Flights.Any(f => !f.Done && f.Reg == l.Reg)) continue;   // that airframe is still around
            if (_done.Contains(Key(l, 'A'))) continue;
            if (active >= maxActive || Flights.Count >= maxObjects) { _done.Add(Key(l, 'A')); Log($"{l.Callsign}: skipped (traffic limit)"); continue; }
            // Start clear of every arrival already flying (a little later than scheduled if needed); if there's no
            // room yet, try again on the next step.
            var d = ArrivalSlot(Math.Clamp(u * 3.2, 7, 40));
            if (d < 0) continue;
            _done.Add(Key(l, 'A'));
            if (SpawnArrival(d, false, l) is not null) { active++; Movements++; }
        }
        // Too many parked aircraft: remove the ones with no flight soon.
        if (Flights.Count > maxObjects)
        {
            var soon = new HashSet<string>(deps.Where(l => Until(l.DepMin) is > 0 and < 120).Select(l => l.Reg));
            var victim = Flights.Where(f => f.Kind == "parked" && f.Phase == "parked" && !soon.Contains(f.Reg))
                .OrderByDescending(f => UserPos is { } u2 ? (f.Pos - u2).Length : 0).FirstOrDefault();
            if (victim is not null) victim.Done = true;
        }
    }

    private IEnumerable<AirportModel.ParkingSpot> UsableSpots()
    {
        var gates = Apt.Spots.Where(s => s.IsGate).ToList();
        return gates.Count >= 3 ? gates : Apt.Spots.Where(s => s.RadiusM >= 14 && s.Type is not (1 or 2 or 11 or 12 or 13));
    }

    private bool SpotFree(AirportModel.ParkingSpot s) =>
        !Flights.Any(f => !f.Done && f.Spot is not null && (f.Spot == s || s.Overlaps.Contains(f.Spot))) &&
        !(UserPos is { } u && (u - s.P).Length < s.RadiusM + 25);

    private AirportModel.ParkingSpot? FindGate(Perf p, string airline, string? alsoAirline = null, string? callsign = null)
    {
        var free = UsableSpots().Where(s => SpotFree(s) && s.Size >= p.GateSize && (s.RadiusM <= 0 || s.RadiusM >= p.SpanM / 2 * 0.85)).ToList();
        if (free.Count == 0) return null;
        var plan = Plan;
        // Today's real gate of this very flight.
        if (plan is not null && callsign is not null && plan.ByFlight.TryGetValue(callsign, out var exact))
        {
            var hit = exact.FirstOrDefault(free.Contains);
            if (hit is not null) return hit;
        }
        // The brand's own stands first (a Delta Connection flight at Delta's gates), then the operator's,
        // then stands next to the airline's real gates, then stands nobody uses; other airlines' stands last.
        var pools = new List<List<AirportModel.ParkingSpot>>();
        if (alsoAirline is not null && alsoAirline != airline) pools.Add(free.Where(s => AirlinesAt(s).Contains(alsoAirline)).ToList());
        pools.Add(free.Where(s => AirlinesAt(s).Contains(airline)).ToList());
        if (plan is not null)
        {
            var mine = new List<AirportModel.ParkingSpot>();
            foreach (var a in new[] { alsoAirline, airline })
                if (a is not null && plan.ByAirline.TryGetValue(a, out var l)) mine.AddRange(l);
            if (mine.Count > 0)
                pools.Add(free.Where(s => AirlinesAt(s).Count == 0 && mine.Any(m => (m.P - s.P).Length < 150)).ToList());
        }
        pools.Add(free.Where(s => AirlinesAt(s).Count == 0).ToList());
        pools.Add(free);
        var pool = pools.First(x => x.Count > 0);
        // The smallest stand that fits, so the big ones stay free for the big aircraft.
        var minSize = pool.Min(s => s.Size);
        pool = pool.Where(s => s.Size == minSize).ToList();
        return pool[Rng.Next(pool.Count)];
    }

    private string NewId() => $"{Apt.Icao}{_nextId++:000}";

    private Flight SpawnParked(AirportModel.ParkingSpot s, ScheduleLeg? leg = null)
    {
        var id = leg is not null ? new AircraftIdentity(leg.Operator, leg.Type, leg.Callsign, leg.Arr)
            : Source.NewAircraft(Apt.Icao, s.Size, Rng, AirlinesAt(s) is { Count: > 0 } al ? al.ToList() : null);
        var f = new Flight
        {
            Id = NewId(), Callsign = id.Callsign, Airline = id.Airline, Type = id.Type, Other = id.Other, Kind = "parked", P = Perf.For(id.Type),
            Pos = s.P, Heading = s.Heading, AltFt = Apt.ElevationFt, Spot = s, Age = Rng.NextDouble() * 3000,
            Reg = leg?.Reg ?? "", Title = leg?.Title ?? "", Brand = leg?.Airline,
        };
        f.SetPhase("parked", Time);
        f.Lights = Rng.NextDouble() < 0.5 ? 0 : Nav | Logo;
        Flights.Add(f);
        return f;
    }

    // ------------------------------------------------------------------ departures

    /// <summary>Starts a departure: an aircraft at a gate starts boarding and pushes back after <paramref name="boarding"/> seconds.</summary>
    public Flight? StartDeparture(Flight? pick, double boarding, ScheduleLeg? leg = null)
    {
        if (DepRwy is null) SelectRunways(true);
        if (DepRwy is null) return null;
        var f = pick ?? Flights.Where(x => x.Kind == "parked" && x.Phase == "parked" && x.Spot is not null && DepRwy.LengthM >= x.P.MinRunwayM)
            .OrderBy(_ => Rng.Next()).FirstOrDefault();
        if (f is null)
        {
            var id = Source.NewAircraft(Apt.Icao, 2, Rng);
            var perf = Perf.For(id.Type);
            var s = FindGate(perf, id.Airline);
            if (s is null) return null;
            f = SpawnParked(s);
        }
        if (leg is not null) { f.Callsign = leg.Callsign; f.Other = leg.Arr; }
        else
        {
            var (cs, other) = Source.NewFlight(Apt.Icao, f.Airline, true, Rng, f.Type);
            f.Callsign = cs;
            f.Other = other;
        }
        f.Kind = "departure";
        f.Rwy = DepRwy;
        f.Timer = boarding;
        f.SetPhase("boarding", Time);
        return f;
    }

    private PushbackPlanner? _pushPlanner;

    private void StartPushback(Flight f)
    {
        var s = f.Spot!;
        var hold = PickRunwayEntry(f.Rwy!, out var entry);
        _pushPlanner ??= new PushbackPlanner(Apt);
        var plan = _pushPlanner.Plan(s, f.P, hold);
        f.Path = new PathGeom(plan.Points, _ => plan.Radius, 0.5);
        f.S = 0;
        f.Reverse = true;
        f.MaxKt = 3.5;                 // about 1.8 m/s, a real tug's pace
        f.HoldNode = hold;
        f.EntryNode = entry;
        f.StartNode = plan.EndNode >= 0 ? plan.EndNode : s.Access;
        f.Stops.Clear();
        f.Spot = null; // the gate is free once the aircraft leaves it
        f.Timer = 1.5; // brakes off, then the push starts
        f.SetPhase("pushback", Time);
        Log($"{f.Callsign}: pushing back from {s.Label} ({plan.Describe()})");
    }

    /// <summary>A new taxi route from where the aircraft is, avoiding the area around another aircraft.</summary>
    private bool Reroute(Flight f, Flight other) => Reroute(f, other.Pos, other.Callsign);

    private bool Reroute(Flight f, V2 otherPos, string otherName)
    {
        int dest;
        if (f.Phase == "taxi") dest = f.HoldNode;
        else if (f.Phase == "taxi in" && f.Spot is not null) dest = f.Spot.Access;
        else return false;
        if (dest < 0) return false;
        var avoid = new HashSet<int>();
        for (var i = 0; i < Apt.TaxiPointCount; i++) if ((Apt.Nodes[i] - otherPos).Length < 70) avoid.Add(i);
        var from = -1; var best = double.MaxValue;
        for (var i = 0; i < Apt.TaxiPointCount; i++)
        {
            if (avoid.Contains(i) || Apt.Adj[i].Count == 0 || Apt.NodeRunway[i] >= 0) continue;
            var d = (Apt.Nodes[i] - f.Pos).Length;
            if (d < best) { best = d; from = i; }
        }
        if (from < 0 || best > 150) return false;
        var route = Apt.Route(from, dest, ActiveRunways(), avoid);
        if (route is null) return false;
        var pts = new List<V2> { f.Pos };
        var fwd = V2.FromBearing(f.Heading);
        var toFirst = Apt.Nodes[route[0]] - f.Pos;
        if (V2.Dot(toFirst, fwd) < 0)
            pts.Add(f.Pos + fwd * 25 + fwd.Perp * 30); // turn around in a teardrop rather than on the spot
        else if (toFirst.Length > 1 && V2.Dot(toFirst.Unit, fwd) < 0.85)
            pts.Add(f.Pos + fwd * 20);                  // roll ahead a little and turn in an arc, not on the spot
        var offset = pts.Count;
        if (f.Phase == "taxi")
        {
            var holdIndex = HoldShortIndex(route, route.Count - 1, f.Rwy!);
            f.LineupNodes = route.Skip(holdIndex + 1).ToList();
            route = route.Take(holdIndex + 1).ToList();
        }
        pts.AddRange(route.Select(n => Apt.Nodes[n]));
        if (f.Phase == "taxi in" && f.Spot is { } gate)
        {
            pts.Add(gate.P - V2.FromBearing(gate.Heading) * Math.Min(40, Math.Max(15, (gate.P - Apt.Nodes[gate.Access]).Length * 0.5)));
            pts.Add(gate.P);
        }
        SetGroundPath(f, pts, route, offset);
        Log($"{f.Callsign}: taking another way round {otherName}");
        return true;
    }

    /// <summary>No pushback while another aircraft is taxiing or pushing right behind the gate.</summary>
    private bool PushbackClear(Flight f)
    {
        var s = f.Spot;
        if (s is null) return true;
        var behind = s.P - V2.FromBearing(s.Heading) * Math.Max(30, f.P.LengthM);
        return !Flights.Any(o => o != f && o.OnGround && !o.Done && o.Phase is "pushback" or "taxi" or "taxi in" && (o.Pos - behind).Length < 90);
    }

    /// <summary>For the log: plans a pushback at every stand of this airport and summarises.</summary>
    public string PushbackSurvey()
    {
        if (DepRwy is null) SelectRunways(true);
        var hold = DepRwy is null ? -1 : PickRunwayEntry(DepRwy, out _);
        _pushPlanner ??= new PushbackPlanner(Apt);
        return _pushPlanner.Survey(Perf.Narrowbody, hold);
    }

    /// <summary>Where departures enter the runway: the connection nearest the threshold. Returns the hold node.</summary>
    private int PickRunwayEntry(RunwayEnd rwy, out int entry)
    {
        entry = -1;
        var links = Apt.RunwayLinks(rwy.Runway).Select(l => (l.on, l.off, along: rwy.Along(Apt.Nodes[l.on]))).ToList();
        if (links.Count == 0) return -1;
        var near = links.Where(l => l.along > -80 && l.along < rwy.LengthM * 0.3).OrderBy(l => l.along).ThenByDescending(l => Apt.NodeHoldShort[l.off] ? 1 : 0).ToList();
        var pick = near.Count > 0 ? near[0] : links.OrderBy(l => Math.Abs(l.along)).First();
        entry = pick.on;
        return pick.off;
    }

    private bool StartTaxiOut(Flight f)
    {
        var start = f.Pos;
        var from = f.StartNode >= 0 ? f.StartNode : Apt.NearestNode(start);
        if (f.HoldNode < 0 || from < 0) { Log($"{f.Callsign}: no taxi route to runway {f.Rwy?.Ident}"); return false; }
        // Carry on the way the nose points: don't plan back through the points just behind the aircraft.
        var nose = V2.FromBearing(f.Heading);
        var behind = new HashSet<int>();
        for (var i = 0; i < Apt.TaxiPointCount; i++)
        {
            var v = Apt.Nodes[i] - start;
            if (v.Length < 40 && V2.Dot(v, nose) < -2 && i != from) behind.Add(i);
        }
        // …and from the first taxi point, don't double straight back along the line the aircraft is on.
        foreach (var e in Apt.Adj[from])
            if (e.B != f.HoldNode && V2.Dot((Apt.Nodes[e.B] - Apt.Nodes[from]).Unit, nose) < -0.5) behind.Add(e.B);
        var route = Apt.Route(from, f.HoldNode, ActiveRunways(), behind) ?? Apt.Route(from, f.HoldNode, ActiveRunways());
        if (route is null) { Log($"{f.Callsign}: no taxi route to runway {f.Rwy!.Ident}"); return false; }
        // Stop at the painted hold-short line, not at the edge of the runway.
        var holdIndex = HoldShortIndex(route, route.Count - 1, f.Rwy!);
        f.LineupNodes = route.Skip(holdIndex + 1).ToList();
        route = route.Take(holdIndex + 1).ToList();
        // Don't drive backwards to a node that's behind the aircraft after pushback.
        var fwd = V2.FromBearing(f.Heading);
        while (route.Count > 1 && V2.Dot(Apt.Nodes[route[0]] - start, fwd) < 5) route.RemoveAt(0);
        var pts = new List<V2> { start };
        pts.AddRange(route.Select(n => Apt.Nodes[n]));
        SetGroundPath(f, pts, route, 1);
        f.Reverse = false;
        f.MaxKt = f.P.Heavy ? 16 : 20;
        f.SetPhase("taxi", Time);
        return true;
    }

    /// <summary>
    /// Index in the route where an aircraft should hold short of the runway reached after route[last]:
    /// the nearest marked hold-short point within 200 m, otherwise the first point at least 75 m from the centreline.
    /// </summary>
    private int HoldShortIndex(List<int> route, int last, RunwayEnd rwy)
    {
        double dist = 0;
        for (var k = last; k >= 0; k--)
        {
            if (k < last) dist += (Apt.Nodes[route[k]] - Apt.Nodes[route[k + 1]]).Length;
            if (dist > 200) break;
            if (Apt.NodeHoldShort[route[k]] && Apt.NodeRunway[route[k]] < 0) return k;
        }
        var minOff = rwy.WidthM / 2 + 55;
        for (var k = last; k >= 0; k--)
            if (Math.Abs(OffsetFrom(rwy.Runway, Apt.Nodes[route[k]])) >= minOff) return k;
        return last;
    }

    private double OffsetFrom(int runway, V2 p)
    {
        var e = Apt.Ends.First(x => x.Runway == runway);
        return e.Offset(p);
    }

    private HashSet<int> ActiveRunways()
    {
        var set = new HashSet<int>();
        if (ArrRwy is not null) set.Add(ArrRwy.Runway);
        if (DepRwy is not null) set.Add(DepRwy.Runway);
        return set;
    }

    /// <summary>Builds a taxi path and its runway-crossing stops. route[i] is the node at pts[i + offset].</summary>
    private void SetGroundPath(Flight f, List<V2> pts, List<int>? route, int offset, double radius = 40)
    {
        f.Path = new PathGeom(pts, _ => radius, 1.0);
        f.S = 0;
        f.Stops.Clear();
        if (route is null) return;
        for (var i = 0; i + 1 < route.Count; i++)
        {
            var a = route[i]; var b = route[i + 1];
            if (Apt.NodeRunway[a] >= 0 || Apt.NodeRunway[b] < 0) continue;
            var r = Apt.NodeRunway[b];
            var j = i + 1;
            while (j < route.Count && Apt.NodeRunway[route[j]] == r) j++;
            if (j >= route.Count) continue; // the route ends on that runway (a departure entering it): handled by line-up
            var hi = HoldShortIndex(route, i, Apt.Ends.First(x => x.Runway == r));
            var stopS = f.Path.PointS[hi + offset];
            var exitS = f.Path.PointS[j + offset] + 10;
            f.Stops.Add(new Stop { S = Math.Max(0, stopS - 2), Runway = r, ExitS = exitS });
        }
    }

    /// <summary>
    /// Seconds before this departure may start its roll, behind the previous departure from the same runway:
    /// wake turbulence time by CWT category (7110.65 3-9-6), and radar separation once airborne — 1 nm when the
    /// two courses diverge by 15° or more right after takeoff (5-8-3), else 3 nm.
    /// </summary>
    private double WakeRemaining(Flight f)
    {
        var l = _lastDep;
        if (l is null || l.Done || l.Rwy is null || f.Rwy is null || l.Rwy.Runway != f.Rwy.Runway) return 0;
        if (l.OnGround) return 30;                                      // still rolling
        var since = Time - l.LiftoffTime;
        var wake = Cwt.DepartureSeconds(l.Wake, f.Wake) - since;
        if (double.IsNaN(f.DepCourse)) PlanDeparture(f);
        var diverging = !double.IsNaN(l.DepCourse) && !double.IsNaN(f.DepCourse) && Math.Abs(Geo2.Diff(l.DepCourse, f.DepCourse)) >= 15;
        // The follower gets airborne about 35 s after starting its roll, by when the leader has gained more distance.
        var needNm = diverging ? 0.4 : 2.0;
        var ahead = (l.Pos - f.Pos).Length / Geo2.MPerNm;
        var radar = ahead >= needNm ? 0 : (needNm - ahead) / Math.Max(2, l.Gs / 3600.0);
        // Same runway, both airborne-capable jets: the leader must be airborne and 6,000 ft down the runway (3-9-6).
        var down = f.Rwy.Along(l.Pos) - f.Rwy.Along(f.Pos);
        var runway = down >= 1829 ? 0 : 5;
        return Math.Max(0, Math.Max(wake, Math.Max(radar, runway)));
    }

    private Flight? _lastDep;

    private bool LineUpAllowed(Flight f)
    {
        var r = f.Rwy!.Runway;
        if (RunwayBusy(r, f)) return false;
        // Only line up if the takeoff can follow without delaying the next arrival.
        var eta = ArrivalEta(r);
        // Line-up ~25 s, short wait, takeoff roll ~55 s, and the departure must be off the runway well before the arrival.
        // (US practice: the departure must be airborne before the arrival crosses the threshold.)
        var needed = Math.Max(WakeRemaining(f), 8) + 25 + 50 + 12;
        if (eta < needed) return false;
        Log($"{f.Callsign}: lining up runway {f.Rwy.Ident} (next arrival {(double.IsInfinity(eta) ? "none" : eta.ToString("0") + " s")})");
        return true;
    }

    private bool TakeoffAllowed(Flight f)
    {
        var r = f.Rwy!.Runway;
        if (RunwayBusy(r, f)) return false;
        if (ArrivalEta(r) < 45) return false;
        if (!CrossingRunwayClearForTakeoff(f)) return false;
        return WakeRemaining(f) <= 0;
    }

    /// <summary>Seconds for a departure starting its roll here to pass a point this far down the runway.</summary>
    private static double RollTime(Flight f, double metres)
    {
        var a = Math.Max(0.5, f.P.RollAccel * 0.5144);
        var liftoff = (f.P.Vr + 7) * 0.5144;
        var tLift = liftoff / a; var dLift = 0.5 * a * tLift * tLift;
        return metres <= dLift ? Math.Sqrt(2 * Math.Max(0, metres) / a) : tLift + (metres - dLift) / (liftoff * 1.1);
    }

    /// <summary>
    /// A runway that crosses the departure runway (7110.65 3-9-8): don't start the roll if an arrival there would
    /// reach the intersection before this departure is through it, or if a landed aircraft hasn't passed it yet.
    /// </summary>
    internal bool CrossingRunwayClearForTakeoff(Flight f)
    {
        var d = f.Rwy!;
        foreach (var end in Apt.Ends)
        {
            if (end.Runway == d.Runway) continue;
            var x = Crossing(d, end);
            if (x is null) continue;
            var tPass = RollTime(f, x.Value.alongA - d.Along(f.Pos)) + 15;
            foreach (var o in Flights)
            {
                if (o == f || o.Done || o.Rwy is null || o.Rwy.Runway != end.Runway) continue;
                if (o.Kind == "arrival" && !o.OnGround && o.Phase != "go-around")
                {
                    // 7110.65 3-10-4: the arrival may not cross its landing threshold until this departure has passed
                    // the intersection, so the departure must be through it before the arrival reaches the threshold.
                    var toThr = (o.ThrS - o.S) / Math.Max(30, o.Gs * Kt);
                    if (toThr < tPass) return false;
                }
                // Landing or taking off on the crossing runway and not yet through the intersection.
                if (o.OnGround && o.OnRunway == end.Runway && o.Phase is "landing roll" or "takeoff roll" && o.Rwy.Along(o.Pos) < (o.Rwy == end ? x.Value.alongB : end.LengthM - x.Value.alongB) + 30)
                    return false;
            }
        }
        return true;
    }

    private void StartLineUp(Flight f)
    {
        var rwy = f.Rwy!;
        var hold = Apt.Nodes[f.HoldNode];
        var e = f.EntryNode >= 0 ? Apt.Nodes[f.EntryNode] : rwy.Threshold;
        var along = Math.Max(rwy.Along(e), 20);
        var onCl = rwy.Threshold + rwy.Dir * along;
        var end = rwy.Threshold + rwy.Dir * (along + 90);
        var pts = new List<V2> { f.Pos };
        pts.AddRange(f.LineupNodes.Where(n => Apt.NodeRunway[n] < 0).Select(n => Apt.Nodes[n]));
        pts.Add(hold);
        pts.Add(onCl);
        pts.Add(end);
        f.Path = new PathGeom(pts.Distinct().ToList(), _ => 30, 1.0);
        f.S = 0;
        f.Stops.Clear();
        f.MaxKt = 10;
        f.Reserve = rwy.Runway;
        if (f.DepPlan is null) PlanDeparture(f);
        f.SetPhase("lining up", Time);
    }

    /// <summary>The departure route: straight ahead past the runway end, then the SID (or a turn out). Also its course.</summary>
    private void PlanDeparture(Flight f)
    {
        var rwy = f.Rwy!;
        var pts = new List<AirPoint> { new(rwy.FarEnd, ""), new(rwy.FarEnd + rwy.Dir * Geo2.MPerNm, "") };
        f.DepRoute = "";
        var sid = PickSid(rwy, out var sidLegs);
        if (sid is not null && sidLegs.Count > 0)
        {
            var sp = Apt.LegsToPoints(sidLegs, rwy.FarEnd, rwy.ElevationFt + 400, out _);
            // Ignore points behind the runway end (some SIDs begin with the runway fix).
            sp = sp.Where(p => rwy.Along(p.P) > rwy.LengthM + Geo2.MPerNm + 300 || (p.P - rwy.FarEnd).Length > 4000).ToList();
            if (sp.Count > 0) { pts.AddRange(sp); f.DepRoute = sid.Name; }
        }
        if (pts.Count <= 2)
        {
            var straight = rwy.FarEnd + rwy.Dir * (5 * Geo2.MPerNm);
            pts.Add(new AirPoint(straight, ""));
            var turn = Geo2.Norm(rwy.Heading + (Rng.NextDouble() < 0.5 ? -1 : 1) * (30 + Rng.NextDouble() * 60));
            pts.Add(new AirPoint(straight + V2.FromBearing(turn, 25 * Geo2.MPerNm), ""));
        }
        f.DepPlan = pts;
        // The course about 4 nm after the runway end (for diverging-departure spacing).
        var want = 4 * Geo2.MPerNm; var run = 0.0; var course = rwy.Heading;
        for (var i = 0; i + 1 < pts.Count; i++)
        {
            var seg = pts[i + 1].P - pts[i].P;
            if (seg.Length < 1) continue;
            course = seg.Bearing;
            run += seg.Length;
            if (run >= want) break;
        }
        f.DepCourse = course;
    }

    private void StartTakeoff(Flight f)
    {
        var rwy = f.Rwy!;
        if (f.DepPlan is null) PlanDeparture(f);
        var pts = new List<AirPoint> { new(f.Pos, "") };
        pts.AddRange(f.DepPlan!);
        f.Route = f.DepRoute;
        // Keep going 30 nm past the last point so the path never runs out before the aircraft is removed.
        var last = pts[^1].P; var prev = pts[^2].P;
        pts.Add(new AirPoint(last + (last - prev).Unit * (30 * Geo2.MPerNm), ""));
        f.Path = AirPath(pts.Select(p => p.P).ToList(), i => i <= 1 ? 1 : 190, f.AltFt + 2000);
        SetSpeedLimits(f, pts);
        f.S = 0;
        f.Stops.Clear();
        f.Ias = 0;
        f.Gs = 0;
        f.SetPhase("takeoff roll", Time);
    }

    /// <summary>The published speed limits of the procedure points on the path (index i of pts is point i of the path).</summary>
    private static void SetSpeedLimits(Flight f, List<AirPoint> pts)
    {
        f.SpeedLimits.Clear();
        if (f.Path is null) return;
        for (var i = 0; i < pts.Count && i < f.Path.PointS.Length; i++)
            if (pts[i].SpeedKt >= 100 && pts[i].SpeedKt < 400) f.SpeedLimits.Add((f.Path.PointS[i], pts[i].SpeedKt));
    }

    /// <summary>
    /// The highest speed the procedure allows here: a limit ahead must be met at its fix (slowing about 10 kt per mile
    /// before it), and the last limit passed holds until the next one (or for 10 nm after the last).
    /// </summary>
    private static double ProcedureLimit(Flight f, bool before)
    {
        var lim = double.PositiveInfinity;
        (double S, double Kt)? passed = null;
        foreach (var c in f.SpeedLimits)
        {
            var ahead = (c.S - f.S) / Geo2.MPerNm;
            if (ahead >= 0) { lim = Math.Min(lim, before ? c.Kt : c.Kt + ahead * 10); }
            else passed = c;
        }
        if (!before && passed is { } p && (f.S - p.S) / Geo2.MPerNm < 10 && !f.SpeedLimits.Any(c => c.S > f.S)) lim = Math.Min(lim, p.Kt);
        else if (!before && passed is { } p2 && f.SpeedLimits.Any(c => c.S > f.S)) lim = Math.Min(lim, Math.Max(p2.Kt, f.SpeedLimits.Where(c => c.S > f.S).Min(c => c.Kt)));
        return lim;
    }

    private Procedure? PickSid(RunwayEnd rwy, out List<ProcedureLeg> legs)
    {
        legs = new List<ProcedureLeg>();
        var cands = Apt.Data.Departures.Where(d => d.RunwayTransitions.Any(t => t.Name == rwy.Ident) || (d.RunwayTransitions.Count == 0 && d.Legs.Count > 0)).ToList();
        if (cands.Count == 0) return null;
        var sid = cands[Rng.Next(cands.Count)];
        var rt = sid.RunwayTransitions.FirstOrDefault(t => t.Name == rwy.Ident);
        if (rt is not null) legs.AddRange(rt.Legs);
        legs.AddRange(sid.Legs);
        if (sid.EnrouteTransitions.Count > 0) legs.AddRange(sid.EnrouteTransitions[Rng.Next(sid.EnrouteTransitions.Count)].Legs);
        return sid;
    }

    /// <summary>An air route with corners sized for the speed flown there (25° bank).</summary>
    private static PathGeom AirPath(List<V2> pts, Func<int, double> plannedIas, double altFt)
    {
        return new PathGeom(pts, i =>
        {
            var ias = plannedIas(i);
            if (ias <= 1) return 30;
            var v = Wind.Tas(ias, altFt) * Kt;
            return Math.Max(600, v * v / (G * Math.Tan(25 * Math.PI / 180)));
        }, G * Math.Tan(25 * Math.PI / 180));
    }

    // ------------------------------------------------------------------ arrivals

    /// <summary>Spawns an arrival <paramref name="distNm"/> track miles from touchdown.</summary>
    public Flight? SpawnArrival(double distNm, bool test, ScheduleLeg? leg = null)
    {
        if (ArrRwy is null) SelectRunways(true);
        var rwy = ArrRwy;
        if (rwy is null) return null;
        AircraftIdentity id;
        Perf perf;
        var tries = 0;
        string cs, other;
        if (leg is not null)
        {
            id = new AircraftIdentity(leg.Operator, leg.Type, leg.Callsign, leg.Dep);
            perf = Perf.For(leg.Type);
            cs = leg.Callsign;
            other = leg.Dep;
        }
        else
        {
            do
            {
                id = Source.NewAircraft(Apt.Icao, 2, Rng);
                perf = Perf.For(id.Type);
            } while (rwy.LengthM < perf.MinRunwayM && ++tries < 6);
            (cs, other) = Source.NewFlight(Apt.Icao, id.Airline, false, Rng, id.Type);
        }
        var f = new Flight { Id = NewId(), Callsign = cs, Airline = id.Airline, Type = id.Type, Other = other, Kind = "arrival", P = perf, Rwy = rwy, Test = test,
            Reg = leg?.Reg ?? "", Title = leg?.Title ?? "", Brand = leg?.Airline };
        f.OnGround = false;
        f.Gear = false;
        BuildArrivalPath(f, null);
        f.S = Math.Max(0, f.ThrS - distNm * Geo2.MPerNm);
        var (p, dir, _) = f.Path!.At(f.S);
        f.Pos = p;
        f.Heading = dir;
        var dth = (f.ThrS - f.S) / Geo2.MPerNm;
        f.AltFt = Math.Min(rwy.ElevationFt + 11000, Profile(rwy, dth));
        f.Ias = ArrivalSpeed(f, dth);
        f.Gs = f.Ias;
        f.Gear = dth < 6.5;
        f.SetPhase(dth < 12 ? "approach" : "arriving", Time);
        Flights.Add(f);
        return f;
    }

    private static double Profile(RunwayEnd rwy, double dthNm) => rwy.ElevationFt + 50 + Math.Max(0, dthNm) * 318.4;

    /// <summary>The arrival route: STAR, then the approach (or vectors to final), then the runway.</summary>
    private void BuildArrivalPath(Flight f, V2? from)
    {
        var rwy = f.Rwy!;
        var route = new List<V2>();
        var speeds = new List<double>();
        var names = new List<string>();
        if (from is { } start)
        {
            route.Add(start);
            route.AddRange(Vectors(start, rwy));
        }
        else
        {
            var star = PickStar(rwy, out var starLegs);
            var starPts = star is null ? new List<AirPoint>() : Apt.LegsToPoints(starLegs, V2.FromBearing(Rng.Next(360), 40 * Geo2.MPerNm), 11000, out _);
            // Many STARs run on to a fix at or near the airport and end in "expect radar vectors" (LaGuardia's VADDR1
            // ends right over the field). Real controllers turn the traffic off them well before that, onto a downwind
            // or base, so the STAR is only flown until it's about 8 nm from the airport.
            var near = starPts.FindIndex(p => p.P.Length < 8 * Geo2.MPerNm);
            if (near >= 0) starPts.RemoveRange(near, starPts.Count - near);
            if (star is not null && starPts.Count > 0) names.Add(star.Name);
            var (appName, appPts) = ApproachPoints(rwy, starPts);
            if (appName.Length > 0) names.Add(appName);
            if (starPts.Count == 0)
            {
                var bearing = Rng.Next(360);
                if (appPts.Count > 0)
                {
                    // Come from the side the approach starts on.
                    bearing = (int)appPts[0].P.Bearing;
                }
                var s0 = V2.FromBearing(bearing, 45 * Geo2.MPerNm);
                starPts.Add(new AirPoint(s0, ""));
            }
            route.AddRange(starPts.Select(p => p.P));
            speeds.AddRange(starPts.Select(p => p.SpeedKt));
            if (appPts.Count > 0) { route.AddRange(appPts.Select(p => p.P)); speeds.AddRange(appPts.Select(p => p.SpeedKt)); }
            else route.AddRange(Vectors(route[^1], rwy));
        }
        var thrIndex = route.Count;
        route.Add(rwy.Threshold);
        route.Add(rwy.FarEnd);
        // Remove points that would make the aircraft double back near the runway.
        f.Route = string.Join(" → ", names);
        var temp = AirPath(route, _ => 200, 5000);
        var thrS = temp.PointS[thrIndex];
        f.Path = AirPath(route, i =>
        {
            if (i >= thrIndex) return 1;
            var d = (thrS - temp.PointS[i]) / Geo2.MPerNm;
            return d > 20 ? 250 : d > 12 ? 210 : d > 7 ? 180 : 160;
        }, 6000);
        f.ThrS = f.Path.PointS[thrIndex];
        f.LandingChecked = false;
        f.StableChecked = false;
        f.SpeedLimits.Clear();
        for (var i = 0; i < speeds.Count && i < f.Path.PointS.Length; i++)
            if (speeds[i] >= 100 && speeds[i] < 400) f.SpeedLimits.Add((f.Path.PointS[i], speeds[i]));
    }

    private Procedure? PickStar(RunwayEnd rwy, out List<ProcedureLeg> legs)
    {
        legs = new List<ProcedureLeg>();
        var cands = Apt.Data.Arrivals.Where(a => a.RunwayTransitions.Count == 0 || a.RunwayTransitions.Any(t => t.Name == rwy.Ident)).ToList();
        if (cands.Count == 0) return null;
        var star = cands[Rng.Next(cands.Count)];
        if (star.EnrouteTransitions.Count > 0) legs.AddRange(star.EnrouteTransitions[Rng.Next(star.EnrouteTransitions.Count)].Legs);
        legs.AddRange(star.Legs);
        var rt = star.RunwayTransitions.FirstOrDefault(t => t.Name == rwy.Ident);
        if (rt is not null) legs.AddRange(rt.Legs);
        return star;
    }

    /// <summary>The approach to the runway, joined to the STAR where they share a fix. Empty if it doesn't line up.</summary>
    private (string name, List<AirPoint> pts) ApproachPoints(RunwayEnd rwy, List<AirPoint> starPts)
    {
        int Pref(int t) => t switch { 4 => 0, 10 => 1, 1 => 2, 5 => 3, _ => 4 };
        var apps = Apt.Data.Approaches.Where(a => a.Runway == rwy.Ident && a.FinalLegs.Count > 0).OrderBy(a => Pref(a.Type)).ToList();
        foreach (var app in apps)
        {
            var legs = new List<ProcedureLeg>();
            Transition? join = null;
            var joinIndex = -1;
            foreach (var t in app.Transitions)
            {
                var first = t.Legs.FirstOrDefault(l => l.Fix.Length > 0)?.Fix;
                if (first is null) continue;
                var k = starPts.FindIndex(p => p.Fix == first);
                if (k >= 0) { join = t; joinIndex = k; break; }
            }
            var keep = join is not null ? joinIndex : starPts.Count;
            var typeLabel = app.Type switch { 4 => "ILS", 10 => "RNAV", 1 => "GPS", 5 => "LOC", 2 => "VOR", 7 => "LDA", _ => "approach" };
            // The STAR doesn't lead into this approach: real controllers vector the arrival from the end of the
            // STAR onto final (downwind, base, intercept) rather than sending it straight across to the approach's
            // first fix, which could mean flying over the airport.
            if (join is null && starPts.Count > 0)
            {
                var firstFix = app.FinalLegs.FirstOrDefault(l => l.Lat != 0 || l.Lon != 0);
                var gap = firstFix is null ? double.MaxValue : (Apt.ToXY(firstFix.Lat, firstFix.Lon) - starPts[^1].P).Length;
                if (gap > 5 * Geo2.MPerNm) return ($"vectors to the {typeLabel} {rwy.Ident}{(app.Suffix.Length > 0 ? " " + app.Suffix : "")}", new List<AirPoint>());
            }
            if (join is not null) legs.AddRange(join.Legs);
            legs.AddRange(app.FinalLegs);
            var start = keep > 0 ? starPts[keep - 1].P : rwy.Threshold - rwy.Dir * (15 * Geo2.MPerNm);
            var pts = Apt.LegsToPoints(legs, start, 4000, out _);
            // Keep only the part before the runway, and stop at the runway fix.
            var cut = pts.FindIndex(p => p.Fix.StartsWith("RW") || rwy.Along(p.P) > -200);
            if (cut >= 0) pts.RemoveRange(cut, pts.Count - cut);
            if (pts.Count == 0) continue;
            // The last point must be on the extended centreline (within 25°, at least 2 nm out).
            var lastP = pts[^1].P;
            var along = rwy.Along(lastP);
            if (along > -2 * Geo2.MPerNm || Math.Abs(rwy.Offset(lastP)) > Math.Abs(along) * Math.Tan(25 * Math.PI / 180)) continue;
            if (keep < starPts.Count) starPts.RemoveRange(keep, starPts.Count - keep);
            var typeName = app.Type switch { 4 => "ILS", 10 => "RNAV", 1 => "GPS", 5 => "LOC", 2 => "VOR", 7 => "LDA", _ => "approach" };
            return ($"{typeName} {rwy.Ident}{(app.Suffix.Length > 0 ? " " + app.Suffix : "")}", pts);
        }
        return ("", new List<AirPoint>());
    }

    /// <summary>Radar vectors to final: a downwind and base if needed, intercepting about 8 miles out at 30-40°.</summary>
    private static List<V2> Vectors(V2 from, RunwayEnd rwy)
    {
        var nm = Geo2.MPerNm;
        var dir = rwy.Dir;
        var left = new V2(-dir.Y, dir.X);
        var along = rwy.Along(from);
        var off = rwy.Offset(from);
        var side = off >= 0 ? 1.0 : -1.0;
        var thr = rwy.Threshold;
        var pts = new List<V2>();
        var straightIn = along < -10 * nm && Math.Abs(off) < Math.Abs(along) * Math.Tan(30 * Math.PI / 180);
        if (!straightIn)
        {
            if (along > -10 * nm)
            {
                // Wrong side of the final approach course: fly a downwind first. Coming from beyond the
                // airport, go around its side rather than over it.
                if (along > 3 * nm && Math.Abs(off) < 5 * nm)
                    pts.Add(thr + left * (side * 6 * nm) + dir * Math.Min(along, 12 * nm));
                pts.Add(thr + left * (side * 5 * nm) + dir * Math.Min(along, 3 * nm));
                pts.Add(thr + left * (side * 5 * nm) - dir * (11 * nm));
            }
            pts.Add(thr + left * (side * 3 * nm) - dir * (12.5 * nm));
        }
        pts.Add(thr - dir * (8 * nm));
        pts.Add(thr - dir * (4 * nm));
        return pts;
    }

    private double ArrivalSpeed(Flight f, double d)
    {
        // Typical controller speeds (250 → 210 → 180 → 170 to a 5-mile final), then the aircraft slows to its
        // approach speed on its own: no speed is assigned inside 5 miles (7110.65 5-7-2).
        double t = d > 20 ? 250 : d > 12 ? 210 : d > 7 ? 180 : d > 5 ? 170 : f.P.Vapp;
        if (f.P.Family == "turboprop" && d <= 20) t = Math.Min(t, d > 5 ? 170 : f.P.Vapp);
        t = Math.Max(t, f.P.Vapp);
        if (f.WentAround) t = Math.Min(t, 200);
        // Published speed limits on the STAR and approach, and 250 knots below 10,000 ft (14 CFR 91.117(a)).
        t = Math.Min(t, ProcedureLimit(f, false));
        if (f.AltFt < 10000) t = Math.Min(t, 250);
        return Math.Max(t, f.P.Vapp);
    }

    internal void GoAround(Flight f, string why)
    {
        Log($"{f.Callsign}: going around ({why})");
        var rwy = f.Rwy!;
        f.SetPhase("go-around", Time);
        f.WentAround = true;
        var p = f.Pos;
        var climbOut = p + rwy.Dir * (4 * Geo2.MPerNm);
        BuildArrivalPath(f, climbOut);
        // Prepend the current position so the path starts where the aircraft is.
        var pts = new List<V2> { p };
        pts.AddRange(f.Path!.Points);
        var thrIndex = pts.Count - 2;
        f.Path = AirPath(pts, i => i >= thrIndex ? 1 : 180, 3000);
        f.ThrS = f.Path.PointS[thrIndex];
        f.S = 0;
        f.LandingChecked = false;
        f.StableChecked = false;
        f.SpeedLimits.Clear(); // radar vectors back around: no procedure limits
    }

    /// <summary>A full circle from the current position, then the rest of the route.</summary>
    private void Orbit(Flight f, string why)
    {
        Log($"{f.Callsign}: making a 360 for spacing ({why})");
        f.LastOrbit = Time;
        var path = f.Path!;
        var (p, dir, _) = path.At(f.S);
        var v = Wind.Tas(f.Ias, f.AltFt) * Kt;
        var r = Math.Max(1500, v * v / (G * Math.Tan(25 * Math.PI / 180)));
        // Turn away from the airport side.
        var right = V2.Cross(V2.FromBearing(dir), f.Rwy!.Threshold - p) > 0;
        var perp = V2.FromBearing(dir).Perp * (right ? 1 : -1);
        var c = p + perp * r;
        var phi0 = (p - c).Bearing;
        var pts = new List<V2> { p };
        for (var k = 1; k < 8; k++) pts.Add(c + V2.FromBearing(phi0 + (right ? 45 : -45) * k, r));
        pts.Add(p + V2.FromBearing(dir, 200));
        // Remaining route points (the kept points after the current distance).
        var remaining = new List<V2>();
        var keptS = path.KeptS;
        for (var i = 0; i < path.Points.Count; i++) if (keptS[i] > f.S + 300) remaining.Add(path.Points[i]);
        pts.AddRange(remaining);
        var oldLimits = f.SpeedLimits.Where(l => l.S > f.S + 300).Select(l => (off: l.S - f.S, l.Kt)).ToList();
        var thrIndex = pts.Count - 2;
        f.Path = AirPath(pts, i => i >= thrIndex ? 1 : i <= 8 ? f.Ias : 180, f.AltFt);
        f.ThrS = f.Path.PointS[thrIndex];
        var added = f.Path.PointS[Math.Min(8, f.Path.PointS.Length - 1)];
        f.SpeedLimits = oldLimits.Select(l => (added + l.off, l.Kt)).ToList();
        f.S = 0;
    }

    /// <summary>
    /// A holding pattern (AIM 5-3-8): from where the aircraft is (or the named fix just ahead), standard right turns,
    /// one-minute legs, at holding speed (200 kt up to 6,000 ft, 230 kt above), then back onto the route.
    /// Far from the airport this replaces 360s when the arrival flow is full.
    /// </summary>
    private void Hold(Flight f, string why)
    {
        var path = f.Path!;
        var (p, dir, _) = path.At(f.S);
        Log($"{f.Callsign}: holding for spacing ({why}), {(f.Ias > 0 ? Math.Min(f.Ias, f.AltFt <= 6000 ? 200 : 230) : 200):0} kt, right turns");
        f.LastHold = Time;
        f.LastOrbit = Time;
        var holdKt = Math.Min(f.Ias, f.AltFt <= 6000 ? 200 : 230);
        var v = Wind.Tas(holdKt, f.AltFt) * Kt;
        var r = Math.Max(1500, v * v / (G * Math.Tan(25 * Math.PI / 180)));
        var leg = Math.Max(2 * Geo2.MPerNm, v * 60);
        var c = V2.FromBearing(dir);
        var right = V2.FromBearing(dir + 90);
        var pts = new List<V2> { p };
        // Turn right onto the outbound leg.
        var c1 = p + right * r;
        var a1 = (p - c1).Bearing;
        for (var k = 1; k <= 4; k++) pts.Add(c1 + V2.FromBearing(a1 + 45 * k, r));
        var outEnd = p + right * (2 * r) - c * leg;
        pts.Add(outEnd);
        // Turn right back to the inbound leg, and fly it to the holding fix.
        var c2 = outEnd - right * r;
        var a2 = (outEnd - c2).Bearing;
        for (var k = 1; k <= 4; k++) pts.Add(c2 + V2.FromBearing(a2 + 45 * k, r));
        pts.Add(p);
        pts.Add(p + c * 200);
        var holdCount = pts.Count;
        var remaining = new List<V2>();
        var keptS = path.KeptS;
        for (var i = 0; i < path.Points.Count; i++) if (keptS[i] > f.S + 300) remaining.Add(path.Points[i]);
        var oldLimits = f.SpeedLimits.Where(l => l.S > f.S).Select(l => (off: l.S - f.S, l.Kt)).ToList();
        pts.AddRange(remaining);
        var thrIndex = pts.Count - 2;
        f.Path = AirPath(pts, i => i >= thrIndex ? 1 : i < holdCount ? holdKt : 180, f.AltFt);
        f.ThrS = f.Path.PointS[thrIndex];
        // The procedure's speed limits move with the route (they come after the hold now).
        var added = f.Path.PointS[holdCount - 1];
        f.SpeedLimits = oldLimits.Select(l => (added + l.off, l.Kt)).ToList();
        f.SpeedLimits.Add((f.Path.PointS[1], holdKt));
        f.S = 0;
    }

    private bool DeparturesWaiting => Flights.Any(o => o.Phase == "holding short" && Time - o.PhaseTime > 40);

    private bool RunwayBusy(int runway, Flight self) =>
        Flights.Any(o => o != self && !o.Done && (o.Reserve == runway || (o.OnRunway == runway && !(o.Kind == "departure" && !o.OnGround))));

    /// <summary>Seconds until the next arrival reaches this runway (infinity if none).</summary>
    private double ArrivalEta(int runway)
    {
        var best = double.PositiveInfinity;
        foreach (var o in Flights)
        {
            if (o.Kind != "arrival" || o.OnGround || o.Rwy is null || o.Rwy.Runway != runway || o.Phase == "go-around") continue;
            var d = o.ThrS - o.S;
            if (d < -300) continue;
            best = Math.Min(best, Math.Max(0, d) / Math.Max(30, o.Gs * Kt));
        }
        return best;
    }

    private void StartRollout(Flight f)
    {
        var rwy = f.Rwy!;
        f.OnGround = true;
        f.Vs = 0;
        f.SetPhase("landing roll", Time);
        var tdAlong = rwy.Along(f.Pos);
        var v0 = f.Gs * Kt;
        var pts = new List<V2> { f.Pos };
        List<int>? route = null;
        var offset = 0;
        AirportModel.ParkingSpot? gate = null;
        if (Apt.HasGround)
        {
            gate = FindGate(f.P, f.Airline, f.Brand, f.Callsign);
            // Exits on the terminal side (where the gate is) first, then the first one the aircraft can make
            // with normal braking: about 2 m/s², slowing to ~30 kt for a high-speed exit or ~18 kt for a 90° turn.
            var side = gate is null ? 0 : Math.Sign(rwy.Offset(gate.P));
            double Needed(double dot) { var vx = dot > 0.5 ? 16.0 : 9.0; return tdAlong + Math.Max(250, (v0 * v0 - vx * vx) / (2 * 2.0)); }
            var links = Apt.RunwayLinks(rwy.Runway)
                .Select(l => (l.on, l.off, along: rwy.Along(Apt.Nodes[l.on]), dir: (Apt.Nodes[l.off] - Apt.Nodes[l.on]).Unit))
                .Where(l => V2.Dot(l.dir, rwy.Dir) > -0.2 && l.along < rwy.LengthM + 30) // never turn back
                .ToList();
            bool WrongSide((int on, int off, double along, V2 dir) l) => side != 0 && Math.Sign(rwy.Offset(Apt.Nodes[l.off])) != side;
            var cands = links.Where(l => l.along >= Needed(V2.Dot(l.dir, rwy.Dir)))
                .OrderBy(l => WrongSide(l) ? 1 : 0).ThenBy(l => l.along).ToList();
            if (cands.Count == 0) cands = links.OrderByDescending(l => l.along).ToList();
            List<int>? taxi = null;
            (int on, int off, double along, V2 dir)? exit = null;
            foreach (var c in cands.Take(5))
            {
                exit ??= c;
                if (gate is null) break;
                taxi = Apt.Route(c.off, gate.Access, ActiveRunways());
                if (taxi is not null) { exit = c; break; }
            }
            if (taxi is null) gate = null;
            if (exit is { } x)
            {
                pts.Add(rwy.Threshold + rwy.Dir * x.along);
                route = new List<int> { x.on };
                route.AddRange(taxi ?? new List<int> { x.off });
                offset = 2;
                pts.AddRange(route.Select(n => Apt.Nodes[n]));
                if (gate is not null)
                {
                    var lead = gate.P - V2.FromBearing(gate.Heading) * Math.Min(40, Math.Max(15, (gate.P - Apt.Nodes[gate.Access]).Length * 0.5));
                    pts.Add(lead);
                    pts.Add(gate.P);
                }
            }
        }
        if (pts.Count == 1) pts.Add(rwy.FarEnd);
        SetGroundPath(f, pts, route, offset, 45);
        f.Spot = gate;
        f.MaxKt = 30;
        f.Reverse = false;
    }

    // ------------------------------------------------------------------ per-flight update

    private void Advance(Flight f, double dt)
    {
        switch (f.Phase)
        {
            case "parked":
                if (f.Kind == "arrival") f.Kind = "parked";
                if (Time - f.PhaseTime > 180 && f.Lights != 0 && f.Age > 600) f.Lights = Rng.NextDouble() < 0.002 ? 0 : f.Lights;
                break;
            case "at gate":
                f.Lights = Nav | Beacon | Logo;
                if (Time - f.PhaseTime > 25) { f.Kind = "parked"; f.SetPhase("parked", Time); f.Lights = Nav | Logo | Cabin; }
                break;
            case "boarding":
                f.Lights = Nav | Logo | Cabin;
                f.Timer -= dt;
                if (f.Timer <= 0 && PushbackClear(f)) StartPushback(f);
                break;
            case "pushback":
                f.Lights = Nav | Beacon | Logo;
                if (f.Timer > 0) { f.Timer -= dt; break; }
                if (MoveGround(f, dt)) { f.SetPhase("engine start", Time); f.Timer = 25 + Rng.NextDouble() * 20; f.Gs = 0; }
                break;
            case "engine start":
                f.Lights = Nav | Beacon | Logo;
                f.Timer -= dt;
                if (f.Timer <= 0 && !StartTaxiOut(f)) f.Done = true;
                break;
            case "taxi":
                f.Lights = Nav | Beacon | Logo | Taxi;
                if (MoveGround(f, dt)) { f.SetPhase("holding short", Time); f.Gs = 0; }
                else Unstick(f, dt);
                break;
            case "holding short":
                f.Lights = Nav | Beacon | Logo | Taxi;
                if (LineUpAllowed(f)) StartLineUp(f);
                break;
            case "lining up":
                f.Lights = Nav | Beacon | Logo | Taxi | Strobes | Landing;
                if (MoveGround(f, dt)) { f.SetPhase("lined up", Time); f.Gs = 0; f.Timer = 3 + Rng.NextDouble() * 4; }
                break;
            case "lined up":
                f.Lights = Nav | Beacon | Logo | Taxi | Strobes | Landing;
                f.Timer -= dt;
                if (f.Timer <= 0 && (f.Test || TakeoffAllowed(f))) StartTakeoff(f);
                break;
            case "takeoff roll":
                f.Lights = Nav | Beacon | Logo | Strobes | Landing;
                TakeoffRoll(f, dt);
                break;
            case "climbing":
                Climb(f, dt);
                break;
            case "arriving" or "approach" or "final" or "flare" or "go-around":
                Arrive(f, dt);
                break;
            case "landing roll":
            case "taxi in":
                f.Lights = f.Phase == "landing roll" && f.Gs > 60 ? Nav | Beacon | Logo | Strobes | Landing : Nav | Beacon | Logo | Taxi;
                var done = MoveGround(f, dt);
                if (!done && f.Phase == "taxi in") Unstick(f, dt);
                if (f.Phase == "landing roll" && f.OnRunway < 0 && f.Gs < 35) { f.SetPhase("taxi in", Time); f.MaxKt = f.P.Heavy ? 16 : 20; }
                if (f.Phase == "landing roll" && f.Gs < 35) f.MaxKt = Math.Min(f.MaxKt, 30);
                if (!Apt.HasGround && f.Phase == "landing roll" && f.Gs < 30) { f.Done = true; break; }
                if (done)
                {
                    if (f.Spot is not null && (f.Pos - f.Spot.P).Length < 5) { f.Heading = f.Spot.Heading; f.SetPhase("at gate", Time); f.Gs = 0; }
                    else if (f.OnRunway < 0) f.Done = true; // nowhere to park: leave once clear of the runway
                    else f.Done = f.Gs < 1;
                }
                break;
        }
        if (f.OnGround)
        {
            f.AltFt = f.OnRunway >= 0 && f.Rwy is not null ? f.Rwy.ElevationFt : Apt.ElevationFt;
            f.Vs = 0;
            f.Bank = 0;
            // Nose comes down: on the ground generally, and 1.5 s after touchdown on the landing roll.
            if (f.Phase != "takeoff roll" && !(f.Phase == "landing roll" && Time - f.PhaseTime < 1.5)) f.Pitch += Math.Clamp(0 - f.Pitch, -2.5 * dt, 2.5 * dt);
        }
        if (f.Reserve >= 0 && f.Phase is "taxi" or "taxi in")
        {
            // Crossing: release the runway once past the far side.
            var st = f.Stops.FirstOrDefault(s => s.Runway == f.Reserve && s.Cleared);
            if (st is null || f.S > st.ExitS) f.Reserve = -1;
        }
    }

    private bool NearStands(V2 p, double margin)
    {
        foreach (var s in Apt.Spots)
            if ((s.P - p).Length < Math.Max(15, s.RadiusM) + margin) return true;
        return false;
    }

    /// <summary>Moves along the ground path. Returns true when the end of the path is reached.</summary>
    private bool MoveGround(Flight f, double dt)
    {
        var path = f.Path!;
        var v = f.Gs * Kt;
        var brake = f.Phase == "landing roll" ? 2.4 : 1.0;
        var vmax = f.MaxKt * Kt;
        if (f.Phase == "landing roll") vmax = Math.Max(vmax, v); // decelerate gently from touchdown speed
        // Taxi speeds (airline practice, AIM 4-3): up to 20 kt on straight taxiways (16 for heavies), slower in
        // turns (sideways acceleration limit), and about 10 kt on the ramp among the gates.
        if (f.Phase is "taxi" or "taxi in" && NearStands(f.Pos, 60)) vmax = Math.Min(vmax, 10 * Kt);
        var target = path.CurveLimit(f.S, f.Phase == "landing roll" ? 2.0 : 1.0, vmax);
        if (f.Phase == "landing roll") target = Math.Min(target, Math.Max(f.MaxKt * Kt, v - 2.0 * dt));
        // Runway crossings: ask for clearance on the way, stop at the hold line if not given.
        Stop? hold = null;
        foreach (var st in f.Stops)
        {
            if (st.Cleared || st.S < f.S - 1) continue;
            if (st.S - f.S < 80 && CrossingAllowed(st.Runway, f)) { st.Cleared = true; f.Reserve = st.Runway; continue; }
            hold = st;
            break;
        }
        if (hold is not null) target = Math.Min(target, Math.Sqrt(2 * 0.8 * Math.Max(0, hold.S - f.S)));
        // Ease into stops (a sudden stop rocks the aircraft on its nose gear).
        target = Math.Min(target, Math.Sqrt(2 * (f.Phase == "landing roll" ? 1.2 : f.Reverse ? 0.3 : 0.7) * Math.Max(0, path.Length - f.S)) + 0.12);
        if (f.Phase is not ("landing roll" or "lining up") && Blocked(f, v)) target = 0;
        // Blocked() may have given the aircraft a new route round the other one: carry on along it next step,
        // not along the old one (which would put it back where the old route started for a moment).
        if (!ReferenceEquals(path, f.Path)) { var (np, nd, _) = f.Path!.At(f.S); f.Pos = np; f.Heading = f.Reverse ? Geo2.Norm(nd + 180) : nd; return false; }
        var accel = f.Reverse ? 0.25 : 0.6;
        v += Math.Clamp(target - v, -brake * dt, accel * dt);
        v = Math.Max(0, v);
        var ns = f.S + v * dt;
        if (hold is not null && ns > hold.S) { ns = hold.S; v = 0; }
        if (ns >= path.Length) { ns = path.Length; v = 0; }
        f.S = ns;
        var (p, dir, _) = path.At(f.S);
        f.Pos = p;
        f.Heading = f.Reverse ? Geo2.Norm(dir + 180) : dir;
        f.Gs = v / Kt;
        f.Ias = f.Gs;
        return f.S >= path.Length - 0.3 && v < 0.3;
    }

    /// <summary>
    /// Never leave an aircraft stuck on the ground (at any airport): blocked by the user's aircraft → find another way;
    /// still stuck after 2 minutes on the way in → another gate; after 5 minutes it's removed rather than sitting forever.
    /// Waiting at a hold line for a runway crossing doesn't count.
    /// </summary>
    private void Unstick(Flight f, double dt)
    {
        var atHold = f.Stops.Any(st => !st.Cleared && Math.Abs(st.S - f.S) < 3);
        if (f.Gs > 0.5 || atHold) { f.StuckFor = 0; return; }
        f.StuckFor += dt;
        if (f.StuckFor > 20 && f.BlockedByUser && UserPos is { } u && Time - f.RerouteTime > 60)
        {
            f.RerouteTime = Time;
            if (Reroute(f, u, "your aircraft")) { f.StuckFor = 0; return; }
        }
        if (f.StuckFor > 120 && f.Phase == "taxi in" && Time - f.RerouteTime > 60)
        {
            f.RerouteTime = Time;
            var old = f.Spot;
            f.Spot = null;
            var gate = FindGate(f.P, f.Airline, f.Brand, f.Callsign);
            if (gate is not null && gate != old)
            {
                f.Spot = gate;
                if (Reroute(f, f.Pos + V2.FromBearing(f.Heading) * 40, "the blockage")) { Log($"{f.Callsign}: going to {gate.Label} instead"); f.StuckFor = 0; return; }
            }
            f.Spot = old;
        }
        if (f.StuckFor > 300)
        {
            Log($"{f.Callsign}: stuck for 5 minutes at ({f.Pos.X:0}, {f.Pos.Y:0}), blocked by {f.BlockedBy?.Callsign ?? (f.BlockedByUser ? "your aircraft" : "nothing")}; removed");
            f.Done = true;
        }
    }

    private bool CrossingAllowed(int runway, Flight f)
    {
        if (RunwayBusy(runway, f)) return false;
        if (ArrivalEta(runway) < 60) return false;
        if (Flights.Any(o => o.Rwy?.Runway == runway && o.Phase is "lined up" or "takeoff roll")) return false;
        return true;
    }

    /// <summary>Another aircraft (or the user) ahead and close: wait. Breaks head-on standoffs after a while.</summary>
    private bool Blocked(Flight f, double v)
    {
        var mdir = V2.FromBearing(f.Reverse ? Geo2.Norm(f.Heading + 180) : f.Heading);
        Flight? by = null;
        var userBlock = false;
        foreach (var o in Flights)
        {
            if (o == f || !o.OnGround || o.Done || o.Phase is "parked" or "boarding" or "takeoff roll") continue;
            if (f.Ignore == o && Time < f.IgnoreUntil) continue;
            var r = o.Pos - f.Pos;
            var dist = r.Length;
            if (dist > 250) continue;
            var ahead = V2.Dot(r, mdir);
            var lat = Math.Abs(V2.Cross(mdir, r));
            var need = f.P.LengthM / 2 + o.P.LengthM / 2 + 25 + v * v / 2.0;
            if (ahead > 0 && ahead < need && lat < (f.P.SpanM + o.P.SpanM) / 2 * 0.6 + 6) { by = o; break; }
            // Paths crossing (intersections): look a few seconds ahead; whoever gets there second waits.
            if (ahead > -10 && dist < 160)
            {
                var odir = V2.FromBearing(o.Reverse ? Geo2.Norm(o.Heading + 180) : o.Heading);
                var ov = o.Gs * Kt;
                var minD = (f.P.SpanM + o.P.SpanM) / 2 * 0.7 + 12;
                for (var t = 2.0; t <= 8; t += 2)
                {
                    var mine = f.Pos + mdir * (Math.Max(v, 3) * t);
                    var theirs = o.Pos + odir * (ov * t);
                    if ((mine - theirs).Length >= minD) continue;
                    // The one closer to the meeting point goes first.
                    var meet = (mine + theirs) * 0.5;
                    if ((o.Pos - meet).Length < (f.Pos - meet).Length) { by = o; }
                    break;
                }
                if (by is not null) break;
            }
        }
        if (by is null && UserOnGround && UserPos is { } u)
        {
            var r = u - f.Pos;
            var ahead = V2.Dot(r, mdir);
            var lat = Math.Abs(V2.Cross(mdir, r));
            if (ahead > 0 && ahead < f.P.LengthM / 2 + 60 + v * v / 2.0 && lat < f.P.SpanM / 2 + 25) userBlock = true;
        }
        f.BlockedByUser = userBlock;
        if (by is null) { f.BlockedBy = null; f.BlockedFor = 0; return userBlock; }
        if (f.BlockedBy == by) f.BlockedFor += 0.25; else { f.BlockedBy = by; f.BlockedFor = 0; }
        // Standoff: both waiting for each other (head-on on a taxilane). The newer flight looks for another way
        // round; if there is none, the older one eventually goes.
        if (by.BlockedBy == f && f.BlockedFor > 8 && string.CompareOrdinal(f.Id, by.Id) > 0 && Time - f.RerouteTime > 60)
        {
            f.RerouteTime = Time;
            if (Reroute(f, by)) { f.BlockedBy = null; f.BlockedFor = 0; return false; }
        }
        if (by.BlockedBy == f && f.BlockedFor > 20 && string.CompareOrdinal(f.Id, by.Id) < 0)
        {
            f.Ignore = by;
            f.IgnoreUntil = Time + 45;
            return userBlock;
        }
        return true;
    }

    private void TakeoffRoll(Flight f, double dt)
    {
        var headwind = Wind.Headwind(f.Heading);
        f.Ias += f.P.RollAccel * dt * (f.Ias < 40 ? 0.7 : 1.0);
        f.Gs = Math.Max(0, f.Ias - headwind);
        if (f.Ias < 25) f.Gs = Math.Min(f.Gs, f.Ias); // early in the roll
        f.S += f.Gs * Kt * dt;
        var (p, dir, _) = f.Path!.At(f.S);
        f.Pos = p;
        f.Heading = dir;
        if (f.Ias >= f.P.Vr) f.Pitch = Math.Min(f.Pitch + 3 * dt, 9);
        if (f.Ias >= f.P.Vr + 7)
        {
            f.OnGround = false;
            f.LiftoffTime = Time;
            _lastDep = f;
            f.Reserve = -1;
            f.Vs = 600;
            f.SetPhase("climbing", Time);
            Log($"{f.Callsign}: airborne from runway {f.Rwy!.Ident}{(f.Route.Length > 0 ? " on the " + f.Route + " departure" : "")}");
        }
    }

    private void Climb(Flight f, double dt)
    {
        var rwy = f.Rwy!;
        var agl = f.AltFt - rwy.ElevationFt;
        f.Lights = Nav | Beacon | Logo | Strobes | (f.AltFt < 10000 ? Landing : 0);
        if (agl > 100 && Time - f.LiftoffTime > 3) f.Gear = false;
        double targetIas = agl < 1500 ? f.P.V2 + 15 : f.AltFt < 10000 ? 250 : 290;
        // 91.117(b): 200 knots at or below 2,500 ft within 4 nm of a Class C or D airport.
        if (agl <= 2500 && f.Pos.Length < 4 * Geo2.MPerNm && Airspace.Has200KnotArea(Apt.Icao)) targetIas = Math.Min(targetIas, 200);
        // The SID's published speed limits.
        targetIas = Math.Max(f.P.V2 + 10, Math.Min(targetIas, ProcedureLimit(f, true)));
        f.Ias += Math.Clamp(targetIas - f.Ias, -1.0 * dt, 1.6 * dt);
        var targetVs = agl < 1500 ? f.P.ClimbFpm : f.P.ClimbFpm * (f.Ias < targetIas - 5 ? 0.7 : 0.9);
        f.Vs += Math.Clamp(targetVs - f.Vs, -500 * dt, 600 * dt);
        f.AltFt += f.Vs / 60 * dt;
        MoveAir(f, dt);
        var fpa = Math.Atan2(f.Vs, Math.Max(60, f.Gs) * 101.27) * 180 / Math.PI;
        var alpha = Math.Clamp(2.5 + (250 - f.Ias) / 30, 2.5, 6.5);
        f.Pitch += Math.Clamp(fpa + alpha - f.Pitch, -2 * dt, 2 * dt);
        if (f.Pos.Length > 28 * Geo2.MPerNm || f.AltFt > 14000) f.Done = true;
    }

    private void Arrive(Flight f, double dt)
    {
        var rwy = f.Rwy!;
        var dth = (f.ThrS - f.S) / Geo2.MPerNm;
        var agl = f.AltFt - rwy.ElevationFt;
        f.Lights = Nav | Beacon | Logo | Strobes | (f.AltFt < 10000 ? Landing : 0);
        if (f.Phase == "go-around")
        {
            f.Ias += Math.Clamp(185 - f.Ias, -1 * dt, 2 * dt);
            f.Vs += Math.Clamp(2000 - f.Vs, -500 * dt, 500 * dt);
            if (Time - f.PhaseTime > 6) f.Gear = false;
            if (agl >= 3000) { f.AltFt = rwy.ElevationFt + 3000; f.Vs = 0; f.SetPhase("arriving", Time); }
            f.AltFt += f.Vs / 60 * dt;
        }
        else
        {
            if (dth < 12 && f.Phase == "arriving") f.SetPhase("approach", Time);
            if (dth < 5 && f.Phase == "approach") f.SetPhase("final", Time);
            var target = ArrivalSpeed(f, dth);
            // Speed restrictions on the procedure just ahead.
            // Spacing behind the arrival ahead on the same runway.
            Flight? leader = null;
            var leaderD = double.MinValue;
            foreach (var o in Flights)
            {
                if (o == f || o.Kind != "arrival" || o.Rwy?.Runway != rwy.Runway || o.Phase == "go-around") continue;
                var od = o.OnGround ? -1 : (o.ThrS - o.S) / Geo2.MPerNm;
                if (od < dth && od > leaderD) { leaderD = od; leader = o; }
            }
            if (leader is not null && !leader.OnGround)
            {
                var gap = dth - leaderD;
                // Wake turbulence (CWT) or radar minimum, a little margin, and room for departures when they're waiting
                // (a full gap on a single runway, a shorter one when they leave from a crossing runway).
                var single = DepRwy is null || DepRwy.Runway == rwy.Runway;
                var need = Cwt.MilesBehind(leader.Wake, f.Wake) + (DeparturesWaiting ? (single ? 3.0 : 1.5) : 1.0);
                // Speed for spacing, never below what a controller may assign (7110.65 5-7-3: jets 210 kt below
                // 10,000 ft, 170 kt within 20 miles; turboprops 200 / 150) and never inside 5 miles (5-7-2).
                if (gap < need && dth > 5)
                {
                    var jet = f.P.Family != "turboprop";
                    var floor = dth > 20 ? (jet ? 210 : 200) : (jet ? 170 : 150);
                    target = Math.Min(target, Math.Max(Math.Max(floor, f.P.Vapp), leader.Ias - 15));
                }
                // Too close to fix with speed: hold (far out) or a 360° turn (closer in), as a controller would.
                if (gap < need - 1.5 && dth > 15 && Time - f.LastHold > 300 && Time - f.LastOrbit > 120) { Hold(f, $"{gap:0.0} nm behind {leader.Callsign}"); return; }
                if (gap < need - 1.5 && dth > 7 && dth <= 15 && Time - f.LastOrbit > 240) { Orbit(f, $"{gap:0.0} nm behind {leader.Callsign}"); return; }
            }
            if (f.Phase == "flare") target = f.P.Vapp - 5;
            f.Ias += Math.Clamp(target - f.Ias, -1.0 * dt, 1.5 * dt);
            if (dth < 6.5 || agl < 2200) f.Gear = true;

            double vsTarget;
            var glideVs = -(f.Gs * 101.27 * Math.Tan(3 * Math.PI / 180));
            if (f.Phase == "flare" || (agl < 30 && dth < 0.4))
            {
                if (f.Phase != "flare") f.SetPhase("flare", Time);
                vsTarget = Math.Max(-650, -140 - agl * 14);
                f.Vs += Math.Clamp(vsTarget - f.Vs, -900 * dt, 900 * dt);
            }
            else
            {
                var err = f.AltFt - Profile(rwy, dth);
                // Below the descent path (after a go-around, or vectored in low): level off until the path comes down
                // to meet the aircraft, as pilots do, rather than carry on descending.
                vsTarget = Math.Clamp(glideVs - Math.Clamp(err * 1.5, -6000, 1500), -3000, 0);
                f.Vs += Math.Clamp(vsTarget - f.Vs, -500 * dt, 500 * dt);
            }
            f.AltFt += f.Vs / 60 * dt;

            // Stabilized approach (AIM 5-4): by 1,000 ft above the runway, gear down, at approach speed and on the glidepath.
            if (!f.StableChecked && agl < 1000 && f.Phase is "final" or "approach")
            {
                f.StableChecked = true;
                var off = f.AltFt - Profile(rwy, dth);
                if (!f.Gear || f.Ias > f.P.Vapp + 20 || Math.Abs(off) > 400)
                {
                    GoAround(f, $"not stable at 1,000 ft ({(f.Gear ? "" : "gear up, ")}{f.Ias:0} kt, {off:+0;-0} ft from the glidepath, {dth:0.0} nm out at {f.AltFt:0} ft{(f.WentAround ? ", after a go-around" : "")})");
                    return;
                }
            }
            // Landing clearance: the runway must be clear when the aircraft is half a mile out.
            if (!f.LandingChecked && dth < 0.4)
            {
                f.LandingChecked = true;
                var toThreshold = dth * Geo2.MPerNm / Math.Max(30, f.Gs * Kt);
                // A departure that will be airborne before this aircraft reaches the threshold is fine.
                bool Clear(Flight o) => o.Phase == "takeoff roll" && (o.P.Vr + 7 - o.Ias) / o.P.RollAccel < toThreshold;
                var blocker = Flights.FirstOrDefault(o => o != f && !o.Done && !Clear(o) && (o.Reserve == rwy.Runway || (o.OnRunway == rwy.Runway && !(o.Kind == "departure" && !o.OnGround))));
                if (blocker is not null) { GoAround(f, $"runway not clear: {blocker.Callsign} {blocker.Phase}"); return; }
                // A departure on a crossing runway that won't be through the intersection in time (7110.65 3-10-4).
                foreach (var end in Apt.Ends)
                {
                    if (end.Runway == rwy.Runway) continue;
                    var x = Crossing(rwy, end);
                    if (x is null) continue;
                    var tArr = toThreshold;   // 3-10-4: through the intersection before this aircraft crosses the threshold
                    var dep = Flights.FirstOrDefault(o => o.Phase == "takeoff roll" && o.Rwy == end && o.Rwy.Along(o.Pos) < x.Value.alongB + 30 &&
                        RollTime(o, x.Value.alongB) - (Time - o.PhaseTime) + 10 > tArr);
                    if (dep is not null) { GoAround(f, $"{dep.Callsign} departing the crossing runway {end.Ident}"); return; }
                }
            }
            if (f.Phase == "flare" && f.AltFt <= rwy.ElevationFt)
            {
                f.AltFt = rwy.ElevationFt;
                f.MaxKt = 30;
                MoveAir(f, dt);
                StartRollout(f);
                Log($"{f.Callsign}: landed runway {rwy.Ident}{(f.Spot is not null ? ", taxiing to " + f.Spot.Label : "")}");
                return;
            }
        }
        MoveAir(f, dt);
        var fpa = Math.Atan2(f.Vs, Math.Max(60, f.Gs) * 101.27) * 180 / Math.PI;
        var alpha = Math.Clamp(2.5 + (250 - f.Ias) / 30, 2.5, 6.5);
        // Real airliners: about 2-3° nose up on final, 4-5° in the flare.
        var pitchTarget = f.Phase == "flare" ? 4.5 : Math.Min(fpa + alpha, f.Gear ? 3.0 : 8.0);
        f.Pitch += Math.Clamp(pitchTarget - f.Pitch, -2 * dt, 2 * dt);
        if (f.S >= f.Path!.Length - 1) f.Done = true;
    }

    /// <summary>Moves along the air path at the ground speed that the airspeed and wind give, crabbing into the wind.</summary>
    private void MoveAir(Flight f, double dt)
    {
        var tas = Wind.Tas(f.Ias, f.AltFt);
        var (_, dir0, _) = f.Path!.At(f.S);
        var (_, gs0) = Wind.Solve(dir0, tas);
        f.S += gs0 * Kt * dt;
        var (p, dir, k) = f.Path.At(f.S);
        var (hdg, gs) = Wind.Solve(dir, tas);
        f.Pos = p;
        f.Heading = f.OnGround || f.Phase == "takeoff roll" ? dir : hdg;
        f.Gs = gs;
        var v = tas * Kt;
        var bankTarget = Math.Clamp(Math.Atan(v * v * k / G) * 180 / Math.PI, -28, 28);
        if (f.AltFt - (f.Rwy?.ElevationFt ?? Apt.ElevationFt) < 50) bankTarget = 0;
        f.Bank += Math.Clamp(bankTarget - f.Bank, -5 * dt, 5 * dt);
    }

    // ------------------------------------------------------------------ tests (control page buttons)

    public (bool ok, string message) TestDeparture()
    {
        SelectRunways(ArrRwy is null);
        if (DepRwy is null) return (false, "No runway data.");
        if (Apt.HasGround)
        {
            var pick = Flights.Where(f => f.Kind == "parked" && f.Phase == "parked" && f.Spot is not null)
                .OrderBy(f => UserPos is { } u ? (f.Pos - u).Length : 0).FirstOrDefault();
            var f = StartDeparture(pick, 5);
            if (f is null) return (false, "No free gate for a test departure.");
            return (true, $"{f.Callsign} ({f.Type}) is about to push back from {f.Spot?.Label ?? "a gate"} for runway {DepRwy.Ident}.");
        }
        var id = Source.NewAircraft(Apt.Icao, 1, Rng);
        var (cs, other) = Source.NewFlight(Apt.Icao, id.Airline, true, Rng, id.Type);
        var rwy = DepRwy;
        var t = new Flight { Id = NewId(), Callsign = cs, Airline = id.Airline, Type = id.Type, Other = other, Kind = "departure", P = Perf.For(id.Type), Rwy = rwy, Test = true };
        t.Pos = rwy.Threshold + rwy.Dir * 60;
        t.Heading = rwy.Heading;
        t.AltFt = rwy.ElevationFt;
        t.Timer = 15;
        t.SetPhase("lined up", Time);
        t.Lights = Nav | Beacon | Logo | Strobes | Landing | Taxi;
        Flights.Add(t);
        return (true, $"{cs} ({id.Type}) is lined up on runway {rwy.Ident}, departing in about 15 seconds.");
    }

    public (bool ok, string message) TestArrival()
    {
        SelectRunways(ArrRwy is null);
        var f = SpawnArrival(Apt.HasGround ? 8 : 5, true);
        if (f is null) return (false, "No runway data.");
        return (true, $"{f.Callsign} ({f.Type}) is on a {(Apt.HasGround ? 8 : 5)}-mile final for runway {f.Rwy!.Ident}.");
    }
}
