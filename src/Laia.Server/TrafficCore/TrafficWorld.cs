using Laia.Common.Airports;

namespace Laia.Server.TrafficCore;

/// <summary>Peak is a test setting: the airport's busiest hours from its real schedule, replayed now (not the real time).</summary>
/// <summary>
/// Off; Light, Medium and Busy fly a share of the real schedule (35%, 65%, all of it) under caps that grow with the
/// airport's size; Real world flies the whole real schedule with no caps; Peak is the stress test — the airport's busiest
/// 3 hours, replayed now, in full and with no caps.
/// </summary>
public enum TrafficLevel { Off, Light, Medium, Busy, RealWorld, Peak }

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
    /// <summary>When it started waiting at this hold line (NaN: not waiting), and whether that wait was logged.</summary>
    public double WaitSince = double.NaN;
    public bool WaitLogged, Rerouted;
    /// <summary>When it was cleared across; a crossing that hasn't moved on gives the runway back.</summary>
    public double ClearedAt = double.NaN, ClearedS;
    /// <summary>It has been on the runway it's crossing (so off it again means across).</summary>
    public bool Entered;
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
    /// <summary>When it parked at its gate after landing here (NaN: it didn't).</summary>
    public double ArrivedAt = double.NaN;
    /// <summary>Parked after landing between 23:00 and 06:00 airport local time: stays for its next flight (user, 24 Nov).</summary>
    public bool Overnight;
    public bool NoTurnLogged;
    /// <summary>When its departure pushes back (world time), for one placed at its gate ahead of it.</summary>
    public double PushAt = double.NaN;
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
    /// <summary>Everyone else in a standoff ring this one was told to go first in (not only the one it saw first).</summary>
    public HashSet<Flight>? IgnoreAll;
    /// <summary>The rest of its filed route (airways, fixes) from the hand-over to its STAR — flown before the STAR.</summary>
    public List<V2>? Via;
    /// <summary>Path distance where its last delay vector rejoins (no new one before it's back on its route).</summary>
    public double VectorRejoinS = double.NegativeInfinity;
    public int DelayVectors, RwySwitches;
    public string? HoldWhy; public bool HoldWhyLogged;
    public double RunwayStopFor, SpeedAt = -1e9;
    public int BreakOffs;
    public bool JunctionHeld, GoFirstFailed;
    public HashSet<int> LeftRunways = new();
    public string? LeaderCs;
    /// <summary>The last arrival on a crossing runway or close parallel its landing slot was moved behind (logged once).</summary>
    public string? DepNote;
    /// <summary>The last kind of change the route check refused for it (logged once per kind).</summary>
    public string? RefusedNote;
    public double GoFirstAt = -1, JhCheckAt = -1e9, JunctionHeldSince = -1e9, JunctionFreeUntil = -1e9;
    public (string name, Flight ahead)? JhLast;
    public string ExitNote = "";
    public bool ReplanFree;
    /// <summary>Departures: its number in its runway's departure order (0 when not in it: at the gate, boarding).</summary>
    public int DepSeq;
    /// <summary>Seconds its current delay vectors add (cleared when its route is built again); the planner counts
    /// without them. Whether the runway planner has seen it yet.</summary>
    public double DetourSec;
    public bool Planned;
    /// <summary>A lower level to keep 1,000 ft under another of this airport's arrivals en route (NaN when none), until when.</summary>
    public double SepCeiling = double.NaN, SepUntil;
    public string? SepFrom;
    /// <summary>The descent's last aim, published floor held and ceilings from other traffic (for a missed restriction's log line).</summary>
    public double DbgWant = double.NaN, DbgFloor = double.NaN, DbgPlan = double.NaN;
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
    /// <summary>Where it has been (for the live map): position, altitude and time, every few seconds.</summary>
    public readonly List<(V2 P, double AltFt, double T)> Trail = new();
    private char? _wake;
    /// <summary>Published speed limits along the path: distance along the path of the fix, and the limit (kt).</summary>
    public List<(double S, double Kt)> SpeedLimits = new();
    /// <summary>The procedure's altitude restrictions on the path: at the fix at S, at or above Min and at or below Max (0 = none).</summary>
    public List<(double S, double Min, double Max)> AltLimits = new();
    /// <summary>The procedure restrictions still to be crossed (fix, altitude window, speed limit), to check they are met.</summary>
    public List<(double S, string Fix, double Min, double Max, double Kt)> Checks = new();
    public int ChecksMet;
    public List<string> ChecksMissed = new();
    /// <summary>The named fixes still ahead on the arrival (distance along the path), their predicted crossing times
    /// (world time, updated every 5 s), and the time predicted for each about 10 minutes before it (to measure it).</summary>
    public List<(double S, string Fix)> Fixes = new();
    /// <summary>The taxi nodes of the current ground route (to see which taxiway segments it will use, and which way).</summary>
    public List<int>? TaxiRoute;
    public List<(string Fix, double At)> FixEtas = new();
    public Dictionary<string, double> FixPred10 = new();
    /// <summary>A departure's predicted take-off time (world time, NaN until predicted), and what was predicted 10 and 5 minutes before.</summary>
    public string DepReport = "";
    public double GateTryTime = -999;
    /// <summary>A departure's climb stopped below crossing traffic (NaN: none), and when that was last checked.</summary>
    public double ClimbStop = double.NaN, ProbeAt = -99, ClimbHoldFt = double.NaN;
    public string? StoppedFor;
    /// <summary>The SID's top altitude (the chart's "maintain 7,000", NaN: none) and whether the climb above it was given.</summary>
    public double TopAltFt = double.NaN, ReleaseAt = -99, TopReachedAt = double.NaN;
    /// <summary>A departure closing in behind another: no faster than it (NaN: free). An arrival kept above another
    /// airport's traffic: not below this (NaN: free), and when it was last checked.</summary>
    public double DepSpeedCap = double.NaN, DescentFloor = double.NaN, DescentProbeAt = -99;
    public string? FloorFor;
    /// <summary>A push then pull: the tow forward after the push (null when there is none).</summary>
    public List<V2>? PullPath;
    public double PullRadius;
    public bool ClimbReleased;
    public string TopSource = "";
    public double TakeoffAt = double.NaN, TakeoffPred10 = double.NaN, TakeoffPred5 = double.NaN, ReadyAt = double.NaN;
    /// <summary>Kept below the user at cruise (an arrival level with them starts down early); NaN: none.</summary>
    public double UserCeiling = double.NaN;
    /// <summary>Waiting at the runway for a release (an arrival being vectored across the way out); since when.</summary>
    public double ReleaseWaitSince = double.NaN;
    /// <summary>A long wait lined up on the runway has been logged (with why).</summary>
    public bool LinedUpWaitLogged;
    public string? ReleaseWaitFor;
    /// <summary>Held at the gate for its departure slot since (world time; NaN: not held), and whether that's over.</summary>
    public double GateHeldSince = double.NaN;
    /// <summary>Its place in the departure queue while held at the gate: when it was ready to go as the hold began.</summary>
    public double QueueReadyAt = double.NaN;
    public bool GateHoldDone;
    /// <summary>The planned departure route (planned when lining up, so the spacing can look at where it goes).</summary>
    public List<AirPoint>? DepPlan;
    /// <summary>The runway DepPlan was planned from: a different runway now (a runway change) means planning again.</summary>
    public RunwayEnd? DepPlanRwy;
    public string DepRoute = "";
    public double DepCourse = double.NaN;   // the track about 4 nm after the runway end
    public bool StableChecked;
    /// <summary>Predicted seconds to the threshold (updated every 5 s), its place in the landing order on its runway,
    /// and the landing times predicted 30, 10 and 5 minutes out (world time), to measure the prediction.</summary>
    public double EtaNominal = double.NaN;
    /// <summary>When the arrival manager last looked at moving it to a runway with less delay, and when it last moved it.</summary>
    public double RwyCheckAt = -999, RwySwitchAt = -999;
    /// <summary>Its place in the landing order: its unhindered time when first sequenced (NaN: not yet). Stretching its
    /// path (delay vectors, the trombone) doesn't change it, so it can't swap the order.</summary>
    public double OrderKey = double.NaN;
    /// <summary>Track nm to the threshold where its STAR starts and where it leaves the STAR (vectors/approach), as built.</summary>
    public double StarNm = double.NaN, AppNm = double.NaN;
    /// <summary>Broken off or gone around: placed behind everyone nearer the runway than it when re-sequenced.</summary>
    public bool Rejoin;
    /// <summary>Sequenced behind the user (it takes the delay; kept there unless the user falls clearly behind it).</summary>
    public bool BehindUser;
    public double BehindUserReleased = double.NegativeInfinity;
    /// <summary>The real tracks off its STAR (shortest to longest), which one it flies, and where that one leaves the STAR.</summary>
    public List<(int cut, List<V2> pts, double lenNm)>? Fan;
    public int FanIdx = -1;
    public string? FanKey;
    public V2 FanLeave;
    public double FanAt = -999;
    public double EtaSec = double.NaN, EtaAt = -999, Pred30 = double.NaN, Pred10 = double.NaN, Pred5 = double.NaN;
    public int Seq;
    /// <summary>Arrival manager: seconds this arrival must lose to land at its slot behind the one ahead (0 = none),
    /// and whether it's flying slower to lose it.</summary>
    public double Delay; public bool Slowed;
    /// <summary>Constant traffic manager: how far its predicted landing (at the speeds and route it's flying) is from its
    /// slot (s; + late, − early), and what it was at 100, 40 and 20 nm to go (NaN until passed).</summary>
    public double SlotDev = double.NaN, Dev100 = double.NaN, Dev40 = double.NaN, Dev20 = double.NaN;
    /// <summary>Arrival manager: the speed reduction assigned for the sequence (kt below the normal speeds, far out),
    /// whether its landing order is fixed (frozen, inside ~15 minutes), the slot it was frozen with, what the manager
    /// is doing with it (for the map), the gap to the one ahead, and when it was last vectored.</summary>
    public double SlowDv, SlowAt = -99, FrozenSlot, LastVector = -999, GapNm = double.NaN, GapSec = double.NaN;
    /// <summary>The learned controller's speed reduction by distance to go (Release 4b): how much of <see cref="SlowDv"/>
    /// it takes at each nm from 20 to 45 out (1 = all of it), worked out for the delay it was last asked to lose.</summary>
    public double[]? AtcSlowShape;
    /// <summary>The flown-path watch (user, 28 Nov: "Even one loop or unrealistic turn is a failure"): headings and
    /// positions every 5 s since the last go-around or break-off, what's been found (once per kind), and whether it's
    /// been established on its final.</summary>
    public readonly List<(double t, V2 p, double hdg, double alt)> Flown = new();
    public readonly HashSet<string> FlownFaults = new();
    public bool FlownOnFinal;
    public double AtcSlowShapeFor = double.NaN;
    public bool Frozen;
    /// <summary>In the landing sequence: within 40 nm of its runway's approach end and at or below 8,000 ft above the
    /// field (or the lowest its procedure still requires there + 500 ft). Before that: spacing only.</summary>
    public bool InSeq;
    public string? MitBehind;
    public string Mgr = "";
    /// <summary>Cruise level of a flight handed over from the whole sky (0 for one started near the airport).</summary>
    public double CruiseFt;
    /// <summary>The STAR (with its entry transition) or SID (with its exit) chosen with the runway, so the two match.</summary>
    public (Procedure p, Transition? t)? StarPick, SidPick;
    public double LastHold = -999;
    /// <summary>When the base turn was last moved (the trombone), and how far out it is now (nm; NaN: no downwind ahead).</summary>
    public double TromboneAdd, LastTrombone = -999, BaseTurnNm = double.NaN, TromboneLoggedNm = double.NaN, TromboneLeft = double.NaN;

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
public sealed partial class TrafficWorld
{
    public const int Nav = 1, Beacon = 2, Strobes = 4, Landing = 8, Taxi = 16, Logo = 32, Wing = 64, Cabin = 128;
    private const double G = 9.81;
    private const double Kt = Geo2.MpsPerKt;

    public AirportModel Apt { get; }
    public IFlightSource Source { get; }
    public Random Rng { get; }
    public Action<string> Log
    {
        get => _logWrap ??= l => { _recentLog.Enqueue($"{Time,6:0} {l}"); while (_recentLog.Count > 600) _recentLog.TryDequeue(out _); _log(l); };
        set { _log = value ?? (_ => { }); _logWrap = null; }
    }
    private Action<string> _log = _ => { };
    private Action<string>? _logWrap;
    /// <summary>The last few hundred lines logged (for what led up to an unrealistic turn).</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _recentLog = new();
    public List<Flight> Flights { get; } = new();
    public double Time { get; private set; }
    /// <summary>The surface wind (the METAR): it picks the runways and is used on the ground.</summary>
    public Wind Wind { get; set; }
    /// <summary>
    /// The wind every 500 ft from 0 to 45,000 ft MSL here (WindsAloft: the user's own sim wind where they've flown,
    /// the winds-aloft forecast elsewhere), so LAIA's aircraft fly in the same air as the user — at the same airspeed
    /// they have the same ground speed (user, 17 Nov). Null: the surface wind at every level.
    /// </summary>
    public Wind[]? Aloft { get; set; }

    /// <summary>The wind at an altitude (feet MSL).</summary>
    public Wind WindAt(double altFt) => Aloft is { Length: > 1 } a ? WindsAloft.FromProfile(a, altFt) : Wind;
    /// <summary>The traffic level. Changing it re-picks the runways (a busy airport may use a second, crossing runway).</summary>
    public TrafficLevel Level
    {
        get => _level;
        set
        {
            if (_level == value) return;
            var wasBusy = _level >= TrafficLevel.Medium;
            _level = value;
            var isBusy = value >= TrafficLevel.Medium;
            if (value != TrafficLevel.Peak) { _peakSet = false; _peakOffset = 0; PeakInfo = null; }
            if ((wasBusy != isBusy || value == TrafficLevel.Peak || _wasPeak) && ArrRwy is not null) SelectRunways(true);
            _wasPeak = value == TrafficLevel.Peak;
            if (value != TrafficLevel.Off) _levelChanged = true;
        }
    }
    /// <summary>The level changed while traffic runs: refit it at once (more or fewer aircraft), not only as new flights come.</summary>
    private bool _levelChanged;
    private TrafficLevel _level = TrafficLevel.Medium;
    public RunwayEnd? ArrRwy { get; private set; }
    /// <summary>Every landing runway in use (the ATIS can name several, e.g. Orlando's 35L and 36L); ArrRwy is the main one.</summary>
    public List<RunwayEnd> ArrRwys { get; private set; } = new();
    /// <summary>Every departure runway in use; DepRwy is the main one.</summary>
    public List<RunwayEnd> DepRwys { get; private set; } = new();
    /// <summary>The FAA preferred routes (which STAR and SID a flight files, by airport pair and flow), when loaded.</summary>
    public PreferredRoutes? Routes { get; set; }
    /// <summary>US fixes, navaids and airways (FAA NASR), to fly whole filed routes in the whole sky.</summary>
    public NavDb? NavData { get; set; }
    /// <summary>How many SIDs/STARs came from the preferred routes (vs. picked by direction).</summary>
    public int RoutesUsed { get; private set; }
    /// <summary>The airport's own published runway rules (read from its FAA AAUPs), when it has any.</summary>
    public AirportRules? Rules { get; set; }
    /// <summary>Where an airport is (lat, lon), for choosing the STAR from the origin and the SID towards the destination.</summary>
    public Func<string, (double lat, double lon)?>? Locate { get; set; }

    private AtisInfo _atis = new();
    /// <summary>What the real ATIS says is in use: it decides the runways and the approach; the wind is only the fallback.</summary>
    public AtisInfo Atis
    {
        get => _atis;
        set
        {
            value ??= new AtisInfo();
            if (value.Key == _atis.Key) { _atis = value; return; }
            _atis = value;
            if (ArrRwy is not null) SelectRunways();
        }
    }
    /// <summary>Sets the ATIS from one combined broadcast's text.</summary>
    public string? AtisText { get => _atis.Text; set => Atis = AtisParse.FromText(value); }
    public List<(string kind, string suffix, string runway)> AtisApproaches => _atis.Approaches;
    public List<string> AtisLanding => _atis.Landing;
    public List<string> AtisDeparting => _atis.Departing;
    /// <summary>The one approach every arrival to the landing runway flies (never a mix), e.g. "RNAV Y 31".</summary>
    public string? ApproachInUse { get; private set; }
    private readonly Dictionary<RunwayEnd, (Approach? app, string key, string? label)> _apps = new();
    private const double MinFinalNm = 1.2; // LGA's RNAV X 31 turns onto its final only 1.6 nm out
    public RunwayEnd? DepRwy { get; private set; }
    public V2? UserPos { get; set; }
    public bool UserOnGround { get; set; }
    public bool Automatic { get; set; } = true;
    /// <summary>While the airport is being prepared: no runways chosen until the ATIS, wind and rules are in.</summary>
    public bool HoldRunways { get; set; }
    public int Movements { get; private set; }
    /// <summary>The airport's real schedule; when set, traffic follows it instead of made-up flights.</summary>
    public ScheduleData? Schedule { get; set; }
    /// <summary>
    /// Made-up airline traffic when the airport has no schedule (tests, and the test airport). Off for real airports:
    /// no schedule means no airline service, and no airliners are made up for it (KPWK and KJVL, 15 Nov: airliners
    /// landing at an executive airport and a regional field with no airline flights).
    /// </summary>
    public bool GenericTraffic { get; set; } = true;
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

    /// <summary>
    /// Most aircraft at the airport, most moving at once, the made-up movements an hour (only without a real schedule) and
    /// the share of parking filled. Light, Medium and Busy caps grow with the airport (<see cref="SizeFactor"/>, up to
    /// double); Real world and Peak have no practical cap — the real schedule decides.
    /// </summary>
    private (int maxObjects, int maxActive, double movementsPerHour, double parkedFill) Budget
    {
        get
        {
            var (o, a, m, f) = Level switch
            {
                // No count of aircraft limits any level (user, 15 Nov: "the limits should never be aircraft on the ground,
                // but rather a percentage of that field's scheduled traffic"): the level is the share of the real schedule
                // (KeepAtLevel); these are only a runaway guard. What the sim draws is limited separately (the nearest 50,
                // 80 at Real world and Peak).
                TrafficLevel.Light => (400, 150, 10.0, 0.25),
                TrafficLevel.Medium => (400, 150, 20.0, 0.40),
                TrafficLevel.Busy => (400, 150, 32.0, 0.55),
                TrafficLevel.RealWorld => (400, 150, 40.0, 0.65),
                TrafficLevel.Peak => (400, 150, 44.0, 0.70),
                _ => (0, 0, 0.0, 0.0),
            };
            return (o, a, m, f);
        }
    }

    private ScheduleData? _sizeFor;
    private double _size = 1;
    /// <summary>
    /// How big the airport is, from its real schedule: its busiest hour's movements over 20 (what the base caps suit),
    /// between 1 and 2. A regional field stays at 1; a hub like Orlando gets double the room. 1 without a schedule.
    /// </summary>
    public double SizeFactor
    {
        get
        {
            var s = Schedule;
            if (s is null) return 1;
            if (!ReferenceEquals(s, _sizeFor))
            {
                _sizeFor = s;
                var me = Apt.Icao;
                var bins = new int[168];
                foreach (var l in s.Legs)
                {
                    if (l.Dep == me) bins[((l.DepMin % 10080 + 10080) % 10080) / 60]++;
                    if (l.Arr == me) bins[((l.ArrMin % 10080 + 10080) % 10080) / 60]++;
                }
                _size = Math.Clamp(bins.Max() / 20.0, 1, 2);
            }
            return _size;
        }
    }

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
        bool WindOk(RunwayEnd e) => Wind.Headwind(e.Heading) >= -5 && Crosswind(e.Heading) <= 20;
        // The Peak test replays the busiest real hours, but the ATIS and live traffic are today's, right now (KORD, 18 Nov
        // at 23:00 local: one landing runway, six closed for the night, under a Monday-afternoon rush — every arrival was
        // queued for 27C). In the Peak test the runways are the ones real traffic used on the learned days in this wind,
        // with no night closures.
        var peak = Level == TrafficLevel.Peak;
        // Closed runways (from the ATIS) are never used.
        if (!peak) usable = usable.Where(e => !_atis.Closed.Contains(e.Ident)).ToList();
        if (usable.Count == 0) return;
        // The real ATIS always decides when it names the runways; the wind is only the fallback.
        // Every runway it names is used: several landing runways (Orlando lands 35L and 36L) and several departure runways.
        List<RunwayEnd> Ends(IEnumerable<string> ids) => ids.Where(id => !_atis.Closed.Contains(id))
            .Select(id => Apt.Ends.FirstOrDefault(e => e.Ident == id)).Where(e => e is not null).Select(e => e!).Distinct().OrderByDescending(e => e.LengthM).ToList();
        if (peak && PeakConfig(usable, Ends) is { } pc)
        {
            // Peak (user, 22 Nov: "for Peak I would not change ops unless the tailwind component is greater than five
            // knots"): once set, the runways stay while no landing runway has more than 5 kt of tailwind — calm, a few
            // knots more or a swing of the wind don't turn the airport round (KLGA Peak, 22 Nov: six changes in the night
            // between 22/31 and 31/31 as the wind wavered round 050 at 0–11 kt). Only better information (the ATIS, the
            // learned days, after the wind alone) or a new start replaces them sooner.
            var keep = !force && ArrRwys.Count > 0 && ArrRwys.All(e => Apt.Ends.Contains(e) && Wind.Headwind(e.Heading) >= -5) && pc.rank <= _peakRank;
            if (keep) return;
            if (!force && ArrRwys.Count > 0 && !pc.arr.SequenceEqual(ArrRwys) && UserRunwayHeld(pc.arr)) return;
            if (!ArrRwys.SequenceEqual(pc.arr) || !DepRwys.SequenceEqual(pc.dep))
                Log($"Runways in use at {Apt.Icao}: landing {string.Join(", ", pc.arr.Select(e => e.Ident))}, departing {string.Join(", ", pc.dep.Select(e => e.Ident))} ({pc.why}; wind {Wind.FromDeg:000} at {Wind.Kt:0} kt)");
            _peakRank = pc.rank; _lastRunwayChange = Time;
            ArrRwys = pc.arr; DepRwys = pc.dep; ArrRwy = pc.arr[0]; DepRwy = pc.dep[0];
            return;
        }
        var atisArrs = peak ? new List<RunwayEnd>() : Ends(AtisLanding);
        if (atisArrs.Count > 0)
        {
            var atisDeps = Ends(AtisDeparting);
            if (atisDeps.Count == 0) atisDeps = new() { atisArrs[0] };
            // The main departure runway is one not also used for landing, when there is one.
            atisDeps = atisDeps.OrderBy(e => atisArrs.Any(a => a.Runway == e.Runway) ? 1 : 0).ThenByDescending(e => e.LengthM).ToList();
            if (!ArrRwys.SequenceEqual(atisArrs) || !DepRwys.SequenceEqual(atisDeps))
                Log($"Runways in use at {Apt.Icao}: landing {string.Join(", ", atisArrs.Select(e => e.Ident))}, departing {string.Join(", ", atisDeps.Select(e => e.Ident))} (from the real ATIS{(!string.IsNullOrEmpty(_atis.CodeArr) ? " info " + _atis.CodeArr : "")}; wind {Wind.FromDeg:000} at {Wind.Kt:0} kt)");
            ArrRwys = atisArrs; DepRwys = atisDeps;
            ArrRwy = atisArrs[0];
            DepRwy = atisDeps[0];
            return;
        }
        // No ATIS: what real traffic is doing ranks above the wind (user rule) — first what it's doing now (live
        // positions), then what it did on the learned day in this wind (real tracks, M6); the wind rule is the last resort.
        if (RealConfig(usable) is { } rc)
        {
            var differs = !ArrRwys.SequenceEqual(rc.arr) || !DepRwys.SequenceEqual(rc.dep);
            // Back from live traffic to the learned days counts too (21 Nov, CYUL: learned 24L/24R → live 24R → learned
            // 24L/24R eight minutes later, while the user sat at the gate).
            if (differs && ArrRwys.Count > 0 && (rc.why.Contains("live traffic") || _rwyFromLive))
            {
                // Live traffic flickers between configurations (CYYC, 13 Nov: 17R → 17R+17L → 17L → 17L+17R in 4 minutes,
                // as the last few arrivals happened to use one or the other). A change from it must be seen for 10
                // minutes running, and at most one every 20 minutes.
                var sig = string.Join(",", rc.arr.Select(e => e.Ident)) + "/" + string.Join(",", rc.dep.Select(e => e.Ident));
                if (sig != _livePendingSig) { _livePendingSig = sig; _livePendingSince = Time; }
                if (!force && (Time - _livePendingSince < 600 || Time - _lastRunwayChange < 1200)) return;
            }
            if (differs && !force && UserRunwayHeld(rc.arr)) return;
            if (differs) { _lastRunwayChange = Time; _livePendingSig = ""; _rwyFromLive = rc.why.Contains("live traffic"); }
            if (!ArrRwys.SequenceEqual(rc.arr) || !DepRwys.SequenceEqual(rc.dep))
                Log($"Runways in use at {Apt.Icao}: landing {string.Join(", ", rc.arr.Select(e => e.Ident))}, departing {string.Join(", ", rc.dep.Select(e => e.Ident))} ({rc.why}; wind {Wind.FromDeg:000} at {Wind.Kt:0} kt)");
            ArrRwys = rc.arr; DepRwys = rc.dep; ArrRwy = rc.arr[0]; DepRwy = rc.dep[0];
            return;
        }
        RunwayEnd best;
        if (Wind.Kt < 4 && ArrRwy is not null && usable.Contains(ArrRwy) && !force) best = ArrRwy;
        else if (Wind.Kt < 4) best = usable.OrderByDescending(e => e.LengthM).First();
        else
        {
            // Like a real tower: the main (longest) runway whenever it's within limits (crosswind 20, tailwind 5),
            // the end more into the wind; only a shorter runway when the main one is out of limits.
            var ok = usable.Where(WindOk).ToList();
            best = ok.Count > 0
                ? ok.OrderByDescending(e => Math.Round(e.LengthM / 100)).ThenByDescending(e => Wind.Headwind(e.Heading)).First()
                : usable.OrderByDescending(e => Wind.Headwind(e.Heading)).ThenByDescending(e => e.LengthM).First();
        }
        // Keep the current runways unless the tailwind on the landing runway gets above 5 knots.
        if (!force && ArrRwy is not null && usable.Contains(ArrRwy) && WindOk(ArrRwy) && (DepRwy is null || WindOk(DepRwy) || DepRwy == ArrRwy)) return;
        var group = usable.Where(e => Math.Abs(Geo2.Diff(e.Heading, best.Heading)) < 25).OrderByDescending(e => e.LengthM).ToList();
        var arr = group[0];
        var dep = group.Skip(1).FirstOrDefault(e => e.Runway != arr.Runway && e.LengthM >= 2000);
        if (dep is null && Level >= TrafficLevel.Medium)
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
        if (ArrRwy != arr && !force && UserRunwayHeld(new() { arr })) return;
        if (ArrRwy != arr || DepRwy != dep) Log($"Runways in use at {Apt.Icao}: landing {arr.Ident}, departing {dep.Ident} (wind {Wind.FromDeg:000} at {Wind.Kt:0} kt)");
        ArrRwy = arr;
        DepRwy = dep;
        ArrRwys = new() { arr };
        DepRwys = new() { dep };
        if (peak) _peakRank = 0;
    }

    private int _peakRank = -1;

    /// <summary>
    /// The Peak test's runways: the real ATIS first (user, 22 Nov: "This is a field with an ATIS, which is supposed to be
    /// the first and most deciding factor"), with the runways real traffic also uses that way round added back when it
    /// names fewer (a night configuration: KORD, 18 Nov, one landing runway and six closed under a daytime rush). Without
    /// an ATIS, the learned days in this wind. rank: 2 ATIS, 1 learned days; null when neither (the wind decides).
    /// </summary>
    private (List<RunwayEnd> arr, List<RunwayEnd> dep, string why, int rank)? PeakConfig(List<RunwayEnd> usable, Func<IEnumerable<string>, List<RunwayEnd>> ends)
    {
        var learned = RealConfig(usable);
        var aA = ends(AtisLanding);
        if (aA.Count == 0) return learned is { } l0 ? (l0.arr, l0.dep, l0.why + ", Peak test", 1) : null;
        var aD = ends(AtisDeparting);
        if (aD.Count == 0) aD = new() { aA[0] };
        aD = aD.OrderBy(e => aA.Any(a => a.Runway == e.Runway) ? 1 : 0).ThenByDescending(e => e.LengthM).ToList();
        var why = $"from the real ATIS{(!string.IsNullOrEmpty(_atis.CodeArr) ? " info " + _atis.CodeArr : "")}";
        if (learned is { } l)
        {
            var addA = l.arr.Where(e => !aA.Contains(e) && Math.Abs(Geo2.Diff(e.Heading, aA[0].Heading)) < 60).ToList();
            var addD = DeparturesWith(aA.Concat(addA).ToList()) is { Count: > 0 } dw && dw.Values.Sum() >= 20
                ? l.dep.Concat(dw.OrderByDescending(kv => kv.Value).Where(kv => kv.Value * 10 >= dw.Values.Max()).Select(kv => usable.FirstOrDefault(u => u.Ident == kv.Key)).Where(e => e is not null).Select(e => e!))
                    .Distinct().Where(e => !aD.Contains(e) && !aA.Concat(addA).Any(a => a.Runway == e.Runway) && Math.Abs(Geo2.Diff(e.Heading, aA[0].Heading)) < 100 && Wind.Headwind(e.Heading) >= -5).ToList()
                : new List<RunwayEnd>();
            // Only when the ATIS names fewer than real traffic uses that way round (a night configuration).
            if (aA.Count >= l.arr.Count(e => Math.Abs(Geo2.Diff(e.Heading, aA[0].Heading)) < 60)) addA.Clear();
            if (aD.Count >= l.dep.Count) addD.Clear();
            if (addA.Count + addD.Count > 0)
            {
                aA = aA.Concat(addA).ToList(); aD = aD.Concat(addD).ToList();
                why += $", with {string.Join(", ", addA.Concat(addD).Select(e => e.Ident))} added as real traffic uses them that way round (the ATIS's night configuration isn't the replayed rush's)";
            }
        }
        return (aA, aD, why + ", Peak test", 2);
    }

    private string _livePendingSig = "";
    private double _livePendingSince, _lastRunwayChange = double.NegativeInfinity;

    /// <summary>
    /// The user's landing runway isn't taken away below 10,000 ft (user, 13 Nov: "never below 10,000 ft"): a new
    /// configuration without it waits until the user has landed (or climbs back above 10,000 ft).
    /// </summary>
    private bool UserRunwayHeld(List<RunwayEnd> newArrs)
    {
        if (UserArr is not { Rwy: { } ur } || UserAltFt >= 10000 || newArrs.Contains(ur)) return false;
        if (!_userHoldLogged) { _userHoldLogged = true; Log($"Runway change at {Apt.Icao} to landing {string.Join("/", newArrs.Select(e => e.Ident))} waits: you're landing {ur.Ident} below 10,000 ft"); }
        return true;
    }
    private bool _userHoldLogged;

    /// <summary>Runways real traffic is landing on and departing from right now (live adsb.lol positions), set by the server.</summary>
    public List<string> LiveLanding { get; set; } = new();
    public List<string> LiveDeparting { get; set; } = new();

    /// <summary>
    /// The runway configuration real traffic shows: live traffic first (both lists, or landing only — then departures
    /// use the same runways); otherwise the learned day's most used runways among those not out of limits in today's
    /// wind (tailwind up to 10 kt, crosswind 25), with parallels used at least a quarter as much. Null when neither.
    /// </summary>
    private bool _rwyFromLive;

    /// <summary>Take-offs (learned samples) per runway within 15 minutes of a landing on one of these runways.</summary>
    private Dictionary<string, int> DeparturesWith(List<RunwayEnd> arr)
    {
        var res = new Dictionary<string, int>();
        if (Learned is null) return res;
        var ids = arr.Select(a => a.Ident).ToHashSet();
        var lands = Learned.Samples.Where(x => x.Kind == "arrival" && ids.Contains(x.Runway) && x.T > 0).Select(x => x.T).OrderBy(t => t).ToList();
        if (lands.Count == 0) return res;
        foreach (var d in Learned.Samples)
        {
            if (d.Kind != "departure" || d.T <= 0) continue;
            var i = lands.BinarySearch(d.T - 900); if (i < 0) i = ~i;
            if (i < lands.Count && lands[i] <= d.T + 900) res[d.Runway] = res.GetValueOrDefault(d.Runway) + 1;
        }
        return res;
    }

    private (List<RunwayEnd> arr, List<RunwayEnd> dep, string why)? RealConfig(List<RunwayEnd> usable)
    {
        List<RunwayEnd> Map(IEnumerable<string> ids) => ids.Select(id => usable.FirstOrDefault(e => e.Ident == id)).Where(e => e is not null).Select(e => e!).Distinct().ToList();
        var la = Level == TrafficLevel.Peak ? new List<RunwayEnd>() : Map(LiveLanding);
        var ld = Level == TrafficLevel.Peak ? new List<RunwayEnd>() : Map(LiveDeparting);
        // Tailwind up to 5 kt (7 for one already in use, so the wind wavering at the limit doesn't turn the airport round
        // and back: 21 Nov, KATL Peak flipped west → east → west at 060–070° 10–12 kt with a 10 kt limit, each flip
        // followed by break-offs and a ground jam).
        bool Ok(RunwayEnd e) => Wind.Kt < 4 || (Wind.Headwind(e.Heading) >= (ArrRwys.Contains(e) || DepRwys.Contains(e) ? -7 : -5) && Crosswind(e.Heading) <= 25);
        List<RunwayEnd> Top(IEnumerable<(string id, int n)> counts)
        {
            var c = counts.Select(x => (e: usable.FirstOrDefault(u => u.Ident == x.id), x.n)).Where(x => x.e is not null && Ok(x.e!) && x.n >= 3)
                .OrderByDescending(x => x.n).ThenByDescending(x => Wind.Headwind(x.e!.Heading)).ToList();
            if (c.Count == 0) return new();
            var top = c[0];
            // Peak replays the busiest hours: a runway with a tenth of the main one's traffic that way joins (KATL 10/28,
            // about 20% of 08L's and 13% of 26R's — the third arrival runway of the rushes); otherwise a quarter.
            var share = Level == TrafficLevel.Peak ? 10 : 4;
            return c.Where(x => x.e == top.e || (x.n * share >= top.n && Math.Abs(Geo2.Diff(x.e!.Heading, top.e!.Heading)) < 30)).Select(x => x.e!).ToList();
        }
        if (la.Count > 0 || ld.Count > 0)
        {
            if (la.Count == 0) la = ld.Take(1).ToList();
            var why = "from live traffic, adsb.lol";
            if (ld.Count == 0)
            {
                // Live traffic shows the landings only: departures off the runways the learned days departed from the
                // same way round, not on top of the landings (CYYZ, 17 Nov: landing and departing 24R at Busy, while
                // real traffic departs 23 — go-arounds, vectors and holds followed).
                if (Learned is not null)
                {
                    var lv = Top(Learned.Departures.Select(kv => (kv.Key, kv.Value.Count)))
                        .Where(d => la.Any(a => Math.Abs(Geo2.Diff(a.Heading, d.Heading)) < 60)).ToList();
                    if (lv.Count > 0) { ld = lv; why = "landing from live traffic (adsb.lol), departing as the learned days did"; }
                }
                if (ld.Count == 0) ld = la.ToList();
            }
            return (la, ld, why);
        }
        if (Learned is null) return null;
        var arr = Top(Learned.Arrivals.Select(kv => (kv.Key, kv.Value.Count)));
        var dep = Top(Learned.Departures.Select(kv => (kv.Key, kv.Value.Count)));
        if (arr.Count == 0) return null;
        // The departure runways real traffic used together with these landing runways (take-offs within 15 minutes of a
        // landing on them), not simply the most used ones overall (22 Nov, KLGA Peak: landing 31 came with departing 31 —
        // LaGuardia's busiest departure runway, but when it lands 31 it departs 04: 174 of 180 take-offs).
        var together = DeparturesWith(arr);
        if (together.Values.Sum() >= 20 && Top(together.Select(kv => (kv.Key, kv.Value))) is { Count: > 0 } tg) dep = tg;
        // Departures off the learned day must be the same way round as today's landings.
        dep = dep.Where(d => arr.Any(a => Math.Abs(Geo2.Diff(a.Heading, d.Heading)) < 100)).ToList();
        if (dep.Count == 0) dep = arr.ToList();
        return (arr, dep, $"as real traffic used them on {Learned.Day} in a wind like today's (adsb.lol)");
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

    /// <summary>
    /// The route check (user, 27 Nov: "we've been dealing with these loops every single release"): every way in an
    /// arrival is given — built at the start, a real track picked off its STAR, a downwind stretched or brought in, a
    /// vector — must be one an aircraft could sensibly fly, whichever part of LAIA built it. Checked from the first point
    /// to the threshold (index <paramref name="thrIndex"/>): it never crosses its own track (a loop, by definition); it
    /// doesn't turn through more than one and a half circles; once on its final (lined up within 0.3 nm, going the
    /// runway's way) it doesn't leave it again (KSFO, 27 Nov: UAL436 4 nm from joining 28R taken north across the bay
    /// and round again); and it isn't far longer than the way it has to go. The reason, or null when it passes.
    /// </summary>
    internal static string? RouteProblem(IReadOnlyList<V2> pts, int thrIndex, RunwayEnd rwy)
    {
        var nm = Geo2.MPerNm;
        var n = Math.Min(thrIndex, pts.Count - 1);
        if (n < 2) return null;
        // Crossing itself (segments of 30 m or more; neighbours share a point and don't count).
        var seg = new List<(V2 a, V2 b, int i)>();
        for (var i = 0; i < n; i++) if ((pts[i + 1] - pts[i]).Length >= 30) seg.Add((pts[i], pts[i + 1], i));
        for (var x = 0; x < seg.Count; x++)
            for (var y = x + 2; y < seg.Count; y++)
            {
                if (seg[y].i - seg[x].i < 2) continue;
                if (SegmentsCross(seg[x].a, seg[x].b, seg[y].a, seg[y].b, out var at))
                    return $"it crosses its own track {(rwy.Threshold - at).Length / nm:0.0} nm from the runway";
            }
        // Turning.
        double turn = 0; double? last = null;
        for (var i = 0; i < n; i++)
        {
            var d = pts[i + 1] - pts[i];
            if (d.Length < 30) continue;
            if (last is { } lb) turn += Math.Abs(Geo2.Diff(lb, d.Bearing));
            last = d.Bearing;
        }
        if (turn > 540) return $"it turns through {turn:0}°";
        // Leaving its final.
        var onFinal = false;
        for (var i = 0; i <= n; i++)
        {
            var q = pts[i];
            var along = rwy.Along(q); var off = Math.Abs(rwy.Offset(q));
            if (!onFinal)
            {
                if (i < n && along < -0.5 * nm && along > -25 * nm && off < 0.3 * nm && (pts[i + 1] - q).Length > 30
                    && Math.Abs(Geo2.Diff((pts[i + 1] - q).Bearing, rwy.Heading)) < 20) onFinal = true;
            }
            else if (along < 0 && off > 1.0 * nm) return $"it leaves its final {-along / nm:0.0} nm out";
        }
        // Far too long for where it starts.
        double len = 0;
        for (var i = 0; i < n; i++) len += (pts[i + 1] - pts[i]).Length;
        len += (rwy.Threshold - pts[n]).Length;
        var straight = (rwy.Threshold - pts[0]).Length;
        if (len > 2 * straight + 40 * nm) return $"too long: {len / nm:0} nm to fly from {straight / nm:0} nm out";
        return null;
    }

    private static bool SegmentsCross(V2 a, V2 b, V2 c, V2 d, out V2 at)
    {
        at = default;
        var r = b - a; var s = d - c;
        var den = V2.Cross(r, s);
        if (Math.Abs(den) < 1e-9) return false;
        var t = V2.Cross(c - a, s) / den; var u = V2.Cross(c - a, r) / den;
        if (t <= 1e-6 || t >= 1 - 1e-6 || u <= 1e-6 || u >= 1 - 1e-6) return false;
        at = a + r * t;
        return true;
    }

    /// <summary>Ways in refused by <see cref="RouteProblem"/> (and logged), for the self-test.</summary>
    private int _routesRefused;
    /// <summary>
    /// No flight flies a copied route (user, 28 Nov: "no flight should have a hard coded route. We want LAIA to learn the
    /// techniques from those busy airports and apply that to all airports"): real tracks aren't stitched into ways in
    /// ("vectors as real traffic flies them") or departures any more — every route is built by LAIA from the published
    /// procedures and its own downwind, base and intercept, with the shape (base distance, downwind, intercept, final)
    /// learned per airport, or across all the hubs where an airport has no data of its own. Kept as a switch for the tests
    /// of the old behaviour only.
    /// </summary>
    public static bool CopyRealTracks { get; set; }
    /// <summary>The pattern shape learned across all the hubs (median base, join, downwind, intercept), for airports
    /// with no real traffic of their own learned; null until the learning service has it.</summary>
    public static VectorShape? GeneralShape { get; set; }
    /// <summary>The learned ways in (Step 6), when the model has beaten the rules on this airport's scoreboard; null otherwise.</summary>
    public RouteModel? LearnedRoutes { get; set; }
    /// <summary>
    /// The controller's techniques learned at the hubs (Release 4b) — the speeds at each distance to go, where speed is
    /// taken off to lose time, the landing order, the gap at the threshold — when they beat the rules on the scoreboard
    /// (at this airport, or across the hubs for one with no data of its own); null otherwise. Never a route.
    /// </summary>
    public AtcModel? Atc
    {
        get => _atc;
        set
        {
            if (ReferenceEquals(value, _atc)) return;
            _atc = value;
            Log?.Invoke(value is null ? $"{Apt.Icao}: the standard speeds, landing order and gaps (no learned ATC)" : $"{Apt.Icao}: learned ATC {value.Id} in use — speeds, landing order and gaps as real controllers at the hubs");
        }
    }
    private AtcModel? _atc;
    /// <summary>Which airport's learned technique <see cref="Atc"/> uses: this one when it learned it, otherwise "general".</summary>
    public string AtcAirport { get; set; } = "general";
    /// <summary>Which of the model's candidate ways in <see cref="BuildArrivalPath"/> tries (the next after a refusal).</summary>
    private int _routeModelTry;
    /// <summary>While set, <see cref="BuildArrivalPath"/> uses no real-track way in (the standard one).</summary>
    private bool _noRealWay;
    /// <summary>Set when the last <see cref="BuildArrivalPath"/> had to drop its real-track way in.</summary>
    private bool _realWayRefused;

    /// <summary>
    /// Parallel runways, same direction, whose centrelines are less than 2,500 ft (762 m) apart and overlap: one runway
    /// for wake turbulence (7110.65 3-9-6 f, 5-5-4 g, 6-1-5).
    /// </summary>
    internal static bool CloseParallel(RunwayEnd a, RunwayEnd b) =>
        a.Runway != b.Runway && Math.Abs(Geo2.Diff(a.Heading, b.Heading)) < 15 && Math.Abs(a.Offset(b.Threshold)) < 762
        && a.Along(b.Threshold) < a.LengthM && a.Along(b.FarEnd) > 0;

    /// <summary>
    /// Seconds from crossing the threshold until a landing aircraft has passed a point this far down the runway — or has
    /// turned off before it, whichever comes first: about 400 m to touchdown at the approach speed, then braking at about
    /// 2.4 m/s² down to taxi speed.
    /// </summary>
    private double LandingTimeTo(RunwayEnd end, Perf p, double along)
    {
        var v0 = Math.Max(55, (p.Vapp - 5) * Kt);
        double t;
        if (along <= 400) t = Math.Max(0, along) / v0;
        else
        {
            const double a = 2.4, vMin = 10;
            var d = along - 400;
            var dSlow = (v0 * v0 - vMin * vMin) / (2 * a);
            t = 400 / v0 + (d <= dSlow ? (v0 - Math.Sqrt(v0 * v0 - 2 * a * d)) / a : (v0 - vMin) / a + (d - dSlow) / vMin);
        }
        return Math.Min(t, RunwayOccupancy(end, p));
    }

    /// <summary>
    /// An arrival to <paramref name="end"/> (crossing this arrival's runway <paramref name="alongX"/> m past its
    /// threshold) that is first to its threshold and won't be through the intersection, or off the runway before it,
    /// <see cref="CrossingMarginSec"/> before this one reaches its threshold in <paramref name="toThr"/> s. Its callsign, or null.
    /// </summary>
    private string? CrossingArrivalAhead(Flight f, RunwayEnd mine, RunwayEnd end, double alongX, double toThr)
    {
        // Seconds until it has passed the intersection: NaN if it isn't landing there or is already through.
        double Pass(V2 pos, double gs, double heading, bool onGround, double altFt, Perf p, out bool first)
        {
            first = false;
            if (Math.Abs(Geo2.Diff(heading, end.Heading)) > 35) return double.NaN;
            var along = end.Along(pos);
            if (Math.Abs(end.Offset(pos)) > (along < 0 ? 900 : end.WidthM / 2 + 30)) return double.NaN;
            if (along >= alongX - 30) return double.NaN;   // through it (or at it)
            if (along < 0)
            {
                if (onGround || along < -4 * Geo2.MPerNm || altFt - end.ElevationFt > 1500) return double.NaN;
                var toIts = -along / Math.Max(30, gs * Kt);
                first = toIts < toThr;
                return toIts + LandingTimeTo(end, p, alongX);
            }
            // On the runway, rolling out: it may turn off before the intersection.
            first = true;
            if (onGround && gs < 40 && alongX - along > 300) return double.NaN;
            var v = Math.Max(5, gs * Kt);
            return Math.Min((alongX - along) / v * 1.3, RunwayOccupancy(end, p));
        }
        foreach (var o in Flights)
        {
            if (o == f || o.Done || o.Kind != "arrival" || o.Rwy?.Runway != end.Runway || o.Phase is "go-around") continue;
            var t = Pass(o.Pos, o.Gs, o.Heading, o.OnGround, o.AltFt, o.P, out var first);
            if (first && !double.IsNaN(t) && t + CrossingMarginSec > toThr) return o.Callsign;
        }
        if (UserPos is { } u && UserArr is { } me0)
        {
            var t = Pass(u, UserGs, UserTrack, UserOnGround, UserAltFt, me0.P, out var first);
            if (first && !double.IsNaN(t) && t + CrossingMarginSec > toThr) return $"{me0.Callsign} (you)";
        }
        else if (UserPos is { } u2 && UserOnGround)
        {
            var t = Pass(u2, UserGs, UserTrack, true, UserAltFt, Perf.For(UserType.Length > 0 ? UserType : "A320"), out var first);
            if (first && !double.IsNaN(t) && UserGs > 30 && t + CrossingMarginSec > toThr) return "you";
        }
        return null;
    }

    /// <summary>
    /// Close in: how much later (s) this arrival must land to keep the stagger with the arrivals to runways that depend on
    /// its own that land before it — the user always, LAIA's own by the planned order (slot) — from the live predicted
    /// times of both; and who that is. 0 when it fits.
    /// </summary>
    private (double add, string? who) DependentAdd(Flight f, double dth)
    {
        if (f.Rwy is not { } me) return (0, null);
        var myT = Time + EtaToThreshold(f, dth);
        var list = new List<(RunwayEnd end, double slot, Flight f)>();
        var pool = UserArr is { } ua ? Flights.Append(ua) : Flights;
        foreach (var o in pool)
        {
            if (o == f || o.Done || o.OnGround || o.Kind != "arrival" || o.Rwy is not { } oe || oe.Runway == me.Runway || o.Phase == "go-around") continue;
            if (Crossing(me, oe) is null && !CloseParallel(me, oe)) continue;
            var user = o == UserArr;
            var od = (o.ThrS - o.S) / Geo2.MPerNm;
            var tO = user ? (double.IsNaN(o.EtaSec) ? double.NaN : Time + o.EtaSec) : Time + EtaToThreshold(o, od);
            if (double.IsNaN(tO)) continue;
            // Who lands first: the user always; between LAIA's own, whichever will be there first now — the plan sets
            // that order far out and speed flies it, but close in it's how they're really flying that counts (the offline
            // test, 27 Nov: AAL724 for 27, vectored off late for spacing, was still "first" by its slot; SWA770 for 36
            // waited for it, got to its threshold first anyway, and one of them had to go around).
            var first = user || tO < myT;
            if (first) list.Add((oe, tO, o));
        }
        if (list.Count == 0) return (0, null);
        var need = DependentSlot(f, myT, list) - myT;
        if (need <= 0) return (0, null);
        var lead = list.Where(x => x.slot <= myT + need).OrderByDescending(x => x.slot).First();
        var what = Crossing(me, lead.end) is not null ? "on the crossing runway" : "on the close parallel";
        return (need, $"{(lead.f == UserArr ? lead.f.Callsign + " (you)" : lead.f.Callsign)} {what} {lead.end.Ident}");
    }

    /// <summary>Off its planned way in: on vectors, broken off or gone around and coming back round, or on a delay detour.</summary>
    private bool OffPlan(Flight x) => x != UserArr && (x.Mgr is "vectored" or "vectored around" || x.Rejoin || x.WentAround || Time - x.LastVector < 240);

    /// <summary>Margin (s) after the first arrival is through the intersection before the second may cross its threshold.</summary>
    private const double CrossingMarginSec = 25;

    /// <summary>
    /// The earliest slot (threshold time) at or after <paramref name="slot"/> that keeps this arrival clear of the arrivals
    /// already planned on runways that depend on its own: on a crossing runway, whoever is second may not cross its
    /// threshold until the first has passed the intersection (or turned off before it), with a margin (3-10-4); on a close
    /// parallel, the wake minimum behind a heavy or B757 as for one runway and a 1.5 nm stagger otherwise (dependent
    /// approaches). Fitted ahead of one where it fits, else after it.
    /// </summary>
    private double DependentSlot(Flight f, double slot, List<(RunwayEnd end, double slot, Flight f)> placed) => DependentSlot(f, slot, placed, out _);

    private double DependentSlot(Flight f, double slot, List<(RunwayEnd end, double slot, Flight f)> placed, out string? after)
    {
        after = null;
        var me = f.Rwy!;
        if (placed.Count == 0) return slot;
        var deps = new List<(double lo, double hi, string who)>();   // forbidden windows for this arrival's threshold time
        var gs = Math.Max(90, f.P.Vapp - WindAt(me.ElevationFt + 1000).Headwind(me.Heading)) / 3600.0;   // nm/s
        foreach (var (oe, ts, o) in placed)
        {
            if (oe.Runway == me.Runway || o == f || double.IsNaN(ts)) continue;
            // One off its plan doesn't hold up one on it (it fits in behind, close in).
            if (OffPlan(o) && !OffPlan(f)) continue;
            if (Crossing(me, oe) is { } x)
            {
                var po = LandingTimeTo(oe, o.P, x.alongB) + CrossingMarginSec;   // it first: this one waits for it to pass
                var pf = LandingTimeTo(me, f.P, x.alongA) + CrossingMarginSec;   // this one first: it must be through before
                deps.Add((ts - pf, ts + po, $"{Who(o)} on the crossing runway {oe.Ident}"));
            }
            else if (CloseParallel(me, oe))
            {
                var stagger = 1.5 / gs;
                var behind = Math.Max(stagger, Cwt.WakeMiles(o.Wake, f.Wake) / gs);
                var ahead = Math.Max(stagger, Cwt.WakeMiles(f.Wake, o.Wake) / gs);
                deps.Add((ts - ahead, ts + behind, $"{Who(o)} on the close parallel {oe.Ident}"));
            }
        }
        if (deps.Count == 0) return slot;
        for (var it = 0; it < 50; it++)
        {
            var hits = deps.Where(w => slot > w.lo && slot < w.hi).ToList();
            if (hits.Count == 0) break;
            var top = hits.MaxBy(w => w.hi);
            slot = top.hi; after = top.who;
        }
        return slot;
        string Who(Flight o) => o == UserArr ? $"{o.Callsign} (you)" : o.Callsign;
    }

    // ------------------------------------------------------------------ main step

    private double _userMovedAt = -1e9;

    public void Step(double dt)
    {
        Time += dt;
        if (UserPos is not null && (!UserOnGround || UserGs > 3)) _userMovedAt = Time;
        if (!HoldRunways && (ArrRwy is null || Time - _lastRunwayCheck > 120)) { SelectRunways(); _lastRunwayCheck = Time; }
        UpdateTaxiFlow();
        foreach (var f in Flights)
        {
            f.Age += dt;
            f.OnRunway = -1;
            var agl = f.AglFt(Apt.ElevationFt);
            if (f.OnGround || agl < 150)
                foreach (var e in Apt.Ends)
                    if (e.Contains(f.Pos, 3)) { f.OnRunway = e.Runway; break; }
        }
        if (Automatic && Level != TrafficLevel.Off && RunwayOnly)
        {
            if (Schedule is { Legs.Count: > 0 } && WeekMinute >= 0) RunNeighbour();
        }
        else if (Automatic && Level != TrafficLevel.Off && Apt.HasGround)
        {
            if (Schedule is { Legs.Count: > 0 } && WeekMinute >= 0) RunSchedule();
            else if (GenericTraffic) Schedule_();
        }
        if (Level == TrafficLevel.Off) foreach (var f in Flights) if (!f.Test) f.Done = true;
        var sig = string.Join(",", ArrRwys.Select(e => e.Ident)) + "|" + string.Join(",", DepRwys.Select(e => e.Ident));
        if (_rwySig.Length > 0 && sig != _rwySig) { try { FlowChanged(); } catch (Exception ex) { Log("Runway change: " + ex.Message); } }
        _rwySig = sig;
        foreach (var f in Flights.ToList())
        {
            if (f.Done) continue;
            try { Advance(f, dt); RecordTrail(f); WatchFlown(f); if (!f.OnGround && f.Checks.Count > 0) CheckRestrictions(f); if (!f.OnGround && f.Fixes.Count > 0) CheckFixes(f); }
            catch (Exception ex) { Log($"{f.Callsign}: {ex.Message}; removed"); f.Done = true; }
        }
        foreach (var f in Flights.Where(f => f.Done && f.Spot is not null)) f.Spot = null;
        if (Time - _lastDepPredict >= 5) { _lastDepPredict = Time; try { PredictDepartures(); } catch { } }
        if (Time - _lastSepWatch >= 2) { _lastSepWatch = Time; try { WatchSeparation(); } catch { } try { SeparateOwnArrivals(); } catch { } }
        // Landing order on each runway, by predicted time, and the arrival manager: each arrival's slot is the later of
        // its own unhindered time and the slot ahead plus the gap needed; the difference is the delay it should lose
        // far out with speed (cruise and descent slower), so it reaches the approach already spaced instead of holding.
        // The user takes part with a generous planned slot (M8): LAIA's arrivals space around it.
        try { UpdateUserArrival(); } catch { UserArr = null; }
        if (Time - _lastTmi >= 60) { _lastTmi = Time; try { TrafficManagement(); } catch { } }
        // Every 30 s, and at once when an arrival it hasn't planned yet appears (before its delay is taken anywhere).
        if (Time - _lastPlan >= 30 || Flights.Any(f => f.Kind == "arrival" && !f.Planned && !f.Done && !f.OnGround && !double.IsNaN(f.EtaNominal)))
        { _lastPlan = Time; try { PlanRunways(); } catch (Exception ex) { Log("Runway planning failed: " + ex.Message); } }
        var sequenced = Flights.Where(f => f.Kind == "arrival" && !f.OnGround && !f.Done && !double.IsNaN(f.EtaSec) && f.Rwy is not null && f.Phase != "go-around").ToList();
        if (UserArr is { } ua) sequenced.Add(ua);
        // Runways that depend on each other — two that cross (7110.65 3-10-4), or close parallels less than 2,500 ft
        // apart (one runway for wake, 5-5-4) — are sequenced together (user, 27 Nov, KPIT: AAL814 landing 32 crossed its
        // threshold 4 s after the user passed over the 28L/32 intersection: each runway had been planned on its own; "crossing
        // runways needs proper approach sequencing as well"). The user's runway is planned first, then the busiest: each
        // arrival on a runway planned later is fitted into the gaps the runways planned before it leave, so speed and the
        // downwind take the stagger from far out, as for any slot.
        var placedAll = new List<(RunwayEnd end, double slot, Flight f)>();
        _userAheadSlot = double.NaN; _userNoneAhead = false;
        foreach (var g in sequenced.GroupBy(f => f.Rwy!.Runway).OrderByDescending(g => UserArr is not null && g.Contains(UserArr)).ThenByDescending(g => g.Count()).ThenBy(g => g.Key))
        {
            var i = 0;
            Flight? prev = null; double prevSlot = 0;
            // Every arrival keeps its place in the landing order from the moment it's sequenced (KORD, 18 Nov: frozen only
            // inside 15 minutes, the order mixed frozen slots with unhindered times and kept changing — arrivals spaced
            // behind aircraft 60 nm behind them, held, vectored and broken off). A new arrival is placed by its own
            // unhindered time among the others' slots; an arrival that can no longer make its slot (its unhindered time
            // is past the next one's) drops back. The user is placed by their predicted time.
            // In the landing sequence only close to the approach (user, 26 Nov: "planes outside a certain range of the
            // approach should not be given a landing sequence, just spacing; sequencing for the approach should only happen
            // close to the assigned approach" — "8,000 ft AGL and less than 40 miles from the approach end of the runway",
            // both together; over high terrain, the lowest its STAR still requires there + 500 ft when that's higher).
            // Further out its place follows its own predicted time (nothing to hold, swap or vector into an order) and
            // it's spaced in its stream (miles-in-trail, by speed); once in, its place is kept as before.
            foreach (var f in g)
            {
                if (f == UserArr || f.InSeq || f.Path is null || f.Rwy is null) continue;
                if (SequencePoint(f) is { } why) { f.InSeq = true; Log($"{f.Callsign}: in the landing sequence for {f.Rwy.Ident} ({why})"); }
            }
            // (The order itself is kept from 60 flying miles in, or once in the sequence if sooner, so the approach can plan
            // its downwinds; further out it simply follows each one's predicted time.)
            foreach (var f in g) if (f == UserArr || double.IsNaN(f.OrderKey) || !f.Frozen || !(f.InSeq || f.Path is null || (f.ThrS - f.S) / Geo2.MPerNm <= 60)) f.OrderKey = f.EtaNominal + (f == UserArr ? 0 : LearnedOrderOffset(f, g.Count()));
            // One coming back round (a break-off, a go-around) goes behind every arrival nearer the runway than it — never
            // in front of one already on its way in, which would then be broken off in turn (KORD Peak, 18 Nov: AAL1974
            // broke off, then AAL2616 and UAL415 behind it, each "200+ s short" behind the one before).
            foreach (var f in g.Where(x => x.Rejoin && x != UserArr && x.Path is not null))
            {
                var d = (f.ThrS - f.S) / Geo2.MPerNm;
                var ahead = g.Where(o => o != f && !o.Rejoin && o.Path is not null && (o.ThrS - o.S) / Geo2.MPerNm < d && !double.IsNaN(o.OrderKey)).Select(o => o.OrderKey).DefaultIfEmpty(double.NaN).Max();
                if (!double.IsNaN(ahead)) f.OrderKey = Math.Max(f.OrderKey, ahead + 1);
                f.Rejoin = false; f.Frozen = true;
            }
            if (UserArr is { } meU && g.Contains(meU)) UserAmongArrivals(meU, g.ToList());
            var ordered = g.OrderBy(f => f.OrderKey).ToList();
            // One that will clearly be first after all (it couldn't lose its delay, or the one ahead was slowed or
            // stretched more than planned): closer to the runway along its route by 2 nm or more, and landing with the
            // full gap before the other at the speeds they're flying now — they swap.
            for (var k = 0; k + 1 < ordered.Count; k++)
            {
                var a = ordered[k]; var b = ordered[k + 1];
                if (a == UserArr || b == UserArr || a.Path is null || b.Path is null || double.IsNaN(a.EtaSec) || double.IsNaN(b.EtaSec)) continue;
                // Not when the one ahead is on a planned detour (delay vectors, an extended downwind): that's the plan.
                var da = (a.ThrS - a.S) / Geo2.MPerNm; var db = (b.ThrS - b.S) / Geo2.MPerNm;
                // …unless the one behind is already established on the final: then the detour has put the one ahead
                // behind it, and the only other way out is to break it off (KORD, 19 Nov: UAL770 vectored off for DAL1390
                // while AAL2740 was on the final 8 nm behind — AAL2740 was broken off "327 s short").
                var bOnFinal = db < 15 && b.Rwy is { } brw && Math.Abs(brw.Offset(b.Pos)) < 0.6 * Geo2.MPerNm && brw.Along(b.Pos) < 0;
                if (!bOnFinal && (Time - a.LastVector < 600 || Time - a.LastTrombone < 60 && !double.IsNaN(a.BaseTurnNm))) continue;
                // …and it also fits behind the one ahead of them both (AAL1974 swapped ahead of AAL2616 and then had to
                // be broken off, 111 s short behind UAL770).
                var before = k > 0 ? ordered[k - 1] : null;
                var fits = before is null || double.IsNaN(before.EtaSec) || b.EtaSec >= before.EtaSec + ThresholdGapNeeded(before, b, forDepartures: false);
                if (fits && db < da - 2 && b.EtaSec + ThresholdGapNeeded(b, a, forDepartures: false) < a.EtaSec)
                {
                    (a.OrderKey, b.OrderKey) = (b.OrderKey, a.OrderKey);
                    ordered[k] = b; ordered[k + 1] = a;
                    Log($"{b.Callsign}: now ahead of {a.Callsign} for {b.Rwy!.Ident} ({db:0} nm to go against {da:0})");
                }
            }
            _timeline[g.Key] = new();
            // A runway for landings and take-offs both (KORD at night, 16 Nov: 10C for everything, arrivals every 107–124 s,
            // so the departures — which need about 165 s between two arrivals — waited 20 minutes and more): the arrival
            // manager plans a departure gap between arrivals for each departure still to go, as a controller spaces the
            // final for them.
            var deps = DepRwys.Count > 0 ? DepRwys : DepRwy is null ? new List<RunwayEnd>() : new() { DepRwy };
            var pending = deps.Any(d => d.Runway == g.Key) ? PendingDepartures(g.Key) : 0;
            foreach (var f in ordered)
            {
                f.Seq = ++i;
                var slot = f.EtaNominal;
                if (prev is not null)
                {
                    var gapNeed = ThresholdGapNeeded(prev, f, forDepartures: false);
                    if (pending > 0 && prev != UserArr && f != UserArr)
                    {
                        var occupancy = Math.Max(RunwayOccupancy(f.Rwy!, prev.P), _rotSeen.GetValueOrDefault(f.Rwy!)) + 10;
                        gapNeed = Math.Max(gapNeed, DepartureGap(occupancy));
                        pending--;
                    }
                    slot = Math.Max(slot, prevSlot + gapNeed + 15);
                    if (f == UserArr) _userAheadSlot = prevSlot + gapNeed + 15;
                }
                else if (f == UserArr) _userNoneAhead = true;
                if (f != UserArr && f.Rwy is not null)
                {
                    var s0 = slot;
                    slot = DependentSlot(f, slot, placedAll, out var after);
                    if (slot - s0 > 20 && after is not null && f.DepNote != after)
                    {
                        f.DepNote = after;
                        Log($"{f.Callsign}: planned to land {slot - s0:0} s later on {f.Rwy.Ident}, after {after}");
                    }
                }
                var outNm = f.Path is null ? 0 : (f.ThrS - f.S) / Geo2.MPerNm;
                // Left out further in too (overload, KATL 21 Nov on a stale ATIS: break-off after break-off close in):
                // 15 minutes late down to 60 nm out, before it's in the sequence near the field, where the only tools
                // left are break-offs.
                if (f != UserArr && !f.Frozen && !f.Test && f.Path is not null && f.BreakOffs == 0 && slot - f.EtaNominal > 900 && outNm > 60)
                {
                    // More than 15 minutes late on arrival, still 100 nm or more out (Peak beyond what the runways take,
                    // KORD 18 Nov: number 36 for 27C with 28 minutes to lose): another runway if one has room, else it's
                    // left out — no 10-minute detours and no holds.
                    f.Delay = slot - f.EtaNominal;
                    var was = f.Rwy;
                    try { BalanceRunway(f); } catch { }
                    if (f.Rwy != was) continue;
                    if (f.Delay > 900)
                    {
                        f.Done = true; _leftOut++;
                        Log($"{f.Callsign}: left out — {f.Delay / 60:0} min of delay for {was!.Ident} at this traffic level (more than the runways take)");
                        continue;
                    }
                }
                _timeline[g.Key].Add((slot, f));
                // The user's actual predicted time (not their held place) for the runways that depend on theirs.
                if (f.Rwy is not null) placedAll.Add((f.Rwy, f == UserArr && !double.IsNaN(f.EtaSec) ? Time + f.EtaSec : slot, f));
                if (f == UserArr) { prev = f; prevSlot = slot; continue; }
                f.Delay = slot - f.EtaNominal;
                if (!f.Frozen && f.Delay >= 60 && !f.Test && !(Planner && ArrRwys.Count >= 2))
                {
                    var was0 = f.Rwy;
                    try { BalanceRunway(f); } catch { }
                    if (f.Rwy != was0) { f.RwyCheckAt = Time; continue; }   // placed on its new runway next pass
                }
                f.Frozen = true; f.FrozenSlot = slot;
                TrackSlot(f, slot);
                if (Time - f.RwyCheckAt >= 30 && !(Planner && ArrRwys.Count >= 2)) { f.RwyCheckAt = Time; try { BalanceRunway(f); } catch { } }
                if (Time - f.SlowAt >= 5) { f.SlowAt = Time; try { AssignSequenceSpeed(f); } catch (Exception ex) { Log($"{f.Callsign}: speed planning failed: {ex.Message}"); } }
                f.Slowed = f.SlowDv > 0;
                prev = f; prevSlot = slot;
            }
            if (UserArr is { } me && ordered.Contains(me)) DescribeUserSequence(ordered);
        }
        try { UpdateNotices(); } catch { }
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
    /// <summary>Minutes an aircraft is at its gate before push-back (user, 24 Nov: 45, was 20), and after arriving (user, 23 Nov).</summary>
    public const int GateBeforeMin = 45, GateAfterMin = 10;
    private int _gateGone; private double _gateLogAt = -1;
    private bool _schedStarted;

    /// <summary>Signed minutes from now until a time of the week (-5040..5040).</summary>
    /// <summary>For tests: the airport's local time (minutes of the day) to use for rules with hours.</summary>
    public int? LocalMinuteOverride { get; set; }
    /// <summary>The airport's local time now (minutes of the day), for AAUP rules with hours ("0700-2300 local").
    /// Real time, like the ATIS: US time zones by longitude, with daylight saving.</summary>
    public int? LocalMinuteNow
    {
        get
        {
            if (LocalMinuteOverride is { } o) return o;
            var lon = Apt.Data.Lon;
            var id = lon > -87.6 ? "America/New_York" : lon > -101 ? "America/Chicago" : lon > -114.5 ? "America/Denver" : lon > -130 ? "America/Los_Angeles" : lon > -150 ? "America/Anchorage" : "Pacific/Honolulu";
            double offMin;
            try { offMin = TimeZoneInfo.FindSystemTimeZoneById(id).GetUtcOffset(DateTime.UtcNow).TotalMinutes; }
            catch { offMin = Math.Round(lon / 15) * 60; }
            // Peak replays the busiest hours now: the airport's rules for those hours apply, not the real clock's
            // (user, MCO: the 17:00 rush replayed after 23:00 local got the night rules — FATHE off 36R/36L).
            var utcMin = Level == TrafficLevel.Peak && (_peakSet || ClockFrom is not null) ? ((SchedNow % 1440) + 1440) % 1440 : DateTime.UtcNow.Hour * 60 + DateTime.UtcNow.Minute;
            return (int)(((utcMin + offMin) % 1440 + 1440) % 1440);
        }
    }

    /// <summary>The airport's local time on the schedule's clock (minutes of the day): the real clock live, the replayed
    /// one at Peak, the test's in tests.</summary>
    public int LocalSchedMinute
    {
        get
        {
            if (LocalMinuteOverride is { } o) return o;
            var lon = Apt.Data.Lon;
            var id = lon > -87.6 ? "America/New_York" : lon > -101 ? "America/Chicago" : lon > -114.5 ? "America/Denver" : lon > -130 ? "America/Los_Angeles" : lon > -150 ? "America/Anchorage" : "Pacific/Honolulu";
            double offMin;
            try { offMin = TimeZoneInfo.FindSystemTimeZoneById(id).GetUtcOffset(DateTime.UtcNow).TotalMinutes; }
            catch { offMin = Math.Round(lon / 15) * 60; }
            var utcMin = WeekMinute >= 0 ? ((SchedNow % 1440) + 1440) % 1440 : DateTime.UtcNow.Hour * 60 + DateTime.UtcNow.Minute;
            return (int)(((utcMin + offMin) % 1440 + 1440) % 1440);
        }
    }

    /// <summary>Between 23:00 and 06:00 at the airport.</summary>
    public bool NightNow => LocalSchedMinute is >= 22 * 60 or < 6 * 60;   // 22:00–06:00 airport time (user, 24 Nov: "change it from 11PM to 10PM")

    /// <summary>How far out arrivals are taken over from the whole sky (nm).</summary>
    /// <summary>How far out arrivals are taken over from the whole sky (user, 19 Nov: "Let's push the bar... 400 —
    /// I rather hit a hardware limit than an arbitrary distance"; was 200).</summary>
    public double HandoverNm { get; set; } = 400;

    /// <summary>An arrival still far out (beyond 60 nm): simulated, but not near enough to count or be drawn.</summary>
    private static bool Far(Flight f) => f.Kind == "arrival" && !f.OnGround && f.Path is not null && (f.ThrS - f.S) / Geo2.MPerNm > 60;

    /// <summary>Where the whole-sky layer has a scheduled arrival right now (null if it isn't airborne or can't be placed).</summary>
    /// <summary>The arrival metering's delay (minutes) for a scheduled arrival, from the whole sky's copy of the leg.</summary>
    public Func<ScheduleLeg, double>? MeterOf { get; set; }

    internal NetPos? NetworkPosition(ScheduleLeg l)
    {
        if (Locate?.Invoke(l.Dep) is not { } d) return null;
        var leg = new NetLeg
        {
            Airline = l.Airline, Operator = l.Operator, Type = l.Type, Reg = l.Reg, Flight = l.Flight, Dep = l.Dep, Arr = Apt.Icao,
            DepMin = l.DepMin, ArrMin = l.ArrMin, DepLat = d.lat, DepLon = d.lon, ArrLat = Apt.Data.Lat, ArrLon = Apt.Data.Lon,
            ArrElev = Apt.ElevationFt, Located = true,
        };
        leg.DistNm = Geo2.DistanceNm(leg.DepLat, leg.DepLon, leg.ArrLat, leg.ArrLon);
        // The arrival metering's delay for this flight (found 19 Nov: the take-over built its own leg with none, so the
        // metering only ever moved the map's dots — arrivals still reached the hand-over bunched as scheduled).
        leg.MeterMin = MeterOf?.Invoke(l) ?? 0;
        var p = NetworkModel.At(leg, SchedNow);
        return p is null ? null : OnRoute(p, l.Type, Key(l, 'A'));
    }

    // ------------------------------------------------------------------ filed routes in the whole sky

    /// <summary>A whole-sky flight's route near this airport (lat/lon points, origin first) and the plan it was made with.</summary>
    private sealed record NetRoute(List<(double lat, double lon)> Pts, double[] Cum, double LengthNm, (RunwayEnd? rwy, (Procedure p, Transition? t)? star)? Plan, string Runways, double MadeAt)
    {
        /// <summary>Index of the first STAR point (arrivals): the filed route's en-route points are before it.</summary>
        public int StarIdx { get; init; } = -1;
    }
    private readonly Dictionary<string, NetRoute> _netRoutes = new();

    /// <summary>
    /// Moves a whole-sky flight to or from this airport onto its filed route: an arrival flies the great circle only
    /// as far as its STAR (with the entry transition chosen for its origin, the same choice the hand-over makes), then
    /// the STAR; a departure flies its SID and exit, then the great circle. The schedule's timing is kept (the same
    /// fraction of the way along); the descent keeps to ~300 ft per nm of route still to go. So when the airport takes
    /// an arrival over ~200 nm out it is already on its STAR or heading for it, as on a filed flight plan.
    /// </summary>
    public NetPos OnRoute(NetPos p, string? type = null, string? key = null)
    {
        var l = p.Leg;
        var arr = l.Arr.Equals(Apt.Icao, StringComparison.OrdinalIgnoreCase);
        var dep = !arr && l.Dep.Equals(Apt.Icao, StringComparison.OrdinalIgnoreCase);
        if (!arr && !dep || l.DistNm < 30) return p;
        try
        {
            var r = RouteFor(l, arr, type ?? l.Type, key ?? $"{(arr ? 'A' : 'D')}|{l.Reg}|{l.Flight}|{l.DepMin}");
            if (r is null || r.LengthNm < 1) return p;
            var frac = Math.Clamp(p.FlownNm / Math.Max(1, l.PathNm), 0, 1);
            var at = frac * r.LengthNm;
            var i = Array.FindIndex(r.Cum, c => c >= at);
            if (i <= 0) i = 1;
            var segLen = r.Cum[i] - r.Cum[i - 1];
            var u = segLen < 1e-6 ? 0 : (at - r.Cum[i - 1]) / segLen;
            var (a, b) = (r.Pts[i - 1], r.Pts[i]);
            var (lat, lon) = NetworkModel.Intermediate(a.lat, a.lon, b.lat, b.lon, u);
            var hdg = Geo2.BearingDeg(lat, lon, b.lat, b.lon);
            // Miles to go: the route's, but no more than the way it will be flown in from here (the hand-over goes direct
            // to the STAR's next fix ahead, so a STAR that loops round left arrivals thousands of feet high — AAL2867
            // 54 nm out at 32,450 ft, every Peak session).
            var rem = Math.Min(r.LengthNm - at, Geo2.DistanceNm(lat, lon, Apt.Data.Lat, Apt.Data.Lon) * 1.3 + 10);
            var alt = p.AltFt; var phase = p.Phase;
            if (arr && phase != "climbing") { alt = Math.Min(p.CruiseFt, l.ArrElev + rem * 300 + 1500); phase = alt < p.CruiseFt - 50 ? "descending" : "cruising"; }
            return p with { Lat = lat, Lon = lon, Heading = hdg, AltFt = Math.Round(alt / 10) * 10, Phase = phase };
        }
        catch { return p; }
    }

    /// <summary>
    /// The filed route's en-route points still ahead of a whole-sky arrival being taken over (airways and fixes up to
    /// its STAR), so it keeps flying them after the hand-over — at 400 nm the hand-over can't just go direct to the STAR
    /// (user, 19 Nov: "Let's push the bar... 400").
    /// </summary>
    private List<V2>? FiledAhead(ScheduleLeg sl, NetPos np)
    {
        try
        {
            var l = np.Leg;
            var r = RouteFor(l, true, sl.Type, Key(sl, 'A'));
            if (r is null || r.StarIdx <= 1 || r.LengthNm < 1) return null;
            var frac = Math.Clamp(np.FlownNm / Math.Max(1, l.PathNm), 0, 1);
            var at = frac * r.LengthNm;
            var res = new List<V2>();
            for (var k = 1; k < r.StarIdx && k < r.Pts.Count; k++)
                if (r.Cum[k] > at + 3) res.Add(Apt.Map.ToXY(r.Pts[k].lat, r.Pts[k].lon));
            return res.Count > 0 ? res : null;
        }
        catch { return null; }
    }

    private NetRoute? RouteFor(NetLeg l, bool arr, string type, string key)
    {
        var rw = string.Join(",", (arr ? ArrRwys : DepRwys).Select(e => e.Ident));
        if (_netRoutes.TryGetValue(key, out var have) && have.Runways == rw && Time - have.MadeAt < 1800) return have;
        if (_netRoutes.Count > 3000) _netRoutes.Clear();
        var other = arr ? (l.DepLat, l.DepLon) : (l.ArrLat, l.ArrLon);
        var otherXY = Apt.Map.ToXY(other.Item1, other.Item2);
        var far = otherXY.Unit * (Math.Min(otherXY.Length, 250 * Geo2.MPerNm));
        List<V2> proc = new();
        (RunwayEnd? rwy, (Procedure p, Transition? t)? star)? plan = null;
        if (arr)
        {
            plan = PlanArrival(arr ? l.Dep : l.Arr, type);
            if (plan.Value.rwy is { } rwy)
            {
                var tmp = new Flight { Id = "", Callsign = "", Airline = "", Type = type, Kind = "arrival", P = Perf.For(type), Other = l.Dep, StarPick = plan.Value.star };
                if (PickStar(tmp, rwy, out var legs) is not null)
                    proc = Apt.LegsToPoints(legs, far, 11000, out _).Select(x => x.P).ToList();
                // Join the STAR where the great circle meets it: skip fixes the next one is nearer the origin than.
                while (proc.Count >= 2 && (proc[1] - otherXY).Length < (proc[0] - otherXY).Length) proc.RemoveAt(0);
                proc.RemoveAll(q => q.Length < 3 * Geo2.MPerNm);
            }
        }
        else
        {
            var deps = DepRwys.Count > 0 ? DepRwys : DepRwy is null ? new() : new List<RunwayEnd> { DepRwy };
            var sid = deps.Count == 0 ? null : ChooseSid(deps, l.Arr);
            var rwy = sid is null ? deps.FirstOrDefault() : deps.FirstOrDefault(e => Serves(sid.Value.p, e)) ?? deps.FirstOrDefault();
            if (rwy is not null)
            {
                var tmp = new Flight { Id = "", Callsign = "", Airline = "", Type = type, Kind = "departure", P = Perf.For(type), Other = l.Arr, Rwy = rwy, SidPick = sid };
                if (PickSid(tmp, rwy, out var legs) is not null && legs.Count > 0)
                    proc = new List<V2> { rwy.FarEnd }.Concat(Apt.LegsToPoints(legs, rwy.FarEnd, rwy.ElevationFt + 400, out _, hybrid: true).Select(x => x.P)).ToList();
                // Leave the SID where the great circle to the destination is the way on.
                while (proc.Count >= 2 && (proc[^2] - otherXY).Length < (proc[^1] - otherXY).Length) proc.RemoveAt(proc.Count - 1);
            }
        }
        // The rest of the filed route (FAA preferred route string, airways and fixes from the FAA's NASR data),
        // between the other airport and the STAR (or after the SID); the great circle when there's none.
        var enroute = EnrouteFor(l, arr, type, plan?.star, proc);
        var pts = new List<(double lat, double lon)>();
        if (arr)
        {
            pts.Add(other);
            pts.AddRange(enroute);
            pts.AddRange(proc.Select(q => Apt.Map.ToLatLon(q)));
            pts.Add((Apt.Data.Lat, Apt.Data.Lon));
        }
        else
        {
            pts.Add((Apt.Data.Lat, Apt.Data.Lon));
            pts.AddRange(proc.Select(q => Apt.Map.ToLatLon(q)));
            pts.AddRange(enroute);
            pts.Add(other);
        }
        var cum = new double[pts.Count];
        for (var k = 1; k < pts.Count; k++) cum[k] = cum[k - 1] + Geo2.DistanceNm(pts[k - 1].lat, pts[k - 1].lon, pts[k].lat, pts[k].lon);
        var r = new NetRoute(pts, cum, cum[^1], plan, rw, Time) { StarIdx = arr ? 1 + enroute.Count : -1 };
        _netRoutes[key] = r;
        return r;
    }

    /// <summary>Routes flown on their filed route string (airways and fixes), for the control page.</summary>
    public int FiledRoutesFlown { get; private set; }

    /// <summary>
    /// The en-route part of a whole-sky flight's filed route: the FAA preferred route between the two airports (the
    /// row that names this STAR, or SID), its fixes and airways placed with the NASR data. Empty when there's no row
    /// or it can't be placed sensibly (then the great circle is flown).
    /// </summary>
    private List<(double lat, double lon)> EnrouteFor(NetLeg l, bool arr, string type, (Procedure p, Transition? t)? star, List<V2> proc)
    {
        var none = new List<(double lat, double lon)>();
        if (NavData is null || Routes is null) return none;
        var jet = Perf.For(type).Family != "turboprop";
        var hdg = ArrRwy?.Heading ?? 0;
        string[] toks;
        if (arr)
        {
            if (star is not { } st) return none;
            var r = Routes.Arrival(l.Dep, Apt.Icao, hdg, jet, tok => AirportRules.Base(tok) == AirportRules.Base(st.p.Name));
            if (r is null) return none;
            var k = Array.FindIndex(r.Value.row.Route, t => AirportRules.Base(t) == AirportRules.Base(st.p.Name));
            if (k < 0) return none;
            toks = r.Value.row.Route[..k];
        }
        else
        {
            var deps = DepRwys.Count > 0 ? DepRwys : DepRwy is null ? new() : new List<RunwayEnd> { DepRwy };
            var sid = deps.Count == 0 ? null : ChooseSid(deps, l.Arr);
            var r = Routes.Departure(Apt.Icao, l.Arr, hdg, jet, tok => sid is { } sd ? AirportRules.Base(tok) == AirportRules.Base(sd.p.Name) : false);
            if (r is null) return none;
            var k = Array.FindIndex(r.Value.row.Route, t => sid is { } sd && AirportRules.Base(t) == AirportRules.Base(sd.p.Name));
            if (k < 0) return none;
            toks = r.Value.row.Route[(k + 1)..];
        }
        var start = arr ? (l.DepLat, l.DepLon) : proc.Count > 0 ? Apt.Map.ToLatLon(proc[^1]) : (Apt.Data.Lat, Apt.Data.Lon);
        var pts = NavData.Expand(toks, start.Item1, start.Item2).Select(x => (x.lat, x.lon)).ToList();
        // Not the airports themselves, nothing already in the procedure part, and only points between the two ends.
        var procLl = proc.Select(q => Apt.Map.ToLatLon(q)).ToList();
        pts = pts.Where(p => Geo2.DistanceNm(p.lat, p.lon, Apt.Data.Lat, Apt.Data.Lon) > 15
                          && Geo2.DistanceNm(p.lat, p.lon, arr ? l.DepLat : l.ArrLat, arr ? l.DepLon : l.ArrLon) > 15
                          && !procLl.Any(q => Geo2.DistanceNm(p.lat, p.lon, q.lat, q.lon) < 1)).ToList();
        if (pts.Count == 0) return none;
        // Sanity: a route more than half as long again as the great circle is a mismatch (a name placed in the wrong
        // part of the country): fly the great circle instead.
        double len = 0; var prev = arr ? (l.DepLat, l.DepLon) : start;
        foreach (var p in pts) { len += Geo2.DistanceNm(prev.Item1, prev.Item2, p.lat, p.lon); prev = p; }
        if (len > 1.5 * l.DistNm + 50) return none;
        FiledRoutesFlown++;
        return pts;
    }

    /// <summary>The arrival plan (runway, STAR) a whole-sky flight's route was made with, so the hand-over keeps it.</summary>
    private (RunwayEnd? rwy, (Procedure p, Transition? t)? star)? RoutePlan(string key) =>
        _netRoutes.TryGetValue(key, out var r) && r.Plan is { } pl && pl.rwy is not null && (ArrRwys.Contains(pl.rwy) || ArrRwy == pl.rwy) ? pl : null;

    /// <summary>
    /// Takes over a scheduled arrival from the whole sky where it is now (position, level): it flies direct to its
    /// STAR, descends from its cruise level at the top of descent, and then the STAR, approach and landing as usual.
    /// </summary>
    internal Flight? SpawnFromNetwork(ScheduleLeg l, NetPos np)
    {
        // Never taken over just off the ground far away (it would be flown on from there at that height).
        if (np.AltFt < 3000 && Geo2.DistanceNm(Apt.Data.Lat, Apt.Data.Lon, np.Lat, np.Lon) > 25) return null;
        var plan = RoutePlan(Key(l, 'A')) ?? PlanArrival(l.Dep, l.Type);
        var f = SpawnArrival(0, false, l, plan, entry: (Apt.Map.ToXY(np.Lat, np.Lon), np.AltFt, np.CruiseFt), via: FiledAhead(l, np));
        // The whole sky flies every leg on its own, so two arrivals can be almost on top of each other when they're
        // taken over (most at the start, when everything within 200 nm appears at once). Far out the arrival manager
        // spaces them with speed; close in it can't, so a clashing one starts on its arrival route one slot further
        // back (5 nm behind the one it clashed with), as if it had been sequenced before it got here.
        // How far apart in track miles (user, 19 Nov): at least 5 on the approach (vectors and final), 7 on the STAR,
        // 10 before it — measured to the same runway, by the farther of the two. And the same distances (straight line,
        // within 1,000 ft) from the arrivals of the other airports LAIA runs, the satellite fields and the main airport
        // ("make sure that includes aircraft to satellite fields as well").
        if (f is not null && plan.rwy is not null)
        {
            var mine = f.ThrS / Geo2.MPerNm;
            var need = InjectGap(f.AppNm, f.StarNm);
            var starNm = f.StarNm;
            bool SameRunway(Flight g)
            {
                var gd = (g.ThrS - g.S) / Geo2.MPerNm;
                return Flights.Any(o => o != g && !o.Done && o.Kind == "arrival" && !o.OnGround && o.Rwy?.Runway == plan.rwy.Runway
                    && (o.Phase == "go-around" ? 12.0 : (o.ThrS - o.S) / Geo2.MPerNm) is var od && Math.Abs(od - gd) < need(Math.Max(od, gd)));
            }
            Foreign? OtherField(Flight g)
            {
                var gd = (g.ThrS - g.S) / Geo2.MPerNm;
                return Others.FirstOrDefault(o => o.Kind == "arrival" && o.Phase != "user" && o.AltFt > 500 && Math.Abs(o.AltFt - g.AltFt) < 1000
                    && (o.Pos - g.Pos).Length < need(gd) * Geo2.MPerNm);
            }
            // A route that loops from the start (KJFK Peak, 24 Nov: AAL1049 and JBU168 on CAMRN5 for 31L turned 437° and
            // 332° in their first 25 nm): started on its own way in at the same distance instead, when that doesn't.
            if (Turned(f, 25) > 300 && !f.Test)
            {
                var turned = Turned(f, 25);
                Flights.Remove(f);
                var alt = SpawnArrival(mine, false, l, plan);
                if (alt is not null && Turned(alt, 25) <= 300) { Log($"{alt.Callsign}: its way in from where it was turns {turned:0}° in the first 25 nm — started on {alt.Route} {mine:0} track nm out instead"); f = alt; }
                else { if (alt is not null) Flights.Remove(alt); Flights.Add(f); }
            }
            var near = OtherField(f);
            // Placed by its landing slot as well (user, 24 Nov: "Sim traffic should be added already sequenced without
            // looping"): where it starts, its predicted landing must fit between the others' with the gap needed, or
            // be late by no more than speed can still take before 20 nm — no delay vectors or loops to fix the start.
            var shortSec = SlotShort(f);
            if (SameRunway(f) || near is not null || shortSec > 0)
            {
                Flights.Remove(f);
                var before = mine > starNm + 0.5;
                var d = mine;
                var why = SameRunway(f) || near is not null ? "" : $"its landing would be {shortSec:0} s more than speed can take behind the traffic for {plan.rwy.Ident}";
                var stepNm = why.Length > 0 ? shortSec / 12 + 1 : 0;   // about 300 kt behind: 12 s a mile
                for (var tries = 0; tries < 8; tries++)
                {
                    d = ArrivalSlot(tries == 0 ? d + stepNm : d + Math.Max(stepNm > 0 ? stepNm : need(d), stepNm > 0 ? 1 : 0), plan.rwy, before ? mine + 80 : Math.Max(60, mine + 40), need);
                    if (d < 0) return null;
                    Flight? g;
                    if (before)
                    {
                        // Still before its STAR: taken over further back on its way in, at the same level.
                        var at = Apt.Map.ToXY(np.Lat, np.Lon);
                        g = SpawnArrival(0, false, l, plan, entry: (at + at.Unit * ((d - mine + 0.3 + tries * 0.5) * Geo2.MPerNm), np.AltFt, np.CruiseFt), via: FiledAhead(l, np));
                    }
                    else g = SpawnArrival(d, false, l, plan);
                    if (g is null) return null;
                    var near2 = OtherField(g);
                    var short2 = near2 is null && !SameRunway(g) ? SlotShort(g) : 0;
                    if (near2 is null && !SameRunway(g) && short2 <= 0)
                    {
                        Log($"{g.Callsign}: taken over from the whole sky, sequenced: starts {(g.ThrS - g.S) / Geo2.MPerNm:0} track nm out (it was {mine:0}, {(why.Length > 0 ? why : $"too close to {(near is not null ? $"{near.Callsign} ({near.Airport})" : "the traffic ahead")}")}), {g.Route} for runway {g.Rwy?.Ident}");
                        return g;
                    }
                    stepNm = short2 > 0 ? short2 / 12 + 1 : 0;
                    near = near2 ?? near;
                    d = Math.Max(d, (g.ThrS - g.S) / Geo2.MPerNm);
                    Flights.Remove(g);
                }
                return null;
            }
        }
        if (f is not null) Log($"{f.Callsign}: taken over from the whole sky {Geo2.DistanceNm(Apt.Data.Lat, Apt.Data.Lon, np.Lat, np.Lon):0} nm out at {np.AltFt:0} ft ({np.Phase}), {f.Route} for runway {f.Rwy?.Ident}");
        return f;
    }

    private double Until(int weekMin) => ((weekMin - SchedNow) % 10080 + 10080 + 5040) % 10080 - 5040;

    // ------------------------------------------------------------------ peak-hour test setting
    private double _peakOffset;
    private bool _peakSet, _wasPeak;
    private double _peakStart, _peakLen = 180;
    /// <summary>What the peak test is replaying, for the control page (null when not in use).</summary>
    public string? PeakInfo { get; private set; }

    /// <summary>The schedule's clock: the real time, or in the peak test the replayed busiest hours.</summary>
    /// <summary>The schedule clock (minute of the week), shifted during the peak test.</summary>
    public double ScheduleClock => SchedNow;
    private double SchedNow => ClockFrom?.Invoke() ?? ((WeekMinute + _peakOffset) % 10080 + 10080) % 10080;

    /// <summary>
    /// A neighbouring field (JFK and EWR around LGA, SFB around MCO): its traffic only appears on its runways —
    /// arrivals land and disappear once slowed, departures appear lined up and take off — no gates or taxiing.
    /// </summary>
    public bool RunwayOnly { get; init; }
    /// <summary>The schedule clock to follow (a neighbour follows the main airport's, so the peak test lines up).</summary>
    public Func<double>? ClockFrom { get; set; }
    public TrafficWorld? ClockOwner { get; set; }
    /// <summary>STAR names also published at a neighbouring field (satellites): only used when nothing else fits.</summary>
    public HashSet<string> NeighbourStars { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
    private bool Keep(ScheduleLeg l) => KeepAirframe(l.Reg.Length > 0 ? l.Reg : l.Callsign);

    private bool KeepAirframe(string key) => KeepAtLevel(Level, key);

    /// <summary>
    /// Whether an airframe flies at this level: Light about a third, Medium about two thirds, Busy and up every one.
    /// Whole airframes are kept or left out (so an aircraft that lands also leaves again), the same ones every time —
    /// at the airports and, since 15 Nov, between them (user: "should the traffic level also thin the traffic between
    /// airports?" — "Yes").
    /// </summary>
    public static bool KeepAtLevel(TrafficLevel level, string key)
    {
        // The share of the real schedule (user, 15 Nov): Light a quarter, Medium half, Busy three quarters, Real world
        // and Peak all of it.
        var pct = level switch { TrafficLevel.Off => 0, TrafficLevel.Light => 25, TrafficLevel.Medium => 50, TrafficLevel.Busy => 75, _ => 100 };
        uint h = 2166136261;
        foreach (var c in key) h = (h ^ c) * 16777619;
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
    private readonly Dictionary<string, double> _retryAt = new();

    /// <summary>The injection gap (track nm) at a distance to go: 5 on the approach, 7 on the STAR, 10 before it.</summary>
    private static Func<double, double> InjectGap(double appNm, double starNm)
    {
        var app = double.IsNaN(appNm) || appNm <= 0 ? 20 : appNm;
        var st = double.IsNaN(starNm) || starNm < app ? Math.Max(app, 60) : starNm;
        return x => x <= app + 0.5 ? 5 : x <= st + 0.5 ? 7 : 10;
    }

    private double ArrivalSlot(double wantNm, RunwayEnd? rwy, double maxNm = 50, Func<double, double>? need = null)
    {
        need ??= InjectGap(double.NaN, double.NaN);
        if (ArrRwy is null) SelectRunways(true);
        rwy ??= ArrRwy;
        var taken = Flights.Where(o => !o.Done && o.Kind == "arrival" && !o.OnGround && o.Rwy is not null && rwy is not null && o.Rwy.Runway == rwy.Runway)
            .Select(o => o.Phase == "go-around" ? 12.0 : (o.ThrS - o.S) / Geo2.MPerNm).OrderBy(x => x).ToList();
        var d = wantNm;
        for (var tries = 0; tries < 60; tries++)
        {
            var clash = taken.FirstOrDefault(o => Math.Abs(o - d) < need(Math.Max(o, d)), double.NaN);
            if (double.IsNaN(clash)) return d <= maxNm ? d : -1;
            d = clash + need(clash + 5);
        }
        return -1;
    }

    /// <summary>
    /// A neighbouring field's schedule, runway only: departures appear lined up about 12 minutes after their gate time
    /// (when the real one would be taking off) and climb out into the whole sky; arrivals are taken over from the whole
    /// sky within 60 nm and land, disappearing once slowed on the runway.
    /// </summary>
    private int _seenReplays;
    private double _capLogged = -1e9;
    /// <summary>How many times the Peak test has started over (neighbouring fields replay with it).</summary>
    public int PeakReplays { get; private set; }

    private void RunNeighbour()
    {
        var legs = Schedule!.Legs;
        var me = Apt.Icao;
        // When the main airport's Peak test replays its busiest hours, so does this field (22 Nov: JFK and Newark kept
        // the list of flights they had flown, so every replay skipped them — Newark's landings went 20 an hour, 6, 4, 0).
        if (ClockOwner is { } co && co.PeakReplays != _seenReplays) { _seenReplays = co.PeakReplays; _done.Clear(); }
        var active = Flights.Count(f => !f.Done);
        // No count of aircraft limits a level (user rule: a level is a share of the field's schedule); only the runaway
        // guard (JFK at Peak had been capped at 16 at once, arrivals and departures together — taken over up to 50
        // minutes out, its inbound traffic alone filled that).
        var (_, cap, _, _) = Budget;
        foreach (var l in legs)
        {
            if (active >= cap)
            {
                if (Time - _capLogged > 600) { _capLogged = Time; Log($"{me}: {active} aircraft at once — the runaway guard; later flights wait"); }
                break;
            }
            if (l.Dep == me && Keep(l))
            {
                var u = Until(l.DepMin);
                if (u > -12 || u < -16 || _done.Contains(Key(l, 'D'))) continue;
                if (CallsignBusy(l.Callsign)) continue;
                if (l.Reg.Length > 0 && Flights.Any(f => !f.Done && f.Reg == l.Reg)) continue;
                if (SpawnLinedUp(l) is not null) { _done.Add(Key(l, 'D')); active++; Movements++; }
            }
            else if (l.Arr == me && Keep(l))
            {
                var u = Until(l.ArrMin);
                if (u > 50 || u < 1 || _done.Contains(Key(l, 'A'))) continue;
                if (CallsignBusy(l.Callsign)) continue;
                if (l.Reg.Length > 0 && Flights.Any(f => !f.Done && f.Reg == l.Reg)) continue;
                var np = NetworkPosition(l);
                if (np is null) continue;
                var dist = Geo2.DistanceNm(Apt.Data.Lat, Apt.Data.Lon, np.Lat, np.Lon);
                // Taken over as far out as the main airport's arrivals (user, 13 Nov, DAL3043 to MLB: taken over only
                // 60 nm from MLB — by then 45 nm north of MCO, still a whole-sky dot: not drawn, not on its STAR, no
                // restrictions). From the hand-over it flies its STAR, restrictions and approach like any arrival.
                if (dist > HandoverNm) continue;
                // Still climbing out of its own field: later, as at the main airport (16 Nov, MDW: SWA1696 taken over
                // 119 nm out at 30 ft, just off the ground, and flown there at 30 ft until it went around 12 nm out).
                // Far out (150 nm+), until it's at its cruise level (20 Nov, KATL: SWA3224 taken over 342 nm out at 9,840 ft
                // still climbing, flew the rest level at 9,840 and crossed STRDR "at or above 24,000" 14,000 ft low).
                // Beyond 100 nm not while it's still climbing at all (21 Nov, KORD: AMZ3838 taken over exactly 150 nm out at
                // 29,420 ft climbing — the "beyond 150" rule never applied to short flights, picked up right at 150).
                if (dist > 25 && np.Phase == "climbing" && (dist > 100 || np.AltFt < Math.Min(10000, np.CruiseFt) - 200)) continue;
                _done.Add(Key(l, 'A'));
                if (dist > 12 && SpawnFromNetwork(l, np) is not null) { active++; Movements++; }
            }
        }
    }

    /// <summary>A neighbour's departure, lined up on the runway for its SID's side (runway-only fields).</summary>
    private Flight? SpawnLinedUp(ScheduleLeg l)
    {
        if (DepRwy is null) SelectRunways(true);
        if (DepRwy is null) return null;
        var f = new Flight
        {
            Id = NewId(), Callsign = l.Callsign, Airline = l.Operator, Type = l.Type, Other = l.Arr, Kind = "departure", P = Perf.For(l.Type),
            Reg = l.Reg, Title = l.Title, Brand = l.Airline,
        };
        f.Rwy = DepartureRunwayFor(f);
        var rwy = f.Rwy;
        if (rwy is null) return null;
        // Only one at a time on the runway; the next one appears when it's free.
        if (Flights.Any(o => !o.Done && o.Rwy?.Runway == rwy.Runway && o.OnGround) || ArrivalEta(rwy.Runway) < 60) return null;
        f.Pos = rwy.Threshold + rwy.Dir * 60;
        f.Heading = rwy.Heading;
        f.AltFt = rwy.ElevationFt;
        f.OnGround = true;
        f.Timer = 10;
        f.SetPhase("lined up", Time);
        f.Lights = Nav | Beacon | Logo | Strobes | Landing | Taxi;
        Flights.Add(f);
        return f;
    }

    private void RunSchedule()
    {
        var (maxObjects, maxActive, _, _) = Budget;
        var legs = Schedule!.Legs;
        var me = Apt.Icao;
        if (Level == TrafficLevel.Peak)
        {
            if (!_peakSet) { PlanPeak(legs); PeakReplays++; _schedStarted = false; foreach (var f in Flights.Where(f => f.Kind == "parked" && f.Phase == "parked" && f.Reg.Length > 0)) f.Done = true; }
            else if (((SchedNow - _peakStart) % 10080 + 10080) % 10080 >= _peakLen)
            {
                // The busiest hours are over: replay them (each flight may run again, but never one that's still around).
                _peakOffset -= _peakLen;
                _done.Clear();
                PeakReplays++;
                Log($"{me}: peak test: replaying the busiest hours again");
            }
        }
        if (_levelChanged)
        {
            // A new level while traffic runs (user, 15 Nov: "when I click Medium or Real world nothing seems to happen"
            // — the level only changed which new flights started, so Busy → Medium or Busy → Real world showed nothing
            // for many minutes). Fewer: the aircraft the new level leaves out go now — parked ones and arrivals still far
            // out; anything taxiing, departing or near the airport carries on. More: the gates and the arrivals on their
            // way in are filled again for the new level, as at the start.
            _levelChanged = false;
            if (_schedStarted)
            {
                var gone = 0;
                foreach (var f in Flights)
                {
                    if (f.Done || f.Test || f.Reg.Length == 0 && f.Callsign.Length == 0) continue;
                    if (KeepAirframe(f.Reg.Length > 0 ? f.Reg : f.Callsign)) continue;
                    if ((f.Kind == "parked" && f.Phase == "parked") || (f.Kind == "arrival" && Far(f))) { f.Done = true; gone++; }
                }
                _schedStarted = false;
                Log($"{me}: traffic level now {Level}: {gone} aircraft the new level leaves out removed; the gates and arrivals refilled for it");
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
            // Aircraft at the gates now: only those pushing back in the next 20 minutes (user, 23 Nov: "Scheduled traffic
            // should only spawn at a gate 20 mins before push back and 10 minutes after arriving at a gate. No other gate
            // fillers"). An airframe that lands first and then leaves isn't put at a gate: it comes in on its flight.
            var parked = Flights.Count(f => f.Kind == "parked");
            var upcoming = new List<ScheduleLeg>();
            foreach (var g in deps.Where(l => Until(l.DepMin) is > 3 and <= GateBeforeMin).GroupBy(l => l.Reg.Length > 0 ? l.Reg : l.Callsign))
            {
                var next = g.OrderBy(l => Until(l.DepMin)).First();
                var untilDep = Until(next.DepMin);
                if (next.Reg.Length > 0 && arrs.Any(a => a.Reg == g.Key && Until(a.ArrMin) is >= -2 && Until(a.ArrMin) < untilDep)) continue;
                upcoming.Add(next);
            }
            upcoming = upcoming.OrderBy(l => Until(l.DepMin)).ToList();
            // Departures whose time passed more than 3 minutes ago have gone: not all started at once now (KORD, 15 Nov:
            // a dozen boarding together the moment the level was picked, and a 15-minute queue from the start).
            foreach (var l in deps.Where(l => Until(l.DepMin) < -3)) _done.Add(Key(l, 'D'));
            var room = Math.Max(0, maxObjects - maxActive - parked);
            var placed = 0;
            foreach (var l in upcoming)
            {
                if (placed >= room) break;
                if (Flights.Any(f => f.Reg == l.Reg && f.Reg.Length > 0)) continue;
                if (CallsignBusy(l.Callsign)) continue;
                var perf = Perf.For(l.Type);
                var gate = FindGate(perf, l.Operator, l.Airline, l.Callsign);
                if (gate is null) continue;
                try { SpawnParked(gate, l); placed++; } catch (Exception ex) { Log($"Couldn't park {l.Callsign}: {ex.Message}"); }
            }
            // Arrivals already on their way in.
            foreach (var l in arrs.Where(l => Until(l.ArrMin) is >= 1 and <= 13).OrderBy(l => Until(l.ArrMin)))
            {
                if (CallsignBusy(l.Callsign)) continue;
                if (l.Reg.Length > 0 && Flights.Any(f => !f.Done && f.Reg == l.Reg)) continue;
                // Spread out along the approach, clear of every arrival already flying, rather than on top of one.
                var plan = PlanArrival(l.Dep, l.Type);
                var d = ArrivalSlot(Math.Clamp(Until(l.ArrMin) * 3.2, 7, 40), plan.rwy);
                if (d < 0) break;
                _done.Add(Key(l, 'A'));
                SpawnArrival(d, false, l, plan);
            }
            Log($"{me}: following the real {Schedule.Source} schedule ({deps.Count} departures and {arrs.Count} arrivals a week at {Level}; {(maxObjects >= 400 ? "no cap" : $"up to {maxObjects} aircraft, {maxActive} moving, airport size x{SizeFactor:0.0}")}); {placed} aircraft at the gates pushing back in the next {GateBeforeMin} minutes");
        }
        // Arrivals still far out (handed over from the whole sky) don't count against the traffic level: only what's
        // near the airport does (and only that is ever drawn in the sim).
        // Moving aircraft: not those still boarding at the gate (held for their slot, or before push-back) — they're
        // parked, and counting them let a queue of held departures stop every new flight (KORD, 15 Nov: 29 flights
        // "skipped (traffic limit)" in 20 minutes).
        var active = Flights.Count(f => f.Kind != "parked" && !f.Test && !Far(f) && !(f.Kind == "departure" && f.Phase == "boarding"));
        var objects = Flights.Count(f => !Far(f));
        // At the gate only 20 minutes before push-back and 10 minutes after arriving (user, 23 Nov): a departure's aircraft
        // appears at its gate 20 minutes before its time; an aircraft that has landed here stays 10 minutes at its gate,
        // longer only when its next flight pushes back within 20 minutes (a quick turn), then it's taken away.
        foreach (var l in deps)
        {
            var u = Until(l.DepMin);
            if (u <= 3 || u > GateBeforeMin) continue;
            if (_done.Contains(Key(l, 'D')) || CallsignBusy(l.Callsign)) continue;
            if (l.Reg.Length > 0 && Flights.Any(f => !f.Done && f.Reg == l.Reg)) continue;   // still flying in, or already there
            if (l.Reg.Length == 0 && Flights.Any(f => !f.Done && f.Kind == "parked" && f.Callsign == l.Callsign)) continue;
            var gate = FindGate(Perf.For(l.Type), l.Operator, l.Airline, l.Callsign);
            if (gate is null) continue;
            try { var pf = SpawnParked(gate, l); pf.Callsign = l.Callsign; } catch (Exception ex) { Log($"Couldn't park {l.Callsign}: {ex.Message}"); }
        }
        foreach (var f in Flights)
        {
            if (f.Done || f.Test || f.Kind != "parked" || f.Phase != "parked" || f == UserArr) continue;
            if (!double.IsNaN(f.ArrivedAt) && Time - f.ArrivedAt < GateAfterMin * 60) continue;
            // Parked for the night (landed after 23:00 local, user 24 Nov: "after 11PM local time of the airport change
            // arriving aircraft to not despawn"): it stays for its next flight; with none in the next 12 hours, until 06:00.
            var within = f.Overnight ? 720 : GateBeforeMin;
            var next = deps.Any(l => !_done.Contains(Key(l, 'D')) && Until(l.DepMin) is var ud && ud > -15 && ud <= within
                && (f.Reg.Length > 0 ? l.Reg == f.Reg : l.Callsign == f.Callsign));
            if (next || (f.Overnight && NightNow)) continue;
            f.Done = true; _gateGone++;
        }
        if (Time - _gateLogAt >= 900 && (_gateGone > 0 || _gateLogAt < 0))
        {
            Log($"{me}: gates — {Flights.Count(f => !f.Done && f.Kind == "parked")} aircraft at the gates (only from {GateBeforeMin} min before push-back, and {GateAfterMin} min after arriving); {_gateGone} taken away after arriving in the last 15 minutes");
            _gateGone = 0; _gateLogAt = Time;
        }
        // Departures: board a few minutes before, push back on time.
        foreach (var l in deps)
        {
            var u = Until(l.DepMin);
            if (u > 8 || u < -15) continue;   // boarding from 8 minutes before (it may push up to 5 early)
            if (_done.Contains(Key(l, 'D'))) continue;
            if (CallsignBusy(l.Callsign)) continue;   // the same flight number still flying: this one waits
            _done.Add(Key(l, 'D'));
            if (active >= maxActive && !UnderShare("departure", maxActive)) { Log($"{l.Callsign}: skipped (traffic limit)"); continue; }
            var f = Flights.FirstOrDefault(x => !x.Done && (l.Reg.Length > 0 ? x.Reg == l.Reg : x.Callsign == l.Callsign && double.IsNaN(x.ArrivedAt))
                && x.Kind == "parked" && x.Phase == "parked" && x.Spot is not null);
            if (f is null)
            {
                var gate = FindGate(Perf.For(l.Type), l.Operator, l.Airline, l.Callsign);
                if (gate is null) continue;
                f = SpawnParked(gate, l);
            }
            if (StartDeparture(f, Math.Max(20, u * 60), l) is not null) { active++; Movements++; }
        }
        // Arrivals: handed over from the whole sky about 200 nm out, where the schedule has them flying; if their
        // origin can't be placed, they appear about 40 track miles out so they land on time.
        // Closest to the airport first (user, 19 Nov: "traffic should be injected on the ground, then on approach, then on
        // the STAR, then at cruise, in the order of closest to the airport and work its way out" — two were injected into
        // the middle of an earlier wave): when many appear at once (an airport prepared, a level or Peak change), each is
        // placed behind the ones already there, never in front of them.
        var npAt = new Dictionary<ScheduleLeg, NetPos?>();
        double UntilArr(ScheduleLeg l) => Until(l.ArrMin) + (MeterOf?.Invoke(l) ?? 0);   // later by its metering
        var arrOrder = arrs.Where(l => UntilArr(l) is var u0 && u0 <= 100 && u0 >= 1)
            .Select(l => { var np0 = NetworkPosition(l); npAt[l] = np0; return (l, d: np0 is null ? double.MaxValue : Geo2.DistanceNm(Apt.Data.Lat, Apt.Data.Lon, np0.Lat, np0.Lon)); })
            .OrderBy(x => x.d).Select(x => x.l).ToList();
        NetPos? NetPosOf(ScheduleLeg l) => npAt.TryGetValue(l, out var v) ? v : NetworkPosition(l);
        foreach (var l in arrOrder)
        {
            var u = UntilArr(l);
            if (u > 75 || u < 1) continue;
            if (l.Reg.Length > 0 && Flights.Any(f => !f.Done && f.Reg == l.Reg)) continue;   // that airframe is still around
            if (_done.Contains(Key(l, 'A'))) continue;
            if (CallsignBusy(l.Callsign)) continue;
            if (u > 13 || NetPosOf(l) is not null)
            {
                var np = NetPosOf(l);
                if (np is null) continue;                                   // not airborne yet, or can't be placed: later
                var dist = Geo2.DistanceNm(Apt.Data.Lat, Apt.Data.Lon, np.Lat, np.Lon);
                if (dist > HandoverNm) continue;                             // still in the whole sky
                // A short flight from a field inside the hand-over ring is still climbing out there: wait until it
                // has reached 10,000 ft (or its cruise level, if lower) — ATC here doesn't own its take-off.
                // Far out (150 nm+), until it's at its cruise level (20 Nov, KATL: SWA3224 taken over 342 nm out at 9,840 ft
                // still climbing, flew the rest level at 9,840 and crossed STRDR "at or above 24,000" 14,000 ft low).
                // Beyond 100 nm not while it's still climbing at all (21 Nov, KORD: AMZ3838 taken over exactly 150 nm out at
                // 29,420 ft climbing — the "beyond 150" rule never applied to short flights, picked up right at 150).
                if (dist > 25 && np.Phase == "climbing" && (dist > 100 || np.AltFt < Math.Min(10000, np.CruiseFt) - 200)) continue;
                if (dist > 25)
                {
                    if (_retryAt.TryGetValue(Key(l, 'A'), out var ra) && Time < ra) continue;
                    if (SpawnFromNetwork(l, np) is not null) { _done.Add(Key(l, 'A')); Movements++; }
                    else _retryAt[Key(l, 'A')] = Time + 20;     // no slot yet: try again shortly
                    continue;
                }
            }
            if (u > 13) continue;
            if ((active >= maxActive && !UnderShare("arrival", maxActive)) || objects >= maxObjects) { _done.Add(Key(l, 'A')); Log($"{l.Callsign}: skipped (traffic limit)"); continue; }
            // Start clear of every arrival already flying (a little later than scheduled if needed); if there's no
            // room yet, try again on the next step.
            var plan = PlanArrival(l.Dep, l.Type);
            var d = ArrivalSlot(Math.Clamp(u * 3.2, 7, 40), plan.rwy);
            if (d < 0) continue;
            _done.Add(Key(l, 'A'));
            if (SpawnArrival(d, false, l, plan) is not null) { active++; Movements++; }
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

    /// <summary>
    /// Departures and arrivals share the moving-aircraft limit: either kind still under half of it may start even when
    /// the limit is reached (KATL at Medium, 15 Nov: arrivals filled the 14 moving slots, so in 50 minutes 27 aircraft
    /// landed and 6 took off, 26 departures "skipped (traffic limit)").
    /// </summary>
    private bool UnderShare(string kind, int maxActive) =>
        Flights.Count(f => f.Kind == kind && !f.Test && !f.Done && !Far(f) && !(kind == "departure" && f.Phase == "boarding")) < (maxActive + 1) / 2;

    private IEnumerable<AirportModel.ParkingSpot> UsableSpots()
    {
        var gates = Apt.Spots.Where(s => s.IsGate).ToList();
        return gates.Count >= 3 ? gates : Apt.Spots.Where(s => s.RadiusM >= 14 && s.Type is not (1 or 2 or 11 or 12 or 13));
    }

    private bool SpotFree(AirportModel.ParkingSpot s) =>
        !Flights.Any(f => !f.Done && f.Spot is not null && (f.Spot == s || s.Overlaps.Contains(f.Spot))) &&
        // Not next to an aircraft bigger than its own stand (a freighter on a ramp stand): its wings cover the neighbours.
        !Flights.Any(f => !f.Done && f.Spot is { } o && o != s && o.RadiusM > 0 && f.P.SpanM / 2 > o.RadiusM + 2 && (o.P - s.P).Length < f.P.SpanM / 2 + Math.Max(s.RadiusM, 10)) &&
        !(UserPos is { } u && (u - s.P).Length < s.RadiusM + 25);

    /// <summary>All-cargo airlines (by ICAO code): they park on cargo ramps, not at passenger gates.</summary>
    private static readonly HashSet<string> CargoAirlines = new(StringComparer.OrdinalIgnoreCase)
    {
        "FDX", "UPS", "GTI", "ABX", "ATN", "CKS", "KYE", "PAC", "CLX", "GEC", "BOX", "AJT", "MPH", "NCA", "LCO", "LTC", "CKK", "SQC",
        "ICV", "CAO", "ABW", "QTR-C", "WGN", "SRR", "CWC", "MXY", "FTA", "AMF", "BCS", "DHK", "DHL", "DAE", "UCA", "SWQ", "TPA", "KFS", "RLB",
        "CJT", "KFA", "SOO", "NCR",
    };
    public static bool IsCargo(string? airline) => airline is { Length: > 0 } a && CargoAirlines.Contains(a);

    /// <summary>
    /// Freighters are left out of the traffic for now (user, 14 Nov: "for now yes, we'll add them at a later date"):
    /// passenger jets only, like the real tracks LAIA learns from. The cargo-ramp parking logic stays for when they return.
    /// </summary>
    public static bool PassengerOnly { get; set; } = true;
    public static bool LeaveOut(string? airline, string? op) => PassengerOnly && (IsCargo(airline) || IsCargo(op));

    private AirportModel.ParkingSpot? FindGate(Perf p, string airline, string? alsoAirline = null, string? callsign = null)
    {
        bool Fits(AirportModel.ParkingSpot s) => SpotFree(s) && s.Size >= p.GateSize && (s.RadiusM <= 0 || s.RadiusM >= p.SpanM / 2 * 0.85);
        // Freighters: the scenery's cargo ramps first, then other ramp stands no airline uses (not GA tie-downs),
        // then a passenger gate only if nothing else fits (the user saw a UPS 757 taxi to a passenger gate at MCO).
        if (IsCargo(airline) || IsCargo(alsoAirline))
        {
            // Ramp stands are drawn small in most sceneries (MCO's cargo ramps 15–17 m, medium ramps 12–18 m, too small
            // on paper for a 757): a freighter may use any cargo, large or medium ramp stand no airline uses, as long
            // as no aircraft is parked within its half-span (user: "allow them to park at medium ramps for cargo").
            bool Clear(AirportModel.ParkingSpot s) => SpotFree(s) && s.Airlines.Count == 0 &&
                !Flights.Any(o => !o.Done && o.Spot is { } os && os != s && (os.P - s.P).Length < p.SpanM / 2 + Math.Max(os.RadiusM, o.P.SpanM / 2) - 4);
            foreach (var types in new[] { new[] { 5, 6 }, new[] { 4, 14 }, new[] { 3 } })
            {
                var cargo = Apt.Spots.Where(s => types.Contains(s.Type) && Clear(s)).ToList();
                if (cargo.Count > 0) return cargo[Rng.Next(cargo.Count)];
            }
        }
        var free = UsableSpots().Where(Fits).ToList();
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
        // Not next to an aircraft that is pushing back or about to (it would meet the inbound head-on in the lane),
        // when another suitable stand is free.
        var calm = pool.Where(s => !Flights.Any(o => !o.Done && (o.Pos - s.P).Length < 130 &&
            (o.Phase is "pushback" or "engine start" || (o.Phase == "boarding" && o.Timer < 150) || (o.Phase == "taxi" && NearStands(o.Pos, 60))))).ToList();
        if (calm.Count > 0) pool = calm;
        // The smallest stand that fits, so the big ones stay free for the big aircraft.
        var minSize = pool.Min(s => s.Size);
        pool = pool.Where(s => s.Size == minSize).ToList();
        return pool[Rng.Next(pool.Count)];
    }

    /// <summary>Breadcrumbs of where each flight has been: every 2 s on the ground, every 5 s in the air, the last ~40 minutes.</summary>
    /// <summary>Every loop or unrealistic turn flown in this process (all airports) — for the tests and the scoreboard.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> AllUnrealistic = new();
    /// <summary>Loops and unrealistic turns flown at this airport.</summary>
    public int UnrealisticCount { get; private set; }

    /// <summary>
    /// The flown-path watch (user, 28 Nov: "Even one loop or unrealistic turn is a failure. That's the goal."). Every AI
    /// aircraft in the air, from what it actually flew (not the route it was given), every 5 s:
    /// - a loop: turning through 330° or more the same way within 15 minutes (no holds are ever given, and no real way
    ///   in — downwind, base, final — turns more than about 270° one way);
    /// - crossing its own track within 2,000 ft of the height it was at before;
    /// - a turn sharper than an airliner flies: more than half again the rate of a 30° bank at its speed;
    /// - an intercept onto its final steeper than 45° (7110.65 5-9-2: 30°, 20° near the approach gate);
    /// - turning off its final once established on it (other than a go-around or a break-off).
    /// Each is logged once per flight as "UNREALISTIC" with where it was. A go-around or a break-off starts the watch
    /// afresh (a circuit back round is what it is: those are counted on their own).
    /// </summary>
    private void WatchFlown(Flight f)
    {
        if (f.OnGround || f.Done || f.Kind is not ("arrival" or "departure") || f.AglFt(Apt.ElevationFt) < 300) { if (f.OnGround) { f.Flown.Clear(); f.FlownOnFinal = false; } return; }
        if (f.Flown.Count > 0 && Time - f.Flown[^1].t < 5) return;
        if (f.Flown.Count > 0 && (f.WentAround && f.FlownFaults.Add("_reset_ga") || f.BreakOffs > 0 && f.FlownFaults.Add($"_reset_bo{f.BreakOffs}")))
        { f.Flown.Clear(); f.FlownOnFinal = false; }
        var nm = Geo2.MPerNm;
        var cur = (t: Time, p: f.Pos, hdg: f.Heading, alt: f.AltFt);
        var where = (f.Rwy is { } wr ? $"{(f.Pos - wr.Threshold).Length / nm:0} nm from {wr.Ident}" : $"{f.Pos.Length / nm:0} nm from {Apt.Icao}") + $" at {f.AltFt:N0} ft";
        void Fault(string kind, string what)
        {
            if (!f.FlownFaults.Add(kind)) return;
            UnrealisticCount++;
            var line = $"{f.Callsign}: UNREALISTIC — {what} ({where})";
            Log(line);
            var before = _recentLog.Where(l => l.Contains(f.Callsign + ":") && !l.Contains("UNREALISTIC")).TakeLast(6);
            AllUnrealistic.Enqueue($"{Apt.Icao} {Time,6:0} {line}\n        route: {f.Route}\n        " + string.Join("\n        ", before));
        }
        if (f.Flown.Count > 0)
        {
            var last = f.Flown[^1];
            var dh = Geo2.Diff(last.hdg, cur.hdg);
            var dt = cur.t - last.t;
            // Too sharp for an airliner: half again a 30° bank's rate at its true airspeed.
            var v = Math.Max(60, Wind.Tas(Math.Max(f.Ias, 120), f.AltFt)) * Kt;
            var maxRate = G * Math.Tan(30 * Math.PI / 180) / v * 180 / Math.PI;
            if (Math.Abs(dh) > 10 && Math.Abs(dh) / dt > 1.5 * maxRate)
                Fault("sharp", $"turned {Math.Abs(dh):0}° in {dt:0} s at {f.Ias:0} kt — {Math.Abs(dh) / dt:0.0}°/s, an airliner turns about {maxRate:0.0}°/s");
            // A loop: 330° the same way within 15 minutes.
            double sum = 0;
            for (var i = f.Flown.Count - 1; i > 0 && cur.t - f.Flown[i].t < 900; i--) sum += Geo2.Diff(f.Flown[i - 1].hdg, f.Flown[i].hdg);
            sum += dh;
            // After a go-around or a break-off the way back round to the final is one circuit (about 360°): more than that is a loop.
            if (Math.Abs(sum) >= (f.WentAround || f.BreakOffs > 0 ? 420 : 330)) Fault("loop", $"a loop: turned {Math.Abs(sum):0}° {(sum > 0 ? "right" : "left")} within 15 minutes");
            // Crossing its own track (segments a minute or more old, within 2,000 ft).
            for (var i = 1; i < f.Flown.Count - 12; i++)
            {
                var a = f.Flown[i - 1]; var b = f.Flown[i];
                if (Math.Abs(b.alt - cur.alt) > 2000) continue;
                if (SegmentsCross(a.p, b.p, last.p, cur.p)) { Fault("crosses", $"crossed its own track from {(cur.t - b.t) / 60:0} minutes before, {Math.Abs(b.alt - cur.alt):0} ft from the height it was at then"); break; }
            }
            if (f.Kind == "arrival" && f.Rwy is { } r)
            {
                var along = r.Along(f.Pos) / nm; var off = Math.Abs(r.Offset(f.Pos)) / nm;
                var hd = Math.Abs(Geo2.Diff(r.Heading, f.Heading));
                if (!f.FlownOnFinal && along is > -20 and < -0.5 && off < 0.3 && hd < 20)
                {
                    f.FlownOnFinal = true;
                    // The heading it came onto the final with: the largest difference in the last 40 s.
                    var cut = f.Flown.Where(q => cur.t - q.t <= 40).Select(q => Math.Abs(Geo2.Diff(r.Heading, q.hdg))).DefaultIfEmpty(0).Max();
                    if (cut > 45 && cut < 135) Fault("intercept", $"joined its final at {cut:0}° ({-along:0.0} nm out) — controllers give 30° at most");
                }
                else if (f.FlownOnFinal && along < -0.3 && hd > 45 && !f.WentAround)
                    Fault("offfinal", $"turned off its final once established ({hd:0}° off the runway heading, {-along:0.0} nm out)");
            }
        }
        f.Flown.Add(cur);
        if (f.Flown.Count > 400) f.Flown.RemoveRange(0, f.Flown.Count - 400);
    }

    private void RecordTrail(Flight f)
    {
        if (f.Phase is "parked" or "boarding") return;
        var every = f.OnGround ? 2.0 : 5.0;
        if (f.Trail.Count > 0 && Time - f.Trail[^1].T < every) return;
        if (f.Trail.Count > 0 && (f.Trail[^1].P - f.Pos).Length < 1 && f.OnGround) return;
        f.Trail.Add((f.Pos, f.AltFt, Time));
        if (f.Trail.Count > 900) f.Trail.RemoveRange(0, f.Trail.Count - 900);
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
        f.Lights = 0;
        if (leg is not null && WeekMinute >= 0) f.PushAt = Time + Until(leg.DepMin) * 60;
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
        f.Rwy = DepartureRunwayFor(f);
        f.Timer = boarding;
        f.SetPhase("boarding", Time);
        return f;
    }

    private PushbackPlanner? _pushPlanner;

    /// <summary>
    /// The planned altitude bent to the procedure's restrictions ahead: never above an "at or below" (or "at") one,
    /// starting down in time to meet it at about 300 ft per nm; never below an "at or above" (or "at") one until its fix.
    /// </summary>
    private static double Restricted(Flight f, double plan, double atS = double.NaN) => Restricted(f, plan, out _, atS);

    /// <summary>As above; <paramref name="floor"/> is the published floor still ahead that holds it up (NaN when none).</summary>
    private static double Restricted(Flight f, double plan, out double floor, double atS = double.NaN)
    {
        var s0 = double.IsNaN(atS) ? f.S : atS;
        double hi = double.MaxValue, lo = double.MinValue, hiS = double.MaxValue, loS = double.MaxValue;
        foreach (var c in f.AltLimits)
        {
            var ahead = (c.S - s0) / Geo2.MPerNm;
            if (ahead < -0.3) continue;
            // With a speed limit at the same fix, get down a few miles early so there's level flight to slow in
            // (a jet can't slow and descend at once): about 1 nm per 10 kt below 250, plus a mile.
            var kt = f.SpeedLimits.Where(sl => Math.Abs(sl.S - c.S) < 100).Select(sl => sl.Kt).DefaultIfEmpty(0).Min();
            // Above 10,000 ft it's coming down at its descent speed (280 kt for jets), not 250: a "max 250 kt" fix up
            // there needs its level piece too (MCO WHOVL "9,000–14,000, max 250": taken over 12 nm before it at
            // 17,500 ft and 280 kt, WJA1272 crossed at 13,000–14,300 ft and 254–280 kt every session).
            var high = c.Max > 10500 || (c.Max <= 0 && c.Min > 10500);
            // Coming down from above 10,000 ft to a ceiling at or below it: 91.117's 250 kt applies there, so it needs
            // the same level piece to slow from its descent speed (Midway JILLZ "at 10,000" crossed at 10,460 ft and
            // 254 kt — still levelled at 10,000 slowing when it got there).
            if (kt == 0 && c.Max > 0 && c.Max <= 10500 && f.AltFt > 10500 && f.Kind == "arrival") { kt = 250; high = true; }
            var from = high ? (f.P.Family == "turboprop" ? 230 : 280) : 250;
            var slowNm = kt > 0 && kt < from ? (from - kt) / 10 + 1 : 0;
            if (c.Max > 0) { var h = c.Max + Math.Max(0, ahead - slowNm) * 300; if (h < hi) { hi = h; hiS = c.S; } }
            // On the final approach no floor is above the glidepath (a 3° path clears every step-down fix; a floor above
            // it is a misread intercept altitude, and it left every arrival 500 ft high at 1,000 ft — CYYC, MCO).
            var min = c.Min;
            if (min > 0 && f.Kind == "arrival" && f.Rwy is { } rw && f.ThrS > 0)
            {
                var dAt = (f.ThrS - c.S) / Geo2.MPerNm;
                // Only a floor well above it (CYYC ULBUD 6,500 against the glideslope's 4,890); one within 400 ft is the
                // published altitude and is kept (20 Nov, KLAS ILS 01L: CODNO "at or above 5,300" flown at 5,133).
                if (dAt is >= 0 and < 12) { var gp = ProfileLow(rw, dAt) + 100; if (min > gp + 300) min = gp; }
            }
            if (min > 0 && min > lo) { lo = min; loS = c.S; }
        }
        var t = Math.Min(plan, hi);
        // A floor at a nearer fix wins over getting down early for a farther one: "at or above 9,000" at MIDDL, then
        // "at 2,000" 18 nm on — it stays at 9,000 until MIDDL and descends more steeply after (it used to start down
        // for the farther fix and cross MIDDL at 7,600 ft; MCO's DORII "at or above 8,000" before BRICE "at 5,000"
        // is the same pattern). Only a ceiling at the same or a nearer fix can take it below a floor.
        if (t < lo) t = hiS > loS + 50 ? lo : Math.Min(lo, hi);
        floor = lo > 0 && Math.Abs(t - lo) < 1 ? lo : double.NaN;
        return t;
    }

    private readonly List<double> _err30 = new(), _err10 = new(), _err5 = new();

    /// <summary>For the log at touchdown: how far off the landing time predicted 30, 10 and 5 minutes out was.</summary>
    private string PredictionReport(Flight f)
    {
        if (f.WentAround) return "";
        var parts = new List<string>();
        void One(double pred, List<double> errs, string label)
        {
            if (double.IsNaN(pred)) return;
            var err = Time - pred;          // + landed later than predicted
            errs.Add(err);
            if (errs.Count > 60) errs.RemoveAt(0);
            parts.Add($"{label} {err:+0;-0} s");
        }
        One(f.Pred30, _err30, "30 min out");
        One(f.Pred10, _err10, "10 min out");
        One(f.Pred5, _err5, "5 min out");
        return parts.Count == 0 ? "" : $" (landing time as predicted {string.Join(", ", parts)})";
    }

    /// <summary>How accurate the landing-time predictions have been (average miss, recent landings).</summary>
    public string? PredictionSummary
    {
        get
        {
            string One(List<double> e, string label) => e.Count == 0 ? "" : $"{label} ±{e.Average(Math.Abs):0} s (n={e.Count})";
            var s = string.Join(", ", new[] { One(_err30, "30 min out"), One(_err10, "10 min out"), One(_err5, "5 min out") }.Where(x => x.Length > 0));
            var fx = One(_errFix, "10 min out");
            var dp = string.Join(", ", new[] { One(_errDep10, "10 min out"), One(_errDep5, "5 min out") }.Where(x => x.Length > 0));
            var all = new List<string>();
            if (s.Length > 0) all.Add("landings " + s);
            if (fx.Length > 0) all.Add("times at the STAR/approach fixes " + fx);
            if (dp.Length > 0) all.Add("take-offs " + dp);
            return all.Count == 0 ? null : string.Join("; ", all);
        }
    }

    /// <summary>Degrees a flight's path turns through in its first nm miles.</summary>
    private static double Turned(Flight f, double nmAhead)
    {
        if (f.Path is null) return 0;
        double total = 0, prev = double.NaN;
        var max = Math.Min(f.Path.Length, f.S + nmAhead * Geo2.MPerNm);
        for (var s2 = f.S; s2 < max; s2 += 150)
        {
            var d = f.Path.At(s2).dir;
            if (!double.IsNaN(prev)) total += Math.Abs(Geo2.Diff(d, prev));
            prev = d;
        }
        return total;
    }

    /// <summary>
    /// For an arrival just taken over: the seconds its landing would have to lose beyond what speed can still take
    /// (about 2.2 s a mile beyond 20 nm, 80 % of it counted, and a minute more where it has a downwind to stretch) to fit
    /// between the landings already planned on its runway with the gap needed. 0 or less when it fits.
    /// </summary>
    private double SlotShort(Flight g)
    {
        if (g.Rwy is null || g.Path is null) return 0;
        var nm = Geo2.MPerNm;
        var dg = (g.ThrS - g.S) / nm;
        var eta = Time + EtaToThreshold(g, dg, nominal: true);
        var others = new List<(Flight o, double t)>();
        foreach (var o in Flights)
        {
            if (o == g || o.Done || o.Kind != "arrival" || o.OnGround || o.Rwy?.Runway != g.Rwy.Runway || o.Path is null || o.Phase == "go-around") continue;
            var t = o.Frozen && o.FrozenSlot > 0 ? o.FrozenSlot : Time + (double.IsNaN(o.EtaSec) ? EtaToThreshold(o, (o.ThrS - o.S) / nm) : o.EtaSec);
            others.Add((o, t));
        }
        if (UserArr is { } me && me.Rwy?.Runway == g.Rwy.Runway && !double.IsNaN(_userSlot)) others.Add((me, _userSlot));
        var slot = eta;
        foreach (var (o, t) in others.OrderBy(x => x.t))
        {
            var gapA = ThresholdGapNeeded(o, g, forDepartures: false);
            if (t <= slot - gapA) continue;                                    // lands well before
            if (t >= slot + ThresholdGapNeeded(g, o, forDepartures: false)) break;   // well after: it fits here
            slot = t + gapA;
        }
        var canLose = Math.Max(0, dg - 20) * 2.2 * 0.8 + (g.Fan is { Count: >= 3 } ? 60 : 0);
        return slot - eta - canLose;
    }

    /// <summary>For the log: a route that turns more than a full circle in its first 25 nm is a loop to look into.</summary>
    private void CheckLoops(Flight f, string what)
    {
        if (f.Path is null || f.Test || f.Phase == "go-around") return;
        double total = 0, prev = double.NaN;
        var max = Math.Min(f.Path.Length, 25 * Geo2.MPerNm);
        for (var s2 = 0.0; s2 < max; s2 += 150)
        {
            var d = f.Path.At(s2).dir;
            if (!double.IsNaN(prev)) total += Math.Abs(Geo2.Diff(d, prev));
            prev = d;
        }
        if (total > 330) Log($"{f.Callsign}: {what} turns {total:0}° in its first {max / Geo2.MPerNm:0} nm ({f.Route}, runway {f.Rwy?.Ident}) — a loop to look into");
    }

    /// <summary>Seconds until a landing aircraft is off the runway: to its exit at its current deceleration, plus turning off.</summary>
    private double TimeToClear(Flight o)
    {
        if (o.OnRunway < 0) return 0;
        if (o.Path is null) return 99;
        // The exit is where the ground path leaves the runway.
        double exitS = double.NaN;
        for (var s2 = o.S; s2 < o.Path.Length; s2 += 10)
            if (!(o.Rwy?.Contains(o.Path.At(s2).p, 3) ?? false)) { exitS = s2; break; }
        if (double.IsNaN(exitS)) return 99;
        var v = Math.Max(4, o.Gs * Kt);
        return (exitS - o.S) / Math.Max(6, v * 0.6) + 4;
    }

    private void StartPushback(Flight f)
    {
        var s = f.Spot!;
        var hold = PickRunwayEntry(f.Rwy!, out var entry, s.P);
        _pushPlanner ??= new PushbackPlanner(Apt);
        var plan = _pushPlanner.Plan(s, f.P, hold);
        f.Path = new PathGeom(plan.Points, _ => plan.Radius, 0.5);
        f.PullPath = plan.Pull; f.PullRadius = plan.Radius;
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

    /// <summary>
    /// Ground control's one-way rule, as segment reservations: every taxiway segment another aircraft will still use
    /// (taxiing out, taxiing in), with its direction. A route that would use one of them the other way costs a lot more,
    /// so out- and inbound traffic take parallel taxiways instead of meeting head-on (the user's map: SWA438 out and
    /// AAL1660 in on the same line at MCO). Used when planning every taxi route.
    /// </summary>
    private Func<int, int, double, double> OpposingTraffic(Flight self)
    {
        var used = new HashSet<(int, int)>();
        foreach (var o in Flights)
        {
            if (o == self || o.Done || !o.OnGround || o.TaxiRoute is not { Count: > 1 } r || o.Phase is not ("taxi" or "taxi in" or "landing roll" or "engine start" or "pushback")) continue;
            // From the node it's nearest now onwards (the part still to be used).
            var k = 0; var best = double.MaxValue;
            for (var i = 0; i < r.Count; i++) { var d = (Apt.Nodes[r[i]] - o.Pos).Length; if (d < best) { best = d; k = i; } }
            for (var i = Math.Max(0, k - 1); i + 1 < r.Count; i++) used.Add((r[i], r[i + 1]));
        }
        return (u, v, len) => used.Contains((v, u)) ? 250 + len * 5 : 0;
    }

    // ------------------------------------------------------------------ taxi flow (TaxiFlow)
    private TaxiFlow? _flow;
    private string _flowKey = "";
    private string? _flowDay;
    private Task<TaxiFlow>? _flowTask;
    /// <summary>The taxi flow for the runways in use (null until the airport has ground data and runways).</summary>
    public TaxiFlow? Flow => _flow;
    /// <summary>The full taxi flow for the runways in use now is ready (or there's none to work out).</summary>
    public bool FlowReady => !UseTaxiFlow || !Apt.HasGround || RunwayOnly || (_flow is { Full: true } fl && fl.Key == _flowKey && _flowTask is null);
    /// <summary>Set false to keep the taxi flow from being worked out (tests that compare without it).</summary>
    public bool UseTaxiFlow { get; set; } = true;
    /// <summary>Tests: wait for the background work and take it now.</summary>
    public bool WaitForTaxiFlow { get; set; }

    /// <summary>
    /// When the runways in use change, the parallel-taxiway preference applies at once and the full flow (rehearsed
    /// routes and directions) is worked out in the background, then taken over and logged.
    /// </summary>
    private void UpdateTaxiFlow()
    {
        if (!UseTaxiFlow || !Apt.HasGround || RunwayOnly || ArrRwys.Count == 0 || DepRwys.Count == 0) return;
        if (_flowTask is { IsCompleted: true } t)
        {
            _flowTask = null;
            if (t.IsCompletedSuccessfully && t.Result.Key == _flowKey)
            {
                _flow = t.Result;
                Log($"{Apt.Icao}: taxi flow for {_flowKey}: {_flow.Summary}");
                if (_flow.Details.Length > 0) Log($"{Apt.Icao}: taxi flow, every one-way stretch (real aircraft that way:the other): {_flow.Details}");
            }
            else if (t.IsFaulted) Log($"{Apt.Icao}: taxi flow couldn't be worked out: {t.Exception?.InnerException?.Message}");
        }
        var key = $"landing {string.Join("/", ArrRwys.Select(r => r.Ident))}, departing {string.Join("/", DepRwys.Select(r => r.Ident))}";
        var learned = Learned is { Taxi.Count: > 0 } lp ? lp : null;
        if (key + learned?.Day == _flowKey + _flowDay) return;
        _flowDay = learned?.Day;
        _flowKey = key;
        _flow = TaxiFlow.Sides(Apt, ArrRwys.Concat(DepRwys).ToList(), key);
        // The hold-short node each departure runway would use from each stand, picked here (same as the real taxi out).
        var holds = new Dictionary<(AirportModel.ParkingSpot, RunwayEnd), int>();
        foreach (var sp in TaxiFlow.RehearsalStands(Apt))
            foreach (var d in DepRwys)
                holds[(sp, d)] = PickRunwayEntry(d, out _, sp.P);
        var arr = ArrRwys.ToList(); var dep = DepRwys.ToList(); var active = ActiveRunways();
        // Real taxi tracks (adsb.lol) for these runways: taxi-ins from the landing runways, taxi-outs to the departure ones.
        var real = new List<(bool arrival, List<(V2 p, double trk)> pts)>();
        if (learned is not null)
        {
            var arrIds = arr.Select(r => r.Ident).ToHashSet(); var depIds = dep.Select(r => r.Ident).ToHashSet();
            foreach (var tt in learned.Taxi)
            {
                var isArr = tt.Kind == "arrival";
                if (!(isArr ? arrIds : depIds).Contains(tt.Runway)) continue;
                real.Add((isArr, tt.Points.Where(q => q.Length >= 3).Select(q => (Apt.Map.ToXY(q[0], q[1]), q[2])).ToList()));
            }
        }
        var src = learned is null ? "" : $"{learned.Source} {learned.Day}";
        _flowTask = Task.Run(() => TaxiFlow.Build(Apt, arr, dep, active, (d, sp) => holds.GetValueOrDefault((sp, d), -1), key, real, src));
        if (WaitForTaxiFlow) { _flowTask.Wait(); UpdateTaxiFlow(); }
    }

    /// <summary>The taxi route's extra costs: traffic coming the other way, and the airport's taxi flow.</summary>
    private Func<int, int, double, double> TaxiCost(Flight f)
    {
        var opp = OpposingTraffic(f);
        var flow = _flow;
        if (flow is null) return opp;
        var inbound = f.Kind == "arrival";
        return (u, v, len) => opp(u, v, len) + flow.Cost(u, v, len, inbound);
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
        // Leave by the taxiways: from the next junction ahead on its own route (not across the grass to the nearest
        // point of another taxiway — the user's map: SWA1138 cutting off the pavement round SWA438).
        var from = -1; var best = double.MaxValue;
        if (f.TaxiRoute is { Count: > 0 } own)
        {
            var fwd0 = V2.FromBearing(f.Heading);
            foreach (var n in own)
            {
                if (avoid.Contains(n) || Apt.NodeRunway[n] >= 0) continue;
                var v = Apt.Nodes[n] - f.Pos;
                if (V2.Dot(v, fwd0) < -5 && v.Length > 15) continue;               // behind it
                if (v.Length < best && v.Length < 300) { best = v.Length; from = n; }
            }
        }
        var onOwn = from >= 0;
        // Along its own route to that junction (its taxiway's bends), not straight across the grass to it.
        var lead = new List<V2>();
        if (onOwn && f.TaxiRoute is { } own2)
        {
            var fi = own2.IndexOf(from);
            var fwdL = V2.FromBearing(f.Heading);
            var ci = -1;
            for (var i = 0; i < fi; i++) { var v = Apt.Nodes[own2[i]] - f.Pos; if (V2.Dot(v, fwdL) > 5) { ci = i; break; } }
            if (ci >= 0) for (var i = ci; i < fi; i++) lead.Add(Apt.Nodes[own2[i]]);
        }
        if (from < 0)
            for (var i = 0; i < Apt.TaxiPointCount; i++)
            {
                if (avoid.Contains(i) || Apt.Adj[i].Count == 0 || Apt.NodeRunway[i] >= 0) continue;
                var d = (Apt.Nodes[i] - f.Pos).Length;
                if (d < best) { best = d; from = i; }
            }
        if (from < 0 || best > (onOwn ? 300 : 40)) return false;
        var route = Apt.Route(from, dest, ActiveRunways(), avoid, TaxiCost(f));
        if (route is null) return false;
        var pts = new List<V2> { f.Pos };
        pts.AddRange(lead);
        var fwd = V2.FromBearing(f.Heading);
        var toFirst = Apt.Nodes[route[0]] - f.Pos;
        if (lead.Count > 0) { }
        else if (V2.Dot(toFirst, fwd) < 0)
        {
            // No 180s on a taxiway (plan, Phase 3: "aircraft turn around only on a ramp, a holding pad or a wide apron"):
            // turning back needs the apron round the stands; on a taxiway it waits instead.
            if (!Apt.Spots.Any(sp => (sp.P - f.Pos).Length < 150))
            {
                if (!f.NoTurnLogged) { f.NoTurnLogged = true; Log($"{f.Callsign}: no room to turn round on the taxiway — waits for {otherName}"); }
                return false;
            }
            pts.Add(f.Pos + fwd * 20); pts.Add(f.Pos + fwd * 50 + fwd.Perp * 40);   // turn around in a wide teardrop, never on the spot
        }
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
        if (f.Phase == "taxi in" && f.Spot is { } gate) LeadIntoGate(pts, route, offset, gate, f.P);
        // Only a way that really goes round: no point of it within 45 m of what it's avoiding (past the first 30 m).
        {
            var test = new PathGeom(pts, _ => 30, 1.0);
            for (var s2 = 30.0; s2 < test.Length; s2 += 10)
                if ((test.At(s2).p - otherPos).Length < 45) return false;
        }
        SetGroundPath(f, pts, route, offset);
        Log($"{f.Callsign}: taking another way round {otherName}");
        return true;
    }

    /// <summary>
    /// The turn into a stand, as a pilot flies the lead-in line: the turn starts on the taxiway well before the stand,
    /// as one smooth arc sized for the aircraft (about 0.7 × wingspan, at least 20 m), and ends lined up with the
    /// stand with a short straight to the stop. Scenery often has only a short stub at a right angle from the
    /// taxiway to the stand (KLGA Parking 83: 15 m), which would otherwise mean turning almost on the spot.
    /// pts[i + offset] is the node route[i]; the route ends at the stand's access node.
    /// </summary>
    private void LeadIntoGate(List<V2> pts, List<int> route, int offset, AirportModel.ParkingSpot gate, Perf p)
    {
        void Plain()
        {
            pts.Add(gate.P - V2.FromBearing(gate.Heading) * Math.Min(40, Math.Max(15, (gate.P - Apt.Nodes[gate.Access]).Length * 0.5)));
            pts.Add(gate.P);
        }
        if (route.Count < 2 || pts.Count < offset + route.Count) { Plain(); return; }
        // The taxiway direction just before the stub: from the node before the junction to the junction.
        var junction = Apt.Nodes[route[^2]];
        var before = route.Count >= 3 ? Apt.Nodes[route[^3]] : pts[Math.Max(0, offset + route.Count - 3)];
        var u = junction - before;
        if (u.Length < 1) { Plain(); return; }
        u = u.Unit;
        var v = V2.FromBearing(gate.Heading);                 // into the stand, nose first
        var cross = V2.Cross(u, v);
        var turn = Math.Acos(Math.Clamp(V2.Dot(u, v), -1, 1));
        if (turn < 10 * Math.PI / 180 || turn > 150 * Math.PI / 180 || Math.Abs(cross) < 1e-6) { Plain(); return; }
        // Where the taxiway line meets the stand's centreline.
        var w = gate.P - junction;
        var a = V2.Cross(w, v) / cross;
        var x = junction + u * a;
        var toStand = V2.Dot(gate.P - x, v);
        if (toStand < 15 || a < -60) { Plain(); return; }
        var tanHalf = Math.Tan(turn / 2);
        var r = Math.Max(20, p.SpanM * 0.7);
        var straight = Math.Min(20, toStand * 0.3);
        r = Math.Min(r, (toStand - straight) / tanHalf);
        if (r < 12) { Plain(); return; }
        var t = r * tanHalf;
        var start = x - u * t;
        var end = x + v * t;
        // Drop the route's tail (junction and stub) from where the turn starts.
        var keep = route.Count;
        while (keep > 1 && V2.Dot(Apt.Nodes[route[keep - 1]] - start, u) > -2) keep--;
        if (keep < 1) { Plain(); return; }
        pts.RemoveRange(offset + keep, pts.Count - (offset + keep));
        route.RemoveRange(keep, route.Count - keep);
        pts.Add(start);
        var side = cross > 0 ? 1.0 : -1.0;                    // + : turning left
        var centre = start + new V2(-u.Y, u.X) * (r * side);
        var a0 = (start - centre).Bearing;
        var sweep = turn * 180 / Math.PI;
        for (var k = 1; k < 8; k++) pts.Add(centre + V2.FromBearing(a0 - side * sweep * k / 8, r));
        pts.Add(end);
        pts.Add(gate.P);
    }

    /// <summary>No pushback while another aircraft is taxiing or pushing right behind the gate.</summary>
    private bool PushbackClear(Flight f)
    {
        var s = f.Spot;
        if (s is null) return true;
        var behind = s.P - V2.FromBearing(s.Heading) * Math.Max(30, f.P.LengthM);
        if (Flights.Any(o => o != f && o.OnGround && !o.Done && o.Phase is "pushback" or "taxi" or "taxi in" && (o.Pos - behind).Length < 90)) return false;
        // Nobody about to pass through where the push goes (test airport, 16 Nov: DAL2568 pushed 126 m back out of A1
        // while UAL2601 taxied in behind it to A4 and came within 4 m): the push's whole way, against where each moving
        // aircraft will be along its own path over the next 400 m.
        if (f.Rwy is not null)
        {
            _pushPlanner ??= new PushbackPlanner(Apt);
            var plan = _pushPlanner.Plan(s, f.P, PickRunwayEntry(f.Rwy, out _, s.P));
            var pg = new PathGeom(plan.Points, _ => plan.Radius, 0.5);
            var pushPts = new List<V2>();
            for (var d = 0.0; d <= pg.Length; d += 10) pushPts.Add(pg.At(d).p);
            pushPts.Add(pg.At(pg.Length).p);
            // Nor the user taxiing through it, or sitting in it (user, 16 Nov: give way on the ground — LAIA's push
            // waits for the user, as ground control would hold it).
            if (UserOnGround && UserPos is { } up)
            {
                var ufwd = V2.FromBearing(UserTrack);
                var ums = Math.Max(UserGs, 0) * 0.5144;
                for (var t = 0.0; t <= 30; t += 2)
                {
                    var q = up + ufwd * (ums * t);
                    if (pushPts.Any(pp => (pp - q).Length < 45)) return false;
                    if (ums < 0.5) break;
                }
            }
            foreach (var o in Flights)
            {
                if (o == f || o.Done || !o.OnGround || o.Path is null || !(o.Phase is "pushback" or "taxi" or "taxi in" or "landing roll")) continue;
                if (o.BlockedBy == f || (o.Gs < 0.5 && o.StuckFor > 60)) continue;   // waiting for this one, or not going anywhere soon
                if ((o.Pos - s.P).Length > 1500) continue;
                for (var d = 0.0; d <= 400; d += 15)
                {
                    var q = o.Path.At(Math.Min(o.Path.Length, o.S + (o.Reverse ? -d : d))).p;
                    if (pushPts.Any(pp => (pp - q).Length < 45)) return false;
                }
            }
        }
        // Hold the push for an aircraft about to come into a gate next to this one (it would meet it head-on in the lane),
        // as ground control does: the inbound goes in first.
        return !Flights.Any(o => o != f && !o.Done && o.Kind == "arrival" && o.Phase is "taxi in" or "landing roll" && o.Spot is { } os
            && (os.P - s.P).Length < 150 && (o.Pos - s.P).Length < 900);
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
    /// <summary>
    /// Where departures enter the runway: the connection nearest the threshold on the aircraft's own side of the
    /// runway (never crossing the runway to line up from the far side), and one it can turn onto the runway from
    /// without a hairpin (not a high-speed exit used backwards). Returns the hold node.
    /// </summary>
    private int PickRunwayEntry(RunwayEnd rwy, out int entry, V2? from = null)
    {
        entry = -1;
        var links = Apt.RunwayLinks(rwy.Runway).Select(l => (l.on, l.off, along: rwy.Along(Apt.Nodes[l.on]))).ToList();
        if (links.Count == 0) return -1;
        var side = from is { } fp ? Math.Sign(rwy.Offset(fp)) : 0;
        bool SameSide((int on, int off, double along) l) => side == 0 || Math.Sign(rwy.Offset(Apt.Nodes[l.off])) == side;
        // Entering means driving off → on, then turning to the runway heading: more than ~120° is a hairpin.
        bool Turnable((int on, int off, double along) l) => V2.Dot((Apt.Nodes[l.on] - Apt.Nodes[l.off]).Unit, rwy.Dir) > -0.5;
        var near = links.Where(l => l.along > -80 && l.along < rwy.LengthM * 0.3)
            .OrderBy(l => SameSide(l) ? 0 : 1).ThenBy(l => Turnable(l) ? 0 : 1)
            .ThenBy(l => Math.Round(l.along / 60)).ThenByDescending(l => Apt.NodeHoldShort[l.off] ? 1 : 0).ToList();
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
        var opp = TaxiCost(f);
        var route = Apt.Route(from, f.HoldNode, ActiveRunways(), behind, opp) ?? Apt.Route(from, f.HoldNode, ActiveRunways(), null, opp);
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
        foreach (var e in ArrRwys) set.Add(e.Runway);
        foreach (var e in DepRwys) set.Add(e.Runway);
        if (ArrRwy is not null) set.Add(ArrRwy.Runway);
        if (DepRwy is not null) set.Add(DepRwy.Runway);
        return set;
    }

    /// <summary>Builds a taxi path and its runway-crossing stops. route[i] is the node at pts[i + offset].</summary>
    private void SetGroundPath(Flight f, List<V2> pts, List<int>? route, int offset, double radius = 40)
    {
        // Taxi turns sized for the aircraft (about 1.1 × wingspan, 30–60 m) and eased in and out, as pilots steer.
        var turnR = Math.Clamp(Math.Max(radius * 0.75, f.P.SpanM * 1.1), 30, 60);
        f.Path = new PathGeom(pts, _ => turnR, 1.0, ease: true);
        f.S = 0;
        f.Stops.Clear();
        f.TaxiRoute = route is null ? null : new List<int>(route);
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
    private double WakeRemaining(Flight f, bool plan = true) => Math.Max(SameRunwayWake(f, plan), OtherRunwayWake(f));

    /// <summary>
    /// Wake time behind a heavy, B757 or super on another runway (seconds still to wait): one departing a close parallel
    /// less than 2,500 ft away — one runway for wake (7110.65 3-9-6 f) — or departing a crossing runway (3-9-8: projected
    /// flight paths cross), counted from its lift-off; and one that landed on a crossing runway whose airborne path this
    /// departure will fly through (the intersection short of its touchdown), counted from its touchdown — 2 minutes (3).
    /// </summary>
    internal double OtherRunwayWake(Flight f)
    {
        if (f.Rwy is not { } d) return 0;
        var wait = 0.0;
        foreach (var e in Apt.Ends)
        {
            if (e.Runway == d.Runway) continue;
            var close = CloseParallel(d, e);
            var x = close ? null : Crossing(d, e);
            if (!close && x is null) continue;
            if (_lastDep.GetValueOrDefault(e.Runway) is { Done: false } l && l.Rwy?.Runway == e.Runway && Cwt.DepartureSeconds(l.Wake, f.Wake) is > 0 and var secs)
            {
                if (l.OnGround && l.Phase is "takeoff roll" or "lined up")
                {
                    var acc = Math.Max(0.5, l.P.RollAccel * 0.5144);
                    var toLift = Math.Max(0, ((l.P.Vr + 7) * 0.5144 - l.Gs * 0.5144) / acc) + (l.Phase == "takeoff roll" ? 0 : 10);
                    wait = Math.Max(wait, toLift + secs);
                }
                else if (!l.OnGround && !double.IsNaN(l.LiftoffTime)) wait = Math.Max(wait, secs - (Time - l.LiftoffTime));
            }
            if (x is { } xx && _lastLanding.TryGetValue(e.Runway, out var ll) && ll.f.Rwy is { } le && Cwt.DepartureSeconds(ll.f.Wake, f.Wake) is > 0 and var s2)
            {
                var alongOnArr = le == e ? xx.alongB : e.LengthM - xx.alongB;
                if (alongOnArr < 450) wait = Math.Max(wait, s2 - (Time - ll.t));
            }
        }
        return Math.Max(0, wait);
    }

    /// <summary>For the tests: the last departure from its runway.</summary>
    internal void NoteDeparture(Flight f) { if (f.Rwy is not null) _lastDep[f.Rwy.Runway] = f; }

    private double SameRunwayWake(Flight f, bool plan)
    {
        var l = f.Rwy is null ? null : _lastDep.GetValueOrDefault(f.Rwy.Runway);
        if (l is null || l.Done || l.Rwy is null || f.Rwy is null || l.Rwy.Runway != f.Rwy.Runway) return 0;
        if (l.OnGround)
        {
            // Still rolling: the time until it lifts off, then the wake time behind it from then (CYYZ, 17 Nov: WJA669
            // lined up behind ITY651's roll counting 30 s, waited the heavy's 2 minutes on the runway, and WJA660 had
            // to go around).
            var acc = Math.Max(0.5, l.P.RollAccel * 0.5144);
            var vLift = (l.P.Vr + 7) * 0.5144;
            var toLift = Math.Max(0, (vLift - l.Gs * 0.5144) / acc) + (l.Phase == "takeoff roll" ? 0 : 10);
            return Math.Max(30, toLift + Cwt.DepartureSeconds(l.Wake, f.Wake));
        }
        var since = Time - l.LiftoffTime;
        var wake = Cwt.DepartureSeconds(l.Wake, f.Wake) - since;
        if (plan) EnsureDeparturePlan(f);
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

    private readonly Dictionary<int, Flight> _lastDep = new();
    private readonly Dictionary<(RunwayEnd, bool), double> _rot = new();
    private readonly Dictionary<RunwayEnd, double> _rotSeen = new();
    /// <summary>
    /// Speeds a landing aircraft turns off at (m/s): about 45 kt onto a high-speed (rapid exit) taxiway — designed for
    /// 50 kt wet (ICAO Annex 14 3.8.15, code 3–4) and up to 60 kt dry; crews typically enter at 40–60 kt and slow on
    /// it — and about 20 kt onto a 90° one. The exit's own curve still limits the speed (scenery arcs can be tight).
    /// </summary>
    private const double ExitSpeedFast = 25, ExitSpeedSquare = 10;

    /// <summary>
    /// Seconds a landing aircraft occupies this runway: braking at about 2 m/s² from touchdown (~400 m in) to the
    /// first usable exit, the roll to it and clearing the runway — so a long rollout to a far exit (Orlando 36L)
    /// gets more room than the 50 s of a runway with a high-speed exit in the right place.
    /// </summary>
    private double RunwayOccupancy(RunwayEnd rwy, Perf p)
    {
        var key = (rwy, p.Heavy);
        if (_rot.TryGetValue(key, out var t)) return t;
        var v0 = (p.Vapp - 5) * Kt;
        double Needed(double dot) { var vx = dot > 0.5 ? ExitSpeedFast : ExitSpeedSquare; return 500 + Math.Max(250, (v0 * v0 - vx * vx) / (2 * 2.4)); }
        var links = Apt.HasGround ? Apt.RunwayLinks(rwy.Runway)
            .Select(l => (along: rwy.Along(Apt.Nodes[l.on]), dot: V2.Dot((Apt.Nodes[l.off] - Apt.Nodes[l.on]).Unit, rwy.Dir)))
            .Where(l => l.dot > -0.2 && l.along < rwy.LengthM + 30).ToList() : new();
        var exit = links.Where(l => l.along >= Needed(l.dot)).OrderBy(l => l.along).Select(l => ((double, double)?)(l.along, l.dot)).FirstOrDefault();
        if (exit is not { } x) t = 50;
        else
        {
            var vx = x.Item2 > 0.5 ? ExitSpeedFast : ExitSpeedSquare;
            var brake = (v0 - vx) / 2.0;                                     // seconds braking
            var brakeDist = (v0 * v0 - vx * vx) / (2 * 2.0);
            var roll = Math.Max(0, x.Item1 - 400 - brakeDist) / vx;          // rolling on to the exit
            t = 3 + brake + roll + (x.Item2 > 0.5 ? 8 : 14);                 // touchdown, braking, roll, turning off
        }
        t = Math.Clamp(t, 40, 120);
        _rot[key] = t;
        return t;
    }

    /// <summary>
    /// The departure release (as a tower asks departure control before a take-off): no take-off into an arrival being
    /// vectored across the way out, when the climb couldn't be stopped below it. Departure control never levels a
    /// departure lower than 2,000 ft above the field, so at a high field with arrivals vectored low (CYYC, 14 Nov:
    /// field 3,557 ft, arrivals at 6,500 ft, departures off 17R came within 2.6–2.9 nm and 900 ft of them four times)
    /// the only way to keep them apart is to wait. The first 3½ minutes of the departure (line-up, roll, climb at
    /// about 2,500 ft/min along its route) are checked against each vectored arrival along its own path; at most 4
    /// minutes' wait, then it goes (logged).
    /// </summary>
    private bool Released(Flight f)
    {
        if (f.ReleaseWaitFor == "" || f.DepPlan is not { Count: > 0 } plan || f.Rwy is not { } rwy) return true;
        var pts = new List<V2> { rwy.Threshold + rwy.Dir * Math.Max(0, rwy.Along(f.Pos)) };
        pts.AddRange(plan.Select(p => p.P));
        V2 At(double dist)
        {
            for (var i = 1; i < pts.Count; i++)
            {
                var seg = (pts[i] - pts[i - 1]).Length;
                if (dist <= seg) return pts[i - 1] + (pts[i] - pts[i - 1]).Unit * dist;
                dist -= seg;
            }
            return pts[^1];
        }
        var floorLevel = Apt.ElevationFt + 2000;
        string? blocker = null;
        foreach (var a in Flights)
        {
            if (a.Kind != "arrival" || a.OnGround || a.Done || a.Path is null || a.Phase is "flare" or "landing roll" or "final" or "approach" or "go-around") continue;
            if ((a.Pos - f.Pos).Length > 20 * Geo2.MPerNm) continue;
            for (var t = 0.0; t <= 210; t += 15)
            {
                var air = Math.Max(0, t - 65);                       // line-up and take-off roll first
                var dist = 1500 + air * 170 * Geo2.MpsPerKt;
                var dAlt = rwy.ElevationFt + air * 2500 / 60;
                if (air <= 0) continue;
                var ap = a.Path.At(Math.Min(a.Path.Length, a.S + a.Gs * 0.5144 * t)).p;
                var aAlt = a.AltFt + Math.Min(0, a.Vs) * t / 60;
                if ((At(dist) - ap).Length < 3.5 * Geo2.MPerNm && Math.Abs(dAlt - aAlt) < 1000
                    && Math.Floor((aAlt - 1000) / 1000) * 1000 < floorLevel) { blocker = $"{a.Callsign} ({aAlt:N0} ft)"; break; }
            }
            if (blocker is not null) break;
        }
        if (blocker is null)
        {
            if (!double.IsNaN(f.ReleaseWaitSince)) Log($"{f.Callsign}: released after {(Time - f.ReleaseWaitSince) / 60:0.0} min");
            f.ReleaseWaitSince = double.NaN; f.ReleaseWaitFor = null;
            return true;
        }
        if (double.IsNaN(f.ReleaseWaitSince))
        {
            f.ReleaseWaitSince = Time;
            Log($"{f.Callsign}: waiting for its release at runway {rwy.Ident}: {blocker} being vectored across the way out, too low to climb under");
        }
        f.ReleaseWaitFor = blocker;
        if (Time - f.ReleaseWaitSince > 240)
        {
            Log($"{f.Callsign}: released after 4 min, {blocker} still near the way out");
            f.ReleaseWaitSince = double.NaN; f.ReleaseWaitFor = "";
            return true;
        }
        return false;
    }

    /// <summary>
    /// The tower has runway safety logic (ASDE-X or ASSC): line up and wait is allowed while another aircraft is cleared
    /// to land on the runway (7110.65 3-9-4 c), except when the ceiling is below 800 ft or the visibility below 2 miles.
    /// </summary>
    public bool SafetyLogic;
    /// <summary>Ceiling below 800 ft or visibility below 2 miles (the METAR): no line up and wait with landing traffic.</summary>
    public bool LowWeather;
    /// <summary>The METAR's lowest broken/overcast base (ft above the field) and temperature (°C); NaN when unknown.</summary>
    public double CloudBaseFt = double.NaN, SurfaceTempC = double.NaN;
    /// <summary>For tests: the time of day used for night lighting (else the real clock, as the sim's sky).</summary>
    public DateTime? LightClock { get; set; }

    /// <summary>The sun's elevation at the airport now (degrees).</summary>
    public double SunElevationDeg()
    {
        var t = LightClock ?? DateTime.UtcNow;
        var d = t.DayOfYear + (t.Hour + t.Minute / 60.0) / 24.0;
        var decl = -23.44 * Math.Cos(2 * Math.PI / 365 * (d + 10));
        var hourAngle = ((t.Hour + t.Minute / 60.0) * 15 + Apt.Data.Lon - 180);
        double R(double x) => x * Math.PI / 180;
        var sin = Math.Sin(R(Apt.Data.Lat)) * Math.Sin(R(decl)) + Math.Cos(R(Apt.Data.Lat)) * Math.Cos(R(decl)) * Math.Cos(R(hourAngle));
        return Math.Asin(Math.Clamp(sin, -1, 1)) * 180 / Math.PI;
    }

    private double _sunAt = double.NegativeInfinity; private bool _dark;
    /// <summary>Dark enough for night lighting (the sun more than 3° below the horizon), checked once a minute.</summary>
    public bool Dark { get { if (Time - _sunAt > 60 || LightClock is not null) { _sunAt = Time; _dark = SunElevationDeg() < -3; } return _dark; } }

    /// <summary>
    /// The lights shown in the sim (user, 24 Nov): parked aircraft are dark unless crewed — for 10 minutes after arriving,
    /// and from 30 minutes before push-back; all exterior lights while crossing a runway (AIM 4-3-23); wing (ice
    /// inspection) lights taxiing at night, and in the air in cloud near freezing (from the METAR's lowest broken or
    /// overcast base to 5,000 ft above it, colder than +5 °C there at 2 °C a thousand feet).
    /// </summary>
    public int ShownLights(Flight f)
    {
        var l = f.Lights;
        if (f.Phase == "parked")
        {
            var crewed = (!double.IsNaN(f.ArrivedAt) && Time - f.ArrivedAt < 600) || (!double.IsNaN(f.PushAt) && f.PushAt - Time < 1800);
            return crewed ? Nav | Logo | Cabin : 0;
        }
        if (f.OnGround && f.OnRunway >= 0 && f.Phase is "taxi" or "taxi in" or "holding short") l |= Strobes | Landing | Taxi;
        if (f.OnGround && Dark && f.Phase is "taxi" or "taxi in" or "pushback" or "holding short" or "lining up" or "lined up" or "takeoff roll" or "landing roll") l |= Wing;
        if (!f.OnGround && !double.IsNaN(CloudBaseFt) && !double.IsNaN(SurfaceTempC))
        {
            var agl = f.AltFt - Apt.ElevationFt;
            var temp = SurfaceTempC - 2 * agl / 1000;
            if (agl >= CloudBaseFt && agl <= CloudBaseFt + 5000 && temp <= 5) l |= Wing;
        }
        return l;
    }

    /// <summary>
    /// An aircraft on the runway a departure may line up and wait behind (7110.65 3-9-4; user, 16 Nov: "any method to help
    /// cut down that time while preserving realism"): a departure ahead already on its take-off roll, or — with runway
    /// safety logic, in fair weather — a landing aircraft rolling out ahead of the entry point, the same way.
    /// </summary>
    private bool MayLineUpBehind(Flight o, Flight f)
    {
        if (o.Rwy is not { } or || f.Rwy is not { } fr || or.Runway != fr.Runway || Math.Abs(Geo2.Diff(or.Heading, fr.Heading)) > 90) return false;
        var entry = f.EntryNode >= 0 ? fr.Along(Apt.Nodes[f.EntryNode]) : 0;
        var ahead = fr.Along(o.Pos) > entry + 300;
        if (o.Kind == "departure" && o.OnGround && o.Phase == "takeoff roll") return ahead;
        if (o.Kind == "arrival" && o.OnGround && o.Phase == "landing roll") return ahead && SafetyLogic && !LowWeather;
        return false;
    }

    /// <summary>
    /// Seconds from starting its roll until a departure is airborne and 6,000 ft down the runway — when the next arrival
    /// may cross the threshold behind it (7110.65 3-10-3, category III) — from where it lines up.
    /// </summary>
    private double TimeToClearForArrival(Flight f)
    {
        var rwy = f.Rwy!;
        var start = f.Phase is "lined up" or "takeoff roll" ? rwy.Along(f.Pos) : f.EntryNode >= 0 ? Math.Max(0, rwy.Along(Apt.Nodes[f.EntryNode])) : 0;
        var a = Math.Max(0.5, f.P.RollAccel * 0.5144);
        var liftoff = (f.P.Vr + 7) * 0.5144;
        var dLift = liftoff * liftoff / (2 * a);
        return RollTime(f, Math.Max(dLift, 1829 - start));
    }

    /// <summary>Seconds to line up from where it waits: to the hold line, onto the centreline and 90 m along, at about
    /// 4 m/s, and the few seconds the crew takes before the roll.</summary>
    private double LineUpSec(Flight f)
    {
        var rwy = f.Rwy!;
        var hold = f.HoldNode >= 0 ? Apt.Nodes[f.HoldNode] : f.Pos;
        var e = f.EntryNode >= 0 ? Apt.Nodes[f.EntryNode] : rwy.Threshold;
        var onCl = rwy.Threshold + rwy.Dir * Math.Max(rwy.Along(e), 20);
        return ((f.Pos - hold).Length + (hold - onCl).Length + 90) / 4.0 + 7;
    }

    private bool LineUpAllowed(Flight f)
    {
        var r = f.Rwy!.Runway;
        // First come, first served at the runway (user, 19 Nov: "it seemed to have no rhyme or reason who got lined up
        // next"): not while another departure for this runway has been holding short longer — unless that one is
        // waiting for its release (a departure route blocked), when the next may go.
        var first = Flights.Where(o => o.Kind == "departure" && !o.Done && o.Phase == "holding short" && o.Rwy?.Runway == r
                && double.IsNaN(o.ReleaseWaitSince))
            .OrderBy(o => o.PhaseTime).FirstOrDefault();
        if (first is not null && first != f && first.PhaseTime < f.PhaseTime - 0.5) { f.HoldWhy = $"{first.Callsign} is first in line"; return false; }
        // Line up and wait behind a take-off roll, or behind a landing rolling out (safety logic, fair weather).
        var behind = Flights.Where(o => o != f && !o.Done && (o.Reserve == r || (o.OnRunway == r && !(o.Kind == "departure" && !o.OnGround)))).ToList();
        if (UserOnRunway(r)) { f.HoldWhy = "your aircraft on the runway"; return false; }
        if (GoAroundAhead(f) is { } ga) { f.HoldWhy = $"{ga.Callsign} going around over the runway"; return false; }
        if (behind.FirstOrDefault(o => !MayLineUpBehind(o, f)) is { } bo) { f.HoldWhy = $"{bo.Callsign} on the runway"; return false; }
        // Aircraft held short to cross get the runway after at most one more departure, or after 2 minutes (user, 23 Nov,
        // KATL: JBU1798 held short of 27R for 12+ minutes while ten departures went with line up and wait — "it's okay to
        // allow at least one aircraft to depart between departures if they are holding short of the runway").
        if (CrosserWaiting(r) is { } cw) { f.HoldWhy = $"letting {cw.Callsign} cross"; return false; }
        // During a runway change, no departures the new way while arrivals still come in the old way (the opposite
        // direction) within 30 nm: the airport turns round once the last of them has landed.
        // Only the opposite way on this runway or a parallel (within 30° of the reciprocal): a crossing runway's
        // arrivals are the crossing rule's (20 Nov, KLAS landing 26L, departing 01R — 110° apart — every 01R departure
        // waited while any 26L arrival was within 30 nm).
        if (Flights.FirstOrDefault(a => a.Kind == "arrival" && !a.Done && !a.OnGround && a.Rwy is { } ar && Math.Abs(Geo2.Diff(ar.Heading, f.Rwy.Heading)) > 150
                && (a.ThrS - a.S) < 30 * Geo2.MPerNm) is { } opp) { f.HoldWhy = $"{opp.Callsign} still landing the opposite way ({opp.Rwy!.Ident})"; return false; }
        if (!Released(f)) { f.HoldWhy = "waiting for its release (traffic on its departure route)"; return false; }
        // Only line up if the takeoff can follow without delaying the next arrival.
        var eta = ArrivalEta(r);
        // Lining up (its own way onto the centreline at about 4 m/s, and a few seconds before the roll), overlapping the
        // wait for a landing ahead to turn off or the wake behind a departure; then the roll until it's airborne and
        // 6,000 ft down the runway, before the next arrival crosses the threshold (3-10-3); and 10 s to spare.
        var rollOut = behind.Where(o => o.Kind == "arrival").Select(TimeToClear).DefaultIfEmpty(0).Max();
        var needed = Math.Max(Math.Max(LineUpSec(f), rollOut), WakeRemaining(f)) + TimeToClearForArrival(f) + 10;
        if (eta < needed) { f.HoldWhy = $"an arrival {eta:0} s out (needs {needed:0} s)"; return false; }
        // Don't line up and wait on the runway while arrivals to a crossing runway keep the takeoff from happening:
        // only line up when the takeoff can follow the line-up (about 30 s later).
        if (!CrossingRunwayClearForTakeoff(f, extraSec: 30)) { f.HoldWhy = "arrivals to a crossing runway"; return false; }
        Log($"{f.Callsign}: lining up runway {f.Rwy.Ident} (next arrival {(double.IsInfinity(eta) ? "none" : eta.ToString("0") + " s")})");
        _lastLineUp[r] = Time;
        return true;
    }

    private readonly Dictionary<int, double> _lastLineUp = new();

    /// <summary>An aircraft held short of this runway to cross it, that has already let one departure go (or waited 2
    /// minutes): the next departure waits for it.</summary>
    private Flight? CrosserWaiting(int runway)
    {
        var lastLu = _lastLineUp.TryGetValue(runway, out var v) ? v : double.NegativeInfinity;
        if (ArrivalEta(runway) < 90) return null;          // an arrival keeps it from crossing anyway: the departure may go
        foreach (var o in Flights)
        {
            if (o.Done || !o.OnGround || (o.Kind == "departure" && o.Rwy?.Runway == runway)) continue;
            foreach (var st in o.Stops)
                if (st.Runway == runway && !st.Cleared && !double.IsNaN(st.WaitSince) && (lastLu > st.WaitSince || Time - st.WaitSince > 120))
                    return o;
        }
        return null;
    }

    /// <summary>A departure already rolling past where this crossing is (300 m or more beyond it): the crossing can go
    /// behind it ("cross behind the departing traffic").</summary>
    private bool PassedCrossing(Flight dep, Flight f, Stop st)
    {
        if (dep.Phase != "takeoff roll" || dep.Rwy is not { } rw || f.Path is not { } path) return false;
        var at = rw.Along(path.At(st.S).p);
        return rw.Along(dep.Pos) > at + 300;
    }

    /// <summary>
    /// A go-around still over or ahead of the departure runway, low (22 Nov, KJFK: DLH410 went around on 31L just as
    /// CPA831 lined up there; they came within 1.0 nm): no line-up or take-off until it has climbed through 3,000 ft
    /// above the field, or is more than 5 nm past the runway's end or 1.5 nm to either side of its line.
    /// </summary>
    private Flight? GoAroundAhead(Flight f)
    {
        var d = f.Rwy!;
        foreach (var o in Flights)
        {
            if (o.Done || o.OnGround || o.Phase != "go-around" || o == f) continue;
            if (o.AltFt - d.ElevationFt > 3000) continue;
            var along = d.Along(o.Pos); var off = Math.Abs(d.Offset(o.Pos));
            if (along > -2 * Geo2.MPerNm && along < d.LengthM + 5 * Geo2.MPerNm && off < 1.5 * Geo2.MPerNm) return o;
        }
        return null;
    }

    private bool TakeoffAllowed(Flight f)
    {
        var r = f.Rwy!.Runway;
        if (RunwayBusy(r, f)) return false;
        if (GoAroundAhead(f) is not null) return false;
        // Airborne and 6,000 ft down the runway before the next arrival crosses the threshold (3-10-3), a few seconds spare.
        if (ArrivalEta(r) < TimeToClearForArrival(f) + 5) return false;
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
    /// <param name="extraSec">Extra time needed before the roll starts (lining up first: about 30 s).</param>
    /// <summary>
    /// Where a runway's final, within 2 nm of its threshold, passes over another runway: the distance along the departure
    /// runway and how far before the landing threshold. Null when it doesn't.
    /// </summary>
    private static (double alongDep, double beforeThr)? FinalOver(RunwayEnd dep, RunwayEnd land)
    {
        var a0 = dep.Threshold; var a1 = dep.Threshold + dep.Dir * dep.LengthM;
        var b0 = land.Threshold - land.Dir * (2 * Geo2.MPerNm); var b1 = land.Threshold;
        var r = a1 - a0; var q = b1 - b0;
        var den = V2.Cross(r, q);
        if (Math.Abs(den) < 1e-6) return null;
        var t = V2.Cross(b0 - a0, q) / den; var u = V2.Cross(b0 - a0, r) / den;
        if (t < 0 || t > 1 || u < 0 || u > 1) return null;
        return (t * dep.LengthM, (1 - u) * 2 * Geo2.MPerNm);
    }

    internal bool CrossingRunwayClearForTakeoff(Flight f, double extraSec = 0)
    {
        var d = f.Rwy!;
        foreach (var end in Apt.Ends)
        {
            if (end.Runway == d.Runway) continue;
            var x = Crossing(d, end);
            if (x is null)
            {
                // The runways don't cross, but the departure runway lies under that runway's final, inside 2 nm (7110.65
                // 3-9-8: runways whose flight paths intersect) — KORD, 19 Nov: UAL1583 rolled on 22L while UAL1813 passed
                // over it at about 500 ft on short final to 28C ("next arrival none"). Not until the arrival is past.
                if (FinalOver(d, end) is { } fo)
                {
                    var tPassF = RollTime(f, Math.Max(0, fo.alongDep - d.Along(f.Pos))) + 15 + extraSec;
                    foreach (var o in Flights)
                    {
                        if (o == f || o.Done || o.Kind != "arrival" || o.OnGround || o.Rwy?.Runway != end.Runway || o.Phase == "go-around") continue;
                        var toPoint = (o.ThrS - o.S - fo.beforeThr) / Math.Max(30, o.Gs * Kt);
                        if (toPoint > -5 && toPoint < tPassF) return false;
                    }
                }
                continue;
            }
            // 25 s to spare: the roll can start a few seconds late, and the arrival shouldn't be on a half-mile final
            // as the departure goes through (24 Nov, test: UAL2717 through the intersection with AAL2736 1,250 m out).
            var tPass = RollTime(f, x.Value.alongA - d.Along(f.Pos)) + 25 + extraSec;
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
                // Landing or taking off on the crossing runway and not yet through the intersection — except a landed
                // aircraft that has finished its landing roll and is turning off before the intersection (3-9-8).
                var toX = (o.Rwy == end ? x.Value.alongB : end.LengthM - x.Value.alongB) - o.Rwy.Along(o.Pos);
                if (o.OnGround && o.OnRunway == end.Runway && o.Phase is "landing roll" or "takeoff roll" && toX > -30)
                {
                    var turningOffBefore = o.Phase == "landing roll" && o.Gs < 40 && toX > 300;
                    if (!turningOffBefore) return false;
                }
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
        EnsureDeparturePlan(f);
        f.SetPhase("lining up", Time);
    }

    /// <summary>The learned typical departure path off a runway nearest a direction (bearing from the airport), in map metres.</summary>
    private List<V2>? LearnedDeparture(RunwayEnd rwy, double bearing)
    {
        if (!CopyRealTracks) return null;
        if (Learned?.Departures.TryGetValue(rwy.Ident, out var d) != true || d!.Paths.Count == 0) return null;
        var best = d.Paths.Where(p => p.Count >= 3).OrderBy(p => Math.Abs(Geo2.Diff(p.Bearing, bearing))).FirstOrDefault();
        if (best is null || Math.Abs(Geo2.Diff(best.Bearing, bearing)) > 60) return null;
        return best.Points.Select(x => Apt.Map.ToXY(x[0], x[1])).ToList();
    }

    /// <summary>The departure route: straight ahead past the runway end, then the SID (or a turn out). Also its course.</summary>
    /// <summary>
    /// The departure route planned off the runway it's actually using (CYYZ, 17 Nov: ACA836, planned off 24R, was moved
    /// to 23 by a runway change and took off from 23 straight across the airport towards 24R's far end).
    /// </summary>
    private void EnsureDeparturePlan(Flight f)
    {
        if (f.DepPlan is null || f.DepPlanRwy != f.Rwy || double.IsNaN(f.DepCourse)) PlanDeparture(f);
    }

    private void PlanDeparture(Flight f)
    {
        var rwy = f.Rwy!;
        f.DepPlanRwy = rwy;
        var pts = new List<AirPoint> { new(rwy.FarEnd, ""), new(rwy.FarEnd + rwy.Dir * Geo2.MPerNm, "") };
        f.DepRoute = "";
        var sid = PickSid(f, rwy, out var sidLegs);
        var vectors = sid is null;
        if (sid is not null && sidLegs.Count > 0)
        {
            var sp = Apt.LegsToPoints(sidLegs, rwy.FarEnd, rwy.ElevationFt + 400, out var endsInVectors, hybrid: true);
            // Ignore points behind the runway end (some SIDs begin with the runway fix).
            sp = sp.Where(p => rwy.Along(p.P) > rwy.LengthM + Geo2.MPerNm + 300 || (p.P - rwy.FarEnd).Length > 4000).ToList();
            // A SID that ends "fly heading, expect vectors" (LGA's): the made-up 5 nm on the heading goes, and the real
            // vectors learned for this runway and direction take over (below).
            if (endsInVectors && sp.Count > 0 && Learned is not null) { sp.RemoveAt(sp.Count - 1); vectors = true; }
            if (sp.Count > 0) { pts.AddRange(sp); f.DepRoute = sid.Name; }
        }
        // Radar-vector departures as the airport's controllers really give them (M6): the typical path off this
        // runway towards the flight's direction, learned from real tracks, from where the SID (if any) leaves off.
        if (vectors && LearnedDeparture(rwy, BearingTo(f.Other) ?? rwy.Heading) is { Count: > 2 } lp0)
        {
            // Smoothed: the learned path is a median position every nm, and a thin or mixed direction bin wiggles.
            var lp = Simplify(lp0, 0.3 * Geo2.MPerNm);
            var from = pts[^1].P;
            var k = 0; var best = double.MaxValue;
            for (var i = 0; i < lp.Count; i++) { var d = (lp[i] - from).Length; if (d < best && lp[i].Length > from.Length - 500) { best = d; k = i; } }
            var keep = pts.Count;
            foreach (var q in lp.Skip(k + 1)) if (q.Length > from.Length) pts.Add(new AirPoint(q, ""));
            // Never a loop (21 Nov, KORD: KAL233 off 27C for Seattle "turns 365° in its first 25 nm"): a learned path
            // that turns through more than 200° is dropped for the plain turn towards the destination below.
            if (TotalTurn(pts.Select(p => p.P).ToList(), 25 * Geo2.MPerNm) > 200) pts.RemoveRange(keep, pts.Count - keep);
            else if (f.DepRoute.Length == 0) f.DepRoute = "vectors";
        }
        if (pts.Count <= 2)
        {
            var straight = rwy.FarEnd + rwy.Dir * (4 * Geo2.MPerNm);
            pts.Add(new AirPoint(straight, ""));
            // Towards the destination (at most 120° each way off the runway heading), else a turn out of 30–90°.
            var turn = BearingTo(f.Other) is { } bt
                ? Geo2.Norm(rwy.Heading + Math.Clamp(Geo2.Diff(bt, rwy.Heading), -120, 120))
                : Geo2.Norm(rwy.Heading + (Rng.NextDouble() < 0.5 ? -1 : 1) * (30 + Rng.NextDouble() * 60));
            pts.Add(new AirPoint(straight + V2.FromBearing(turn, 25 * Geo2.MPerNm), ""));
            if (f.DepRoute.Length == 0) f.DepRoute = "vectors";
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
        EnsureDeparturePlan(f);
        if (Math.Abs(rwy.Offset(f.Pos)) > 60 || rwy.Along(f.Pos) < -60 || rwy.Along(f.Pos) > rwy.LengthM * 0.6)
            Log($"{f.Callsign}: WARNING starting the take-off roll off runway {rwy.Ident}'s centreline ({rwy.Offset(f.Pos):0} m aside, {rwy.Along(f.Pos):0} m along)");
        var pts = new List<AirPoint> { new(f.Pos, "") };
        pts.AddRange(f.DepPlan!);
        f.Route = f.DepRoute;
        (f.TopAltFt, f.TopSource) = TopAltitude(f.DepRoute);
        f.ClimbReleased = double.IsNaN(f.TopAltFt);
        // Keep going 30 nm past the last point so the path never runs out before the aircraft is removed.
        var last = pts[^1].P; var prev = pts[^2].P;
        pts.Add(new AirPoint(last + (last - prev).Unit * (30 * Geo2.MPerNm), ""));
        f.Path = DeparturePath(pts.Select(p => p.P).ToList());
        CheckLoops(f, "departure route");
        SetSpeedLimits(f, pts);
        f.S = 0;
        f.Stops.Clear();
        f.Ias = 0;
        f.Gs = 0;
        f.SetPhase("takeoff roll", Time);
        f.DepReport = TakeoffReport(f);
    }

    /// <summary>The published speed limits of the procedure points on the path (index i of pts is point i of the path).</summary>
    private static void SetSpeedLimits(Flight f, List<AirPoint> pts)
    {
        f.SpeedLimits.Clear();
        f.AltLimits.Clear();
        if (f.Path is null) return;
        for (var i = 0; i < pts.Count && i < f.Path.PointS.Length; i++)
        {
            if (pts[i].SpeedKt >= 100 && pts[i].SpeedKt < 400) f.SpeedLimits.Add((f.Path.PointS[i], pts[i].SpeedKt));
            if (pts[i].AltMinFt > 0 || pts[i].AltMaxFt > 0) f.AltLimits.Add((f.Path.PointS[i], pts[i].AltMinFt, pts[i].AltMaxFt));
        }
        SetChecks(f, pts.Take(f.Path.PointS.Length).Select((p, i) => (p, i)).Where(x => x.p.Fix.Length > 0).Select(x => (f.Path.PointS[x.i], x.p)));
    }

    /// <summary>The restricted fixes to check at crossing (altitude window and/or speed limit).</summary>
    private static void SetChecks(Flight f, IEnumerable<(double S, AirPoint p)> fixes)
    {
        f.Checks.Clear(); f.ChecksMet = 0; f.ChecksMissed.Clear();
        foreach (var (s, p) in fixes)
        {
            var kt = p.SpeedKt is >= 100 and < 400 ? p.SpeedKt : 0;
            if (p.AltMinFt > 0 || p.AltMaxFt > 0 || kt > 0) f.Checks.Add((s, p.Fix, p.AltMinFt, p.AltMaxFt, kt));
        }
        f.Checks.Sort((a, b) => a.S.CompareTo(b.S));
    }

    /// <summary>After the route is rebuilt with extra distance before the rest (a hold or 360 inserted at S), the
    /// restrictions still ahead move with it: they now lie at newStart + (old S - S).</summary>
    private static void ShiftRestrictions(Flight f, double oldS, double newStart)
    {
        f.AltLimits = f.AltLimits.Where(c => c.S > oldS).Select(c => (newStart + c.S - oldS, c.Min, c.Max)).ToList();
        f.Checks = f.Checks.Where(c => c.S > oldS).Select(c => (newStart + c.S - oldS, c.Fix, c.Min, c.Max, c.Kt)).ToList();
        f.Fixes = f.Fixes.Where(c => c.S > oldS).Select(c => (newStart + c.S - oldS, c.Fix)).ToList();
    }

    /// <summary>Fixes just crossed: the published altitude (±150 ft) and speed (+10 kt) met or not.</summary>
    private void CheckRestrictions(Flight f)
    {
        while (f.Checks.Count > 0 && f.S >= f.Checks[0].S)
        {
            var c = f.Checks[0]; f.Checks.RemoveAt(0);
            bool altOk = (c.Min <= 0 || f.AltFt >= c.Min - 150) && (c.Max <= 0 || f.AltFt <= c.Max + 150);   // ±150 ft (pilots hold assigned levels well within this)
            bool spdOk = c.Kt <= 0 || f.Ias <= c.Kt + 10;
            _checksTotal++;
            if (altOk && spdOk) { f.ChecksMet++; _checksMet++; continue; }
            string Want()
            {
                var w = new List<string>();
                if (c.Min > 0 && c.Max > 0) w.Add(c.Min == c.Max ? $"at {c.Min:N0}" : $"between {c.Min:N0} and {c.Max:N0}");
                else if (c.Min > 0) w.Add($"at or above {c.Min:N0}");
                else if (c.Max > 0) w.Add($"at or below {c.Max:N0}");
                if (c.Kt > 0) w.Add($"max {c.Kt:0} kt");
                return string.Join(", ", w);
            }
            var why = altOk ? "" : $" [aiming for {f.DbgWant:N0}, plan {f.DbgPlan:N0}, floor {(double.IsNaN(f.DbgFloor) ? "none" : f.DbgFloor.ToString("N0"))}, {f.Vs:0} ft/min" +
                (double.IsNaN(f.DescentFloor) ? "" : $", kept above {f.DescentFloor:N0}") + (double.IsNaN(f.UserCeiling) ? "" : $", below the user {f.UserCeiling:N0}") + "]";
            var miss = $"{c.Fix} at {f.AltFt:N0} ft {f.Ias:0} kt (published {Want()}){why}";
            f.ChecksMissed.Add(miss);
            _recentMisses.Add($"{f.Callsign} {miss}");
            if (_recentMisses.Count > 5) _recentMisses.RemoveAt(0);
        }
    }

    private int _checksTotal, _checksMet;
    private readonly List<string> _recentMisses = new();

    /// <summary>For the control page: how many published restrictions were met at their fixes, and the latest misses.</summary>
    public string? RestrictionSummary => _checksTotal == 0 ? null :
        $"{_checksMet} of {_checksTotal} met ({100.0 * _checksMet / _checksTotal:0}%)" + (_recentMisses.Count > 0 ? "; latest misses: " + string.Join("; ", _recentMisses) : "");

    /// <summary>Predicted crossing times at the named fixes still ahead (one pass along the path), and the prediction
    /// made about 10 minutes before each, kept to measure it.</summary>
    private void PredictFixes(Flight f, double dth)
    {
        f.FixEtas.Clear();
        if (f.Fixes.Count == 0 || f.WentAround) return;
        var ahead = f.Fixes.Where(x => x.S > f.S).ToList();
        if (ahead.Count == 0) return;
        var marks = ahead.Select(x => (f.ThrS - x.S) / Geo2.MPerNm).ToArray();     // largest first (fixes are in path order)
        var at = new double[marks.Length];
        EtaToThreshold(f, dth, marks: marks, at: at);
        for (var k = 0; k < ahead.Count; k++)
        {
            var when = Time + at[k];
            f.FixEtas.Add((ahead[k].Fix, when));
            if (at[k] is <= 600 and > 480 && !f.FixPred10.ContainsKey(ahead[k].Fix)) f.FixPred10[ahead[k].Fix] = when;
        }
    }

    /// <summary>Fixes just crossed: how far off the time predicted 10 minutes before was.</summary>
    private void CheckFixes(Flight f)
    {
        while (f.Fixes.Count > 0 && f.S >= f.Fixes[0].S)
        {
            var fx = f.Fixes[0].Fix; f.Fixes.RemoveAt(0);
            if (f.FixPred10.TryGetValue(fx, out var pred))
            {
                _errFix.Add(Time - pred);
                if (_errFix.Count > 200) _errFix.RemoveAt(0);
            }
        }
    }

    private readonly List<double> _errFix = new(), _errDep10 = new(), _errDep5 = new();

    /// <summary>
    /// Every departure's take-off time, predicted every 5 seconds per runway: when it will be ready at the runway
    /// (boarding, pushback, engine start and taxi still to go), then in queue order, a gap behind the departure
    /// ahead (more behind a heavy) and, on a runway also used for landing, not into the window of an arrival.
    /// </summary>
    private void PredictDepartures()
    {
        const double taxiMs = 7.5;   // about 15 kt average with the turns and stops
        foreach (var g in Flights.Where(f => f.Kind == "departure" && !f.Done && f.Rwy is not null && f.OnGround && f.Phase != "takeoff roll").GroupBy(f => f.Rwy!.Runway))
        {
            foreach (var f in g)
            {
                var hold = f.HoldNode >= 0 ? Apt.Nodes[f.HoldNode] : f.Rwy!.Threshold;
                double toGo = f.Phase switch
                {
                    "boarding" => Math.Max(0, f.Timer) + 30 + 35 + (f.Pos - hold).Length * 1.25 / taxiMs,   // (offline: was ~100 s pessimistic)
                    "pushback" => 25 + 35 + (f.Pos - hold).Length * 1.25 / taxiMs,
                    "engine start" => Math.Max(0, f.Timer) + (f.Pos - hold).Length * 1.25 / taxiMs,
                    "taxi" => f.Path is null ? 0 : Math.Max(0, f.Path.Length - f.S) / taxiMs,
                    _ => 0,
                };
                // Learned correction: how late this airport's departures have really been against the prediction.
                f.ReadyAt = Time + toGo + (f.Phase is "boarding" or "pushback" or "engine start" or "taxi" ? _depBias : 0);
            }
            var arrs = Flights.Where(a => a.Kind == "arrival" && !a.OnGround && !a.Done && a.Rwy?.Runway == g.Key && !double.IsNaN(a.EtaSec))
                .Select(a => Time + a.EtaSec - (Time - a.EtaAt))
                .Concat(UserArr is { Rwy: { } ur } me && ur.Runway == g.Key && !double.IsNaN(me.EtaSec) ? new[] { Time + me.EtaSec } : Array.Empty<double>())
                .OrderBy(x => x).ToList();
            var occ = _rotSeen.TryGetValue(g.First().Rwy!, out var seen) ? seen : 55;
            double prev = double.NegativeInfinity; Flight? prevF = null;
            var depN = 0;
            // A departure held at the gate keeps its place in the queue (when it was ready as the hold began): its own
            // ready time slides on while it waits, and ordered by that, every held one stayed behind the rest and was
            // only let go by the 15-minute limit (KORD, 15 Nov: 13 held, three pushed together after 15 minutes).
            foreach (var f in g.OrderBy(x => x.Phase is "lined up" or "lining up" ? 0 : x.Phase == "holding short" ? 1 : 2)
                         .ThenBy(x => !double.IsNaN(x.GateHeldSince) && !double.IsNaN(x.QueueReadyAt) ? x.QueueReadyAt : x.ReadyAt))
            {
                var gap = prevF is null ? 0 : prevF.P.Heavy ? 120 : 75;
                var t = Math.Max(f.ReadyAt + (f.Phase is "lined up" ? 5 : 35), prev + gap);   // lining up and rolling: ~35 s
                // Not into an arrival's window: it must be rolling ~45 s before the arrival and can't go while it's on the runway.
                foreach (var a in arrs) if (t > a - 45 && t < a + occ) t = a + occ;
                f.TakeoffAt = t;
                f.DepSeq = f.Phase is "lined up" or "lining up" or "holding short" or "taxi" ? ++depN : 0;
                if (double.IsNaN(f.TakeoffPred10) && t - Time is <= 600 and > 480) f.TakeoffPred10 = t;
                if (double.IsNaN(f.TakeoffPred5) && t - Time is <= 300 and > 240) f.TakeoffPred5 = t;
                prev = t; prevF = f;
            }
        }
    }

    /// <summary>For the log at take-off: how far off the predicted take-off time was.</summary>
    private string TakeoffReport(Flight f)
    {
        var parts = new List<string>();
        if (!double.IsNaN(f.TakeoffPred10)) { var e = Time - f.TakeoffPred10; _errDep10.Add(e); if (_errDep10.Count > 100) _errDep10.RemoveAt(0); parts.Add($"10 min out {e:+0;-0} s"); }
        if (!double.IsNaN(f.TakeoffPred5)) { var e = Time - f.TakeoffPred5; _errDep5.Add(e); if (_errDep5.Count > 100) _errDep5.RemoveAt(0); parts.Add($"5 min out {e:+0;-0} s"); _depBias = Math.Clamp(_depBias + 0.3 * e, -300, 300); }
        return parts.Count == 0 ? "" : $" (take-off time as predicted {string.Join(", ", parts)})";
    }

    /// <summary>
    /// Planned departures (departure metering, as real airports do): a departure ready to push whose predicted slot on
    /// the runway is more than 4 minutes after it could be there waits at the gate instead of in a queue at the runway,
    /// and pushes when the wait is down to 2 minutes (at most 15 minutes held). Fewer engines running in line, and the
    /// take-off time is planned from the slot, not from a queue's guess.
    /// </summary>
    private bool GateHold(Flight f)
    {
        if (f.Kind != "departure" || f.GateHoldDone || RunwayOnly || double.IsNaN(f.TakeoffAt) || double.IsNaN(f.ReadyAt)) return false;
        var wait = f.TakeoffAt - (f.ReadyAt + 35);
        if (double.IsNaN(f.GateHeldSince))
        {
            if (wait <= 240) return false;
            f.GateHeldSince = Time;
            f.QueueReadyAt = f.ReadyAt;
            Log($"{f.Callsign}: held at the gate for its departure slot (the wait at runway {f.Rwy?.Ident} would be {wait / 60:0.0} min)");
            return true;
        }
        if (wait > 120 && Time - f.GateHeldSince < 900) return true;
        f.GateHoldDone = true;
        Log($"{f.Callsign}: released from the gate hold after {(Time - f.GateHeldSince) / 60:0.0} min (wait now {Math.Max(0, wait) / 60:0.0} min)");
        return false;
    }

    /// <summary>No other departure on its way to this one's runway, and no wait predicted there.</summary>
    private bool RunwayQuiet(Flight f)
    {
        if (RunwayOnly || f.Rwy is not { } r) return false;
        if (Flights.Any(o => o != f && !o.Done && o.Kind == "departure" && o.Rwy?.Runway == r.Runway
                && (o.Phase is "pushback" or "engine start" or "taxi" or "holding short" or "lining up" or "lined up" || (o.Phase == "boarding" && o.Timer < 420)))) return false;   // nor others about to push for it
        return double.IsNaN(f.TakeoffAt) || double.IsNaN(f.ReadyAt) || f.TakeoffAt - (f.ReadyAt + 35) < 60;
    }

    private double _lastDepPredict = -999;
    /// <summary>Seconds added to departures' ready times, learned from how late (or early) take-offs have been.</summary>
    private double _depBias;

    /// <summary>For the log: the flight's own result ("restrictions 6 of 6 met" or the misses).</summary>
    private static string ChecksReport(Flight f)
    {
        var n = f.ChecksMet + f.ChecksMissed.Count;
        if (n == 0) return "";
        return f.ChecksMissed.Count == 0 ? $"; restrictions {n} of {n} met" : $"; restrictions {f.ChecksMet} of {n} met, missed {string.Join("; ", f.ChecksMissed)}";
    }

    /// <summary>
    /// The highest speed the procedure allows here: a limit ahead must be met at its fix (slowing about 5 kt per mile,
    /// at it 1½ nm before), and the last limit passed holds until the next one (or for 10 nm after the last).
    /// </summary>
    private static double ProcedureLimit(Flight f, bool before) => ProcedureLimitAt(f, f.S, before);

    /// <summary>The same limit at any point along its path (s, metres) — what the flight model will fly there, so the
    /// predictions fly the published speeds too (user, 24 Nov: "see if the restrictions on the STAR are enough").</summary>
    private static double ProcedureLimitAt(Flight f, double s, bool before)
    {
        var lim = double.PositiveInfinity;
        (double S, double Kt)? passed = null;
        foreach (var c in f.SpeedLimits)
        {
            var ahead = (c.S - s) / Geo2.MPerNm;
            // Slowing about 5 kt per nm, at the limit 1½ nm before the fix (16 Nov, KAUS: every WLEEE7 arrival crossed
            // MUSEC "max 230" at 241 kt — at the limit only half a mile before, descending as it slowed, it got there late).
            if (ahead >= 0) { lim = Math.Min(lim, before ? c.Kt : c.Kt + Math.Max(0, ahead - 1.5) * 5); }
            else passed = c;
        }
        if (!before && passed is { } p && (s - p.S) / Geo2.MPerNm < 10 && !f.SpeedLimits.Any(c => c.S > s)) lim = Math.Min(lim, p.Kt);
        else if (!before && passed is { } p2 && f.SpeedLimits.Any(c => c.S > s)) lim = Math.Min(lim, Math.Max(p2.Kt, f.SpeedLimits.Where(c => c.S > s).Min(c => c.Kt)));
        return lim;
    }

    private static bool Serves(Procedure p, RunwayEnd rwy) => p.RunwayTransitions.Count == 0 || p.RunwayTransitions.Any(t => t.Name == rwy.Ident);

    /// <summary>
    /// A STAR's runway transition for this runway — or, when it has none, a parallel's (user, 19 Nov: Midway's ENDEE8,
    /// FISSK7 and PANGG7 have transitions for 04R/13L/22L/31R only, so arrivals to 22R flew no STAR and none of its
    /// restrictions — the 6,000 ft at STKNY or HALIE). Controllers vector the arrival from the STAR to the parallel.
    /// </summary>
    private static Transition? StarTrans(Procedure p, RunwayEnd rwy)
    {
        var t = p.RunwayTransitions.FirstOrDefault(x => x.Name == rwy.Ident);
        if (t is not null) return t;
        static string Num(string id) => new string(id.Where(char.IsDigit).ToArray()).TrimStart('0');
        var n = Num(rwy.Ident);
        return n.Length == 0 ? null : p.RunwayTransitions.FirstOrDefault(x => Num(x.Name) == n);
    }
    private static bool ServesStar(Procedure p, RunwayEnd rwy) => p.RunwayTransitions.Count == 0 || StarTrans(p, rwy) is not null;

    /// <summary>The SID's outer fix: the last fix of its exit transition (or of the SID itself).</summary>
    private static ProcedureLeg? ExitFix(Procedure sid, Transition? t) =>
        t?.Legs.LastOrDefault(l => l.Lat != 0 || l.Lon != 0) ?? sid.Legs.LastOrDefault(l => l.Lat != 0 || l.Lon != 0)
        ?? sid.RunwayTransitions.SelectMany(r => r.Legs).LastOrDefault(l => l.Lat != 0 || l.Lon != 0);

    /// <summary>The STAR's entry fix: the first fix of its entry transition (or of the STAR itself).</summary>
    private static ProcedureLeg? EntryFix(Procedure star, Transition? t) =>
        t?.Legs.FirstOrDefault(l => l.Lat != 0 || l.Lon != 0) ?? star.Legs.FirstOrDefault(l => l.Lat != 0 || l.Lon != 0)
        ?? star.RunwayTransitions.SelectMany(r => r.Legs).FirstOrDefault(l => l.Lat != 0 || l.Lon != 0);

    /// <summary>The SID and exit (among those serving any of these runways) that best points at the destination.</summary>
    private (Procedure p, Transition? t)? ChooseSid(IEnumerable<RunwayEnd> rwys, string? dest)
    {
        var list = rwys.ToList();
        var cands = Apt.Data.Departures.Where(d => (d.RunwayTransitions.Count > 0 || d.Legs.Count > 0) && list.Any(r => Serves(d, r))).ToList();
        if (cands.Count == 0) return null;
        // The FAA preferred route for this pair names the SID (and its exit) airlines file: use it when it's published here.
        if (Routes is not null && dest is not null && list.Count > 0 &&
            Routes.Departure(Apt.Icao, dest, ArrRwy?.Heading ?? list[0].Heading, true, tok => cands.Any(c => AirportRules.Base(c.Name) == AirportRules.Base(tok))) is { } pr)
        {
            var sid = cands.First(c => AirportRules.Base(c.Name) == AirportRules.Base(pr.sid));
            var tr = sid.EnrouteTransitions.FirstOrDefault(t => t.Name.Equals(pr.transition, StringComparison.OrdinalIgnoreCase));
            if (tr is not null || sid.EnrouteTransitions.Count == 0) { RoutesUsed++; return RnavInstead(sid, tr, cands, dest) ?? (sid, tr); }
        }
        if (Routes is not null && BearingTo(dest) is { } db && list.Count > 0 &&
            Routes.DepartureToDirection(Apt.Icao, db, ArrRwy?.Heading ?? list[0].Heading, FaaWhere, tok => cands.Any(c => AirportRules.Base(c.Name) == AirportRules.Base(tok))) is { } pd)
        {
            var sid = cands.First(c => AirportRules.Base(c.Name) == AirportRules.Base(pd.sid));
            var tr = sid.EnrouteTransitions.FirstOrDefault(t => t.Name.Equals(pd.transition, StringComparison.OrdinalIgnoreCase));
            if (tr is null && sid.EnrouteTransitions.Count > 0)
                tr = sid.EnrouteTransitions.OrderBy(t => Score(db, ExitFix(sid, t))).First();
            RoutesUsed++;
            return RnavInstead(sid, tr, cands, dest) ?? (sid, tr);
        }
        var want = BearingTo(dest);
        var options = new List<(Procedure p, Transition? t, double score)>();
        foreach (var sid in cands)
            foreach (var t in sid.EnrouteTransitions.Count > 0 ? sid.EnrouteTransitions.Select(t => (Transition?)t).ToList() : new List<Transition?> { null })
                options.Add((sid, t, Score(want, ExitFix(sid, t))));
        return Best(options);
    }

    /// <summary>
    /// A radar-vectors SID (no fix of its own: AUS7, ORD's OHARE9) named by the FAA's preferred routes gives way to a
    /// published RNAV SID whose exit points within 45° of the destination (user, 16 Nov, KAUS: "departures didn't seem
    /// to follow SIDs" — nearly every departure was given AUS7, while the real ones fly ELOEL3, ILEXY4, MUCKY3…: the
    /// preferred routes still name the old vectors departure). Null to keep the routes' choice.
    /// </summary>
    private (Procedure p, Transition? t)? RnavInstead(Procedure chosen, Transition? tr, List<Procedure> cands, string? dest)
    {
        if (ExitFix(chosen, tr) is not null || BearingTo(dest) is not { } want) return null;
        var options = new List<(Procedure p, Transition? t, double score)>();
        foreach (var sid in cands)
            foreach (var t in sid.EnrouteTransitions.Count > 0 ? sid.EnrouteTransitions.Select(t => (Transition?)t).ToList() : new List<Transition?> { null })
                if (ExitFix(sid, t) is not null) options.Add((sid, t, Score(want, ExitFix(sid, t))));
        options = options.Where(o => o.score <= 45).ToList();
        return options.Count == 0 ? null : Best(options);
    }

    /// <summary>The STAR and entry (among those serving any of these runways) that starts nearest the origin's direction.</summary>
    private (Procedure p, Transition? t)? ChooseStar(IEnumerable<RunwayEnd> rwys, string? origin, bool jet = true)
    {
        var list = rwys.ToList();
        var cands = Apt.Data.Arrivals.Where(a => list.Any(r => ServesStar(a, r))).ToList();
        // Never a propeller-only arrival for a jet (the FAA routes say which), when anything else serves.
        if (jet && Routes is not null && cands.Any(c => !Routes.PropsOnly(c.Name))) cands = cands.Where(c => !Routes.PropsOnly(c.Name)).ToList();
        if (cands.Count == 0) return null;
        // The FAA preferred route from this origin names the STAR (and the fix it's joined at) airlines file, by flow:
        // use it when that STAR is published here for a runway in use.
        if (Routes is not null && origin is not null && list.Count > 0 &&
            Routes.Arrival(origin, Apt.Icao, list[0].Heading, true, tok => cands.Any(c => AirportRules.Base(c.Name) == AirportRules.Base(tok))) is { } pr)
        {
            var star = cands.First(c => AirportRules.Base(c.Name) == AirportRules.Base(pr.star));
            var tr = star.EnrouteTransitions.FirstOrDefault(t => t.Name.Equals(pr.transition, StringComparison.OrdinalIgnoreCase))
                ?? star.EnrouteTransitions.FirstOrDefault(t => t.Legs.Any(l => l.Fix.Equals(pr.transition, StringComparison.OrdinalIgnoreCase)));
            if (tr is not null || star.EnrouteTransitions.Count == 0 || star.Legs.Any(l => l.Fix.Equals(pr.transition, StringComparison.OrdinalIgnoreCase)))
            { RoutesUsed++; return (star, tr); }
        }
        // Origin not in the database (international, or a pair it doesn't list): the STAR the database's routes from
        // airports in the same direction use, for this flow.
        if (Routes is not null && BearingTo(origin) is { } wb && list.Count > 0 &&
            Routes.ArrivalFromDirection(Apt.Icao, wb, list[0].Heading, FaaWhere, tok => cands.Any(c => AirportRules.Base(c.Name) == AirportRules.Base(tok))) is { } pd)
        {
            var star = cands.First(c => AirportRules.Base(c.Name) == AirportRules.Base(pd.star));
            var tr = star.EnrouteTransitions.FirstOrDefault(t => t.Name.Equals(pd.transition, StringComparison.OrdinalIgnoreCase))
                ?? star.EnrouteTransitions.FirstOrDefault(t => t.Legs.Any(l => l.Fix.Equals(pd.transition, StringComparison.OrdinalIgnoreCase)));
            if (tr is null && star.EnrouteTransitions.Count > 0)
                tr = star.EnrouteTransitions.OrderBy(t => Score(wb, EntryFix(star, t))).First();
            RoutesUsed++;
            return (star, tr);
        }
        var want = BearingTo(origin);
        var options = new List<(Procedure p, Transition? t, double score)>();
        // Stopgap until real filed routes: a STAR also published at a neighbouring field counts as 60° worse, so it's
        // only used when nothing of the airport's own fits. (Shared doesn't always mean satellite — MCO's ALYNA4 is for
        // weather and oceanic routings — so filed routes are the real answer.)
        foreach (var st in cands)
            foreach (var t in st.EnrouteTransitions.Count > 0 ? st.EnrouteTransitions.Select(t => (Transition?)t).ToList() : new List<Transition?> { null })
                options.Add((st, t, Score(want, EntryFix(st, t)) + (NeighbourStars.Contains(AirportRules.Base(st.Name)) ? 60 : 0)
                    // The FAA routes show which STARs end here and which only at nearby airports (JAFAR3 → ORL, BITHO7 → MLB).
                    + (Routes is null ? 0 : Routes.StarFamiliarity(st.Name, Apt.Icao) switch { 0 => 0, 1 => 30, _ => 90 })));
        return Best(options);
    }

    private Procedure? PickSid(Flight f, RunwayEnd rwy, out List<ProcedureLeg> legs)
    {
        legs = new List<ProcedureLeg>();
        var choice = f.SidPick is { } sp && Serves(sp.p, rwy) ? sp : ChooseSid(new[] { rwy }, f.Other);
        if (choice is null) return null;
        var (pick, tr) = choice.Value;
        var rt = pick.RunwayTransitions.FirstOrDefault(t => t.Name == rwy.Ident);
        if (rt is not null) legs.AddRange(rt.Legs);
        legs.AddRange(pick.Legs);
        if (tr is not null) legs.AddRange(tr.Legs);
        return pick;
    }

    /// <summary>Where an airport named the FAA way ("BNA", or a full ICAO code) lies from here: bearing and distance.</summary>
    private (double bearing, double nm)? FaaWhere(string id)
    {
        if (Locate is null) return null;
        var p = (id.Length == 3 ? Locate("K" + id) : null) ?? Locate(id);
        if (p is not { } q) return null;
        return (Geo2.BearingDeg(Apt.Data.Lat, Apt.Data.Lon, q.lat, q.lon), Geo2.DistanceNm(Apt.Data.Lat, Apt.Data.Lon, q.lat, q.lon));
    }

    /// <summary>Bearing from this airport to another (null if unknown or too close to matter).</summary>
    private double? BearingTo(string? icao)
    {
        if (string.IsNullOrEmpty(icao) || Locate?.Invoke(icao) is not { } p) return null;
        if (Geo2.DistanceNm(Apt.Data.Lat, Apt.Data.Lon, p.lat, p.lon) < 25) return null;
        return Geo2.BearingDeg(Apt.Data.Lat, Apt.Data.Lon, p.lat, p.lon);
    }

    /// <summary>How far (degrees) a procedure's outer fix is from the wanted direction; 0 when the direction is unknown.</summary>
    private double Score(double? want, ProcedureLeg? fix)
    {
        if (want is null) return 0;
        if (fix is null) return 90;   // radar-vector procedures (no fix to point anywhere): only when nothing points the right way
        var b = Geo2.BearingDeg(Apt.Data.Lat, Apt.Data.Lon, fix.Lat, fix.Lon);
        return Math.Abs(Geo2.Diff(b, want.Value));
    }

    /// <summary>The best-pointing option; a random one among those within 15° of the best (and all of them when the direction is unknown).</summary>
    private (Procedure p, Transition? t) Best(List<(Procedure p, Transition? t, double score)> options)
    {
        var min = options.Min(o => o.score);
        var good = options.Where(o => o.score <= min + 15).ToList();
        var o = good[Rng.Next(good.Count)];
        return (o.p, o.t);
    }

    private static string Compass(double deg) => new[] { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" }[(int)Math.Round(deg / 45) % 8];

    /// <summary>Where each runway sits across the airport, -1 … +1 between the outermost two.</summary>
    private static List<double> Across(List<RunwayEnd> list, out V2 axis)
    {
        axis = list[0].Dir;
        var ax = axis;
        var lat = list.Select(e => V2.Cross(ax, (e.Threshold + e.FarEnd) * 0.5)).ToList();
        var mid = (lat.Max() + lat.Min()) / 2;
        var half = Math.Max(1, (lat.Max() - lat.Min()) / 2);
        return lat.Select(x => (x - mid) / half).ToList();
    }

    /// <summary>Which side of the airport a fix is on, across the runways: -1 … +1 (0 straight ahead or behind).</summary>
    private double SideOf(V2 axis, ProcedureLeg? fix)
    {
        if (fix is null) return 0;
        var p = Apt.Map.ToXY(fix.Lat, fix.Lon);
        return p.Length < 1 ? 0 : V2.Cross(axis, p * (1 / p.Length));
    }

    /// <summary>
    /// The landing runway and STAR for an arrival from this origin. The STAR comes from the origin's direction, and
    /// the runway from the STAR's side of the airport, as real approach controls run it (Orlando: PRICY and GRNCH
    /// from the west land the western runway, SNFLD and MUNGI from the east the eastern one). Only a much longer
    /// queue on that side moves an arrival to the other runway — the exception, not the rule.
    /// </summary>
    public (RunwayEnd? rwy, (Procedure p, Transition? t)? star) PlanArrival(string? origin, string? type = null)
    {
        var jet = type is null || Perf.For(type).Family != "turboprop";
        if (ArrRwy is null) SelectRunways(true);
        var list = ArrRwys.Count > 0 ? ArrRwys : ArrRwy is null ? new() : new List<RunwayEnd> { ArrRwy };
        if (list.Count == 0) return (null, null);
        var star = ChooseStar(list, origin, jet);
        if (list.Count == 1) return (list[0], star);
        var pos = Across(list, out var axis).Select(x => Math.Abs(x) < 0.3 ? x : Math.Sign(x)).ToList();
        var serving = Enumerable.Range(0, list.Count).Where(i => star is null || ServesStar(star.Value.p, list[i])).ToList();
        if (serving.Count == 0) serving = Enumerable.Range(0, list.Count).ToList();
        // The airport's published rule for this STAR (its AAUP), when it names runways that are in use.
        if (star is { } srr && Rules?.ForStar(srr.p.Name, LocalMinuteNow) is { } rule)
        {
            var ruled = serving.Where(i => rule.Runways.Contains(list[i].Ident)).ToList();
            if (ruled.Count > 0) serving = ruled;
        }
        // A landing runway the ATIS lists but real traffic hardly lands on is left to the others (user, 19 Nov, KLAS
        // "landing 26L, 19L": 19L had 6 real arrivals in 7 days against 500+ on 26L, yet every arrival from the
        // north-west was sent to its RNAV approach — "the RNAV 19L arrivals were a real problem"): under 5% of the real
        // arrivals to the listed runways, with 30 or more learned, it isn't given arrivals while another one is in use.
        if (serving.Count > 1 && Learned is { Samples.Count: > 0 } lp)
        {
            var real = serving.ToDictionary(i => i, i => lp.Arrivals.TryGetValue(list[i].Ident, out var ra) ? ra.Count : 0);
            var tot = real.Values.Sum();
            if (tot >= 30)
            {
                var used = serving.Where(i => real[i] >= tot * 0.05).ToList();
                if (used.Count > 0) serving = used;
            }
        }
        // Which of these runways real arrivals from this direction land on (the learned tracks, counted by the
        // direction they came from): shared out in those proportions — a runway gets the next arrival when its queue is
        // shortest for its real share — when 8 or more real arrivals from about this direction landed on them.
        var fromBrg = star is { } st0 && EntryFix(st0.p, st0.t) is { } ef ? (double?)Apt.Map.ToXY(ef.Lat, ef.Lon).Bearing : BearingTo(origin);
        // By the STAR it flies first (real arrivals on that STAR), else by direction.
        if (serving.Count > 1 && RealWeights(serving.Select(i => list[i]).ToList(), star?.p.Name, fromBrg) is { } rwW)
        {
            var w = serving.ToDictionary(i => i, i => rwW.GetValueOrDefault(list[i]));
            var tw = w.Values.Sum();
            if (tw > 0)
            {
                var cand = serving.Where(i => w[i] >= tw * 0.05).ToList();
                var pickR = cand.OrderByDescending(i => w[i] / tw / (0.5 + Queue(list[i]))).ThenByDescending(i => w[i]).First();
                return (list[pickR], star);
            }
        }
        var side = star is { } st ? SideOf(axis, EntryFix(st.p, st.t)) : BearingTo(origin) is { } b ? V2.Cross(axis, V2.FromBearing(b)) : 0;
        // The STAR's side is a firm preference (user: SNFLD/MUNGI to the east runway, PRICY/GRNCH to the west "9 out of
        // 10 times"), even for a STAR that comes in only a little off the runway axis (SNFLD from the north-north-east
        // in north flow): only a clearly longer queue on that side moves an arrival across.
        side = Math.Abs(side) < 0.1 ? side / 0.1 : Math.Sign(side);
        int Queue(RunwayEnd e) => Flights.Count(o => !o.Done && o.Kind == "arrival" && !o.OnGround && o.Rwy?.Runway == e.Runway);
        var minQ = serving.Min(i => Queue(list[i]));
        // Three or more landing runways (Atlanta: 08L in the north, 09R and 10 in the south): every runway on the STAR's
        // side, or in the middle, shares that side's arrivals by queue — not all to the outermost one (user, 15 Nov:
        // "planes sequenced for 8L and 10, but none for 9R"). With two, it's the STAR's side as before.
        if (list.Count >= 3)
        {
            var raw = Across(list, out _);
            var sideSet = serving.Where(i => Math.Abs(side) < 0.3 || Math.Abs(raw[i]) < 0.3 || Math.Sign(raw[i]) == Math.Sign(side)).ToList();
            if (sideSet.Count > 0)
            {
                var pick = sideSet.OrderBy(i => Queue(list[i])).ThenBy(i => Math.Abs(raw[i] - side)).ThenByDescending(i => list[i].LengthM).First();
                // Only a clearly longer queue on that side moves an arrival across the airport.
                if (Queue(list[pick]) - minQ <= 4) return (list[pick], star);
            }
        }
        var best = serving.OrderByDescending(i => pos[i] * side * 2 - 0.25 * (Queue(list[i]) - minQ)).ThenByDescending(i => list[i].LengthM).First();
        return (list[best], star);
    }

    public RunwayEnd? ArrivalRunwayFor(string? origin) => PlanArrival(origin).rwy;

    /// <summary>
    /// The departure runway and SID for a flight: the SID towards its destination, and the runway on that SID's side
    /// (westbound departures off the western runway). A stand outside the runways (a cargo or FBO ramp on the west)
    /// uses the runway on its own side whatever the direction. A runway also used for landing is the second choice.
    /// </summary>
    private RunwayEnd? DepartureRunwayFor(Flight f)
    {
        var all = DepRwys.Count > 0 ? DepRwys : DepRwy is null ? new() : new List<RunwayEnd> { DepRwy };
        if (all.Count == 0) return DepRwy;
        var list = all.Where(e => e.LengthM >= f.P.MinRunwayM).ToList();
        if (list.Count == 0) list = all;
        var sid = ChooseSid(list, f.Other);
        f.SidPick = sid;
        if (list.Count == 1) return list[0];
        var pos = Across(list, out var axis).Select(x => Math.Abs(x) < 0.3 ? x : Math.Sign(x)).ToList();
        var serving = Enumerable.Range(0, list.Count).Where(i => sid is null || Serves(sid.Value.p, list[i])).ToList();
        if (serving.Count == 0) serving = Enumerable.Range(0, list.Count).ToList();
        // The airport's own rule for this SID (its AAUP), when it names runways that are in use.
        // Order: the flow (ATIS, or the wind without one) decides the runways in use; the AAUP rule then narrows the
        // choice among them — if its runway isn't in use, or it's outside its hours, the ATIS wins.
        var ruleNote = "";
        var rule = sid is { } sr ? Rules?.ForSid(sr.p.Name, LocalMinuteNow) : null;
        if (rule is null && sid is { } sr0 && Rules?.ForSid(sr0.p.Name) is { } off) ruleNote = $"its AAUP rule applies only{off.HoursText}";
        if (rule is not null)
        {
            var ruled = serving.Where(i => rule.Runways.Contains(list[i].Ident)).ToList();
            if (ruled.Count > 0) serving = ruled;
            else { ruleNote = $"its AAUP runway ({string.Join("/", rule.Runways)}) isn't in use, so the ATIS wins"; rule = null; }
        }
        var side = sid is { } sd ? SideOf(axis, ExitFix(sd.p, sd.t)) : BearingTo(f.Other) is { } b ? V2.Cross(axis, V2.FromBearing(b)) : 0;
        // A stand beyond the outermost runway on one side: that side's runway (no crossing the airport to depart).
        var gate = f.Spot?.P ?? f.Pos;
        var lats = list.Select(e => V2.Cross(axis, (e.Threshold + e.FarEnd) * 0.5)).ToList();
        var g = V2.Cross(axis, gate);
        var ramp = g < lats.Min() - 150 ? -1.0 : g > lats.Max() + 150 ? 1.0 : 0.0;
        // Where the stand sits across the field (-1 … +1 between the outermost runways): for a departure going
        // straight out, the runway on the terminal's side (no crossing the field to depart).
        var mid = (lats.Max() + lats.Min()) / 2; var half = Math.Max(1, (lats.Max() - lats.Min()) / 2);
        var gatePos = Math.Clamp((g - mid) / half, -1, 1);
        var straight = Math.Abs(side) < 0.4;
        int Queue(RunwayEnd e) => Flights.Count(o => !o.Done && o.Kind == "departure" && o.OnGround && o.Rwy?.Runway == e.Runway);
        var minQ = serving.Min(i => Queue(list[i]));
        var best = serving.OrderByDescending(i => pos[i] * side * 2 + pos[i] * ramp * 4 + (straight ? pos[i] * gatePos * 1.5 : 0)
                - (ArrRwys.Any(a => a.Runway == list[i].Runway) ? 1.0 : 0) - 0.25 * (Queue(list[i]) - minQ))
            .ThenByDescending(i => list[i].LengthM).First();
        // For the log: why this runway, in compass terms (the side of the field the SID heads for).
        string Side(double v) => Math.Abs(v) < 0.25 ? "straight out" : "to the " + Compass(Geo2.Norm(axis.Bearing + (v > 0 ? -90 : 90)));
        var why = new List<string>();
        if (rule is not null) why.Add(rule.Source + rule.HoursText);
        else if (ruleNote.Length > 0) why.Add(ruleNote);
        if (sid is { } sd2) why.Add($"{sd2.p.Name}{(sd2.t is { } tt ? "." + tt.Name : "")} towards {f.Other} exits {Side(side)}");
        else why.Add($"no SID; {f.Other} lies {Side(side)}");
        if (straight && ramp == 0 && Math.Abs(gatePos) > 0.2) why.Add($"its stand is on the {Compass(Geo2.Norm(axis.Bearing + (gatePos > 0 ? -90 : 90)))} side of the field");
        if (ramp != 0) why.Add($"its stand is outside the runways, {(ramp * pos[best] > 0 ? "on this runway's side" : "on the other side")}");
        var q = Queue(list[best]) - minQ;
        if (q > 0) why.Add($"{q} more waiting here than on the least busy runway");
        if (ArrRwys.Any(a => a.Runway == list[best].Runway)) why.Add("also a landing runway");
        Log($"{f.Callsign}: departing runway {list[best].Ident} of {string.Join("/", list.Select(e => e.Ident))} — {string.Join("; ", why)}");
        return list[best];
    }

    /// <summary>An air route with corners sized for the speed flown there (25° bank).</summary>
    /// <param name="from">The aircraft it's for, when it's already flying: the new path begins with the turn it can
    /// fly from the way it's going now, at its speed (25° of bank) — never a swing round on the spot.</param>
    private static PathGeom AirPath(List<V2> pts, Func<int, double> plannedIas, double altFt, Flight? from = null, Func<int, double>? altAt = null)
    {
        double? startDir = null; double startR = 0;
        if (from is { OnGround: false, Path: { } old } && pts.Count > 0 && (pts[0] - from.Pos).Length < 100)
        {
            var d = old.At(Math.Clamp(from.S, 0, old.Length)).dir;
            startDir = from.Reverse ? Geo2.Norm(d + 180) : d;
            var v0 = Wind.Tas(Math.Max(from.Ias, 140), from.AltFt) * Kt;
            startR = Math.Max(600, v0 * v0 / (G * Math.Tan(25 * Math.PI / 180)));
        }
        return new PathGeom(pts, i =>
        {
            var ias = plannedIas(i);
            if (ias <= 1) return 30;
            var v = Wind.Tas(ias, altAt?.Invoke(i) ?? altFt) * Kt;
            return Math.Max(600, v * v / (G * Math.Tan(25 * Math.PI / 180)));
        }, G * Math.Tan(25 * Math.PI / 180), startDir: startDir, startRadius: startR);
    }

    /// <summary>
    /// A departure's corners sized for the speed it will really be flying there (user, 28 Nov: "Even one loop or
    /// unrealistic turn is a failure" — the SID's corners were sized for 190 kt, and at 13 nm a jet is doing 250):
    /// about 190 kt in the first 3 nm, 230 to 8 nm, 250 below 10,000 ft and 290 above, the height from a normal climb
    /// (about 800 ft per nm), each at 25° of bank.
    /// </summary>
    private PathGeom DeparturePath(List<V2> pts)
    {
        var cum = new double[pts.Count];
        for (var i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + (pts[i] - pts[i - 1]).Length / Geo2.MPerNm;
        return new PathGeom(pts, i =>
        {
            if (i <= 1) return 30;
            var dNm = cum[Math.Clamp(i, 0, cum.Length - 1)];
            var alt = Math.Min(Apt.ElevationFt + 800 * dNm, 18000);
            var ias = dNm < 3 ? 190 : dNm < 8 ? 230 : alt < 10000 ? 250 : 290;
            var v = Wind.Tas(ias, alt) * Kt;
            return Math.Max(600, v * v / (G * Math.Tan(25 * Math.PI / 180)));
        }, G * Math.Tan(25 * Math.PI / 180));
    }

    // ------------------------------------------------------------------ arrivals

    /// <summary>Spawns an arrival <paramref name="distNm"/> track miles from touchdown.</summary>
    public Flight? SpawnArrival(double distNm, bool test, ScheduleLeg? leg = null, (RunwayEnd? rwy, (Procedure p, Transition? t)? star)? planned = null,
        (V2 pos, double altFt, double cruiseFt)? entry = null, List<V2>? via = null)
    {
        if (ArrRwy is null) SelectRunways(true);
        var plan = planned ?? (leg is not null ? PlanArrival(leg.Dep, leg.Type) : (ArrRwy, null));
        var rwy = plan.Item1;
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
            plan = PlanArrival(other);
            if (plan.Item1 is { } r2 && r2.LengthM >= perf.MinRunwayM) rwy = r2;
        }
        var f = new Flight { Id = NewId(), Callsign = cs, Airline = id.Airline, Type = id.Type, Other = other, Kind = "arrival", P = perf, Rwy = rwy, Test = test,
            Reg = leg?.Reg ?? "", Title = leg?.Title ?? "", Brand = leg?.Airline };
        f.StarPick = plan.Item2;
        f.Via = via;
        f.OnGround = false;
        f.Gear = false;
        if (entry is { } en)
        {
            BuildArrivalPath(f, null, en.pos);
            f.S = 0;
            var (ep, edir, _) = f.Path!.At(0);
            f.Pos = ep; f.Heading = edir;
            f.CruiseFt = Math.Max(en.cruiseFt, en.altFt);
            // Never start above what the procedure ahead allows: at the start of a session flights appear wherever the
            // schedule has them, some already on the STAR (SWA2236 29 nm out at 11,270 ft, 5 nm before "at 7,000" at
            // BUNIE) — they'd have been brought down to it already.
            f.AltFt = Math.Min(en.altFt, Restricted(f, en.altFt));
            // And no higher than a 3:1 descent along the way it will fly, +1,000 ft (22 Nov: as an airport or neighbour
            // starts, arrivals appear where the whole sky had them — UAL2036 74 nm out at 30,850 ft, SWA2463 52 nm out
            // at 24,640 ft — the whole sky's own descent for an airport not yet run in detail).
            var dth0 = f.ThrS / Geo2.MPerNm;
            f.AltFt = Math.Max(Math.Min(f.AltFt, rwy.ElevationFt + 1000 + 300 * dth0), Math.Min(en.altFt, rwy.ElevationFt + 3000));
            f.Ias = ArrivalSpeed(f, dth0);
            f.Gs = Wind.Tas(f.Ias, f.AltFt);
            f.SetPhase("arriving", Time);
            Flights.Add(f);
            return f;
        }
        BuildArrivalPath(f, null);
        f.S = Math.Max(0, f.ThrS - distNm * Geo2.MPerNm);
        var (p, dir, _) = f.Path!.At(f.S);
        f.Pos = p;
        f.Heading = dir;
        var dth = (f.ThrS - f.S) / Geo2.MPerNm;
        f.AltFt = Restricted(f, Math.Min(rwy.ElevationFt + 11000, Profile(rwy, dth)));
        // Restrictions already behind where it starts aren't its to meet.
        f.Checks.RemoveAll(c => c.S <= f.S + 50);
        f.Ias = ArrivalSpeed(f, dth);
        f.Gs = f.Ias;
        f.Gear = dth < 6.5;
        f.SetPhase(dth < 12 ? "approach" : "arriving", Time);
        Flights.Add(f);
        return f;
    }

    /// <summary>
    /// The planned descent (energy model): about 318 ft per nm (3°, the pilots' 3:1), except level pieces where the
    /// aircraft slows down, because a jet can't slow while descending on a normal path (Airbus / FSF: clean on a 3°
    /// path it can't decelerate at all). Slowing in level flight takes about 1 nm per 10 kt clean and 12.5 kt per nm
    /// with approach flaps, so 250→210 levels off for 4 nm at 20 nm out, 210→180 for 2.4 nm at 12 nm and 180→170
    /// for 0.8 nm at 7 nm; the glidepath is then joined at about 2,000 ft, 6 nm out.
    /// </summary>
    private static double Profile(RunwayEnd rwy, double dthNm)
    {
        // Above 10,000 ft: a descent path with a 4 nm level piece at 10,000 ft to slow to 250 kt (14 CFR 91.117).
        var d10 = D10(rwy);
        // A slightly shallower plan above it (about 290 ft per nm) puts the top of descent a little early, so the
        // aircraft reaches 10,000 ft on the path with room to slow, as crews plan (3:1 plus a margin).
        if (dthNm > d10 + 4) return 10000 + (dthNm - d10 - 4) * 290;
        if (dthNm > d10) return 10000;   // 4 nm level: 280 → 250 kt at about 10 kt per nm
        return ProfileLow(rwy, dthNm);
    }

    /// <summary>Distance from the runway where the planned descent reaches 10,000 ft.</summary>
    private static double D10(RunwayEnd rwy)
    {
        var level = SlowSteps.Sum(st => (st.fromKt - st.toKt) / (st.fromKt > 210 ? 10.0 : 12.5));
        return (10000 - rwy.ElevationFt - 50) / 318.4 + level;
    }

    private static double ProfileLow(RunwayEnd rwy, double dthNm)
    {
        var d = Math.Max(0, dthNm);
        var level = 0.0;
        foreach (var (at, from, to) in SlowSteps)
        {
            var len = (from - to) / (from > 210 ? 10.0 : 12.5);
            level += Math.Clamp(d - (at - len), 0, len);
        }
        return rwy.ElevationFt + 50 + (d - level) * 318.4;
    }

    private static readonly (double atNm, double fromKt, double toKt)[] SlowSteps = { (20, 250, 210), (12, 210, 180), (7, 180, 170) };

    /// <summary>
    /// How fast the aircraft can slow down right now (kt per second), from its configuration and how steeply it is
    /// descending: in level flight about 10 kt per nm clean, 12.5 with approach flaps, 25 with gear and landing
    /// flaps (Airbus Flight Operations Briefing Note / FSF ALAR 4.2), minus about 1 kt per nm for every 32 ft per nm
    /// of descent (a 3° path takes ~10, so clean on a 3° path ≈ 0), plus a little for speedbrakes when in a hurry.
    /// </summary>
    private static double DecelRate(Flight f, double targetKt, double aglFt)
    {
        double cfg = f.Gear ? 25 : f.Ias <= f.P.MinClean + 2 ? 12.5 : 10;
        var gs = Math.Max(60, f.Gs);
        var gradient = -f.Vs * 60 / gs;   // ft per nm of descent (negative when climbing)
        var brakes = !f.Gear && aglFt > 1000 && f.Ias - targetKt > 10 ? (f.Ias > 250 ? 3.0 : 1.5) : 0;
        var perNm = cfg + brakes - gradient / 32;
        return Math.Clamp(perNm * gs / 3600, 0, 1.5);
    }

    /// <summary>The arrival route: STAR, then the approach (or vectors to final), then the runway.</summary>
    /// <summary>
    /// The filed-route points still ahead of a position: those after the leg it is on (the leg nearest to it, the leg
    /// into the first point counting from the position itself), and none within 3 nm. The old test (is the next point
    /// nearer than the one before it?) kept every point once it was well past several (22–23 Nov, KBOS: a longer real
    /// track for JBU734 rebuilt its route back to airway points 100+ nm behind — 155 nm to go became 488 — and it turned
    /// round; the whole stream behind it was torn up).
    /// </summary>
    public static List<V2> ViaAhead(List<V2> fv, V2 pos)
    {
        if (fv.Count == 0) return new();
        // The leg (k-1 → k) nearest the position; leg 0 is the one into the first point.
        var bestK = 0; var bestD = double.MaxValue;
        for (var k = 1; k < fv.Count; k++)
        {
            var d = SegDist(pos, fv[k - 1], fv[k]);
            if (d < bestD - 1) { bestD = d; bestK = k; }
        }
        // Beyond the last point (closer to it, and past it along the last leg): nothing left.
        if (fv.Count >= 2)
        {
            var last = fv[^1]; var legDir = (last - fv[^2]);
            if (legDir.Length > 1 && V2.Dot(pos - last, legDir) > 0 && (pos - last).Length < bestD + 1) return new();
        }
        if (fv.Count == 1 || (bestK == 1 && (pos - fv[0]).Length <= bestD + 1 && V2.Dot(pos - fv[0], fv[1] - fv[0]) < 0)) bestK = 0;
        var res = fv.Skip(bestK).ToList();
        while (res.Count > 0 && (res[0] - pos).Length < 3 * Geo2.MPerNm) res.RemoveAt(0);
        return res;
    }

    private void BuildArrivalPath(Flight f, V2? from, V2? entry = null)
    {
        f.VectorRejoinS = double.NegativeInfinity;
        f.DetourSec = 0;
        var altFixes = new List<AirPoint>();
        var named = new List<AirPoint>();
        var rwy = f.Rwy!;
        var route = new List<V2>();
        var speeds = new List<double>();
        var names = new List<string>();
        int starIdx0 = -1, starIdx1 = -1;
        var usedModel = false; var modelNote = "";
        if (from is { } start)
        {
            route.Add(start);
            route.AddRange(Vectors(start, rwy, ShapeFor(rwy)));
        }
        else
        {
            var star = PickStar(f, rwy, out var starLegs);
            var starPts = star is null ? new List<AirPoint>() : Apt.LegsToPoints(starLegs, entry ?? V2.FromBearing(Rng.Next(360), 40 * Geo2.MPerNm), 11000, out _);
            // A STAR shared with another airport (MLB's BITHO7 is MCO's too): where this copy has no restriction at a fix
            // and the other airport's copy does, that one applies.
            if (star is not null && SharedArrivals is { } shared)
                for (var k = 0; k < starPts.Count; k++)
                {
                    var sp = starPts[k];
                    if (sp.Fix.Length == 0 || sp.AltMinFt > 0 || sp.AltMaxFt > 0) continue;
                    var other = shared.Where(a => AirportRules.Base(a.Name) == AirportRules.Base(star.Name))
                        .SelectMany(a => a.Legs.Concat(a.RunwayTransitions.SelectMany(t => t.Legs)).Concat(a.EnrouteTransitions.SelectMany(t => t.Legs)))
                        .FirstOrDefault(l => l.Fix.Equals(sp.Fix, StringComparison.OrdinalIgnoreCase) && l.AltDesc > 0);
                    if (other is null) continue;
                    double mn = 0, mx = 0;
                    switch (other.AltDesc) { case 1: mn = mx = other.Alt1Ft; break; case 2: mn = other.Alt1Ft; break; case 3: mx = other.Alt1Ft; break; case 4: mx = other.Alt1Ft; mn = other.Alt2Ft; break; }
                    starPts[k] = sp with { AltMinFt = mn, AltMaxFt = mx, SpeedKt = sp.SpeedKt is > 0 and < 400 ? sp.SpeedKt : other.SpeedKt };
                }
            // The chart's "expect" altitudes and speeds are restrictions too (user, 13 Nov), where the procedure data
            // has none at that fix.
            if (star is not null && StarExpects is { } se && se.TryGetValue(star.Name, out var exps))
                for (var k = 0; k < starPts.Count; k++)
                {
                    var sp = starPts[k];
                    if (sp.Fix.Length == 0) continue;
                    // All that apply to this arrival (BITHO7's LAMMA: 12,000 ft landing Orlando, and 250 kt when
                    // Orlando lands south); one for this runway or flow first.
                    foreach (var ex in exps.Where(e => e.fix.Equals(sp.Fix, StringComparison.OrdinalIgnoreCase) && ExpectApplies(e.when, rwy.Ident, rwy.Heading))
                                 .OrderBy(e => e.when.Length == 0))
                    {
                        if (sp.AltMinFt <= 0 && sp.AltMaxFt <= 0 && (ex.min > 0 || ex.max > 0)) sp = sp with { AltMinFt = ex.min, AltMaxFt = ex.max };
                        if (sp.SpeedKt is <= 0 or >= 400 && ex.kt > 0) sp = sp with { SpeedKt = ex.kt };
                    }
                    starPts[k] = sp;
                }
            // Handed over from the whole sky: direct to the STAR, joining it at the first fix that isn't further out
            // than the aircraft already is.
            // The fix it's cleared direct to must be ahead: not behind it on the way in (heading away from the airport),
            // and not one it would have to pass and then turn back from by more than 90° to follow the STAR. The whole sky
            // flies the great circle, so an arrival can be beside its STAR's first fix (JBU228 from San Juan, east of
            // MUNGI): it then goes direct to the next fix (HIJON) instead of flying back to MUNGI and looping round.
            // Many STARs run on to a fix at or near the airport and end in "expect radar vectors" (LaGuardia's VADDR1
            // ends right over the field). Real controllers turn the traffic off them well before that, onto a downwind
            // or base, so the STAR is only flown until it's about 8 nm from the airport.
            var near0 = starPts.FindIndex(p => p.P.Length < 8 * Geo2.MPerNm);
            if (near0 >= 0) starPts.RemoveRange(near0, starPts.Count - near0);
            // The chart's "Expect radar vectors to final approach course after PAPPI" (WYNDE3 landing west, user 21 Nov):
            // without real tracks for this STAR and runway to follow, the STAR is flown to that fix and the vectors
            // start there, not at the STAR's last fix.
            if (star is not null && StarExpects is { } se2 && se2.TryGetValue(star.Name, out var vexp)
                && vexp.Where(e => e.how == "vectors after" && ExpectApplies(e.when, rwy.Ident, rwy.Heading)).OrderBy(e => e.when.Length == 0).ToList() is [var vAfter, ..])
            {
                var vi = starPts.FindIndex(p => p.Fix.Equals(vAfter.fix, StringComparison.OrdinalIgnoreCase));
                if (vi >= 0 && vi < starPts.Count - 1 && RealFan(rwy, starPts) is not { Count: >= 3 })
                {
                    starPts.RemoveRange(vi + 1, starPts.Count - vi - 1);
                    if (_vecNoteLogged.Add($"{star.Name}|{rwy.Ident}")) Log($"{star.Name} for {rwy.Ident}: vectors to final after {vAfter.fix} (the chart), no real tracks to follow");
                }
            }
            var fullStar = starPts.ToList();
            var trimmed = 0;
            // Still on its filed route (airways) to the STAR: those points first, dropping the ones it has passed; the
            // STAR is joined from the last of them.
            var via = new List<V2>();
            if (entry is { } env && f.Via is { Count: > 0 } fv)
            {
                via = ViaAhead(fv, env);
                f.Via = via.Count > 0 ? via : null;
            }
            if ((via.Count > 0 ? via[^1] : entry) is { } en0)
            {
                bool Ahead(int k)
                {
                    var p = starPts[k].P;
                    if (p.Length > en0.Length + 5 * Geo2.MPerNm) return false;
                    var to = p - en0;
                    if (to.Length < 2 * Geo2.MPerNm) return k + 1 >= starPts.Count;             // practically there: the next one, if any
                    if (Math.Abs(Geo2.Diff(to.Bearing, (new V2(0, 0) - en0).Bearing)) > 90) return false;     // behind, away from the airport
                    if (k + 1 >= starPts.Count) return true;
                    var outb = starPts[k + 1].P - p;
                    return outb.Length < 1 || Math.Abs(Geo2.Diff(to.Bearing, outb.Bearing)) <= 90;
                }
                var k0 = Enumerable.Range(0, starPts.Count).FirstOrDefault(Ahead, -1);
                if (k0 > 0) { starPts.RemoveRange(0, k0); trimmed = k0; }
                else if (k0 < 0) { trimmed = starPts.Count; starPts.Clear(); }
            }
            // Where real traffic is turned off this STAR and how it's vectored onto final (adsb.lol): the STAR up to
            // there, then the way a real arrival on it flew to the final (user, 15 Nov, KORD: arrivals from the
            // northwest "blindly following the STAR and not taking into consideration the ADSB data on vectors to final").
            (int cut, List<V2> pts)? realVec = null;
            // The fan comes from the whole STAR (as far as 8 nm out), so an arrival that starts part-way along it, or
            // already past where real traffic turns off it, still flies the real tracks.
            var fan = fullStar.Count >= 2 && !_noRealWay ? RealFan(rwy, fullStar) : null;
            var fanKey = $"{rwy.Ident}|{star?.Name}";
            f.Fan = null;
            if (fan is { Count: >= 3 })
            {
                if (f.FanKey != fanKey) { f.FanKey = fanKey; f.FanIdx = _fanQuiet; }
                f.FanIdx = Math.Clamp(f.FanIdx, 0, fan.Count - 1);
                var cutT = fan[f.FanIdx].cut - trimmed;
                if (cutT >= 0 && cutT < starPts.Count)
                {
                    realVec = (cutT, fan[f.FanIdx].pts);
                    f.Fan = fan;
                    f.FanLeave = fan[f.FanIdx].pts[0];
                }
                else if (entry is { } en2)
                {
                    // Already past where real traffic turns off (user, 19 Nov, UAL1664 put on a made-up downwind when
                    // traffic started close in): join the real track that passes nearest, from the point after the
                    // nearest one, if one passes within 5 nm.
                    var bestD = 5 * Geo2.MPerNm; int bi = -1, bj = -1;
                    for (var t = 0; t < fan.Count; t++)
                    {
                        var tp = fan[t].pts;
                        for (var j = 0; j + 3 < tp.Count; j++)
                        {
                            var d = (tp[j] - en2).Length;
                            if (d < bestD) { bestD = d; bi = t; bj = j; }
                        }
                    }
                    if (bi >= 0)
                    {
                        f.FanIdx = bi; f.Fan = fan; f.FanLeave = fan[bi].pts[0];
                        starPts.Clear();
                        starPts.AddRange(fan[bi].pts.Skip(bj + 1).Select(q => new AirPoint(q, "")));
                        realVec = (starPts.Count - 1, new List<V2>());
                    }
                }
            }
            if (f.Fan is null) f.FanKey = null;
            // The learned way in (Step 6, part 2): from where the arrival comes within 45 nm of the runway — the STAR
            // up to there, or where it is when it's already inside — the way the model says real traffic flies to this
            // runway from there, in this wind and this busy. It replaces the real-track pieces above; the final, the
            // route check (with the next candidate, then the standard way in, if it's refused) and the spacing tools
            // all work on it as on any way in.
            if (LearnedRoutes is { } model && !_noRealWay && f != UserArr && _routeModelTry < model.K)
            {
                var nm45 = 45 * Geo2.MPerNm;
                V2 mAt; double mTrack; var keepStar = -1;
                if (entry is { } enm && (enm - rwy.Threshold).Length <= nm45)
                {
                    mAt = enm;
                    var nxt = via.Count > 0 ? via[0] : starPts.Count > 0 ? starPts[0].P : rwy.Threshold;
                    mTrack = (nxt - enm).Length > 100 ? (nxt - enm).Bearing : f.Heading;
                }
                else
                {
                    var k = starPts.FindIndex(q => (q.P - rwy.Threshold).Length <= nm45);
                    if (k < 0 && starPts.Count > 0) k = starPts.Count - 1;
                    if (k < 0) { mAt = default; mTrack = double.NaN; }
                    else
                    {
                        mAt = starPts[k].P; keepStar = k - 1;
                        var prev = k > 0 ? starPts[k - 1].P : entry ?? starPts[k].P - V2.FromBearing(f.Heading, 1000);
                        mTrack = (mAt - prev).Length > 100 ? (mAt - prev).Bearing : f.Heading;
                    }
                }
                if (!double.IsNaN(mTrack))
                {
                    var distNm = (mAt - rwy.Threshold).Length / Geo2.MPerNm;
                    var altEst = rwy.ElevationFt + Math.Min(16000, 330 * distNm);
                    var myEta = Time + distNm * 3600 / 250;
                    var arrs = Flights.Where(o => o != f && !o.Done && o.Kind == "arrival" && !o.OnGround && !double.IsNaN(o.EtaNominal) && Math.Abs(o.EtaNominal - myEta) < 1800).ToList();
                    var feats = RouteModel.Features(rwy, mAt, altEst, mTrack, Wind, arrs.Count(o => o.Rwy?.Runway == rwy.Runway), arrs.Count);
                    var ways = model.Predict(Apt.Icao, rwy, feats);
                    if (_routeModelTry < ways.Count)
                    {
                        var way = ways[_routeModelTry];
                        // Up to where it's lined up on the final (inside 12 nm, within 0.3 nm of the centreline, going the
                        // runway's way): the approach takes over from there.
                        // (Starting at the entry itself: the route already begins there.)
                        var mp = keepStar < 0 && entry is not null ? new List<V2>() : new List<V2> { mAt };
                        for (var i = 0; i < way.Points.Count; i++)
                        {
                            var q = way.Points[i];
                            var outNm = -rwy.Along(q) / Geo2.MPerNm;
                            if (outNm < 2.5) break;
                            mp.Add(q);
                            var lined = i > 0 && Math.Abs(rwy.Offset(q)) < 0.3 * Geo2.MPerNm && outNm < 12
                                && Math.Abs(Geo2.Diff((q - way.Points[i - 1]).Bearing, rwy.Heading)) < 20;
                            if (lined) break;
                        }
                        if (mp.Count == 0 && keepStar < 0) mp.Add(mAt + (rwy.Threshold - mAt).Unit * 100);
                        realVec = (keepStar, mp);
                        if (keepStar < 0) via.Clear();
                        f.Fan = null; f.FanKey = null;
                        usedModel = true;
                        modelNote = $"the learned way in (LAIA's model, {100 * way.Prob:0}% likely{(_routeModelTry > 0 ? $", choice {_routeModelTry + 1}" : "")})";
                    }
                }
            }
            var starOnly = starPts.Count;
            if (realVec is { } rv)
            {
                starOnly = rv.pts.Count == 0 ? 0 : rv.cut + 1;   // joined a real track: no STAR left
                starPts.RemoveRange(rv.cut + 1, starPts.Count - rv.cut - 1);
                starPts.AddRange(rv.pts.Select(q => new AirPoint(q, "")));
            }
            // No real tracks off this STAR to this runway (user, 19 Nov, AAL2867 on SHAIN2 for 27C sent straight across
            // the field to the approach's first fix): from where the STAR ends, the way real arrivals to this runway
            // that passed there flew on — whichever STAR they came by.
            // Real traffic often leaves a STAR long before its end (20 Nov, KLAS: every CRESO5 arrival to 01L flew the
            // STAR round the east side and a built pattern back — SWA2522's loop — while real arrivals from there are
            // turned straight in): from the STAR's end back towards its start, the first point real arrivals to this
            // runway passed going its way, and the STAR is left there.
            if (realVec is null && starPts.Count > 0 && !_noRealWay)
            {
                for (var k = starPts.Count - 1; k >= 0; k--)
                {
                    // At the STAR's end any way on (as before); earlier along it, only traffic going the STAR's way.
                    var dir = k == starPts.Count - 1 ? double.NaN : k > 0 ? (starPts[k].P - starPts[k - 1].P).Bearing : (starPts[1].P - starPts[0].P).Bearing;
                    if (DirectionTrack(rwy, starPts[k].P, dir) is not { } dtr) continue;
                    starPts.RemoveRange(k + 1, starPts.Count - k - 1);
                    starPts.AddRange(dtr.Select(q => new AirPoint(q, "")));
                    realVec = (starPts.Count - 1, new List<V2>());
                    break;
                }
            }
            if (star is not null && starPts.Count > 0) names.Add(star.Name);
            // The STAR's restrictions, kept before the approach join trims the STAR (the joining fix keeps its restriction).
            altFixes.AddRange(starPts.Where(p => p.AltMinFt > 0 || p.AltMaxFt > 0 || p.SpeedKt is >= 100 and < 400));
            named.AddRange(starPts.Where(p => p.Fix.Length > 0));
            var (appName, appPts) = realVec is not null ? FinalFrom(rwy, starPts[^1].P) : ApproachPoints(rwy, starPts);
            if (usedModel) appName = appName.Replace("vectors as real traffic flies them (adsb.lol)", modelNote);
            if (appName.Length > 0) names.Add(appName);
            if (entry is { } en1)
            {
                route.Add(en1);
                speeds.Add(0);
                foreach (var v in via) { route.Add(v); speeds.Add(0); }
            }
            else if (starPts.Count == 0)
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
            altFixes.AddRange(appPts.Where(p => p.AltMinFt > 0 || p.AltMaxFt > 0 || p.SpeedKt is >= 100 and < 400));
            named.AddRange(appPts.Where(p => p.Fix.Length > 0));
            starIdx0 = route.Count; starIdx1 = route.Count + Math.Min(starOnly, starPts.Count);
            route.AddRange(starPts.Select(p => p.P));
            speeds.AddRange(starPts.Select(p => p.SpeedKt));
            if (appPts.Count > 0) { route.AddRange(appPts.Select(p => p.P)); speeds.AddRange(appPts.Select(p => p.SpeedKt)); }
            else route.AddRange(Vectors(route[^1], rwy, ShapeFor(rwy)));
            while (speeds.Count < route.Count) speeds.Add(0);
            if (realVec is null) ShapeFinal(route, speeds, rwy, ShapeFor(rwy));   // real vectors are flown as real traffic flew them
        }
        var thrIndex = route.Count;
        route.Add(rwy.Threshold);
        route.Add(rwy.FarEnd);
        // The route check: a real-track way in that fails it is dropped for the standard one (published arrival, then
        // a downwind, base and final), and that's logged.
        if (!_noRealWay) _realWayRefused = false;
        if (from is null && !_noRealWay && RouteProblem(route, thrIndex, rwy) is { } why)
        {
            _routesRefused++;
            if (usedModel && LearnedRoutes is { } lm && _routeModelTry + 1 < lm.K)
            {
                Log($"{f.Callsign}: route refused — {why}; the learned way's next choice instead");
                _routeModelTry++;
                try { BuildArrivalPath(f, from, entry); } finally { _routeModelTry = 0; }
                return;
            }
            Log($"{f.Callsign}: route refused — {why}; flies the standard way in instead");
            _noRealWay = true;
            try { BuildArrivalPath(f, from, entry); } finally { _noRealWay = false; }
            _realWayRefused = true;
            return;
        }
        // Remove points that would make the aircraft double back near the runway.
        f.Route = string.Join(" → ", names);
        var temp = AirPath(route, _ => 200, 5000);
        var thrS = temp.PointS[thrIndex];
        f.Path = AirPath(route, i =>
        {
            if (i >= thrIndex) return 1;
            var d = (thrS - temp.PointS[i]) / Geo2.MPerNm;
            return d > 40 ? 290 : d > 20 ? 250 : d > 12 ? 210 : d > 7 ? 180 : 160;
        }, 6000, from: f, altAt: i => i >= thrIndex ? 0 : Math.Clamp(rwy.ElevationFt + 300 * (thrS - temp.PointS[i]) / Geo2.MPerNm, 3000, 16000));
        f.ThrS = f.Path.PointS[thrIndex];
        f.StarNm = starIdx0 >= 0 && starIdx1 > starIdx0 ? (f.ThrS - f.Path.PointS[starIdx0]) / Geo2.MPerNm : 0;
        f.AppNm = starIdx1 >= 0 && starIdx1 < thrIndex ? (f.ThrS - f.Path.PointS[starIdx1]) / Geo2.MPerNm : f.ThrS / Geo2.MPerNm;
        if (f.StarNm < f.AppNm) f.StarNm = f.AppNm;
        CheckLoops(f, "arrival route");
        f.LandingChecked = false;
        f.StableChecked = false;
        // A STAR fix's speed limit stays when the approach joins there (the approach's own copy of the fix often has none).
        foreach (var c in altFixes.Where(c => c.SpeedKt is >= 100 and < 400))
        {
            var i = route.FindIndex(q => (q - c.P).Length < 1);
            if (i >= 0 && i < speeds.Count && speeds[i] < 100) speeds[i] = c.SpeedKt;
        }
        f.SpeedLimits.Clear();
        for (var i = 0; i < speeds.Count && i < f.Path.PointS.Length; i++)
            if (speeds[i] >= 100 && speeds[i] < 400) f.SpeedLimits.Add((f.Path.PointS[i], speeds[i]));
        // The STAR's and approach's altitude restrictions, at the fixes still on the route (vectors may have cut some).
        f.AltLimits.Clear();
        var onRoute = new List<(double, AirPoint)>();
        foreach (var c in altFixes)
        {
            var i = route.FindIndex(q => (q - c.P).Length < 1);
            if (i < 0 || i >= thrIndex) continue;
            if (c.AltMinFt > 0 || c.AltMaxFt > 0) f.AltLimits.Add((f.Path.PointS[i], c.AltMinFt, c.AltMaxFt));
            if (c.Fix.Length > 0 && !onRoute.Any(x => x.Item2.Fix == c.Fix)) onRoute.Add((f.Path.PointS[i], c));
        }
        // Real traffic's altitudes along the whole way in, as ceilings where nothing published says otherwise (user, 19
        // Nov: "at the very least it could give the injection some guidance at what altitudes to start aircraft if the
        // STAR data is missing or not specified"): every 3 nm from where it is to 8 nm out, no higher than the real
        // arrivals to this runway near there (median + 500 ft, five or more needed) — never below a published floor
        // still ahead. The hand-over starts an arrival no higher than these allow (and down in time for them).
        if (Learned is { Samples.Count: > 0 })
        {
            var published = f.AltLimits.ToList();
            var nmR = Geo2.MPerNm;
            for (var sR = 0.0; sR < f.ThrS - 8 * nmR; sR += 3 * nmR)
            {
                var at = f.Path.At(sR);
                var alt = RealAltAt(rwy, at.p, at.dir);
                if (double.IsNaN(alt)) continue;
                var ceil = Math.Ceiling((alt + 500) / 100) * 100;
                // Never one far under this arrival's own descent for the miles it still has to fly (27 Nov, KJFK: the
                // real-track downwind for 22L crosses the final 2 nm out; the real arrivals' altitude there — on short
                // final — became a ceiling 25 flying miles from landing, and AAL234, JBU1119, AAL307 and JBU2824 came down
                // to 853 ft, flew the downwind level there and went around "not stable, 12 nm out at 855 ft").
                if (ceil < ProfileLow(rwy, (f.ThrS - sR) / nmR) - 1500) continue;
                // Published restrictions win (user, 19 Nov: "make sure STAR altitude restrictions trump ADSB data"): no
                // real-traffic ceiling under a published floor still ahead, none near a published limit, and none
                // tighter than the 3:1 descent to a published "at or below" ahead would allow anyway is needed.
                var floor = published.Where(c => c.S >= sR && c.Min > 0).Select(c => c.Min).DefaultIfEmpty(0).Max();
                if (published.Any(c => Math.Abs(c.S - sR) < 2 * nmR && c.Max > 0)) continue;   // a published one right there
                // …nor so soon after a published floor that it couldn't be met: room to be at that floor over its fix and
                // come down at 300 ft per nm after it (Midway, 19 Nov: STKNY/HALIE "at 6,000" crossed at 5,550 ft — a
                // real-traffic ceiling 2–3 nm past the fix pulled the descent through it).
                var behind = published.Where(c => c.S < sR && c.Min > 0).Select(c => c.Min - (sR - c.S) / nmR * 300).DefaultIfEmpty(0).Max();
                f.AltLimits.Add((sR, 0, Math.Max(Math.Max(ceil, floor), behind)));
            }
            f.AltLimits.Sort((x, y) => x.S.CompareTo(y.S));
        }
        SetChecks(f, onRoute);
        // Every named fix still on the route (for the predicted times at the fixes).
        f.Fixes.Clear(); f.FixEtas.Clear(); f.FixPred10.Clear();
        foreach (var c in named)
        {
            var i = route.FindIndex(q => (q - c.P).Length < 1);
            if (i >= 0 && i < thrIndex && !f.Fixes.Any(x => x.Fix == c.Fix)) f.Fixes.Add((f.Path.PointS[i], c.Fix));
        }
        f.Fixes.Sort((a, b) => a.S.CompareTo(b.S));
    }

    /// <summary>
    /// The STAR (and its entry transition) that starts on the side of the airport the flight comes from: an arrival
    /// from San Juan comes in on a STAR from the southeast, not one from the north.
    /// </summary>
    private Procedure? PickStar(Flight f, RunwayEnd rwy, out List<ProcedureLeg> legs)
    {
        legs = new List<ProcedureLeg>();
        var choice = f.StarPick is { } sp && ServesStar(sp.p, rwy) ? sp : ChooseStar(new[] { rwy }, f.Other, f.P.Family != "turboprop");
        if (choice is null) return null;
        var (star, tr) = choice.Value;
        if (tr is not null) legs.AddRange(tr.Legs);
        legs.AddRange(star.Legs);
        var rt = StarTrans(star, rwy);
        if (rt is not null) legs.AddRange(rt.Legs);
        return star;
    }

    /// <summary>
    /// Real traffic's vectors off a STAR (adsb.lol tracks learned for the airport): of the real arrivals to this runway
    /// that flew this STAR (within 2 nm of it for at least 5 nm), where they were turned off it (more than 2 nm away from
    /// it and staying away), and the path the typical one flew from there until established on final (within 0.3 nm of
    /// the centreline). The typical one is the one turned off at the median distance from the airport. Null when fewer
    /// than three real arrivals flew the STAR, or they stayed on it to its end (then the STAR and the usual vectors apply).
    /// </summary>
    private (int cut, List<V2> pts)? RealVectors(RunwayEnd rwy, List<AirPoint> starPts)
    {
        if (Learned is not { Samples.Count: > 0 } prof) return null;
        var nm = Geo2.MPerNm;
        var star = starPts.Select(p => p.P).ToList();
        double ToStar(V2 p) { var d = double.MaxValue; for (var i = 0; i + 1 < star.Count; i++) d = Math.Min(d, DistToSegment(p, star[i], star[i + 1])); return d; }
        // How far along the STAR (m from its first point) the nearest point to p is.
        double Progress(V2 p)
        {
            double best = double.MaxValue, at = 0, run = 0;
            for (var i = 0; i + 1 < star.Count; i++)
            {
                var seg = star[i + 1] - star[i]; var len = seg.Length;
                var t = len < 1 ? 0 : Math.Clamp(V2.Dot(p - star[i], seg) / (len * len), 0, 1);
                var d = (star[i] + seg * t - p).Length;
                if (d < best) { best = d; at = run + t * len; }
                run += len;
            }
            return at;
        }
        var found = new List<(double leaveNm, V2 leave, List<V2> rest, List<double> alts, HashSet<string>? deps)>();
        _depIdsNow = (DepRwys.Count > 0 ? DepRwys : DepRwy is null ? new List<RunwayEnd>() : new() { DepRwy }).Select(d => d.Ident).ToHashSet();
        foreach (var smp in prof.Samples)
        {
            if (smp.Kind != "arrival" || smp.Runway != rwy.Ident || smp.Points.Count < 6) continue;
            var pq = smp.Points.Where(q => q.Length >= 2).ToList();
            var pts = pq.Select(q => Apt.Map.ToXY(q[0], q[1])).ToList();
            // Flew the STAR: within 2 nm of it for 5 nm or more.
            int i = 0; double onFor = 0; int lastOn = -1;
            for (; i < pts.Count; i++)
            {
                if (ToStar(pts[i]) < 2 * nm) { if (lastOn >= 0) onFor += (pts[i] - pts[lastOn]).Length; lastOn = i; }
                else if (lastOn >= 0 && onFor >= 5 * nm)
                {
                    // Left it: and stays more than 2 nm away from here on (not a wiggle).
                    var stays = true;
                    for (var j = i; j < Math.Min(pts.Count, i + 4); j++) if (ToStar(pts[j]) < 2 * nm) { stays = false; break; }
                    if (stays) break;
                }
            }
            if (lastOn < 0 || onFor < 5 * nm || i >= pts.Count) continue;
            // Came in on this STAR, not across it (user, 19 Nov, DAL2646 on WYNDE3: "no ADS-B track even comes close"):
            // traffic from another arrival running beside a piece of the STAR for 5 nm — a downwind passing over its
            // last leg — counted as having flown it, and its downwind became a "WYNDE3" track. The track must be on the
            // STAR within its first 20 nm (it starts 45 nm out, on its way in) and move along it in the STAR's direction.
            int first = -1; double flown = 0;
            for (var k = 0; k < pts.Count; k++)
            {
                if (k > 0) flown += (pts[k] - pts[k - 1]).Length;
                if (flown > 20 * nm) break;
                if (ToStar(pts[k]) < 2 * nm) { first = k; break; }
            }
            if (first < 0 || Progress(pts[lastOn]) - Progress(pts[first]) < 4 * nm) continue;
            // From the turn off the STAR until established on final.
            var rest = new List<V2>();
            var alts = new List<double>();
            for (var j = lastOn; j < pts.Count; j++)
            {
                rest.Add(pts[j]);
                alts.Add(pq[j].Length >= 3 ? pq[j][2] : double.NaN);
                if (Math.Abs(rwy.Offset(pts[j])) < 0.3 * nm && rwy.Along(pts[j]) < -2 * nm && rwy.Along(pts[j]) > -20 * nm) break;
            }
            if (rest.Count < 2 || Math.Abs(rwy.Offset(rest[^1])) >= 0.3 * nm) continue;
            found.Add((pts[lastOn].Length / nm, pts[lastOn], rest, alts, DepsAround(prof, smp.T)));
            _fanBusy[rest] = Busy(prof, smp.T);
        }
        // Only the ordinary ways in (user, 19 Nov: WYNDE3 arrivals put on downwinds "no ADS-B track even comes close to
        // doing"; the fan ran from 41 to 113 nm): no track that loops (a hold, a 360, a go-around: turning through more
        // than 300° in all), and none much shorter or longer than the usual one.
        static double Len(List<V2> p) { double l = 0; for (var k = 1; k < p.Count; k++) l += (p[k] - p[k - 1]).Length; return l; }
        static double Turned(List<V2> p)
        {
            double t = 0; double? prev = null;
            for (var k = 1; k < p.Count; k++)
            {
                var seg = p[k] - p[k - 1];
                if (seg.Length < 300) continue;
                var b = seg.Bearing;
                if (prev is { } pb) t += Math.Abs(Geo2.Diff(b, pb));
                prev = b;
            }
            return t;
        }
        found = found.Where(x => Turned(x.rest) <= 300).ToList();
        if (found.Count < 3) return null;
        // Never through the departures (user, 24 Nov, KMSP: DAL2654 for 12R on a real track northwest at 10,000–5,000 ft
        // through the fan of runway 17's departures — "absolutely not, it's in the departure corridor"; on the replay 0–2
        // of 1,000 real 12R arrivals passed within a mile and 2,500 ft of it, and up to 41 of 500 real runway-17
        // departures). Each track's whole low part is compared with the real flow: where real departures from the
        // runways in use now are many and real arrivals to this runway few, for 2 nm or more, it isn't used.
        if (CorridorGrids(prof, rwy) is { } grids)
        {
            var kept = found.Where(x => !InDepartureCorridor(x.rest, x.alts, grids)).ToList();
            if (kept.Count >= 3) found = kept;
        }
        // Flown in the same runway configuration where enough were (the departure runways in use then, from the learned
        // take-offs around its landing): a night-time shortcut flown when today's departure runway was quiet isn't one
        // to copy with it busy.
        if (_depIdsNow.Count > 0)
        {
            var same = found.Where(x => x.deps is { } dset && _depIdsNow.All(dset.Contains)).ToList();
            if (same.Count >= 3) found = same;
        }
        var lens = found.Select(x => Len(x.rest)).OrderBy(x => x).ToList();
        var med = lens[lens.Count / 2];
        // Much longer than usual is a hold or a loop; short is kept down to a third of the usual — at a busy airport most
        // real tracks are rush-hour ones, and the quiet-time shortcuts are what an empty sky should get (the repeat check
        // below takes out one-offs).
        found = found.Where(x => Len(x.rest) is var lx && lx >= med * 0.35 && lx <= med * 1.35).ToList();
        // Patterns real controllers used again and again, not one aircraft's one-off (user, 21 Nov: "not just any route
        // it sees willy nilly"): with six or more, each needs another that left the STAR within 3 nm of it and was
        // within 10% of its length.
        if (found.Count >= 6)
        {
            var all = found;
            found = found.Where(x => all.Any(o => !ReferenceEquals(o.rest, x.rest) && (o.leave - x.leave).Length < 3 * nm
                && Math.Abs(Len(o.rest) - Len(x.rest)) < 0.1 * Len(x.rest))).ToList();
        }
        if (found.Count < 3) return null;
        _fanFound = found.OrderBy(x => x.leaveNm).Select(x => (x.leaveNm, x.leave, x.rest)).ToList();
        var typical = _fanFound[_fanFound.Count / 2];
        return Fanned(typical, star);
    }
    private List<(double leaveNm, V2 leave, List<V2> rest)>? _fanFound;
    private HashSet<string> _depIdsNow = new();

    /// <summary>The departure runways real traffic used within 20 minutes of a time (learned take-offs), or null.</summary>
    private HashSet<string>? DepsAround(AirportProfile prof, double t)
    {
        if (t <= 0) return null;
        if (!ReferenceEquals(_depTFor, prof))
        {
            _depTFor = prof;
            _depT = prof.Samples.Where(x => x.Kind == "departure" && x.T > 0).Select(x => (x.T, x.Runway)).OrderBy(x => x.T).ToArray();
        }
        if (_depT.Length == 0) return null;
        int lo = 0, hi = _depT.Length; while (lo < hi) { var m = (lo + hi) / 2; if (_depT[m].T < t - 1200) lo = m + 1; else hi = m; }
        var res = new HashSet<string>();
        for (var i = lo; i < _depT.Length && _depT[i].T <= t + 1200; i++) res.Add(_depT[i].Runway);
        return res.Count == 0 ? null : res;
    }
    private object? _depTFor; private (double T, string Runway)[] _depT = Array.Empty<(double, string)>();

    /// <summary>
    /// Where real traffic flies, on a grid of 1-nm squares and 2,500-ft layers: the real departures from the runways in
    /// use now, and the real arrivals to this runway (each counted once a square). Cached per runway configuration.
    /// </summary>
    private (Dictionary<(int, int, int), int> dep, Dictionary<(int, int, int), int> arr)? CorridorGrids(AirportProfile prof, RunwayEnd rwy)
    {
        if (_depIdsNow.Count == 0) return null;
        var key = $"{rwy.Ident}|{string.Join(",", _depIdsNow.OrderBy(x => x))}";
        if (ReferenceEquals(_gridFor, prof) && _grids.TryGetValue(key, out var g)) return g;
        if (!ReferenceEquals(_gridFor, prof)) { _gridFor = prof; _grids.Clear(); }
        var dep = new Dictionary<(int, int, int), int>(); var arr = new Dictionary<(int, int, int), int>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var smp in prof.Samples)
        {
            var d = smp.Kind == "departure" && _depIdsNow.Contains(smp.Runway);
            var a = smp.Kind == "arrival" && smp.Runway == rwy.Ident;
            if (!d && !a) continue;
            seen.Clear();
            foreach (var q in smp.Points)
            {
                if (q.Length < 3 || q[2] > 15000) continue;
                var c = Cell(Apt.Map.ToXY(q[0], q[1]), q[2]);
                if (seen.Add(c)) { var m = d ? dep : arr; m[c] = m.GetValueOrDefault(c) + 1; }
            }
        }
        var res = (dep, arr);
        _grids[key] = res;
        return res;
    }
    private object? _gridFor;
    private readonly Dictionary<string, (Dictionary<(int, int, int), int> dep, Dictionary<(int, int, int), int> arr)> _grids = new();
    private static (int, int, int) Cell(V2 p, double altFt) => ((int)Math.Floor(p.X / Geo2.MPerNm), (int)Math.Floor(p.Y / Geo2.MPerNm), (int)Math.Floor(altFt / 2500));

    /// <summary>
    /// A real track's low part (below 12,000 ft, beyond 4 nm from the field) runs through where the active departures
    /// fly and the arrivals to its runway don't — 10 or more real departures around a point (a mile and a layer either
    /// way) and fewer than a fifth as many arrivals — for 2 nm or more of it.
    /// </summary>
    private static bool InDepartureCorridor(List<V2> rest, List<double> alts, (Dictionary<(int, int, int), int> dep, Dictionary<(int, int, int), int> arr) g)
    {
        int Around(Dictionary<(int, int, int), int> m, (int x, int y, int z) c)
        {
            var n = 0;
            for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
                n += m.GetValueOrDefault((c.x + dx, c.y + dy, c.z + dz));
            return n;
        }
        double bad = 0;
        for (var i = 1; i < rest.Count && i < alts.Count; i++)
        {
            var alt = alts[i];
            if (double.IsNaN(alt) || alt > 12000 || rest[i].Length < 4 * Geo2.MPerNm) continue;
            var c = Cell(rest[i], alt);
            var dn = Around(g.dep, c); var an = Around(g.arr, c);
            if (dn >= 10 && an * 5 < dn) { bad += (rest[i] - rest[i - 1]).Length; if (bad >= 2 * Geo2.MPerNm) return true; }
        }
        return false;
    }
    private readonly Dictionary<List<V2>, int> _fanBusy = new(ReferenceEqualityComparer.Instance);
    private object? _busyFor; private double[] _busyT = Array.Empty<double>();

    /// <summary>How busy it was when a real arrival landed: the real arrivals (any runway) within 15 minutes of it.</summary>
    private int Busy(AirportProfile prof, double t)
    {
        if (!ReferenceEquals(_busyFor, prof))
        {
            _busyFor = prof;
            _busyT = prof.Samples.Where(x => x.Kind == "arrival" && x.T > 0).Select(x => x.T).OrderBy(x => x).ToArray();
        }
        if (t <= 0 || _busyT.Length == 0) return 0;
        int Lower(double v) { int lo = 0, hi = _busyT.Length; while (lo < hi) { var m = (lo + hi) / 2; if (_busyT[m] < v) lo = m + 1; else hi = m; } return lo; }
        return Math.Max(0, Lower(t + 900.001) - Lower(t - 900) - 1);
    }

    /// <summary>The fan track to start an arrival on when nothing yet asks for delay: the shortest of those flown when
    /// real traffic was quiet (the lower half by how busy it was) — the way in controllers give when the sky is empty
    /// (user, 21 Nov, AMZ3838 at 3 am on the typical daytime downwind). Longer tracks come from the sequence's need.</summary>
    private int _fanQuiet;

    /// <summary>
    /// A point on a real track, with no higher than real traffic to this runway flies there as its ceiling (user, 19 Nov:
    /// Midway arrivals taken over above where real traffic is — off the STAR on real vectors, the STAR's own "6,000 at
    /// STKNY" no longer applied): the median altitude of the real arrivals to the runway within about a mile of it, plus
    /// 500 ft, when five or more passed there. Unnamed, so it isn't counted as a published restriction.
    /// </summary>
    private AirPoint RealPoint(RunwayEnd rwy, V2 q)
    {
        var ceil = RealAltAt(rwy, q);
        return new AirPoint(q, "", 0, double.IsNaN(ceil) ? 0 : Math.Ceiling((ceil + 500) / 100) * 100);
    }
    private AirportProfile? _altGridFor;
    private readonly Dictionary<string, Dictionary<(int, int), List<(double alt, double brg)>>> _altGrid = new();
    /// <summary>
    /// The median altitude of real arrivals to this runway within about a mile of <paramref name="q"/> (five or more) —
    /// only those going the same way (within 60°) when <paramref name="dir"/> is given: a path that crosses the final
    /// on its downwind isn't flying the final there.
    /// </summary>
    private double RealAltAt(RunwayEnd rwy, V2 q, double dir = double.NaN)
    {
        if (Learned is not { Samples.Count: > 0 } prof) return double.NaN;
        if (!ReferenceEquals(_altGridFor, prof)) { _altGrid.Clear(); _altGridFor = prof; }
        var nm = Geo2.MPerNm;
        if (!_altGrid.TryGetValue(rwy.Ident, out var grid))
        {
            grid = new();
            foreach (var smp in prof.Samples)
            {
                if (smp.Kind != "arrival" || smp.Runway != rwy.Ident) continue;
                var seen = new HashSet<(int, int)>();
                V2? prevXy = null;
                foreach (var pt in smp.Points)
                {
                    if (pt.Length < 3 || pt[2] <= 0) continue;
                    var xy = Apt.Map.ToXY(pt[0], pt[1]);
                    var brg = prevXy is { } pv && (xy - pv).Length > 50 ? (xy - pv).Bearing : double.NaN;
                    prevXy = xy;
                    var key = ((int)Math.Floor(xy.X / nm), (int)Math.Floor(xy.Y / nm));
                    if (!seen.Add(key)) continue;   // each real aircraft once per cell
                    if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<(double, double)>();
                    l.Add((pt[2], brg));
                }
            }
            _altGrid[rwy.Ident] = grid;
        }
        var c = ((int)Math.Floor(q.X / nm), (int)Math.Floor(q.Y / nm));
        var alts = new List<double>();
        for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++)
            if (grid.TryGetValue((c.Item1 + dx, c.Item2 + dy), out var l))
                alts.AddRange(l.Where(x => double.IsNaN(dir) || double.IsNaN(x.brg) || Math.Abs(Geo2.Diff(x.brg, dir)) <= 60).Select(x => x.alt));
        if (alts.Count < 5) return double.NaN;
        alts.Sort();
        return alts[alts.Count / 2];
    }

    /// <summary>
    /// The typical real way to this runway's final from a point (the end of a STAR no real track was matched to): real
    /// arrivals to the runway that passed within 5 nm of it, from there until established on final — the one of
    /// middling length among them (three or more needed; no loops). Points about 1 nm apart. Null when there aren't
    /// enough.
    /// </summary>
    private List<V2>? DirectionTrack(RunwayEnd rwy, V2 from, double dir = double.NaN)
    {
        if (!CopyRealTracks) return null;
        if (Learned is not { Samples.Count: > 0 } prof) return null;
        var nm = Geo2.MPerNm;
        // Each runway's real arrivals in this airport's frame, worked out once per learned profile.
        if (!ReferenceEquals(_dirXyFor, prof)) { _dirXyFor = prof; _dirXy.Clear(); }
        if (!_dirXy.TryGetValue(rwy.Ident, out var tracks))
            _dirXy[rwy.Ident] = tracks = prof.Samples.Where(smp => smp.Kind == "arrival" && smp.Runway == rwy.Ident && smp.Points.Count >= 6)
                .Select(smp => smp.Points.Where(q => q.Length >= 2).Select(q => Apt.Map.ToXY(q[0], q[1])).ToList()).ToList();
        var cands = new List<(double len, List<V2> rest)>();
        foreach (var pts in tracks)
        {
            int bi = -1; var bd = 5 * nm;
            for (var i = 0; i < pts.Count; i++) { var d = (pts[i] - from).Length; if (d < bd) { bd = d; bi = i; } }
            if (bi < 0) continue;
            // Going the STAR's way there, not across it.
            if (!double.IsNaN(dir) && bi + 1 < pts.Count && Math.Abs(Geo2.Diff((pts[bi + 1] - pts[bi]).Bearing, dir)) > 60) continue;
            var rest = new List<V2>();
            for (var j = bi + 1; j < pts.Count; j++)
            {
                if (rest.Count == 0 || (pts[j] - rest[^1]).Length >= 1 * nm) rest.Add(pts[j]);
                if (Math.Abs(rwy.Offset(pts[j])) < 0.3 * nm && rwy.Along(pts[j]) < -2 * nm && rwy.Along(pts[j]) > -20 * nm) { if (rest[^1] != pts[j]) rest.Add(pts[j]); break; }
            }
            if (rest.Count < 2 || Math.Abs(rwy.Offset(rest[^1])) >= 0.3 * nm) continue;
            double len = (rest[0] - from).Length, turned = 0; double? pb = null;
            for (var k = 1; k < rest.Count; k++)
            {
                var seg = rest[k] - rest[k - 1]; len += seg.Length;
                if (seg.Length < 300) continue;
                if (pb is { } b0) turned += Math.Abs(Geo2.Diff(b0, seg.Bearing));
                pb = seg.Bearing;
            }
            if (turned > 300) continue;
            cands.Add((len, rest));
        }
        if (cands.Count < 3) return null;
        return Simplify(cands.OrderBy(c => c.len).ElementAt(cands.Count / 2).rest, 0.25 * nm);
    }

    private object? _dirXyFor;
    private readonly Dictionary<string, List<List<V2>>> _dirXy = new();

    /// <summary>One real track off the STAR as a path piece: where on the STAR it turned off, and its points 1 nm apart.</summary>
    private (int cut, List<V2> pts) Fanned((double leaveNm, V2 leave, List<V2> rest) tr, List<V2> star)
    {
        var nm = Geo2.MPerNm;
        int cut = 0; double best = double.MaxValue;
        for (var k = 0; k + 1 < star.Count; k++) { var d = DistToSegment(tr.leave, star[k], star[k + 1]); if (d < best) { best = d; cut = k; } }
        // Thin the real track to points at least 1 nm apart (the radar positions are dense and a little noisy).
        var thin = new List<V2>();
        foreach (var q in tr.rest) if (thin.Count == 0 || (q - thin[^1]).Length >= 1 * nm) thin.Add(q);
        if ((tr.rest[^1] - thin[^1]).Length > 1) thin.Add(tr.rest[^1]);
        // Clean legs (user, 21 Nov, AMZ3838's winding base-to-final): one real aircraft's small heading changes and the
        // radar noise taken out — the shape stays (downwind, base, intercept), turns are flown at the aircraft's radius.
        return (cut, Simplify(thin, 0.25 * nm));
    }

    /// <summary>
    /// The real tracks off this STAR, as path pieces, each with its length from the STAR's first point to established
    /// on final (nm) — the fan real controllers spread arrivals across (user, 18 Nov: "not following the real world
    /// tracks close enough on the STAR, downwind or approach"). Up to 15, from the shortest to the longest way round.
    /// </summary>
    private List<(int cut, List<V2> pts, double lenNm)>? RealFan(RunwayEnd rwy, List<AirPoint> starPts)
    {
        if (!CopyRealTracks) return null;
        if (RealVectors(rwy, starPts) is null || _fanFound is not { Count: >= 3 } found) return null;
        var star = starPts.Select(p => p.P).ToList();
        var nm = Geo2.MPerNm;
        var res = new List<(int cut, List<V2> pts, double lenNm, int busy)>();
        var n = Math.Min(15, found.Count);
        for (var i = 0; i < n; i++)
        {
            var tr = found[(int)Math.Round(i * (found.Count - 1) / (double)Math.Max(1, n - 1))];
            var (cut, pts) = Fanned(tr, star);
            // Real tracks that loop or were vectored round (crossing themselves, leaving the final) aren't ways in to
            // copy: the route check applies to them before they're offered.
            {
                var whole = star.Take(Math.Min(cut + 1, star.Count)).Concat(pts).ToList();
                var ti = whole.Count; whole.Add(rwy.Threshold);
                if (RouteProblem(whole, ti, rwy) is not null) continue;
            }
            double len = 0;
            for (var k = 0; k < cut && k + 1 < star.Count; k++) len += (star[k + 1] - star[k]).Length;
            var prev = star[Math.Min(cut, star.Count - 1)];
            foreach (var q in pts) { len += (q - prev).Length; prev = q; }
            len += -rwy.Along(prev);
            res.Add((cut, pts, len / nm, _fanBusy.TryGetValue(tr.rest, out var bz) ? bz : 0));
        }
        if (res.Count < 3) return null;
        res = res.OrderBy(x => x.lenNm).ToList();
        var medB = res.Select(x => x.busy).OrderBy(x => x).ElementAt(res.Count / 2);
        _fanQuiet = Math.Max(0, res.FindIndex(x => x.busy <= medB));
        return res.Select(x => (x.cut, x.pts, x.lenNm)).ToList();
    }

    /// <summary>The final approach from where a real-vectors path is established on it: the runway's approach points
    /// closer in than that (with their restrictions), or straight down the centreline.</summary>
    private (string name, List<AirPoint> pts) FinalFrom(RunwayEnd rwy, V2 established)
    {
        var app = ApproachFor(rwy) ?? Apt.Data.Approaches.Where(a => a.Runway == rwy.Ident && a.FinalLegs.Count > 0).OrderBy(a => ApproachPref(a.Type)).FirstOrDefault();
        var at = rwy.Along(established);
        if (app is null) return ("vectors as real traffic flies them (adsb.lol)", new List<AirPoint>());
        var pts = Apt.LegsToPoints(app.FinalLegs, established, 3000, out _);
        TrimAtRunway(pts, rwy);
        var keep = pts.Where(p => rwy.Along(p.P) > at + 0.3 * Geo2.MPerNm && Math.Abs(rwy.Offset(p.P)) < 0.5 * Geo2.MPerNm).ToList();
        var typeLabel = app.Type switch { 4 => "ILS", 10 => "RNAV", 1 => "GPS", 5 => "LOC", 2 => "VOR", 7 => "LDA", _ => "approach" };
        return ($"vectors as real traffic flies them (adsb.lol) to the {typeLabel} {rwy.Ident}{(app.Suffix.Length > 0 ? " " + app.Suffix : "")}", keep);
    }

    /// <summary>The approach to the runway, joined to the STAR where they share a fix. Empty if it doesn't line up.</summary>
    private (string name, List<AirPoint> pts) ApproachPoints(RunwayEnd rwy, List<AirPoint> starPts)
    {
        // One approach at a time for the runway (the ATIS's, or a fixed choice): never a mix of, say, LGA's RNAV X and Y 31.
        var chosen = ApproachFor(rwy);
        var apps = chosen is not null ? new List<Approach> { chosen }
            : Apt.Data.Approaches.Where(a => a.Runway == rwy.Ident && a.FinalLegs.Count > 0).OrderBy(a => ApproachPref(a.Type)).ToList();
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
                if (gap > 5 * Geo2.MPerNm)
                {
                    // Vector the arrival to the approach's own first fix, lined up with its inbound course, then fly
                    // the real approach — not a generic straight-in final on the extended centreline, which can run
                    // through a neighbour's airspace (LGA's RNAV Y 31 curves in from GEMKE in the east, clear of JFK;
                    // a straight 10-nm final to 31 points right at JFK).
                    // First choice: the approach's published transition that starts closest to where the STAR ends
                    // (within about 12 nm), flown exactly as published — e.g. LGA's RNAV Y 31 from the southwest STARs
                    // (ending at PROUD) via FEDEM, crossing the field at 4,000 ft and round to the east.
                    var label0 = $"{typeLabel} {rwy.Ident}{(app.Suffix.Length > 0 ? " " + app.Suffix : "")}";
                    var e0 = starPts[^1].P;
                    Transition? bestT = null; var bestD = double.MaxValue;
                    foreach (var tr in app.Transitions)
                    {
                        var first = tr.Legs.FirstOrDefault(l => l.Lat != 0 || l.Lon != 0);
                        if (first is null) continue;
                        var dd = (Apt.ToXY(first.Lat, first.Lon) - e0).Length;
                        if (dd < bestD) { bestD = dd; bestT = tr; }
                    }
                    if (bestT is not null && bestD <= 12 * Geo2.MPerNm)
                    {
                        var tl = new List<ProcedureLeg>(bestT.Legs);
                        tl.AddRange(app.FinalLegs);
                        var tp = Apt.LegsToPoints(tl, e0, 4000, out _);
                        TrimAtRunway(tp, rwy);
                        if (tp.Count >= 2)
                        {
                            var lt = tp[^1].P;
                            var at = rwy.Along(lt);
                            if (at <= -MinFinalNm * Geo2.MPerNm && Math.Abs(rwy.Offset(lt)) <= Math.Abs(at) * Math.Tan(25 * Math.PI / 180))
                                return ($"{label0} via {bestT.Name}", tp);
                        }
                    }
                    // Otherwise: vectors to the approach's first fix.
                    var app1 = Apt.LegsToPoints(app.FinalLegs, starPts[^1].P, 3000, out _);
                    TrimAtRunway(app1, rwy);
                    var label = $"{typeLabel} {rwy.Ident}{(app.Suffix.Length > 0 ? " " + app.Suffix : "")}";
                    if (app1.Count >= 2)
                    {
                        var last1 = app1[^1].P;
                        var along1 = rwy.Along(last1);
                        if (along1 <= -MinFinalNm * Geo2.MPerNm && Math.Abs(rwy.Offset(last1)) <= Math.Abs(along1) * Math.Tan(25 * Math.PI / 180))
                        {
                            var fix = app1[0];
                            var inbound = (app1[1].P - fix.P).Unit;
                            // Joined at least 8 nm out (3 nm before the fix, further when the fix is close in), so the
                            // final is intercepted at a controller's angle (30° at most, 7110.65 5-9-2) — never turned
                            // onto from the side 6 nm out (28 Nov: DAL500–DAL510 joined the ILS 27 at 68–83°).
                            var fixOut = -rwy.Along(fix.P) / Geo2.MPerNm;
                            var lead = fix.P - inbound * (Math.Max(3, 8 - fixOut) * Geo2.MPerNm);
                            var route1 = new List<AirPoint>();
                            // Keep clear of the airport on the way there: go round its side, not over it.
                            var e = starPts[^1].P;
                            if (DistToSegment(new V2(0, 0), e, lead) < 4 * Geo2.MPerNm)
                            {
                                var mid = (e + lead) * 0.5;
                                var away = mid.Length > Geo2.MPerNm ? mid.Unit : (lead - e).Unit.Perp;
                                route1.Add(new AirPoint(away * (6 * Geo2.MPerNm), ""));
                            }
                            route1.Add(new AirPoint(lead, ""));
                            route1.AddRange(app1);
                            return ($"vectors to {(fix.Fix.Length > 0 ? fix.Fix : "the approach")} for the {label}", route1);
                        }
                    }
                    return ($"vectors to the {label}", new List<AirPoint>());
                }
            }
            if (join is not null) legs.AddRange(join.Legs);
            legs.AddRange(app.FinalLegs);
            var start = keep > 0 ? starPts[keep - 1].P : rwy.Threshold - rwy.Dir * (15 * Geo2.MPerNm);
            var pts = Apt.LegsToPoints(legs, start, 4000, out _);
            // Keep only the part before the runway, and stop at the runway fix.
            TrimAtRunway(pts, rwy);
            if (pts.Count == 0) continue;
            // The last point must be on the extended centreline (within 25°, at least 2 nm out).
            var lastP = pts[^1].P;
            var along = rwy.Along(lastP);
            if (along > -MinFinalNm * Geo2.MPerNm || Math.Abs(rwy.Offset(lastP)) > Math.Abs(along) * Math.Tan(25 * Math.PI / 180)) continue;
            if (keep < starPts.Count) starPts.RemoveRange(keep, starPts.Count - keep);
            var typeName = app.Type switch { 4 => "ILS", 10 => "RNAV", 1 => "GPS", 5 => "LOC", 2 => "VOR", 7 => "LDA", _ => "approach" };
            return ($"{typeName} {rwy.Ident}{(app.Suffix.Length > 0 ? " " + app.Suffix : "")}", pts);
        }
        return ("", new List<AirPoint>());
    }

    private static int ApproachPref(int t) => t switch { 4 => 0, 10 => 1, 1 => 2, 5 => 3, _ => 4 };

    public static string ApproachLabel(Approach a) =>
        $"{(a.Type switch { 4 => "ILS", 10 => "RNAV", 1 => "GPS", 5 => "LOC", 2 => "VOR", 7 => "LDA", _ => "approach" })}{(a.Suffix.Length > 0 ? " " + a.Suffix : "")} {a.Runway}";

    private static bool KindMatches(int type, string kind) => kind switch
    {
        "ILS" => type == 4, "LOC" => type == 5, "VOR" => type == 2, "LDA" => type == 7,
        "RNAV" or "RNP" or "GPS" => type is 10 or 1, _ => false,
    };

    /// <summary>Whether the approach's final ends lined up with the runway, so it can actually be flown to a landing.</summary>
    private bool Flyable(Approach a, RunwayEnd rwy)
    {
        var pts = Apt.LegsToPoints(a.FinalLegs, rwy.Threshold - rwy.Dir * (15 * Geo2.MPerNm), 4000, out _);
        TrimAtRunway(pts, rwy);
        if (pts.Count == 0) return false;
        var last = pts[^1].P;
        var along = rwy.Along(last);
        return along <= -MinFinalNm * Geo2.MPerNm && Math.Abs(rwy.Offset(last)) <= Math.Abs(along) * Math.Tan(25 * Math.PI / 180);
    }

    /// <summary>
    /// The approach in use on the landing runway: the one the real ATIS names; otherwise a fixed choice (an ILS
    /// first, else the RNAV with the most published transitions, so the most arrivals join it the published way).
    /// It changes only when the runway or the ATIS changes. Picking it from the neighbours' flows comes with M4.
    /// </summary>
    private Approach? ApproachFor(RunwayEnd rwy)
    {
        var key = string.Join(";", AtisApproaches.Select(x => $"{x.kind}|{x.suffix}|{x.runway}")) + "#" + string.Join(";", NeighbourFinals.Select(n => n.who));
        if (_apps.TryGetValue(rwy, out var had) && had.key == key) return had.app;
        var apps = Apt.Data.Approaches.Where(a => a.Runway == rwy.Ident && a.FinalLegs.Count > 0 && Flyable(a, rwy)).ToList();
        Approach? pick = null;
        var why = "";
        foreach (var (kind, suffix, runway) in AtisApproaches)
        {
            if (runway != rwy.Ident) continue;
            pick = apps.FirstOrDefault(a => KindMatches(a.Type, kind) && (suffix.Length == 0 || a.Suffix == suffix))
                ?? (suffix.Length == 0 ? null : apps.FirstOrDefault(a => KindMatches(a.Type, kind)));
            if (pick is not null) { why = "as the real ATIS says"; break; }
        }
        if (pick is null)
        {
            pick = apps.OrderBy(a => ApproachPref(a.Type)).ThenByDescending(a => a.Transitions.Count).ThenBy(a => a.Suffix).FirstOrDefault();
            why = AtisApproaches.Count > 0 ? "the ATIS names none LAIA can fly here, so the standard choice" : "no ATIS approach known, so the standard choice";
            // Tied to the neighbours' flow (plan, Phase 4): where the standard choice runs through a neighbouring
            // airport's final in use (a mile or more of it within 2 nm of that centreline), the approach that keeps
            // clearest of them is used instead.
            if (pick is not null && NeighbourConflict(pick, rwy, out var whoStd) is var cStd && cStd > Geo2.MPerNm)
            {
                var alt = apps.Select(a => (a, c: NeighbourConflict(a, rwy, out _))).OrderBy(x => x.c).ThenBy(x => ApproachPref(x.a.Type)).First();
                if (alt.a != pick && alt.c < cStd * 0.5)
                {
                    why = $"the standard {ApproachLabel(pick)} runs through {whoStd}'s final in use ({cStd / Geo2.MPerNm:0.0} nm of it), this one {alt.c / Geo2.MPerNm:0.0} nm";
                    pick = alt.a;
                }
            }
        }
        var label = pick is null ? null : ApproachLabel(pick);
        if (!_apps.ContainsKey(rwy) || had.label != label) Log($"Approach in use at {Apt.Icao}: {label ?? "none (vectors to final)"} for runway {rwy.Ident} ({why})");
        _apps[rwy] = (pick, key, label);
        ApproachInUse = string.Join(", ", (ArrRwys.Count > 0 ? ArrRwys : new List<RunwayEnd> { rwy }).Select(r => _apps.TryGetValue(r, out var x) ? x.label ?? $"vectors {r.Ident}" : null).Where(x => x is not null));
        return pick;
    }

    /// <summary>
    /// The approaches a D-ATIS text says are in use: "ILS RWY 22 APCH IN USE", "EXPECT RNAV Y RWY 31 APPROACH",
    /// "RNAV (RNP) Z RY 13". Approaches mentioned as out of service are ignored; visual approaches aren't flown yet.
    /// </summary>
    /// <summary>
    /// Ends an approach's points before the runway: at the runway fix (RWxx) if it has one, else drops the trailing
    /// points that lie past the threshold. Points past the threshold earlier in the route are kept — a transition can
    /// start on the far side of the field (LGA's FEDEM for the RNAV Y 31 is 3 nm beyond runway 31's threshold).
    /// </summary>
    private static void TrimAtRunway(List<AirPoint> pts, RunwayEnd rwy)
    {
        var rw = pts.FindIndex(p => p.Fix.StartsWith("RW"));
        if (rw >= 0) pts.RemoveRange(rw, pts.Count - rw);
        while (pts.Count > 0 && rwy.Along(pts[^1].P) > -200) pts.RemoveAt(pts.Count - 1);
    }

    /// <summary>
    /// Each SID and STAR for the runways in use, as LAIA will fly it, against the real tracks learned for the airport
    /// (airline passenger jets only). Departures: every real take-off from the runway is matched to the SID whose path it
    /// stays closest to (on average within 2 nm, over its first 25 nm); then, every half mile along LAIA's path, the
    /// median distance to those real tracks — the largest is how far LAIA's version is from what's really flown
    /// (flagged over 0.5 nm: MCO's FATHE4 off 35L was 0.9 nm west of the real tracks 3–4 nm out, a "heading to intercept"
    /// leg flown as 3 nm straight ahead). Arrivals: for each STAR fix, the median distance real arrivals to that runway
    /// pass from it (more than 2 nm: real traffic is usually vectored off by there). A report, nothing is changed.
    /// </summary>
    public (string summary, List<string> lines) RouteCheck()
    {
        var lines = new List<string>();
        var prof = Learned;
        if (prof is null || prof.Samples.Count == 0) return ("no real tracks to compare", lines);
        var nm = Geo2.MPerNm;
        int checkedN = 0, differ = 0;
        var worst = "";
        double worstNm = 0;
        List<V2> Track(AirportProfile.SampleTrack x) => x.Points.Where(q => q.Length >= 2).Select(q => Apt.Map.ToXY(q[0], q[1])).ToList();
        static double ToLine(V2 p, List<V2> line)
        {
            var d = double.MaxValue;
            for (var i = 0; i + 1 < line.Count; i++) d = Math.Min(d, DistToSegment(p, line[i], line[i + 1]));
            return line.Count == 1 ? (p - line[0]).Length : d;
        }
        static List<V2> Dense(List<V2> pts, double step, double maxFromOrigin)
        {
            var res = new List<V2>();
            for (var i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[i + 1]; var len = (b - a).Length;
                for (double s = 0; s < len; s += step)
                {
                    var q = a + (b - a) * (s / len);
                    if (q.Length > maxFromOrigin) return res;
                    res.Add(q);
                }
            }
            return res;
        }
        static double Median(List<double> v) { v.Sort(); return v.Count == 0 ? double.NaN : v[v.Count / 2]; }

        // ---- departures
        foreach (var rwy in DepRwys.DistinctBy(r => r.Ident))
        {
            var real = prof.Samples.Where(x => x.Kind == "departure" && x.Runway == rwy.Ident).Select(Track).Where(l => l.Count >= 4).ToList();
            if (real.Count < 3) continue;
            var paths = new List<(string name, List<V2> pts)>();
            foreach (var sid in Apt.Data.Departures)
            {
                var rt = sid.RunwayTransitions.FirstOrDefault(t => t.Name == rwy.Ident);
                if (rt is null && sid.RunwayTransitions.Count > 0) continue;
                var legs = new List<ProcedureLeg>();
                if (rt is not null) legs.AddRange(rt.Legs);
                legs.AddRange(sid.Legs);
                var sp = Apt.LegsToPoints(legs, rwy.FarEnd, rwy.ElevationFt + 400, out _, hybrid: true).Select(q => q.P).ToList();
                sp.RemoveAll(q => rwy.Along(q) < rwy.LengthM - 50 && Math.Abs(rwy.Offset(q)) < 200);   // points behind the runway end
                sp.Insert(0, rwy.FarEnd);
                var dense = Dense(sp, 0.5 * nm, 25 * nm);
                if (dense.Count >= 4) paths.Add((sid.Name, dense));
            }
            if (paths.Count == 0) continue;
            var mine = paths.ToDictionary(x => x.name, _ => new List<List<V2>>());
            foreach (var tr in real)
            {
                var near = tr.Where(q => q.Length < 25 * nm).ToList();
                if (near.Count < 3) continue;
                var best = paths.Select(x => (x.name, avg: near.Average(q => ToLine(q, x.pts)))).OrderBy(x => x.avg).First();
                if (best.avg < 2 * nm) mine[best.name].Add(near);
            }
            foreach (var (name, pts) in paths)
            {
                var trs = mine[name];
                if (trs.Count < 3) continue;
                checkedN++;
                // Along LAIA's path, the median distance to the real departures. Real traffic is cleared direct and
                // leaves the SID somewhere: from where it's more than 1.5 nm off and stays off, it's no longer the
                // procedure being compared — only the part before that counts, and where they leave is reported.
                var devs = new List<(double along, double dev, V2 p)>();
                double along = 0;
                for (var i = 0; i < pts.Count; i++)
                {
                    if (i > 0) along += (pts[i] - pts[i - 1]).Length;
                    devs.Add((along, Median(trs.Select(t => ToLine(pts[i], t)).ToList()), pts[i]));
                }
                var leave = -1;
                for (var i = 0; i + 2 < devs.Count; i++)
                    if (devs[i].dev > 1.5 * nm && devs[i + 1].dev > 1.0 * nm && devs[i + 2].dev > 1.0 * nm) { leave = i; break; }
                var flown = leave < 0 ? devs : devs.Take(leave).ToList();
                if (flown.Count == 0) continue;
                var mx = flown.MaxBy(x => x.dev);
                var ok = mx.dev <= 0.5 * nm;
                if (!ok) differ++;
                if (!ok && mx.dev > worstNm) { worstNm = mx.dev; worst = $"{name} {rwy.Ident} up to {mx.dev / nm:0.0} nm off {mx.along / nm:0.0} nm after the runway"; }
                var leaveFix = leave < 0 ? null : Apt.Data.Departures.First(d => d.Name == name).Legs
                    .Concat(Apt.Data.Departures.First(d => d.Name == name).RunwayTransitions.Where(t => t.Name == rwy.Ident).SelectMany(t => t.Legs))
                    .Where(l => l.Fix.Length > 0 && (Math.Abs(l.Lat) > 0.0001 || Math.Abs(l.Lon) > 0.0001))
                    .OrderBy(l => (Apt.Map.ToXY(l.Lat, l.Lon) - devs[leave].p).Length).Select(l => l.Fix).FirstOrDefault();
                lines.Add($"{name} off {rwy.Ident}: {(ok ? "matches" : "DIFFERS from")} {trs.Count} real departures — LAIA's path is at most {mx.dev / nm:0.0} nm from them ({mx.along / nm:0.0} nm after the runway)"
                    + (leave < 0 ? "; flown as published as far as compared" : $"; real traffic leaves the SID about {devs[leave].along / nm:0} nm out{(leaveFix is null ? "" : $", near {leaveFix}")}, cleared direct"));
            }
        }

        // ---- arrivals: where real traffic leaves each STAR
        foreach (var rwy in ArrRwys.DistinctBy(r => r.Ident))
        {
            var real = prof.Samples.Where(x => x.Kind == "arrival" && x.Runway == rwy.Ident).Select(Track).Where(l => l.Count >= 4).ToList();
            if (real.Count < 3) continue;
            foreach (var star in Apt.Data.Arrivals)
            {
                var rt = StarTrans(star, rwy);
                if (rt is null && star.RunwayTransitions.Count > 0) continue;
                var fixes = star.Legs.Concat(rt?.Legs ?? new List<ProcedureLeg>())
                    .Where(l => l.Fix.Length > 0 && (Math.Abs(l.Lat) > 0.0001 || Math.Abs(l.Lon) > 0.0001))
                    .Select(l => (l.Fix, p: Apt.Map.ToXY(l.Lat, l.Lon))).Where(x => x.p.Length < 60 * nm).DistinctBy(x => x.Fix).ToList();
                if (fixes.Count < 2) continue;
                // Real arrivals that came this way: within 2 nm of at least two of its fixes.
                var trs = real.Where(t => fixes.Count(f => ToLine(f.p, t) < 2 * nm) >= 2).ToList();
                if (trs.Count < 3) continue;
                checkedN++;
                var devs = fixes.Select(f => (f.Fix, d: Median(trs.Select(t => ToLine(f.p, t)).ToList()))).ToList();
                var off = devs.FirstOrDefault(x => x.d > 2 * nm);
                lines.Add($"{star.Name} to {rwy.Ident}: {trs.Count} real arrivals — " + string.Join(", ", devs.Select(x => $"{x.Fix} {x.d / nm:0.0}")) + " nm"
                    + (off.Fix is { } of ? $"; real traffic is vectored off before {of}" : "; flown as published"));
            }
        }
        var summary = checkedN == 0 ? "not enough real tracks for the runways in use"
            : $"{checkedN} SIDs/STARs checked; {differ} SID{(differ == 1 ? "" : "s")} more than 0.5 nm from the real tracks" + (worst.Length > 0 ? $" (worst: {worst})" : "");
        return (summary, lines);
    }

    /// <summary>A line simplified (Ramer–Douglas–Peucker): points within tol of the straight line between their
    /// neighbours dropped, the ends kept. Takes the wiggles out of real tracks so they fly as clean legs.</summary>
    public static List<V2> Simplify(List<V2> pts, double tol)
    {
        if (pts.Count < 3) return new List<V2>(pts);
        var keep = new bool[pts.Count]; keep[0] = keep[^1] = true;
        var stack = new Stack<(int a, int b)>(); stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            int idx = -1; double md = tol;
            for (var i = a + 1; i < b; i++) { var d = DistToSegment(pts[i], pts[a], pts[b]); if (d > md) { md = d; idx = i; } }
            if (idx < 0) continue;
            keep[idx] = true; stack.Push((a, idx)); stack.Push((idx, b));
        }
        return pts.Where((_, i) => keep[i]).ToList();
    }

    /// <summary>How far a line turns in all (degrees) over its first lenM metres.</summary>
    public static double TotalTurn(List<V2> pts, double lenM)
    {
        double t = 0, run = 0; double? prev = null;
        for (var i = 1; i < pts.Count && run < lenM; i++)
        {
            var seg = pts[i] - pts[i - 1];
            run += seg.Length;
            if (seg.Length < 300) continue;
            if (prev is { } pb) t += Math.Abs(Geo2.Diff(seg.Bearing, pb));
            prev = seg.Bearing;
        }
        return t;
    }

    private static double DistToSegment(V2 p, V2 a, V2 b)
    {
        var ab = b - a;
        var t = ab.Length < 1e-6 ? 0 : Math.Clamp(V2.Dot(p - a, ab) / V2.Dot(ab, ab), 0, 1);
        return (a + ab * t - p).Length;
    }

    /// <summary>Where vectored arrivals join the final: about 9.5 nm out (best practice 8–10 nm).</summary>
    private const double JoinNm = 9.5;

    /// <summary>Real tracks learned for this airport (M6), or null until it has been learned.</summary>
    public AirportProfile? Learned { get; set; }

    /// <summary>
    /// The vectors-to-final shape for a runway: what the airport's real controllers do, learned from real tracks (where
    /// they join the final — a different place for each flight, within the middle half of the real spread — the base
    /// turn, the downwind's distance and the intercept angle), or the generic numbers until the airport is learned.
    /// </summary>
    private VectorShape ShapeFor(RunwayEnd rwy, double? fixedJoin = null)
    {
        if (Learned?.Arrivals.TryGetValue(rwy.Ident, out var a) != true || a!.Count < 5 || double.IsNaN(a.JoinP50))
        {
            var g = GeneralShape ?? VectorShape.Default;
            return fixedJoin is { } fj0 ? g with { JoinNm = fj0, BaseNm = Math.Max(g.BaseNm, fj0 + 2) } : g;
        }
        var join = fixedJoin ?? Math.Clamp(a.JoinP25 + (a.JoinP75 - a.JoinP25) * Rng.NextDouble(), 6, 16);
        var bas = double.IsNaN(a.BaseNm) ? join + 3 : Math.Clamp(a.BaseNm, join + 1.5, join + 10);
        var dw = double.IsNaN(a.DownwindNm) ? double.NaN : Math.Clamp(a.DownwindNm, 2, 10);
        var icpt = double.IsNaN(a.InterceptDeg) ? 28 : Math.Clamp(a.InterceptDeg, 15, 45);
        return new VectorShape(join, bas, dw, icpt);
    }

    /// <summary>
    /// Radar vectors to final the way a controller gives them (7110.65 5-9-2): a downwind, a base leg at right angles
    /// to the final, then an intercept heading about 25° to the final approach course, joining about 9.5 nm out
    /// (at least 2 nm outside the approach gate). From behind, inside a 30° cone, it's a straight-in to the join point.
    /// Coming from beyond the airport, it goes round the side rather than over the field.
    /// </summary>
    private static List<V2> Vectors(V2 from, RunwayEnd rwy, VectorShape? shape = null)
    {
        var sh = shape ?? VectorShape.Default;
        var JoinNm = sh.JoinNm;
        var nm = Geo2.MPerNm;
        var dir = rwy.Dir;
        var left = new V2(-dir.Y, dir.X);
        var along = rwy.Along(from);
        var off = rwy.Offset(from);
        var side = off >= 0 ? 1.0 : -1.0;
        var thr = rwy.Threshold;
        var join = thr - dir * (JoinNm * nm);
        var pts = new List<V2>();
        // Straight in: behind the join point and inside a 30° intercept cone.
        if (along < -(JoinNm + 2) * nm && Math.Abs(off) <= (-along - JoinNm * nm) * Math.Tan(30 * Math.PI / 180))
        {
            pts.Add(join);
            return pts;
        }
        // Generic: base 12.5 nm out, turning to intercept 1.6 nm from the course (≈ 28°); learned: the airport's own.
        var baseNm = sh.BaseNm;
        var endOfBaseNm = Math.Clamp((baseNm - JoinNm) * Math.Tan(sh.InterceptDeg * Math.PI / 180), 0.8, 4);
        if (along > -baseNm * nm)
        {
            var dw = double.IsNaN(sh.DownwindNm) ? Math.Clamp(Math.Abs(off), 3 * nm, 6 * nm) : sh.DownwindNm * nm;
            // From beyond the airport, close to its extended centreline: go round its side first.
            if (along > 3 * nm && Math.Abs(off) < 5 * nm)
                pts.Add(thr + left * (side * 5 * nm) + dir * Math.Min(along, 12 * nm));
            if (along > -6 * nm) pts.Add(thr + left * (side * dw) + dir * Math.Min(along, 2 * nm));   // on the downwind
            pts.Add(thr + left * (side * dw) - dir * (baseNm * nm));                        // turn base
        }
        else
        {
            // Beyond the base turn but off to the side: a base from about where it is.
            pts.Add(thr + left * (side * Math.Clamp(Math.Abs(off), 3 * nm, 8 * nm)) - dir * (Math.Min(-along, 16 * nm)));
        }
        pts.Add(thr + left * (side * endOfBaseNm * nm) - dir * (baseNm * nm));             // end of base: intercept heading
        pts.Add(join);
        return pts;
    }

    /// <summary>
    /// Fixes how a route joins the final approach course: a published transition that hooks onto the final with a
    /// hairpin, or joins it far out from the side (Orlando's ILS 36L "via EXBAN" turning 180° onto a 13 nm final), is
    /// replaced by a downwind, base and an intercept heading about 25° to the course 8–10 nm out — what a controller
    /// does when the arrival doesn't feed straight into the approach. Straight-in joins are left alone.
    /// </summary>
    internal static void ShapeFinal(List<V2> route, List<double> speeds, RunwayEnd rwy, VectorShape? shape = null)
    {
        var JoinNm = (shape ?? VectorShape.Default).JoinNm;
        var nm = Geo2.MPerNm;
        if (route.Count < 3) return;
        bool OnFinal(V2 p) => Math.Abs(rwy.Offset(p)) < 0.3 * nm && rwy.Along(p) < -0.3 * nm;
        if (!OnFinal(route[^1])) return;                       // a curved final (LGA's RNAV X): leave it as published
        var k = route.Count - 1;
        while (k > 0 && OnFinal(route[k - 1])) k--;
        if (k == 0) return;
        var joinNm = -rwy.Along(route[k]) / nm;
        var inc = route[k] - route[k - 1];
        if (inc.Length < 1) return;
        var angle = Math.Abs(Geo2.Diff(inc.Bearing, rwy.Heading));
        // A short final joined from the side by design (LGA's RNAV Y 31 at MANLL, 4 nm out) is the procedure: keep it.
        if (joinNm < 6) return;
        if (angle <= 35 && !(joinNm > 11.5 && angle > 15)) return;
        // Keep the route up to where it's still out on its own (not yet in the hook beyond the base turn).
        var m = k - 1;
        while (m > 0)
        {
            var a = rwy.Along(route[m]); var o = Math.Abs(rwy.Offset(route[m]));
            if (!(a < -11 * nm && o < 7 * nm) && o >= 1 * nm) break;
            m--;
        }
        var vec = Vectors(route[m], rwy, shape);
        // No zigzag: a route that cuts in towards the final just before (the keep-clear point beside the airport, or a
        // fix inside the downwind) and would then be sent back out to the downwind starts its vectors from the point
        // before instead (MCO GRNCH5 for 36L: DORII → a point 1 nm inside the downwind → straight back out to it).
        while (m > 0 && vec.Count > 0)
        {
            var o0 = Math.Abs(rwy.Offset(route[m]));
            if (!(Math.Abs(rwy.Offset(vec[0])) > o0 + 0.5 * nm && Math.Abs(rwy.Offset(route[m - 1])) > o0 + 0.3 * nm)) break;
            m--;
            vec = Vectors(route[m], rwy, shape);
        }
        var tail = new List<(V2 p, double kt)>();
        for (var i = k; i < route.Count; i++)
            if (-rwy.Along(route[i]) < JoinNm * nm - 300) tail.Add((route[i], i < speeds.Count ? speeds[i] : 0));
        var newRoute = route.Take(m + 1).Concat(vec).Concat(tail.Select(t => t.p)).ToList();
        var newSpeeds = speeds.Take(Math.Min(m + 1, speeds.Count)).ToList();
        while (newSpeeds.Count < m + 1) newSpeeds.Add(0);
        newSpeeds.AddRange(vec.Select(_ => 0.0));
        newSpeeds.AddRange(tail.Select(t => t.kt));
        route.Clear(); route.AddRange(newRoute);
        speeds.Clear(); speeds.AddRange(newSpeeds);
    }

    private double ArrivalSpeed(Flight f, double d)
    {
        // Typical controller speeds (250 → 210 → 180 → 170 to a 5-mile final), then the aircraft slows to its
        // approach speed on its own: no speed is assigned inside 5 miles (7110.65 5-7-2).
        var t = PlannedSpeed(f, d);
        if (f.WentAround) t = Math.Min(t, 200);
        // Published speed limits on the STAR and approach, and 250 knots below 10,000 ft (14 CFR 91.117(a)).
        t = Math.Min(t, ProcedureLimit(f, false));
        if (f.AltFt < 11500) t = Math.Min(t, 250);   // slowing for 250 kt by 10,000 ft
        return Math.Max(t, f.P.Vapp);
    }

    /// <summary>
    /// The planned speed at a distance from the runway: typical controller speeds, and never faster than the
    /// aircraft can still slow from in time — approach speed + 10 kt per nm beyond 3 nm (FSF / Airbus: about 10 kt
    /// per nm on the glidepath, stable by 1,000 ft about 3 nm out). "170 to a 5-mile final" only fits jets whose
    /// approach speed is 150 kt or more.
    /// </summary>
    private double PlannedSpeed(Flight f, double d, bool nominal = false)
    {
        // Descent speed above 10,000 ft (about 280 kt for jets), slowing to 250 kt on the level piece at 10,000 ft.
        var high = f.Rwy is not null && d > D10(f.Rwy) + 4;
        double t = high ? (f.P.Family == "turboprop" ? 230 : 280) : d > 20 ? 250 : d > 12 ? 210 : d > 7 ? 180 : d > 5 ? 170 : f.P.Vapp;
        // The learned way (Release 4b): below 10,000 ft, the speed real controllers have this kind of aircraft fly at
        // this distance to go when nothing's in the way — within 40 kt of the standard steps, never over 250 kt.
        if (!high && Atc is { } atc && d >= AtcModel.NomFrom)
        {
            var ls = atc.NominalIas(AtcAirport, AtcModel.Class(f.P), d);
            if (!double.IsNaN(ls)) t = Math.Clamp(ls, Math.Max(f.P.Vapp, t - 40), Math.Min(250, t + 40));
        }
        // Losing time for its slot (arrival manager): slower in the cruise and descent and 210 kt below 10,000 ft until
        // 20 nm — the controller's speed control far out (7110.65 5-7) instead of a hold.
        // The reduction is graded (5-kt steps, as much as the delay needs), never below 240 kt high / 210 kt low for
        // jets (200 / 180 for turboprops).
        if (f.SlowDv > 0 && !nominal && d > 20)
        {
            var tp = f.P.Family == "turboprop";
            // Learned: where along the way real controllers take the speed off (more of it far out, or close in),
            // for this much delay — the same floors.
            var dv = f.SlowDv * SlowShare(f, d);
            t = high ? Math.Max(tp ? 200 : 240, t - dv) : Math.Max(tp ? 180 : 210, Math.Min(t, (Atc is null ? 250 : t) - dv));
        }
        if (f.P.Family == "turboprop" && d <= 20) t = Math.Min(t, d > 5 ? 170 : f.P.Vapp);
        if (d < 12) t = Math.Min(t, f.P.Vapp + 10 * Math.Max(0, d - 3));
        return Math.Max(t, f.P.Vapp);
    }

    /// <summary>Predicted seconds until this arrival crosses the threshold, flying its planned speeds (and the wind on final).</summary>
    private double EtaToThreshold(Flight f, double dth, bool nominal = false, double[]? marks = null, double[]? at = null)
    {
        if (dth <= 0) return 0;
        var hw = f.Rwy is null ? 0 : Wind.Headwind(f.Rwy.Heading);
        double t = 0;
        const double step = 0.25;
        var mk = 0;
        for (var d = dth; d > 0; d -= step)
        {
            // Times at points on the way (marks: distances to the threshold, largest first).
            while (marks is not null && mk < marks.Length && marks[mk] >= d - 1e-9) { at![mk] = t; mk++; }
            var v = PlannedSpeed(f, d, nominal);
            // The published speed limits along the way (a STAR's "max 230", the approach's 210 at the IAF): flown by
            // the flight model, so counted here — speed is assigned only for what the procedure doesn't already do.
            if (f.SpeedLimits.Count > 0 && f.Path is not null) v = Math.Max(f.P.Vapp, Math.Min(v, ProcedureLimitAt(f, f.ThrS - d * Geo2.MPerNm, false)));
            if (d > dth - 1.5 && !nominal) v = Math.Min(v, Math.Max(f.Ias, f.P.Vapp));      // it can't be faster than it is now right away
            if (f.WentAround) v = Math.Min(v, 200);
            var alt = Profile(f.Rwy!, d);
            if (f.CruiseFt > 0) alt = Math.Min(alt, f.CruiseFt);
            double gs;
            if (Aloft is not null && f.Path is { } fp && f.Rwy is not null)
            {
                // In the wind at the altitude it'll be there, along the way it'll be going there.
                var dir = d < 0.3 ? f.Rwy.Heading : fp.At(Math.Clamp(f.ThrS - d * Geo2.MPerNm, 0, fp.Length)).dir;
                gs = WindAt(alt).Solve(dir, Wind.Tas(v, alt)).groundSpeed;
            }
            else gs = Wind.Tas(v, alt) - (d < 15 ? hw : hw * 0.5);
            t += Math.Min(step, d) / Math.Max(60, gs) * 3600;
        }
        while (marks is not null && mk < marks.Length) { at![mk] = t; mk++; }
        return t;
    }

    /// <summary>
    /// The gap needed at the threshold behind the leader (seconds): the wake or radar spacing at this aircraft's
    /// final approach groundspeed, or the leader's runway occupancy (about 50 s to land and turn off) plus a margin,
    /// whichever is larger; more when departures are waiting to go between the arrivals.
    /// </summary>
    /// <summary>
    /// The gap a departure needs between two arrivals on its runway: the first one off the runway (occupancy) — the
    /// departure lining up and waiting behind it as it rolls out, where the tower has runway safety logic and the
    /// weather is fair (3-9-4), else lining up after it (about 45 s) — then the roll until it's airborne and 6,000 ft
    /// down the runway (about 40 s for a jet, 3-10-3), and 10 s to spare: about 110 s (4 nm at 140 kt), or 150 s.
    /// </summary>
    private double DepartureGap(double occupancy) => SafetyLogic && !LowWeather ? Math.Max(occupancy, 45) + 40 + 10 : occupancy + 45 + 40 + 10;

    /// <summary>Departures for this runway still to go: taxiing, waiting at it, or due off the gate within 20 minutes.</summary>
    private int PendingDepartures(int runway) => Flights.Count(o => o.Kind == "departure" && !o.Done && o.OnGround && o.Rwy is not null && o.Rwy.Runway == runway
        && (!(o.Phase is "parked" or "boarding" or "at gate") || (!double.IsNaN(o.ReadyAt) && o.ReadyAt - Time < 1200)));

    /// <summary>
    /// How much of the speed reduction is taken at this distance to go (Release 4b): the learned controller's slowing
    /// at each nm from 20 to 45 out, for the delay this arrival has, against its slowing at 30 nm (0.5–1.5); 1 without
    /// the learned way.
    /// </summary>
    private double SlowShare(Flight f, double d)
    {
        if (Atc is not { } atc) return 1;
        var lose = Math.Round(Math.Max(30, f.Delay) / 30) * 30;
        if (f.AtcSlowShape is null || f.AtcSlowShapeFor != lose)
        {
            var cls = AtcModel.Class(f.P);
            var sh = new double[26];
            double Cut(int nm) => 1 - atc.SpeedRatio(AtcAirport, cls, nm, f.Rwy is null ? 10000 : Math.Min(Profile(f.Rwy, nm), 18000) - f.Rwy.ElevationFt, lose, Flights.Count(o => o.Kind == "arrival" && !o.Done));
            var refCut = Cut(30);
            for (var k = 0; k < sh.Length; k++) sh[k] = refCut <= 0.01 ? 1 : Math.Clamp(Cut(20 + k) / refCut, 0.5, 1.5);
            f.AtcSlowShape = sh; f.AtcSlowShapeFor = lose;
        }
        return f.AtcSlowShape[Math.Clamp((int)Math.Round(d - 20), 0, f.AtcSlowShape.Length - 1)];
    }

    /// <summary>
    /// The learned landing order (Release 4b): seconds added to (or taken from) an arrival's own landing time when the
    /// order is set, the way real controllers order arrivals level with each other — from where it is (distance to go,
    /// height, speed, the side it comes from, how far off the final's line) and what it is. Between −4 and +4 minutes;
    /// 0 without the learned way.
    /// </summary>
    private double LearnedOrderOffset(Flight f, int load)
    {
        if (Atc is not { } atc || f.Rwy is not { } r || f.Path is null) return 0;
        var nm = Geo2.MPerNm;
        var dtg = (f.ThrS - f.S) / nm;
        if (dtg > 60) return 0;
        var side = Geo2.Diff(r.Heading + 180, (f.Pos - r.Threshold).Bearing) * Math.PI / 180;
        return atc.OrderOffset(AtcAirport, AtcModel.Class(f.P), dtg, f.AltFt - r.ElevationFt, f.Gs, Math.Sin(side), Math.Cos(side), r.Offset(f.Pos) / nm, load);
    }

    private double ThresholdGapNeeded(Flight leader, Flight f, bool forDepartures = true)
    {
        var gs = Math.Max(90, f.P.Vapp - WindAt(f.Rwy!.ElevationFt + 1000).Headwind(f.Rwy.Heading));
        var wake = Cwt.MilesBehind(leader.Wake, f.Wake) / gs * 3600;
        var occupancy = Math.Max(RunwayOccupancy(f.Rwy!, leader.P), _rotSeen.GetValueOrDefault(f.Rwy!)) + 10;
        var deps = DepRwys.Count > 0 ? DepRwys : DepRwy is null ? new List<RunwayEnd>() : new() { DepRwy };
        var mixed = deps.Count == 0 || deps.Any(d => d.Runway == f.Rwy.Runway);
        var crossing = deps.Any(d => Crossing(f.Rwy, d) is not null);
        // Room for a departure between arrivals only when one is waiting for, or due at, this runway (or one that
        // crosses it) — not because some other runway has a queue.
        var mine = forDepartures && mixed && DeparturesFor(r => r == f.Rwy.Runway);
        var cross = forDepartures && !mine && crossing && DeparturesFor(r => deps.Any(d => d.Runway == r && Crossing(f.Rwy, d) is not null));
        var need = mine ? Math.Max(Math.Max(wake, occupancy) + 10, DepartureGap(occupancy)) : Math.Max(wake, occupancy) + 10 + (cross ? 30 : 0);
        // The gap real controllers leave behind this kind of leader when the queue is pressed (Release 4b) — more than
        // the minimums only, and at most a minute more.
        if (Atc is { } atc && leader != UserArr && f != UserArr)
        {
            var lg = atc.TargetGap(AtcAirport, AtcModel.Class(leader.P), AtcModel.Class(f.P));
            if (!double.IsNaN(lg)) need = Math.Max(need, Math.Min(lg, need + 60));
        }
        // The human gets a generous gap either side (about 10–12 nm between the aircraft around them).
        return leader == UserArr || f == UserArr ? Math.Max(need, UserSideGapSec) : need;
    }

    /// <summary>
    /// Breaking off an approach early (5–12 nm out) when the gap at the threshold is predicted to be too small and
    /// speed can no longer fix it (no speed adjustments inside 5 nm, 7110.65 5-7-2): a turn away from final at the
    /// same altitude, then vectors back around — calmer and more realistic than a go-around at half a mile.
    /// </summary>
    private double _lastBreakoffAt = -999, _lastBreakoffSide;
    /// <summary>
    /// A real turn at the start of a new path (user, 19 Nov, UAL415: "a sharp turn that's not even possible"): a path
    /// built from the aircraft's position straight to a point off its heading made it swing round on the spot. Now the
    /// path starts with the arc it can fly — its speed's radius at 25° of bank, in 10° steps — from its heading until it
    /// points at the next point far enough beyond the turn; points it can no longer reach are dropped. Returns the full
    /// list (p first) and how many points were added in front of <paramref name="rest"/>'s first kept one.
    /// </summary>
    private (List<V2> pts, int added, int dropped) WithTurn(V2 p, double dir, List<V2> rest, double ias, double altFt)
    {
        var v = Wind.Tas(Math.Max(ias, 140), altFt) * Kt;
        var r = Math.Max(1200, v * v / (G * Math.Tan(25 * Math.PI / 180)));
        var res = new List<V2> { p };
        var drop = 0;
        // A point inside the circle it turns on can't be pointed at: go on to the next.
        bool Inside(V2 t) { var tn = Geo2.Diff(dir, (t - p).Bearing); var c = p + V2.FromBearing(dir + (tn >= 0 ? 90 : -90), r); return (t - c).Length < 1.1 * r || (t - p).Length < 0.5 * r; }
        while (drop < rest.Count - 1 && Inside(rest[drop])) drop++;
        var target = rest[drop];
        var turn = Geo2.Diff(dir, (target - p).Bearing);       // + clockwise (right)
        if (Math.Abs(turn) > 2)
        {
            var h = dir; var q = p;
            for (var k = 0; k < 80; k++)
            {
                var left = Geo2.Diff(h, (target - q).Bearing);
                if (Math.Abs(left) <= 2) break;
                var step = Math.Sign(turn) * Math.Min(5, Math.Abs(left));
                q += V2.FromBearing(h + step / 2, r * Math.Abs(step) * Math.PI / 180); h += step;
                res.Add(q);
            }
        }
        var added = res.Count - 1;
        for (var i = drop; i < rest.Count; i++) res.Add(rest[i]);
        return (res, added, drop);
    }

    /// <summary>
    /// Close in, short of room behind the one ahead: another landing runway whose final it's nearer to than its own,
    /// where it lands sooner than it can on its own runway (30 s or more) — vectored straight there instead of being
    /// vectored off or broken off. Re-sequenced on that runway behind the arrivals nearer to it.
    /// </summary>
    /// <summary>The landing-time predictions worked out again at once, for a new runway or path. The sequence pass runs
    /// straight after a runway change, and with the old runway's time it put the arrival in the wrong place on the new
    /// runway's order (offline rush, 20 Nov: DAL602 moved to 27R went around behind DAL600).</summary>
    private void RefreshEta(Flight f)
    {
        if (f.Path is null || f.Rwy is null) return;
        var dth = (f.ThrS - f.S) / Geo2.MPerNm;
        f.EtaAt = Time;
        f.EtaSec = EtaToThreshold(f, dth);
        f.EtaNominal = Time + (f.Slowed ? EtaToThreshold(f, dth, nominal: true) : f.EtaSec);
    }

    private bool OtherRunway(Flight f, double shortSec, string why)
    {
        if (f.Rwy is not { } cur || f.Path is null || f.Test || f == UserArr || ArrRwys.Count < 2 || Time - f.RwySwitchAt < 300) return false;
        var nm = Geo2.MPerNm;
        var (p, dir, _) = f.Path.At(f.S);
        var dth = (f.ThrS - f.S) / nm;
        var mine = Time + EtaToThreshold(f, dth) + shortSec;
        var kt = Math.Max(170, Math.Min(f.Gs, 250) * 0.85 + f.P.Vapp * 0.15);
        RunwayEnd? best = null; List<V2>? bestPts = null; var bestAt = mine - 30;
        foreach (var r in ArrRwys)
        {
            if (r.Runway == cur.Runway || r.LengthM < f.P.MinRunwayM || RarelyLanded(r)) continue;
            // Nearer that final than its own, and still outside it (not turning back to it).
            if (Math.Abs(r.Offset(p)) >= Math.Abs(cur.Offset(p)) || r.Along(p) > -6 * nm) continue;
            var after = Vectors(p, r, ShapeFor(r));
            if (after.Count == 0) continue;
            var pts = WithTurn(p, dir, after, f.Ias, f.AltFt).pts;
            double len = 0; for (var k = 1; k < pts.Count; k++) len += (pts[k] - pts[k - 1]).Length;
            len += (r.Threshold - pts[^1]).Length;
            var at = Time + len / (kt * Kt);
            var slot = EarliestSlot(r.Runway, at, f);
            if (slot - at > 20 || slot >= bestAt) continue;
            best = r; bestPts = pts; bestAt = slot;
        }
        if (best is null || bestPts is null) return false;
        var thrIndex = bestPts.Count;
        bestPts.Add(best.Threshold); bestPts.Add(best.FarEnd);
        f.Rwy = best; f.Fan = null; f.FanKey = null;
        f.Path = AirPath(bestPts, i => i >= thrIndex ? 1 : 200, f.AltFt, from: f);
        f.ThrS = f.Path.PointS[thrIndex];
        f.S = 0;
        f.SpeedLimits.Clear();
        f.AltLimits.Clear(); f.Checks.Clear(); f.Fixes.Clear(); f.FixEtas.Clear();
        f.LandingChecked = false; f.StableChecked = false;
        if (f.Phase == "final") f.SetPhase("approach", Time);
        f.Frozen = false; f.Rejoin = true; f.SlowDv = 0; f.EtaAt = -99;
        f.RwySwitchAt = Time; f.LastVector = Time; f.LastTrombone = Time;
        RefreshEta(f);
        f.Mgr = "vectored";
        _rwySwitches++;
        var far = bestPts.FirstOrDefault(q => (q - p).Length > 2 * nm, bestPts[^1]);
        var hdg = (far - p).Bearing;
        Log($"{f.Callsign}: runway {best.Ident} instead of {cur.Ident} ({why}): lands about {(mine - bestAt):0} s sooner there — vectors for {best.Ident}, turn {(Geo2.Diff(dir, hdg) < 0 ? "left" : "right")} heading {Geo2.Norm(hdg):000}");
        return true;
    }

    private void BreakOff(Flight f, string why)
    {
        var rwy = f.Rwy!;
        var (p, dir, _) = f.Path!.At(f.S);
        var nm = Geo2.MPerNm;
        var left = new V2(-rwy.Dir.Y, rwy.Dir.X);
        var side = rwy.Offset(p) >= 0 ? 1.0 : -1.0;              // break off to the side it's already on
        // A second break-off soon after another on the same side goes wider and further out, not to the same spot.
        var wide = Time - _lastBreakoffAt < 120 && _lastBreakoffSide == side;
        _lastBreakoffAt = Time; _lastBreakoffSide = side;
        var outPt = p + left * (side * (wide ? 7 : 4) * nm) - rwy.Dir * ((wide ? 8 : 4) * nm);
        var heading = (outPt - p).Bearing;
        _breakoffs++; f.BreakOffs++; f.Mgr = "vectored around"; f.Frozen = false; f.Rejoin = true;   // re-sequenced behind those nearer in
        Log($"{f.Callsign}: breaking off the approach for spacing ({why}): turn {(V2.Cross(V2.FromBearing(dir), outPt - p) > 0 ? "left" : "right")} heading {heading:000}, vectors back around");
        f.LastOrbit = Time; f.LastVector = Time; f.LastTrombone = Time;   // nothing else for it straight away
        // A real turn out to the break-off point, then another from there onto the vectors back round (the corner at
        // the point itself was too tight to fly: 12°/s in the offline check).
        var first = WithTurn(p, dir, new List<V2> { outPt }, f.Ias, f.AltFt).pts;
        var dirOut = first.Count >= 2 ? (first[^1] - first[^2]).Bearing : (outPt - p).Bearing;
        var back = Vectors(outPt, rwy, ShapeFor(rwy));
        var pts = back.Count > 0 ? first.Concat(WithTurn(outPt, dirOut, back, 200, f.AltFt).pts.Skip(1)).ToList() : first;
        var thrIndex = pts.Count;
        pts.Add(rwy.Threshold);
        pts.Add(rwy.FarEnd);
        f.Path = AirPath(pts, i => i >= thrIndex ? 1 : 200, f.AltFt, from: f);
        f.ThrS = f.Path.PointS[thrIndex];
        f.S = 0;
        f.SpeedLimits.Clear();
        f.AltLimits.Clear(); f.Checks.Clear(); f.Fixes.Clear(); f.FixEtas.Clear();   // vectored: ATC's altitudes now
        f.LandingChecked = false;
        f.StableChecked = false;
        if (f.Phase == "final") f.SetPhase("approach", Time);
    }

    internal void GoAround(Flight f, string why)
    {
        Log($"{f.Callsign}: going around ({why})");
        var rwy = f.Rwy!;
        f.Frozen = false; f.Rejoin = true;   // re-sequenced behind those nearer in: the arrivals behind no longer space on it
        f.SetPhase("go-around", Time);
        f.WentAround = true;
        var p = f.Pos;
        var climbOut = p + rwy.Dir * (4 * Geo2.MPerNm);
        // A departure rolling or just off this runway (or one crossing ahead): the go-around turns 25° away from where it
        // goes, as the tower would ("turn left heading …"), instead of climbing out straight behind or into it.
        var dep = Flights.Where(o => o != f && !o.Done && o.Kind == "departure" && o.Rwy is { } dr
                && (dr.Runway == rwy.Runway || Crossing(rwy, dr) is not null)
                && (o.Phase is "takeoff roll" or "lined up" || (!o.OnGround && o.AltFt - rwy.ElevationFt < 4000 && (o.Pos - p).Length < 8 * Geo2.MPerNm)))
            .OrderBy(o => (o.Pos - p).Length).FirstOrDefault();
        if (dep is not null)
        {
            // Which side of the go-around's line the departure is or will be (its route 3 nm on).
            var dp = dep.Path is { } dpath ? dpath.At(Math.Min(dpath.Length, dep.S + 3 * Geo2.MPerNm)).p : dep.Pos;
            var side = V2.Cross(rwy.Dir, dp - p);
            var turn = Math.Abs(side) < 300 ? 25 : side > 0 ? 25 : -25;   // V2.Cross > 0: to the left; turn right then
            var hdg = Geo2.Norm(rwy.Heading + turn);
            climbOut = p + rwy.Dir * (0.5 * Geo2.MPerNm) + V2.FromBearing(hdg) * (3.5 * Geo2.MPerNm);
            Log($"{f.Callsign}: go-around turns {(turn > 0 ? "right" : "left")} heading {hdg:000} away from {dep.Callsign} departing");
        }
        BuildArrivalPath(f, climbOut);
        // Prepend the current position so the path starts where the aircraft is.
        var pts = new List<V2> { p };
        pts.AddRange(f.Path!.Points);
        var thrIndex = pts.Count - 2;
        f.Path = AirPath(pts, i => i >= thrIndex ? 1 : 180, 3000, from: f);
        f.ThrS = f.Path.PointS[thrIndex];
        f.S = 0;
        f.LandingChecked = false;
        f.StableChecked = false;
        f.SpeedLimits.Clear(); // radar vectors back around: no procedure limits
        var pre = f.Path.PointS.Length > 1 ? f.Path.PointS[1] : 0;
        f.AltLimits = f.AltLimits.Select(c => (c.S + pre, c.Min, c.Max)).ToList();
        f.Checks.Clear(); f.Fixes.Clear(); f.FixEtas.Clear();
    }

    /// <summary>A full circle from the current position, then the rest of the route.</summary>
    /// <summary>
    /// Speed control for the sequence (7110.65 5-7): the reduction, in 5-kt steps, that loses the arrival's delay before
    /// 20 nm out — worked out on its own predicted path — and logged when it changes by 10 kt or more, like a
    /// controller's "reduce speed to 250".
    /// </summary>
    /// <summary>Each runway's landing order from the last pass: (slot, flight).</summary>
    private readonly Dictionary<int, List<(double slot, Flight f)>> _timeline = new();

    /// <summary>The earliest slot on a runway at or after a time that keeps the gaps needed to the arrivals either side.</summary>
    private double EarliestSlot(int r, double eta, Flight f)
    {
        if (!_timeline.TryGetValue(r, out var tl) || tl.Count == 0) return eta;
        var list = tl.Where(x => x.f != f && !x.f.Done && !x.f.OnGround).OrderBy(x => x.slot).ToList();
        var t = eta;
        for (var i = 0; i <= list.Count; i++)
        {
            if (i > 0) t = Math.Max(t, list[i - 1].slot + ThresholdGapNeeded(list[i - 1].f, f, forDepartures: false) + 15);
            if (i == list.Count || t + ThresholdGapNeeded(f, list[i].f, forDepartures: false) + 15 <= list[i].slot) return t;
        }
        return t;
    }

    /// <summary>
    /// Planning the runway (user, 18 Nov: "planning runway sequencing"): an arrival still 50 nm or more out with two
    /// minutes or more of delay on its runway moves to another landing runway its STAR serves when that one has a slot
    /// at least 90 s sooner — as the arrival manager balances parallel runways — at most once every 10 minutes.
    /// </summary>
    private double _lastPlan = -999;
    public bool Planner { get; set; } = true;

    /// <summary>
    /// The runway planner (user, 19 Nov: "how can we make our tool more of a constant traffic manager — seems to be more
    /// of a traffic planner and hoping for the best"; "ADSB data … should weigh into LAIA's calculations"). Every 30 s
    /// it plans every arrival still 40 nm or more out together, not one at a time: in order of their unhindered landing
    /// times, each is given the landing runway (of those its STAR serves) where it costs least — its delay there plus
    /// the extra flying to get there, plus a price for going against how real traffic from its direction uses the
    /// runways (learned), plus a price for changing its runway (more for each change; three at most) — and its slot is
    /// booked there before the next is planned. Arrivals closer in, and those whose runway is fixed, are booked first
    /// where they are. A change is only made when it saves 45 s or more and the last was over two minutes ago.
    /// </summary>
    private void PlanRunways()
    {
        if (!Planner || ArrRwys.Count < 2) return;
        var nm = Geo2.MPerNm;
        var arrs = Flights.Where(f => f.Kind == "arrival" && !f.OnGround && !f.Done && f.Rwy is not null && f.Path is not null
            && f.Phase != "go-around" && !double.IsNaN(f.EtaNominal)).ToList();
        if (arrs.Count == 0) return;
        // Not one already on a delay vector: the detour was given for its runway's plan, and a new runway throws it away
        // (offline rush, 20 Nov: DAL608 vectored 209 s for 27, moved to 27R 20 s later, the detour gone — the delay came
        // back close in as vectors, a break-off and a go-around).
        bool Movable(Flight f) => f != UserArr && !f.Test && f.Phase == "arriving" && (f.ThrS - f.S) / nm >= (f.ReplanFree ? 25 : 40) && f.RwySwitches < 3 && (f.DetourSec <= 0 || f.ReplanFree);
        // The plan's timelines, per runway: the fixed ones booked first.
        var book = ArrRwys.ToDictionary(r => r.Runway, _ => new List<(double slot, Flight f)>());
        double Earliest(int r, double eta, Flight f)
        {
            if (!book.TryGetValue(r, out var tl)) return eta;
            var list = tl.OrderBy(x => x.slot).ToList();
            var t = eta;
            for (var i = 0; i <= list.Count; i++)
            {
                if (i > 0) t = Math.Max(t, list[i - 1].slot + ThresholdGapNeeded(list[i - 1].f, f, forDepartures: false) + 15);
                if (i == list.Count || t + ThresholdGapNeeded(f, list[i].f, forDepartures: false) + 15 <= list[i].slot) return t;
            }
            return t;
        }
        void Book(Flight f, int r, double eta) => book[r].Add((Earliest(r, eta, f), f));
        foreach (var f in arrs.Where(x => !Movable(x)).OrderBy(x => x.EtaNominal))
            if (book.ContainsKey(f.Rwy!.Runway)) Book(f, f.Rwy.Runway, f.EtaNominal);
        if (UserArr is { Rwy: { } urw } u && book.ContainsKey(urw.Runway) && !double.IsNaN(u.EtaNominal)) Book(u, urw.Runway, u.EtaNominal);
        // An arrival's first plan is its runway, not a change (21 Nov, KATL Peak: 52 "changes" for 54 landings — nearly
        // every one the planner's first look at an arrival just taken over far out).
        var firstPlan = arrs.Where(x => !x.Planned).ToHashSet();
        foreach (var f in arrs) f.Planned = true;
        foreach (var f in arrs.Where(Movable).OrderBy(x => x.EtaNominal - x.DetourSec))
        {
            var isFirst = firstPlan.Contains(f) || f.ReplanFree;
            f.ReplanFree = false;
            var cur = f.Rwy!;
            var nominal = f.EtaNominal - f.DetourSec;   // without the delay vectors it's on (a new runway drops them)
            var star = f.StarPick?.p;
            var opts = ArrRwys.Where(r => r.LengthM >= f.P.MinRunwayM && (star is null || ServesStar(star, r)) && !RarelyLanded(r)).ToList();
            if (!opts.Contains(cur)) opts.Add(cur);
            var share = RealShare(opts, f);
            double Cost(RunwayEnd r, out double eta, out double slot)
            {
                eta = (r.Runway == cur.Runway ? f.EtaNominal : nominal + ((f.Pos - r.Threshold).Length - (f.Pos - cur.Threshold).Length) / (220 * Kt));
                slot = Earliest(r.Runway, Math.Max(eta, nominal), f);
                var c = slot - nominal;                                        // delay (detours included) plus the extra flying
                // A runway real traffic from its direction uses less costs up to 2 minutes — but only for a clear
                // difference: within about a quarter of the busier one's share, nothing (24 Nov, KMSP at night: real
                // arrivals 45 % 12R / 55 % 12L, and the flat "less used costs more" sent every arrival to 12L for 23 s —
                // 7 landings to 1). A near-even split is then shared by the delay each runway gives; 90/10 still leans hard.
                if (share is not null)
                {
                    var rel = 1 - share.GetValueOrDefault(r.Runway) / Math.Max(1e-6, share.Values.Max());
                    c += 120 * Math.Max(0, rel - 0.25) / 0.75;
                }
                if (r.Runway != cur.Runway && !isFirst) c += 45 + 60 * f.RwySwitches;     // changing costs, more each time
                // Going back to a runway it was moved off, inside 60 nm, costs two minutes more (22 Nov, KMSP: SWA4367 30L →
                // 30R for 80 s, then back to 30L ten minutes later for 11 s). Further out it is free: re-balancing far
                // out is what keeps a rush flowing (the offline rush from 230 nm broke one off with it everywhere).
                if (r.Runway != cur.Runway && !isFirst && f.LeftRunways.Contains(r.Runway) && (f.ThrS - f.S) / nm < 60) c += 120;
                return c;
            }
            RunwayEnd best = cur; var bestC = Cost(cur, out var bestEta, out _);
            foreach (var r in opts)
            {
                if (r.Runway == cur.Runway) continue;
                var c = Cost(r, out var e, out _);
                if (c < bestC) { bestC = c; best = r; bestEta = e; }
            }
            // Two minutes before a first change, ten before another (20 Nov, KATL: DAL2453 27L → 26L → 27L in five minutes
            // for 7–14 s each time).
            if (best.Runway != cur.Runway && (isFirst || Time - f.RwySwitchAt >= (f.RwySwitches == 0 ? 120 : 600)))
            {
                var curC = Cost(cur, out _, out _);
                f.Rwy = best;
                BuildArrivalPath(f, null, f.Pos);
                f.S = 0; f.Frozen = false; f.SlowDv = 0; f.EtaAt = -99;
                if (!isFirst) { f.RwySwitchAt = Time; f.RwySwitches++; _rwySwitches++; f.LeftRunways.Add(cur.Runway); }
                RefreshEta(f);
                Log($"{f.Callsign}: runway {best.Ident} instead of {cur.Ident} ({(isFirst ? "first plan" : "planned with the others")}: {curC - bestC:0} s better there" +
                    (share is not null ? $"; real arrivals on its STAR or from its direction: {string.Join(", ", opts.Select(o => $"{o.Ident} {100 * share.GetValueOrDefault(o.Runway) / Math.Max(1e-6, share.Values.Sum()):0}%"))}" : "") + ")");
                Book(f, best.Runway, bestEta);
            }
            else Book(f, cur.Runway, f.EtaNominal);
        }
    }

    /// <summary>How real arrivals shared these runways: those that flew this flight's STAR (counted from the real tracks
    /// on it), else those from about its direction; null when fewer than 8 landed on them.</summary>
    private Dictionary<int, double>? RealShare(List<RunwayEnd> rws, Flight f)
    {
        var star = f.StarPick;
        double? brg = star is { } st0 && EntryFix(st0.p, st0.t) is { } ef ? Apt.Map.ToXY(ef.Lat, ef.Lon).Bearing : BearingTo(f.Other);
        return RealWeights(rws, star?.p.Name, brg)?.ToDictionary(kv => kv.Key.Runway, kv => kv.Value);
    }

    /// <summary>
    /// Real arrivals' runways by STAR (user, 21 Nov: "I would bet a lot of STARs go into certain runways than others";
    /// KATL's 14 days: OZZZI2/CHPPR1/ONDRE1 from the north ~80% to 26R/08L, GNDLF3 ~98% to 10/09R, JJEDI4 ~93% to
    /// 27L/28). Each real arrival track is put on the STAR it followed (5 or more of its first points within 2 nm of
    /// the STAR's legs, the STAR it followed longest), weighted by its runway's full count over the samples kept (the
    /// samples are capped per runway). With fewer than 8 on the given runways from that STAR, by direction as before.
    /// </summary>
    private Dictionary<RunwayEnd, double>? RealWeights(IReadOnlyList<RunwayEnd> rws, string? star, double? fromBearing)
    {
        if (Learned is not { } lp || rws.Count == 0) return null;
        if (star is not null && StarRunways(lp).TryGetValue(star, out var byRw))
        {
            var raw = rws.Sum(r => byRw.raw.GetValueOrDefault(r.Ident));
            if (raw >= 8) return rws.ToDictionary(r => r, r => byRw.w.GetValueOrDefault(r.Ident));
        }
        if (fromBearing is not { } fb) return null;
        var bin = (int)Math.Floor(Geo2.Norm(fb) / 30) % 12;
        var w = rws.ToDictionary(r => r, r => lp.Arrivals.TryGetValue(r.Ident, out var ra) && ra.FromBearing is { Length: 12 } fbs
            ? fbs[bin] + 0.5 * fbs[(bin + 11) % 12] + 0.5 * fbs[(bin + 1) % 12] : 0);
        return w.Values.Sum() >= 8 ? w : null;
    }

    private object? _starRwFor;
    private Dictionary<string, (Dictionary<string, double> w, Dictionary<string, int> raw)> _starRw = new();

    private Dictionary<string, (Dictionary<string, double> w, Dictionary<string, int> raw)> StarRunways(AirportProfile lp)
    {
        if (ReferenceEquals(_starRwFor, lp)) return _starRw;
        _starRwFor = lp;
        var res = new Dictionary<string, (Dictionary<string, double> w, Dictionary<string, int> raw)>();
        var nm = Geo2.MPerNm;
        // Each STAR's legs (every transition) as segments in this airport's frame.
        var stars = new List<(string name, List<(V2 a, V2 b)> segs)>();
        foreach (var p in Apt.Data.Arrivals)
        {
            var segs = new List<(V2, V2)>();
            void Add(IEnumerable<Laia.Common.Airports.ProcedureLeg> legs)
            {
                var pts = legs.Where(l => l.Lat != 0 || l.Lon != 0).Select(l => Apt.Map.ToXY(l.Lat, l.Lon)).ToList();
                for (var i = 1; i < pts.Count; i++) if ((pts[i] - pts[i - 1]).Length > 1) segs.Add((pts[i - 1], pts[i]));
            }
            foreach (var t in p.EnrouteTransitions) Add(t.Legs);
            Add(p.Legs);
            foreach (var t in p.RunwayTransitions) Add(t.Legs);
            if (segs.Count > 0) stars.Add((p.Name, segs));
        }
        if (stars.Count == 0) { _starRw = res; return res; }
        var kept = lp.Samples.Where(x => x.Kind == "arrival").GroupBy(x => x.Runway).ToDictionary(g => g.Key, g => g.Count());
        foreach (var smp in lp.Samples)
        {
            if (smp.Kind != "arrival" || smp.Points.Count < 6) continue;
            var pts = smp.Points.Take(40).Where(q => q.Length >= 2).Select(q => Apt.Map.ToXY(q[0], q[1])).ToList();
            string? best = null; var bestN = 4;
            foreach (var (name, segs) in stars)
            {
                var n = 0;
                foreach (var q in pts) { foreach (var (a, b) in segs) if (DistToSegment(q, a, b) < 2 * nm) { n++; break; } }
                if (n > bestN) { bestN = n; best = name; }
            }
            if (best is null) continue;
            var weight = lp.Arrivals.TryGetValue(smp.Runway, out var ra) && kept.TryGetValue(smp.Runway, out var k) && k > 0 ? Math.Max(1.0, (double)ra.Count / k) : 1.0;
            if (!res.TryGetValue(best, out var e)) res[best] = e = (new(), new());
            e.w[smp.Runway] = e.w.GetValueOrDefault(smp.Runway) + weight;
            e.raw[smp.Runway] = e.raw.GetValueOrDefault(smp.Runway) + 1;
        }
        _starRw = res;
        Log("Real arrivals' runways by STAR (adsb.lol): " + string.Join("; ", res.OrderBy(kv => kv.Key).Select(kv =>
        {
            var tot = kv.Value.w.Values.Sum();
            return $"{kv.Key} {string.Join(" ", kv.Value.w.OrderByDescending(x => x.Value).Where(x => x.Value / tot >= 0.05).Select(x => $"{x.Key} {100 * x.Value / tot:0}%"))}";
        })));
        return res;
    }

    /// <summary>A landing runway in use that real traffic hardly lands on (under 5% of the real arrivals to the runways in use).</summary>
    private bool RarelyLanded(RunwayEnd r)
    {
        if (Learned is not { Samples.Count: > 0 } lp || ArrRwys.Count < 2) return false;
        // The full counts, not the samples (kept up to 500 a runway, so a busy runway's share was understated).
        int N(RunwayEnd e) => lp.Arrivals.TryGetValue(e.Ident, out var ra) ? ra.Count : 0;
        var tot = ArrRwys.Sum(N);
        return tot >= 30 && N(r) < tot * 0.05;
    }

    private void BalanceRunway(Flight f)
    {
        if (f.Rwy is not { } cur || f.Path is null || f.Phase != "arriving" || f.Test) return;
        var dth = (f.ThrS - f.S) / Geo2.MPerNm;
        // When first sequenced, any runway with a slot 45 s sooner (the first choice is by STAR and a count of the queue,
        // not by time); after that, two minutes or more of delay and 90 s gained, at most every 10 minutes.
        var first = !f.Frozen;
        // Never back and forth (19 Nov, the first 400 nm hand-over: 40,699 switches in five hours, AAL814 alone 9,408 —
        // not yet frozen, it was "first sequenced" again every pass and each runway looked better than the one it was on
        // once it had left it): two minutes between switches, ten after the first, and at most three in all.
        if (Time - f.RwySwitchAt < (f.RwySwitches == 0 ? 120 : 600) || f.RwySwitches >= 3) return;
        if (dth < 50 || f.Delay < (first ? 60 : 120) || ArrRwys.Count < 2 || double.IsNaN(f.EtaNominal)) return;
        var star = f.StarPick?.p;
        double best = f.Delay; RunwayEnd? pick = null;
        foreach (var r in ArrRwys)
        {
            if (r.Runway == cur.Runway || r.LengthM < f.P.MinRunwayM) continue;
            if (star is not null && !ServesStar(star, r)) continue;
            if (RarelyLanded(r)) continue;
            // Its time to that runway instead: the difference in distance at about 220 kt.
            var eta = f.EtaNominal + ((f.Pos - r.Threshold).Length - (f.Pos - cur.Threshold).Length) / (220 * Kt);
            var d = EarliestSlot(r.Runway, eta, f) - f.EtaNominal;
            if (d < best - (first ? 45 : 90)) { best = d; pick = r; }
        }
        if (pick is null) return;
        var was = f.Delay;
        f.Rwy = pick;
        BuildArrivalPath(f, null, f.Pos);
        f.S = 0; f.Frozen = false; f.SlowDv = 0; f.RwySwitchAt = Time; f.EtaAt = -99; f.RwySwitches++;
        RefreshEta(f);
        _rwySwitches++;
        Log($"{f.Callsign}: runway {pick.Ident} instead of {cur.Ident} for less delay ({best:0} s instead of {was:0} s)");
    }
    private int _rwySwitches, _leftOut, _fanMoves;

    /// <summary>
    /// Spacing along the real tracks (user, 18 Nov): while still on its STAR before it's turned off, an arrival is moved
    /// to the real track in the fan whose length gains (or saves) the time needed — turned off later for a longer way
    /// round, earlier for a shorter one — so it flies the way real arrivals were actually vectored. Returns the seconds it
    /// couldn't absorb (0 when it fits); NaN when it's past the turn-off (or has no fan).
    /// </summary>
    private double FanPick(Flight f, double addSec, string why)
    {
        if (f.Fan is not { Count: >= 3 } fan || f.Path is null || f.Rwy is null) return double.NaN;
        // Past the turn-off (within 3 nm of it, or beyond it towards the runway): nothing left to choose.
        double FromLeave(List<V2> pts) { double l = 0; for (var k = 1; k < pts.Count; k++) l += (pts[k] - pts[k - 1]).Length; return (l - f.Rwy.Along(pts[^1])) / Geo2.MPerNm; }
        var toGo = (f.ThrS - f.S) / Geo2.MPerNm;
        var curPts = fan[Math.Clamp(f.FanIdx, 0, fan.Count - 1)].pts;
        if ((f.Pos - f.FanLeave).Length < 3 * Geo2.MPerNm || toGo < FromLeave(curPts) + 3) return double.NaN;
        if (Math.Abs(addSec) < 1) return 0;
        var gs = Math.Max(160, Math.Min(f.Gs, 260));
        var cur = fan[Math.Clamp(f.FanIdx, 0, fan.Count - 1)].lenNm;
        var wantNm = cur + addSec * gs / 3600;
        // One step at a time: at most 12 nm longer or shorter per change (the next look can go further).
        wantNm = Math.Clamp(wantNm, cur - 12, cur + 12);
        var best = Enumerable.Range(0, fan.Count).OrderBy(i => Math.Abs(fan[i].lenNm - wantNm)).First();
        // Its turn-off must still be ahead.
        if (best == f.FanIdx || (f.Pos - fan[best].pts[0]).Length < 3 * Geo2.MPerNm || toGo < FromLeave(fan[best].pts) + 3) return (wantNm - cur) * 3600 / gs;
        var was = f.FanIdx;
        var (oldPath, oldS, oldThr, oldVia, oldFan) = (f.Path, f.S, f.ThrS, f.Via, f.Fan);
        var (oldLeave, oldKey, oldRoute, oldStarNm, oldAppNm, oldRejoin, oldDetour) = (f.FanLeave, f.FanKey, f.Route, f.StarNm, f.AppNm, f.VectorRejoinS, f.DetourSec);
        f.FanIdx = best;
        BuildArrivalPath(f, null, f.Pos);
        f.S = 0;
        if (_realWayRefused)
        {
            f.FanIdx = was; f.Path = oldPath; f.S = oldS; f.ThrS = oldThr; f.Via = oldVia; f.Fan = oldFan; f.FanAt = Time;
            (f.FanLeave, f.FanKey, f.Route, f.StarNm, f.AppNm, f.VectorRejoinS, f.DetourSec) = (oldLeave, oldKey, oldRoute, oldStarNm, oldAppNm, oldRejoin, oldDetour);
            return double.NaN;
        }
        // A rebuild that makes its way in much longer than the track change asked for, or that starts by turning
        // back, is thrown away (the old way is kept).
        var grown = (f.ThrS - f.S) / Geo2.MPerNm - toGo - (fan[best].lenNm - cur);
        double firstTurn = 0, firstLeg = 0;
        if (f.Path is { Points.Count: > 1 } np) { var d0 = np.Points[1] - np.Points[0]; firstTurn = Math.Abs(Geo2.Diff(d0.Bearing, f.Heading)); firstLeg = d0.Length; }
        if (grown > 20 || (firstTurn > 100 && firstLeg > 2 * Geo2.MPerNm))
        {
            Log($"{f.Callsign}: real track change not made — the new way would have been {grown:0} nm longer than asked for{(firstTurn > 100 ? $", turning back {firstTurn:0}°" : "")}");
            f.FanIdx = was; f.Path = oldPath; f.S = oldS; f.ThrS = oldThr; f.Via = oldVia; f.Fan = oldFan; f.FanAt = Time;
            (f.FanLeave, f.FanKey, f.Route, f.StarNm, f.AppNm, f.VectorRejoinS, f.DetourSec) = (oldLeave, oldKey, oldRoute, oldStarNm, oldAppNm, oldRejoin, oldDetour);
            return double.NaN;
        }
        f.EtaAt = -99; f.LastTrombone = Time; f.FanAt = Time;
        _fanMoves++;
        f.Mgr = fan[best].lenNm > fan[was].lenNm ? "longer real track" : "shorter real track";
        Log($"{f.Callsign}: {(fan[best].lenNm > cur ? "a longer" : "a shorter")} real track off its STAR ({fan[best].lenNm:0} nm instead of {cur:0}) — {why}");
        return (wantNm - fan[best].lenNm) * 3600 / gs;
    }

    /// <summary>
    /// Constant traffic manager (plan: "target times at points along each route with small early corrections"): every
    /// pass, each arrival's predicted landing against its slot — the target time — and that difference kept as it passes
    /// 100, 40 and 20 nm to go, so the landing line, the map and the self-test show how early the flow was set up: a
    /// small difference far out, nothing left by 20 nm, rather than a big one fixed late with vectors.
    /// </summary>
    private void TrackSlot(Flight f, double slot)
    {
        if (double.IsNaN(f.EtaSec)) return;
        f.SlotDev = Time + f.EtaSec - slot;
        var togo = (f.ThrS - f.S) / Geo2.MPerNm;
        if (togo <= 100 && double.IsNaN(f.Dev100) && togo > 90) f.Dev100 = f.SlotDev;
        if (togo <= 40 && double.IsNaN(f.Dev40) && togo > 35) f.Dev40 = f.SlotDev;
        if (togo <= 20 && double.IsNaN(f.Dev20) && togo > 17) f.Dev20 = f.SlotDev;
    }

    private readonly List<double> _dev40 = new(), _dev20 = new();

    /// <summary>For the landing line: against its slot at 100, 40 and 20 nm.</summary>
    private string SlotReport(Flight f)
    {
        if (double.IsNaN(f.Dev100) && double.IsNaN(f.Dev40) && double.IsNaN(f.Dev20)) return "";
        if (!double.IsNaN(f.Dev40)) { _dev40.Add(Math.Abs(f.Dev40)); if (_dev40.Count > 200) _dev40.RemoveAt(0); }
        if (!double.IsNaN(f.Dev20)) { _dev20.Add(Math.Abs(f.Dev20)); if (_dev20.Count > 200) _dev20.RemoveAt(0); }
        string D(double d) => double.IsNaN(d) ? "–" : d.ToString("+0;-0;0");
        return $"; against its slot at 100/40/20 nm: {D(f.Dev100)}/{D(f.Dev40)}/{D(f.Dev20)} s";
    }

    /// <summary>For the control page and self-test: share of arrivals within 30 s of their slot at 40 and 20 nm.</summary>
    public string? SlotSummary => _dev40.Count == 0 && _dev20.Count == 0 ? null
        : $"on their slot (±30 s) at 40 nm {(_dev40.Count == 0 ? 0 : 100.0 * _dev40.Count(d => d <= 30) / _dev40.Count):0}%, at 20 nm {(_dev20.Count == 0 ? 0 : 100.0 * _dev20.Count(d => d <= 30) / _dev20.Count):0}% (last {Math.Max(_dev40.Count, _dev20.Count)} landings)";

    /// <summary>
    /// Traffic management (user, 26 Nov: "how could LAIA talk to LAIA that maybe it needs more MIT into a TRACON?" —
    /// and "how can we make it so LAIA doesn't abuse it?"). Every minute, per landing runway: the delay the approach is
    /// actually absorbing (its sequenced arrivals) and the arrivals due in the next 25 minutes against the slots the runway
    /// has. Only when both say "too much" for 5 minutes are miles-in-trail set on the streams feeding it — 10 nm, then up
    /// 5 nm at most every 20 minutes, never past 25 — and each step lapses 30 minutes after the evidence stops. None by
    /// default; set and lifted in the log and counted for the self-test.
    /// </summary>
    private void TrafficManagement()
    {
        foreach (var rw in ArrRwys)
        {
            var st = _mit.TryGetValue(rw.Runway, out var x) ? x : (nm: 0.0, setAt: double.NegativeInfinity, evidSince: double.NaN, lastEvid: double.NegativeInfinity);
            var seq = Flights.Where(f => f.Kind == "arrival" && !f.Done && !f.OnGround && f.InSeq && f.Rwy?.Runway == rw.Runway).ToList();
            var approachDelay = seq.Count == 0 ? 0 : seq.Average(f => Math.Max(0, f.Delay));
            var gap = 95.0;
            var demand = Flights.Count(f => f.Kind == "arrival" && !f.Done && !f.OnGround && !f.InSeq && f.Rwy?.Runway == rw.Runway && f.EtaNominal - Time is > 0 and < 1500);
            var slots = 1500 / gap;
            var evidence = approachDelay >= 120 && demand > slots;
            if (evidence)
            {
                if (double.IsNaN(st.evidSince)) st.evidSince = Time;
                st.lastEvid = Time;
                if (Time - st.evidSince >= 300 && st.nm < 25 && Time - st.setAt >= 1200)
                {
                    st.nm = st.nm == 0 ? 10 : st.nm + 5; st.setAt = Time; TmiSet++;
                    Log($"Traffic management: {st.nm:0} MIT for {rw.Ident} on every stream feeding it — the approach is absorbing {approachDelay / 60:0.0} min of delay, {demand} arrivals due in the next 25 min for {slots:0} slots");
                }
            }
            else
            {
                st.evidSince = double.NaN;
                if (st.nm > 0 && Time - st.lastEvid >= 1800 && Time - st.setAt >= 1800)
                {
                    st.nm = st.nm <= 10 ? 0 : st.nm - 5; st.setAt = Time;
                    Log(st.nm == 0 ? $"Traffic management: miles-in-trail for {rw.Ident} lifted (quiet for 30 minutes)" : $"Traffic management: {st.nm:0} MIT for {rw.Ident} (eased: quiet for 30 minutes)");
                }
            }
            _mit[rw.Runway] = st;
        }
    }
    private readonly Dictionary<int, (double nm, double setAt, double evidSince, double lastEvid)> _mit = new();
    private double _lastTmi;
    public int TmiSet;

    /// <summary>The miles-in-trail on an arrival's stream (its STAR to its runway), and the gap to the one ahead of it there.</summary>
    private (double nm, double gapNm, string ahead)? MitFor(Flight f)
    {
        if (f.Rwy is null || f.Path is null || !_mit.TryGetValue(f.Rwy.Runway, out var st) || st.nm <= 0) return null;
        var star = f.StarPick?.p.Name ?? "";
        var mine = (f.ThrS - f.S) / Geo2.MPerNm;
        Flight? best = null; var bestGap = double.PositiveInfinity;
        foreach (var o in Flights)
        {
            if (o == f || o.Done || o.OnGround || o.Kind != "arrival" || o.Rwy?.Runway != f.Rwy.Runway || o.Path is null || (o.StarPick?.p.Name ?? "") != star) continue;
            var gap = mine - (o.ThrS - o.S) / Geo2.MPerNm;
            if (gap > 0 && gap < bestGap) { bestGap = gap; best = o; }
        }
        return best is null ? null : (st.nm, bestGap, best.Callsign);
    }

    /// <summary>Whether an arrival has reached the point its landing sequence starts (and why), or null.</summary>
    internal string? SequencePoint(Flight f)
    {
        if (f.Rwy is not { } r) return null;
        var d = (f.Pos - r.Threshold).Length / Geo2.MPerNm;
        if (d > 40) return null;
        var elev = Apt.ElevationFt;
        var floorAhead = f.AltLimits.Where(c => c.S > f.S && c.Min > 0).Select(c => c.Min).DefaultIfEmpty(0).Max();
        var limit = Math.Max(elev + 8000, floorAhead + 500);
        if (f.AltFt > limit + 50) return null;
        return $"{d:0} nm from the runway, {f.AltFt - elev:N0} ft above the field" + (limit > elev + 8000 ? $" — its procedure keeps it at {floorAhead:N0} ft or above" : "");
    }

    /// <summary>
    /// The user among LAIA's arrivals to their runway (user, 24 Nov, KIAH–KMSP: DAL2654 and the user abreast 150 nm out
    /// on routes that merge; the order flipped as the user crossed 150 nm, so DAL2654 stayed slowed for the user and the
    /// user was told to slow behind it — both lost the time; later the user was clearly ahead of DAL2654 and still
    /// sequenced behind it). LAIA's own aircraft do the fine-tuning, the user's part stays simple:
    /// - one directly ahead of the user that the user will reach the runway with or before (by flying miles at the
    ///   speeds each will fly, published limits included), and that can still lose the time by speed, goes behind them;
    /// - one behind the user stays behind (it takes the delay) — a few seconds of estimate either way never flips it —
    ///   until the user is clearly later than it would land on its own (3 minutes) or it's much nearer the runway.
    /// </summary>
    private void UserAmongArrivals(Flight me, List<Flight> g)
    {
        var meEta = me.EtaSec;
        if (double.IsNaN(meEta)) return;
        var du = me.ThrS / Geo2.MPerNm;
        foreach (var o in g)
        {
            if (o == me || o.Path is null) continue;
            var d = (o.ThrS - o.S) / Geo2.MPerNm;
            var own = o.EtaNominal - Time;   // when it would land with nothing in its way
            if (o.BehindUser)
            {
                if (meEta - own > 180 || (d < du - 15 && d < 40) || o.Rejoin)
                {
                    o.BehindUser = false; o.OrderKey = o.EtaNominal; o.BehindUserReleased = Time;
                    Log($"{o.Callsign}: no longer kept behind {me.Callsign} (you) — {(meEta - own > 180 ? $"you're {(meEta - own) / 60:0.0} min later than it would land" : "it's well ahead of you now")}");
                }
                else if (o.OrderKey <= me.OrderKey) o.OrderKey = me.OrderKey + 1;
                continue;
            }
            if (o.OrderKey > me.OrderKey)
            {
                // Behind the user now: kept there from here on (the hysteresis), when the two are close in the order.
                // (Not again for 10 minutes once let go — 25 Nov, KEWR: UAL2480 was let go and kept again every pass, 189
                // times, the user's number flipping 6 ↔ 5.)
                if (o.OrderKey - me.OrderKey < 300 && Time - o.BehindUserReleased > 600) o.BehindUser = true;
                continue;
            }
        }
        // The one directly ahead: abreast, or the user clearly ahead of it.
        var ahead = g.Where(o => o != me && !o.BehindUser && o.OrderKey <= me.OrderKey && o.Path is not null && !o.Rejoin && !o.WentAround)
            .OrderByDescending(o => o.OrderKey).FirstOrDefault();
        if (ahead is null) return;
        var da = (ahead.ThrS - ahead.S) / Geo2.MPerNm;
        // When it would land: with the delay it already has (on a delay vector, slowed in a queue), not as if it had
        // none (KATL, 25 Nov: DAL1816, number 28 on a 300 s delay vector, predicted to land 1.9 min after the user, still
        // ahead of them — and the user told Mach .64 to get behind it).
        var ownA = Math.Max(ahead.EtaNominal - Time, double.IsNaN(ahead.EtaSec) ? 0 : ahead.EtaSec);
        var onFinal = da < 15 && ahead.Rwy is { } ar && Math.Abs(ar.Offset(ahead.Pos)) < 0.6 * Geo2.MPerNm && ar.Along(ahead.Pos) < 0;
        if (onFinal || da < 25) return;
        // What it would have to lose to land the gap behind the user, and what speed can still take from it (about
        // 2.2 s a mile beyond 20 nm: 280 → 240 kt high, 250 → 210 low) with the downwind's give (a minute).
        var lose = meEta + ThresholdGapNeeded(me, ahead, forDepartures: false) - ownA;
        var canLose = Math.Max(0, da - 20) * 2.2 + (ahead.Fan is { Count: >= 3 } || !double.IsNaN(ahead.BaseTurnNm) ? 60 : 0);
        if (meEta <= ownA + 30 && du <= da + 10 && lose <= canLose && Time - ahead.BehindUserReleased > 600)
        {
            ahead.OrderKey = me.OrderKey + 1;
            ahead.BehindUser = true;
            Log($"{ahead.Callsign}: now behind {me.Callsign} (you) for {me.Rwy?.Ident} — {(meEta < ownA - 30 ? "you're ahead of it" : "abreast")} ({du:0} against {da:0} flying nm), it takes the {Math.Max(0, lose):0} s");
        }
    }

    private void AssignSequenceSpeed(Flight f)
    {
        if (f.Rwy is null || f.Path is null) return;
        var dth = (f.ThrS - f.S) / Geo2.MPerNm;
        var old = f.SlowDv;
        // One decision per arrival close in (KMSP, 24 Nov, SWA696: its base turn was brought in for a gap too big
        // behind DAL2078, and three seconds later speed control slowed it 40 kt for its slot — then vectors and a
        // break-off; it landed 319 s behind where 88 were needed). Once the tactical spacing has a leader and a
        // downwind/base to move (inside 45 nm), the base turn and the speed come from the same prediction — the gap
        // behind the one ahead: speed takes only what the trombone can't, and no delay vectors on top.
        // (Only where the two disagree: the base turn being brought in to close a gap that's too big means no speed
        // reduction for the slot at the same time; otherwise the slot's delay stands — merging streams need it.)
        var tactical = dth <= 45 && f.LeaderCs is not null && Time - f.LastTrombone < 20 && !double.IsNaN(f.TromboneLeft) && f.TromboneAdd < -25;
        var delay = tactical ? Math.Max(0, f.TromboneLeft) : f.Delay;
        // Before its sequence: miles-in-trail in its stream (its STAR to its runway) when traffic management has set
        // them — taken by speed like any delay.
        if (!f.InSeq && MitFor(f) is { } mit && mit.gapNm < mit.nm)
        {
            var mitDelay = (mit.nm - mit.gapNm) * 3600 / Math.Max(200, f.Gs);
            if (mitDelay > delay) { delay = mitDelay; f.MitBehind = mit.ahead; }
        }
        else f.MitBehind = null;
        if (dth <= 22 || delay < 10) { if (delay < 5 || dth <= 22) f.SlowDv = 0; }
        else
        {
            var keep = f.SlowDv;
            f.SlowDv = 0;
            var base0 = EtaToThreshold(f, dth);
            var dv = 0.0;
            for (var c = 5.0; c <= 40; c += 5)   // 40 kt at most (250 → 210, 280 → 240)
            {
                f.SlowDv = c;
                dv = c;
                if (EtaToThreshold(f, dth) - base0 >= delay) break;
            }
            // Few, steady speed instructions (7110.65 5-7-1: the minimum necessary, no alternating; 20 Nov, CYUL: ACA336
            // and ACA376 got 12–14 each): a change of less than 10 kt is never given, and within 2½ minutes of the last
            // one only a change of 20 kt or more.
            // Kept only when the speed it has is the slower one (a little early to its slot is fixed by the trombone; too
            // fast for it, by nothing short of a break-off): any further reduction it needs is given.
            f.SlowDv = keep > 0 && keep >= dv && (keep - dv < 10 || (Time - f.SpeedAt < 150 && keep - dv < 20)) ? keep : dv;
            // More delay than speed (40 kt slower) and the trombone near the runway can take: delay vectors on the way
            // in, far out (beyond 40 nm), a few minutes at a time — never a hold.
            // Far out, the delay speed can't take is planned onto a longer real track (the way real controllers stretch
            // arrivals: a longer downwind, a wider base) before any delay vectors — one plan, made by the arrival
            // manager from the slot; close in (45 nm) the tactical spacing adjusts it from the aircraft ahead.
            if (!tactical && dv >= 40 && dth > 45 && f.Fan is { Count: >= 3 } && Time - f.FanAt > 20)
            {
                var bySpeed0 = EtaToThreshold(f, dth) - base0;
                if (f.Delay - bySpeed0 > 20) FanPick(f, f.Delay - bySpeed0, $"{f.Delay:0} s delay for the sequence (number {f.Seq} for {f.Rwy.Ident}), {bySpeed0:0} s of it by speed");
            }
            // At most three delay vectors for one arrival (UAL2211, 19 Nov: six in 18 minutes). Waiting for each to
            // rejoin before the next left too much delay for close in (offline rush: 23 min and 2 break-offs, not 18 and 0).
            // With the runway planner on, only straight after a planning pass (it runs just before), so the runway is the
            // one the plan has just confirmed — the planner won't move it once it's on a delay vector.
            var planOk = !Planner || ArrRwys.Count < 2 || Time == _lastPlan;
            if (!tactical && !f.InSeq && dv >= 40 && dth > 40 && Time - f.LastVector > 180 && f.DelayVectors < 2 && planOk)   // before its sequence only, two at most (user, 25 Nov: vectors "way over used")
            {
                var bySpeed = EtaToThreshold(f, dth) - base0;
                var tromb = Trombone(f, 0, "");
                var byTrombone = double.IsNaN(tromb) || double.IsNaN(f.BaseTurnNm) ? 0 : Math.Max(0, 25 - f.BaseTurnNm) * 2 * Geo2.MPerNm / (200 * Kt) * 0.6;
                // What the longest real track would still add, at about 200 kt.
                var byFan = f.Fan is { Count: >= 3 } fn ? Math.Max(0, fn.Max(x => x.lenNm) - fn[Math.Clamp(f.FanIdx, 0, fn.Count - 1)].lenNm) * 3600 / 200 : 0;
                var left = f.Delay - bySpeed - byTrombone - byFan;
                if (left > 90)
                    if (Vector(f, Math.Min(left, 300), rejoinNm: Math.Clamp(dth - 30, 25, 45), why: $"{f.Delay:0} s delay for the sequence (number {f.Seq} for {f.Rwy.Ident}), {bySpeed:0} s of it by speed")) f.DelayVectors++;
            }
        }
        // Not slowed again for a small delay just after being let go (a delay wavering around 5–10 s turned it off and on).
        if (old == 0 && f.SlowDv > 0 && delay < 30 && Time - f.SpeedAt < 300) f.SlowDv = 0;
        if (f.SlowDv != old) f.SpeedAt = Time;
        if (f.SlowDv > 0 && (old == 0 || Math.Abs(f.SlowDv - old) >= 10))
        {
            _speedAssigns++;
            Log(f.InSeq
                ? $"{f.Callsign}: reduce speed {f.SlowDv:0} kt for the sequence (number {f.Seq} for {f.Rwy.Ident}, {delay:0} s to lose{(tactical ? $", behind {f.LeaderCs} after the base turn's share" : "")})"
                : $"{f.Callsign}: reduce speed {f.SlowDv:0} kt for spacing ({delay:0} s to lose for {f.Rwy.Ident}{(f.MitBehind is { } mb && _mit.TryGetValue(f.Rwy.Runway, out var mt) ? $", {mt.nm:0} nm in trail behind {mb}" : "")})");
        }
        f.Mgr = f.Phase == "holding" ? "holding" : f.SlowDv > 0 ? $"slowed {f.SlowDv:0} kt" : f.Frozen ? "sequenced" : "";
    }

    /// <summary>
    /// Path stretching (vectors): when speed can't make the gap, the arrival is turned off its route on a heading and
    /// brought back to a point further along it (its STAR, or the downwind/base it's on), the detour as long as the
    /// time it must lose — the controller's tool before a hold. Turned away from the airport side, never more than
    /// 70° off, rejoining at least 9.5 nm from the runway. False when there's not enough route left to do it.
    /// </summary>
    private bool Vector(Flight f, double addSec, string why, double rejoinNm = 25)
    {
        var path = f.Path!;
        var (p, dir, _) = path.At(f.S);
        var gs = Math.Max(120, f.Gs) * Kt;
        var addM = addSec * gs;
        var keptS = path.KeptS;
        var latest = f.ThrS - 9.5 * Geo2.MPerNm;
        var wantS = Math.Min(f.S + rejoinNm * Geo2.MPerNm, latest);
        // At least 8 nm of route to stretch: shorter detours are a string of little turns, not vectors.
        if (wantS - f.S < 8 * Geo2.MPerNm) return false;
        var j = -1;
        for (var i = 0; i < path.Points.Count; i++) if (keptS[i] >= wantS - 1) { j = i; break; }
        if (j < 0 || keptS[j] > latest + 1) { for (var i = path.Points.Count - 1; i >= 0; i--) if (keptS[i] <= latest && keptS[i] > f.S + 4 * Geo2.MPerNm) { j = i; break; } }
        if (j < 0) return false;
        var rj = path.Points[j];
        var L = (rj - p).Length;
        var right = V2.Cross(V2.FromBearing(dir), f.Rwy!.Threshold - p) > 0;
        var axis = (rj - p).Unit;
        // A real vector: no more than 60° off the way it's flying now (user, 19 Nov: DAL1084 on WYNDE3 given headings
        // 175, 189, 232 and 010 in turn) — when the time wanted needs more than that, the most a 60° turn gives.
        V2 mid = default; var ok = false;
        for (var tries = 0; tries < 6 && addM > 20 * gs; tries++, addM *= 0.7)
        {
            var d = Math.Sqrt(Math.Pow((L + addM) / 2, 2) - Math.Pow(L / 2, 2));
            if (Math.Atan2(d, L / 2) * 180 / Math.PI > 70) continue;
            var m = p + (rj - p) * 0.5 + axis.Perp * (right ? 1 : -1) * d;
            if (Math.Abs(Geo2.Diff((m - p).Bearing, dir)) > 60) continue;
            mid = m; ok = true; break;
        }
        if (!ok) return false;
        addSec = addM / gs;
        var after = new List<V2> { mid, rj };
        var oldS = keptS[j];
        for (var i = j + 1; i < path.Points.Count; i++) after.Add(path.Points[i]);
        var (pts, added, dropped) = WithTurn(p, dir, after, f.Ias, f.AltFt);
        if (dropped > 0) return false;   // the dogleg's corner is inside the turn: not a vector it can fly
        var rjIdx = 2 + added;
        var oldLimits = f.SpeedLimits.Where(l => l.S > oldS).Select(l => (off: l.S - oldS, l.Kt)).ToList();
        var thrIndex = pts.Count - 2;
        if (RouteProblem(pts, thrIndex, f.Rwy!) is { } bad)
        {
            _routesRefused++;
            if (f.RefusedNote != "vectors") { f.RefusedNote = "vectors"; Log($"{f.Callsign}: route refused — vectors for spacing: {bad}; keeps its way in"); }
            f.LastVector = Time;
            return false;
        }
        f.Path = AirPath(pts, i => i >= thrIndex ? 1 : i <= rjIdx ? f.Ias : 180, f.AltFt, from: f);
        f.ThrS = f.Path.PointS[thrIndex];
        var at = f.Path.PointS[rjIdx];
        f.VectorRejoinS = at;
        f.DetourSec += addSec;
        f.SpeedLimits = oldLimits.Select(l => (at + l.off, l.Kt)).ToList();
        ShiftRestrictions(f, oldS, at);
        f.S = 0;
        f.LastVector = Time;
        f.Mgr = "vectored";   // keeps its place in the landing order (the detour is how it gets there)
        _vectors++;
        var hdg = (mid - p).Bearing;
        var back = f.Fixes.FirstOrDefault(x => Math.Abs(x.S - at) < 300).Fix;
        Log($"{f.Callsign}: vectors for spacing ({why}): turn {(V2.Cross(V2.FromBearing(dir), mid - p) > 0 ? "left" : "right")} heading {Geo2.Norm(hdg):000}, back {(back is { Length: > 0 } ? "direct " + back : "on course")} in {(at / Geo2.MPerNm):0} nm (+{addSec:0} s)");
        return true;
    }


    /// <summary>
    /// The trombone (user, 17 Nov: "a downwind to final is very fluid and dynamic … LAIA has to use more of its forms of
    /// creating separation and adjust better"): an arrival with a downwind and base still ahead has its base turn moved
    /// all the time — further out to lose time behind the aircraft ahead, earlier to close up a gap that's too big —
    /// rather than being held or vectored off. The downwind and the final each get longer (or shorter) by the same
    /// amount; the base stays at right angles to the final, the intercept angle as before, the join on the final where it
    /// was. The base turn stays at least 1 nm ahead of the aircraft, beyond the join, and no more than 25 nm out.
    /// Returns the seconds it could NOT absorb (0 when done; NaN when there's no downwind and base ahead).
    /// </summary>
    private double Trombone(Flight f, double addSec, string why)
    {
        var path = f.Path!; var rwy = f.Rwy!;
        var nm = Geo2.MPerNm;
        var pts0 = path.Points; var keptS = path.KeptS;
        var first = -1;
        for (var i = 0; i < pts0.Count; i++) if (keptS[i] > f.S + 1) { first = i; break; }
        if (first < 0) return double.NaN;
        // The base leg ahead: turn base (A), end of base (B), the join on the final (C).
        var ia = -1;
        for (var i = first; i + 2 < pts0.Count; i++)
        {
            V2 A = pts0[i], B = pts0[i + 1], C = pts0[i + 2];
            double aA = rwy.Along(A), aB = rwy.Along(B), aC = rwy.Along(C), oA = rwy.Offset(A), oB = rwy.Offset(B);
            if (Math.Abs(rwy.Offset(C)) > 0.3 * nm || aC > -1 * nm) continue;
            if (Math.Abs(aA - aB) > 0.6 * nm || Math.Abs(oA) - Math.Abs(oB) < 0.8 * nm || Math.Sign(oA) != Math.Sign(oB) || aA > aC) continue;
            // Reached along a downwind: the leg into the base turn runs away from the runway, beside it.
            var prev = i > first ? pts0[i - 1] : f.Pos;
            var leg = A - prev;
            if (leg.Length > 300 && V2.Dot(leg.Unit, rwy.Dir) > -0.5) continue;
            ia = i; break;
        }
        if (ia < 0) { f.BaseTurnNm = double.NaN; return double.NaN; }
        V2 pA = pts0[ia], pB = pts0[ia + 1], pC = pts0[ia + 2];
        var side = Math.Sign(rwy.Offset(pA));
        var dw = Math.Abs(rwy.Offset(pA));
        var baseNm = -rwy.Along(pA) / nm;
        var joinNm = -rwy.Along(pC) / nm;
        var tanI = Math.Abs(rwy.Offset(pB)) / Math.Max(0.5 * nm, rwy.Along(pC) - rwy.Along(pB));
        f.BaseTurnNm = baseNm;
        // Ground speed over the stretch it changes: downwind and final speeds.
        var gs = Math.Max(140, (Math.Min(f.Gs, 230) + f.P.Vapp + 20) / 2) * Kt;
        var minNm = Math.Max(joinNm + 1.5, -rwy.Along(f.Pos) / nm + 1.0);
        var maxNm = 25.0;
        var wantNm = baseNm + addSec * gs / 2 / nm;
        if (minNm > maxNm) return double.NaN;   // still further out than any base turn could be: nothing to move yet
        // Out a step at a time (4 nm an update, every 8 s or more) — a single odd reading can't throw an aircraft 12 nm
        // out at once.
        if (wantNm > baseNm + 4) wantNm = baseNm + 4;
        // Once it is turning base or about to (within 1.5 nm of the turn along its path), the base turn isn't brought in:
        // speed does the rest (22 Nov, KMSP: SWA4367 turned base, then a base turn moved behind it sent it round a loop
        // to rejoin). Further out it may still be pushed out (a downwind is extended until the turn).
        var newNm = Math.Clamp(wantNm, minNm, maxNm);
        // ... and never brought in once it is that close: a base turn moved in behind it means a turn back.
        if (newNm < baseNm && keptS[ia] - f.S < 1.5 * nm) return addSec;
        // Never pushed out when it isn't short: an aircraft already near its base turn turns there (it would otherwise
        // ride the "1 nm ahead" limit down an endless downwind).
        if (addSec <= 0 && newNm > baseNm) newNm = baseNm;
        if (addSec > 0 && newNm < baseNm) newNm = baseNm;
        if (Math.Abs(newNm - baseNm) < 0.3) return Math.Abs(wantNm - newNm) * 2 * nm / gs * Math.Sign(addSec);
        var left = new V2(-rwy.Dir.Y, rwy.Dir.X);
        var thr = rwy.Threshold;
        var endOff = Math.Clamp((newNm - joinNm) * nm * tanI, 0.8 * nm, 4 * nm);
        var nA = thr + left * (side * dw) - rwy.Dir * (newNm * nm);
        var nB = thr + left * (side * endOff) - rwy.Dir * (newNm * nm);
        var pts = new List<V2> { f.Pos };
        for (var i = first; i < ia; i++)
            if (rwy.Along(pts0[i]) > rwy.Along(nA) + 200) pts.Add(pts0[i]);   // downwind points beyond the new base turn go
        var newA = pts.Count;
        pts.Add(nA); pts.Add(nB);
        for (var i = ia + 2; i < pts0.Count; i++) pts.Add(pts0[i]);
        // Never a new way that starts by turning back (more than 100° from where it's heading) or loops (more than 300°
        // of turning in its next 15 nm): that change isn't made.
        if (newNm < baseNm && pts.Count > 1 && (pts[1] - pts[0]).Length > 300 && Math.Abs(Geo2.Diff((pts[1] - pts[0]).Bearing, f.Heading)) > 100) return addSec;
        var oldAS = keptS[ia];
        var thrIndex = pts.Count - 2;
        if (RouteProblem(pts, thrIndex, rwy) is { } bad)
        {
            _routesRefused++;
            // Once per arrival (the next look at its downwind comes 8 s later and would say the same).
            if (f.RefusedNote != "base") { f.RefusedNote = "base"; Log($"{f.Callsign}: route refused — base turn at {newNm:0.0} nm: {bad}; keeps its way in"); }
            f.LastTrombone = Time;
            return double.NaN;
        }
        var oldS = f.S;
        var oldLimits = f.SpeedLimits.Where(l => l.S > oldS).ToList();
        var oldAlt = f.AltLimits.Where(c => c.S > oldS).ToList();
        var oldChecks = f.Checks.Where(c => c.S > oldS).ToList();
        var oldFixes = f.Fixes.Where(c => c.S > oldS).ToList();
        f.Path = AirPath(pts, i => i >= thrIndex ? 1 : i == 0 ? Math.Max(f.Ias, 160) : 200, f.AltFt, from: f);
        var newAS = f.Path.KeptS[newA];
        double Map(double sOld) => sOld <= oldAS ? sOld - oldS : sOld - oldAS + newAS;
        f.SpeedLimits = oldLimits.Select(l => (Map(l.S), l.Kt)).ToList();
        f.AltLimits = oldAlt.Select(c => (Map(c.S), c.Min, c.Max)).ToList();
        f.Checks = oldChecks.Select(c => (Map(c.S), c.Fix, c.Min, c.Max, c.Kt)).ToList();
        f.Fixes = oldFixes.Select(c => (Map(c.S), c.Fix)).ToList();
        f.ThrS = f.Path.PointS[thrIndex];
        f.S = 0;
        f.EtaAt = -99;
        f.LastTrombone = Time;
        f.BaseTurnNm = newNm;
        f.Mgr = newNm > baseNm ? "downwind extended" : "base turn early";
        if (Math.Abs(newNm - f.TromboneLoggedNm) >= 1.5 || double.IsNaN(f.TromboneLoggedNm))
        {
            f.TromboneLoggedNm = newNm;
            _trombones++;
            Log($"{f.Callsign}: {(newNm > baseNm ? "downwind extended" : "turn base earlier")}, base turn now {newNm:0.0} nm out (was {baseNm:0.0}) — {why}");
        }
        return (wantNm - newNm) * 2 * nm / gs;
    }
    private int _trombones;

    private int _vectors, _speedAssigns, _holds, _orbits, _breakoffs;
    private readonly Dictionary<int, (Flight f, double t)> _lastLanding = new();
    private readonly List<double> _gapExcess = new();
    private int _gapsShort;

    /// <summary>At touchdown: the gap behind the previous landing on the same runway against the gap needed.</summary>
    private string LandingGapReport(Flight f)
    {
        if (f.Rwy is null) return "";
        var key = f.Rwy.Runway;
        var res = "";
        if (_lastLanding.TryGetValue(key, out var prev) && Time - prev.t < 600)
        {
            var gap = Time - prev.t;
            var need = ThresholdGapNeeded(prev.f, f, forDepartures: false);
            _gapExcess.Add(gap - need); if (_gapExcess.Count > 200) _gapExcess.RemoveAt(0);
            if (gap < need - 5) _gapsShort++;
            res = $"; {gap:0} s behind {prev.f.Callsign} (needed {need:0})";
        }
        _lastLanding[key] = (f, Time);
        return res;
    }

    private string _rwySig = "";

    /// <summary>
    /// The runways in use changed (the real ATIS turned the airport round, or the wind): as controllers do, arrivals
    /// close in (25 nm or less) finish on the runway they were given; the others get a runway now in use, with the
    /// STAR ending and approach for it, from where they are. Departures not yet lining up get a departure runway now
    /// in use and taxi to it from where they are (those still at the gate simply get the new runway).
    /// </summary>
    private void FlowChanged()
    {
        int arr = 0, dep = 0, replan = 0;
        foreach (var f in Flights.ToList())
        {
            if (f.Done || f.Rwy is null) continue;
            // Still on a runway in use, 25 nm or more out: planned again with the others on the new set — a runway just
            // opened takes its share (21 Nov, KATL: 09R and 10 opened at 15:23 behind a 30-deep queue for 08L, but those
            // on delay vectors kept 08L — 29 landed on 08L afterwards against 6 and 4). Its first plan again: free.
            if (f.Kind == "arrival" && !f.OnGround && f.Phase == "arriving" && ArrRwys.Contains(f.Rwy) && (f.ThrS - f.S) / Geo2.MPerNm > 25 && f != UserArr && !f.Test)
            { f.ReplanFree = true; f.Planned = false; replan++; }
            if (f.Kind == "arrival" && !f.OnGround && f.Phase == "arriving" && !ArrRwys.Contains(f.Rwy) && (f.ThrS - f.S) / Geo2.MPerNm > 25 && f.Path is not null)
            {
                var plan = PlanArrival(f.Other, f.Type);
                if (plan.Item1 is null) continue;
                f.Rwy = plan.Item1; f.StarPick = plan.Item2;
                f.Frozen = false; f.SlowDv = 0;
                BuildArrivalPath(f, null, f.Pos);
                f.S = 0;
                arr++;
            }
            else if (f.Kind == "departure" && f.OnGround && !DepRwys.Contains(f.Rwy) && f.Phase is "boarding" or "pushback" or "engine start" or "taxi" or "holding short")
            {
                var r = DepartureRunwayFor(f);
                if (r is null || r == f.Rwy) continue;
                f.Rwy = r;
                f.DepPlan = null; f.DepCourse = double.NaN;
                if (f.Phase is "taxi" or "holding short")
                {
                    var hold = PickRunwayEntry(r, out var entry, f.Pos);
                    f.HoldNode = hold; f.EntryNode = entry; f.StartNode = -1;
                    if (StartTaxiOut(f)) dep++;
                }
                else
                {
                    // Still at the gate or pushing back: its hold line moves to the new runway too (it would otherwise
                    // taxi to the old one and roll from there).
                    var hold = PickRunwayEntry(r, out var entry, f.Pos);
                    if (hold >= 0) { f.HoldNode = hold; f.EntryNode = entry; }
                    dep++;
                }
            }
        }
        Log($"Runway change at {Apt.Icao}: landing {string.Join(", ", ArrRwys.Select(e => e.Ident))}, departing {string.Join(", ", DepRwys.Select(e => e.Ident))} — {arr} arrival{(arr == 1 ? "" : "s")} and {dep} departure{(dep == 1 ? "" : "s")} given the new runways{(replan > 0 && ArrRwys.Count > 1 ? $", {replan} planned again across the runways now in use" : "")}; arrivals within 25 nm finish on their runway");
        // The planned changeover (plan, Phase 4): the last arrival to the old runways and the first to the new.
        var lastOld = Flights.Where(f => !f.Done && f.Kind == "arrival" && !f.OnGround && f.Rwy is not null && !ArrRwys.Contains(f.Rwy) && f != UserArr)
            .OrderByDescending(f => f.ThrS - f.S).FirstOrDefault();
        var firstNew = Flights.Where(f => !f.Done && f.Kind == "arrival" && !f.OnGround && f.Rwy is not null && ArrRwys.Contains(f.Rwy) && f != UserArr)
            .OrderBy(f => f.ThrS - f.S).FirstOrDefault();
        if (lastOld is not null || firstNew is not null)
            Log($"Runway change at {Apt.Icao}: the last arrival to the old runway{(lastOld is null ? " — none, it's clear" : $" is {lastOld.Callsign} on {lastOld.Rwy!.Ident} ({(lastOld.ThrS - lastOld.S) / Geo2.MPerNm:0} nm out)")}; the first to the new {(firstNew is null ? "— none yet" : $"is {firstNew.Callsign} on {firstNew.Rwy!.Ident} ({(firstNew.ThrS - firstNew.S) / Geo2.MPerNm:0} nm out)")}");
        _changeNote = (Time, string.Join("/", ArrRwys.Select(e => e.Ident)), string.Join("/", DepRwys.Select(e => e.Ident)), lastOld?.Callsign);
    }

    /// <summary>The last runway change, for the user's notice: when, landing, departing, the last arrival the old way.</summary>
    private (double at, string land, string dep, string? lastOld)? _changeNote;

    /// <summary>For the control page: what the arrival manager has done and how well the landings were spaced.</summary>
    public string? ArrivalManagerSummary
    {
        get
        {
            var gaps = _gapExcess.Count == 0 ? "" : $"landings {_gapExcess.Average():+0;-0} s on average beyond the gap needed ({_gapsShort} short)";
            var acts = $"{_speedAssigns} speed reductions, {_trombones} downwind changes, {_rwySwitches} runway moves, {_vectors} vectors, {_breakoffs} break-offs";
            return gaps.Length == 0 && _speedAssigns + _vectors + _trombones + _rwySwitches + _breakoffs == 0 ? null : (gaps.Length > 0 ? gaps + "; " : "") + acts + (SlotSummary is { } ss ? "; " + ss : "");
        }
    }

    private void Orbit(Flight f, string why)
    {
        Log($"{f.Callsign}: making a 360 for spacing ({why})");
        _orbits++; f.Mgr = "360"; f.Frozen = false;
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
        f.Path = AirPath(pts, i => i >= thrIndex ? 1 : i <= 8 ? f.Ias : 180, f.AltFt, from: f);
        f.ThrS = f.Path.PointS[thrIndex];
        var added = f.Path.PointS[Math.Min(8, f.Path.PointS.Length - 1)];
        f.SpeedLimits = oldLimits.Select(l => (added + l.off, l.Kt)).ToList();
        ShiftRestrictions(f, f.S, added);
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
        _holds++; f.Mgr = "holding"; f.Frozen = false;
        Log($"{f.Callsign}: holding for spacing ({why}), {(f.Ias > 0 ? Math.Min(f.Ias, f.AltFt <= 6000 ? 200 : f.AltFt <= 14000 ? 230 : 265) : 200):0} kt, right turns");
        f.LastHold = Time;
        f.LastOrbit = Time;
        var holdKt = Math.Min(f.Ias, f.AltFt <= 6000 ? 200 : f.AltFt <= 14000 ? 230 : 265);
        var v = Wind.Tas(holdKt, f.AltFt) * Kt;
        var r = Math.Max(1500, v * v / (G * Math.Tan(25 * Math.PI / 180)));
        var leg = Math.Max(2 * Geo2.MPerNm, v * (f.AltFt <= 14000 ? 60 : 90));   // 1-minute legs at or below 14,000 ft, 1.5 above
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
        f.Path = AirPath(pts, i => i >= thrIndex ? 1 : i < holdCount ? holdKt : 180, f.AltFt, from: f);
        f.ThrS = f.Path.PointS[thrIndex];
        // The procedure's speed limits move with the route (they come after the hold now).
        var added = f.Path.PointS[holdCount - 1];
        f.SpeedLimits = oldLimits.Select(l => (added + l.off, l.Kt)).ToList();
        f.SpeedLimits.Add((f.Path.PointS[1], holdKt));
        ShiftRestrictions(f, f.S, added);
        f.S = 0;
    }

    /// <summary>Departures waiting to go: at the hold line for a while, or lined up and kept waiting.</summary>
    /// <summary>Departures waiting at, or predicted ready within 3 minutes for, a runway that matches.</summary>
    private bool DeparturesFor(Func<int, bool> runway) => Flights.Any(o => o.Kind == "departure" && !o.Done && o.OnGround && o.Rwy is not null && runway(o.Rwy.Runway)
        && ((o.Phase == "holding short" && Time - o.PhaseTime > 40) || (o.Phase is "lined up" or "lining up" && Time - o.PhaseTime > 20)
            || (o.Phase == "taxi" && !double.IsNaN(o.ReadyAt) && o.ReadyAt - Time < 180)));


    private bool RunwayBusy(int runway, Flight self) =>
        UserOnRunway(runway) || Flights.Any(o => o != self && !o.Done && (o.Reserve == runway || (o.OnRunway == runway && !(o.Kind == "departure" && !o.OnGround))));

    /// <summary>The user is on this runway (on the ground, on the strip): nobody lines up, takes off or lands on it.</summary>
    private bool UserOnRunway(int runway)
    {
        if (!UserOnGround || UserPos is not { } u) return false;
        foreach (var r in Apt.Ends)
        {
            if (r.Runway != runway) continue;
            var along = r.Along(u);
            // On the runway itself (its half width + 10 m, between its ends): a box 45 m either side and 60 m past each
            // end also caught the user holding short at a runway's end (20 Nov, KLAS: no 01R departure for 7 minutes
            // while the user waited near its end; SWA2617 lined up the moment the user moved).
            if (Math.Abs(r.Offset(u)) < r.WidthM / 2 + 10 && along > -10 && along < r.LengthM + 10) return true;
        }
        return false;
    }

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
        // The user, landing on this runway (user, 13 Nov: LAIA "sent a plane right at me like I wasn't there").
        if (UserArr is { Rwy: { } ur } me && ur.Runway == runway && !double.IsNaN(me.EtaSec)) best = Math.Min(best, Math.Max(0, me.EtaSec));
        return best;
    }

    private readonly HashSet<string> _vecNoteLogged = new();
    private object? _rexFor;
    private readonly Dictionary<string, Dictionary<int, int>> _rex = new();

    /// <summary>Real turn-offs from a runway: each learned taxi-in's first position off it, put on the nearest of the
    /// runway's exits (within 250 m of where the exit leaves it), counted per exit; null with fewer than 20.</summary>
    public Dictionary<int, int>? RealExits(RunwayEnd rwy)
    {
        if (Learned is not { Taxi.Count: > 0 } lp) return null;
        if (!ReferenceEquals(_rexFor, lp)) { _rexFor = lp; _rex.Clear(); }
        var key = rwy.Ident;
        if (!_rex.TryGetValue(key, out var res))
        {
            res = new Dictionary<int, int>();
            var offs = Apt.RunwayLinks(rwy.Runway).Select(l => l.off).Distinct().ToList();
            foreach (var t in lp.Taxi)
            {
                if (t.Kind != "arrival" || t.Runway != rwy.Ident || t.Points.Count == 0) continue;
                var p0 = Apt.ToXY(t.Points[0][0], t.Points[0][1]);
                var best = -1; var bd = 250.0;
                foreach (var o in offs) { var d = (Apt.Nodes[o] - p0).Length; if (d < bd) { bd = d; best = o; } }
                if (best >= 0) res[best] = res.GetValueOrDefault(best) + 1;
            }
            _rex[key] = res;
        }
        return res.Values.Sum() >= 20 ? res : null;
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
        if (Apt.HasGround && !RunwayOnly)
        {
            gate = FindGate(f.P, f.Airline, f.Brand, f.Callsign);
            // Exits on the terminal side (where the gate is) first, then the first one the aircraft can make
            // with normal braking (about 2 m/s²): a high-speed exit is taken at about 45 kt, a 90° one at about 20 kt.
            var side = gate is null ? 0 : Math.Sign(rwy.Offset(gate.P));
            // Not every landing brakes the same (user, 21 Nov, KATL: "a lot exiting the runway at the same spot … a bit
            // too frequent"): most at about 2.4 m/s², about one in six gently (the next exit or so), one in thirty a
            // long roll; heavies a little gentler.
            // Only with room behind (21 Nov, KPHL: DAL2703 went around 90 s behind SWA2463 still on its long roll-out —
            // the spacing assumes a normal one): the next arrival to this runway 2½ minutes or more out (3 for a long
            // roll), as pilots expedite with traffic close behind.
            var u0 = Rng.NextDouble();
            var nextIn = Flights.Where(o => o != f && o.Kind == "arrival" && !o.Done && !o.OnGround && o.Rwy?.Runway == rwy.Runway && !double.IsNaN(o.EtaSec))
                .Select(o => o.EtaSec).DefaultIfEmpty(double.MaxValue).Min();
            if (UserArr is { Done: false, OnGround: false } ua && ua.Rwy?.Runway == rwy.Runway && !double.IsNaN(ua.EtaSec)) nextIn = Math.Min(nextIn, ua.EtaSec);
            if (u0 >= 0.97 && nextIn < 180) u0 = 0.9;
            if (u0 >= 0.80 && nextIn < 150) u0 = 0.5;
            var decel = 2.4 * (u0 < 0.80 ? 0.9 + 0.2 * Rng.NextDouble() : u0 < 0.97 ? 0.65 + 0.15 * Rng.NextDouble() : 0.5 + 0.1 * Rng.NextDouble()) * (u0 >= 0.80 && f.P.Heavy ? 0.9 : 1.0);
            double Needed(double dot) { var vx = dot > 0.5 ? ExitSpeedFast : ExitSpeedSquare; return tdAlong + Math.Max(250, (v0 * v0 - vx * vx) / (2 * decel)); }
            var links = Apt.RunwayLinks(rwy.Runway)
                .Select(l => (l.on, l.off, along: rwy.Along(Apt.Nodes[l.on]), dir: (Apt.Nodes[l.off] - Apt.Nodes[l.on]).Unit))
                .Where(l => V2.Dot(l.dir, rwy.Dir) > -0.2 && l.along < rwy.LengthM + 30) // never turn back
                .ToList();
            bool WrongSide((int on, int off, double along, V2 dir) l) => side != 0 && Math.Sign(rwy.Offset(Apt.Nodes[l.off])) != side;
            var cands = links.Where(l => l.along >= Needed(V2.Dot(l.dir, rwy.Dir)))
                .OrderBy(l => WrongSide(l) ? 1 : 0).ThenBy(l => l.along).ToList();
            if (cands.Count == 0) cands = links.OrderByDescending(l => l.along).ToList();
            // Where real aircraft turned off this runway (the learned taxi-ins' first positions off it): among the exits
            // it can make, one picked in proportion to real use (user, 21 Nov: "a lot exiting the runway at the same
            // spot"); with traffic close behind, only the first two it can make (expedite).
            else if (RealExits(rwy) is { } rex)
            {
                var right = cands.Where(l => !WrongSide(l)).ToList();
                var pool = (nextIn < 150 ? (right.Count > 0 ? right : cands).OrderBy(l => l.along).Take(2) : right.Count > 0 ? right : cands).ToList();
                var w = pool.Select(l => (double)rex.GetValueOrDefault(l.off)).ToList();
                if (w.Sum() >= 10)
                {
                    var pick = Rng.NextDouble() * w.Sum(); var k = 0;
                    while (k < w.Count - 1 && pick >= w[k]) { pick -= w[k]; k++; }
                    var chosen = pool[k];
                    cands.Remove(chosen); cands.Insert(0, chosen);
                }
            }
            List<int>? taxi = null;
            (int on, int off, double along, V2 dir)? exit = null;
            foreach (var c in cands.Take(5))
            {
                exit ??= c;
                if (gate is null) break;
                taxi = Apt.Route(c.off, gate.Access, ActiveRunways(), null, TaxiCost(f));
                if (taxi is not null) { exit = c; break; }
            }
            if (taxi is null) gate = null;
            if (exit is { } x)
            {
                var exName = Apt.Adj[x.off].FirstOrDefault(e => e.Type == 1 && e.Name.Length > 0)?.Name;
                f.ExitNote = $"; turning off {(exName is null ? "" : "at " + exName + " ")}{x.along:N0} m down{(nextIn < 600 ? $" (next arrival {nextIn:0} s behind)" : "")}";
                pts.Add(rwy.Threshold + rwy.Dir * x.along);
                route = new List<int> { x.on };
                route.AddRange(taxi ?? new List<int> { x.off });
                offset = 2;
                pts.AddRange(route.Select(n => Apt.Nodes[n]));
                if (gate is not null) LeadIntoGate(pts, route, offset, gate, f.P);
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
                break;
            case "at gate":
                f.Lights = Nav | Beacon | Logo;
                if (Time - f.PhaseTime > 25) { f.Kind = "parked"; f.SetPhase("parked", Time); f.Lights = Nav | Logo | Cabin; f.ArrivedAt = Time; f.Overnight = NightNow; }
                break;
            case "boarding":
                f.Lights = Nav | Logo | Cabin;
                f.Timer -= dt;
                if (f.Timer <= 0 && GateHold(f)) break;
                if (f.Timer <= 0 && PushbackClear(f)) StartPushback(f);
                // Up to 5 minutes early when its runway is quiet (user, 23 Nov: traffic "doesn't have to be beholden to
                // strict schedules" — departures as efficient as possible): nobody else on the way to it, no wait there.
                else if (f.Timer > 0 && f.Timer <= 300 && RunwayQuiet(f) && PushbackClear(f))
                {
                    Log($"{f.Callsign}: pushing back {f.Timer / 60:0.0} min early — runway {f.Rwy?.Ident} is quiet");
                    f.Timer = 0; f.GateHoldDone = true;
                    StartPushback(f);
                }
                break;
            case "pushback":
                f.Lights = Nav | Beacon | Logo;
                if (f.Timer > 0) { f.Timer -= dt; break; }
                if (MoveGround(f, dt))
                {
                    // Push then pull: the tug now tows it forward onto the open side, a few seconds after stopping.
                    if (f.PullPath is { } pull) { f.Path = new PathGeom(pull, _ => f.PullRadius, 0.5); f.PullPath = null; f.S = 0; f.Reverse = false; f.Gs = 0; f.Timer = 4; break; }
                    f.SetPhase("engine start", Time); f.Timer = 25 + Rng.NextDouble() * 20; f.Gs = 0;
                }
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
                if (f.EntryNode >= 0 && f.Rwy is { } hr && hr.Along(Apt.Nodes[f.EntryNode]) > hr.LengthM * 0.5)
                {
                    // Holding short near the far end of its runway (the runway changed under it): taxi to the right end.
                    var hold = PickRunwayEntry(hr, out var entry, f.Pos);
                    if (hold >= 0 && hold != f.HoldNode)
                    {
                        f.HoldNode = hold; f.EntryNode = entry; f.StartNode = -1;
                        Log($"{f.Callsign}: at the far end of runway {hr.Ident}, taxiing to its departure end");
                        StartTaxiOut(f);
                        break;
                    }
                }
                if (LineUpAllowed(f)) StartLineUp(f);
                // A departure waiting at its runway says why (20 Nov, KLAS: SWA2617 held short of 01R with nothing logged).
                else if (Time - f.PhaseTime > 90 && !f.HoldWhyLogged && f.HoldWhy is { } hw)
                {
                    f.HoldWhyLogged = true;
                    Log($"{f.Callsign}: holding short of {f.Rwy?.Ident} for departure for {Time - f.PhaseTime:0} s: {hw}");
                }
                break;
            case "lining up":
                f.Lights = Nav | Beacon | Logo | Taxi | Strobes | Landing;
                if (MoveGround(f, dt)) { f.SetPhase("lined up", Time); f.Gs = 0; f.Timer = 3 + Rng.NextDouble() * 4; }
                break;
            case "lined up":
                f.Lights = Nav | Beacon | Logo | Taxi | Strobes | Landing;
                f.Timer -= dt;
                if (f.Timer <= 0 && (f.Test || TakeoffAllowed(f))) StartTakeoff(f);
                else if (f.Timer <= 0 && Time - f.PhaseTime > 45 && !f.LinedUpWaitLogged)
                {
                    f.LinedUpWaitLogged = true;
                    var r = f.Rwy!.Runway;
                    var why = new List<string>();
                    if (UserOnRunway(r)) why.Add("you're on the runway");
                    foreach (var o in Flights.Where(o => o != f && !o.Done && (o.Reserve == r || (o.OnRunway == r && !(o.Kind == "departure" && !o.OnGround)))))
                        why.Add($"{o.Callsign} on the runway ({o.Phase})");
                    var eta = ArrivalEta(r);
                    if (eta < TimeToClearForArrival(f) + 5) why.Add($"the next arrival {eta:0} s out (needs {TimeToClearForArrival(f) + 5:0})");
                    if (!CrossingRunwayClearForTakeoff(f)) why.Add("traffic on a crossing runway");
                    var wk = WakeRemaining(f);
                    if (wk > 0) why.Add($"wake/spacing behind {_lastDep.GetValueOrDefault(r)?.Callsign} ({wk:0} s more)");
                    Log($"{f.Callsign}: lined up on {f.Rwy.Ident} for {Time - f.PhaseTime:0} s, waiting: {(why.Count > 0 ? string.Join("; ", why) : "nothing found")}");
                }
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
                if (f.Phase == "landing roll" && f.OnRunway < 0 && f.Gs < 45)
                {
                    // How long it really took to clear: spacing on this runway learns from it.
                    var rot = Time - f.PhaseTime;
                    if (f.Rwy is not null && rot is > 20 and < 300)
                    {
                        _rotSeen[f.Rwy] = _rotSeen.TryGetValue(f.Rwy, out var old) ? old * 0.7 + rot * 0.3 : rot;
                        if (rot > 60) Log($"{f.Callsign}: off runway {f.Rwy.Ident} {rot:0} s after touchdown (runway {f.Rwy.Ident} now averages {_rotSeen[f.Rwy]:0} s)");
                    }
                    f.SetPhase("taxi in", Time); f.MaxKt = f.P.Heavy ? 16 : 20;
                }
                if (f.Phase == "landing roll" && f.Gs < 35) f.MaxKt = Math.Min(f.MaxKt, 30);
                if ((!Apt.HasGround || RunwayOnly) && f.Phase == "landing roll" && f.Gs < 30) { f.Done = true; break; }
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
            if (st is not null && f.OnRunway == f.Reserve) st.Entered = true;
            if (st is null || f.S > st.ExitS) f.Reserve = -1;
            // Across and off the runway: it's the runway's no longer, even if it stops just past it (KORD, 15 Nov: an
            // aircraft that had crossed 9R stopped in the queue for 9C, short of where 9R counted as clear, and kept 9R
            // reserved — a line of departures held short of 9R behind it).
            else if (st.Entered && f.OnRunway != f.Reserve) f.Reserve = -1;
            // Cleared but not moving on (stopped before the runway behind something): give the runway back and wait
            // at the hold line again, rather than keeping every other crossing waiting.
            else if (!double.IsNaN(st.ClearedAt) && Time - st.ClearedAt > 30 && f.S < st.S + 5 && f.Gs < 1 && f.OnRunway != f.Reserve)
            {
                st.Cleared = false; st.ClearedAt = double.NaN; f.Reserve = -1;
            }
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
        if (f.Phase == "landing roll")
        {
            // Brake to a fast taxi (about 40 kt) and keep it to the exit; the exit's curve sets the turn-off speed.
            // Crawling the rest of the way at 30 kt kept Orlando's 36L arrivals on the runway for 70 s.
            var roll = f.OnRunway >= 0 ? Math.Max(f.MaxKt * Kt, ExitSpeedFast) : f.MaxKt * Kt;
            target = Math.Min(target, Math.Max(roll, v - 2.0 * dt));
        }
        // Runway crossings: ask for clearance on the way, stop at the hold line if not given.
        Stop? hold = null;
        foreach (var st in f.Stops)
        {
            if (st.Cleared || st.S < f.S - 1) continue;
            if (st.S - f.S < 80)
            {
                var why = CrossingBlock(st, f);
                if (why is null) { st.Cleared = true; st.ClearedAt = Time; st.ClearedS = f.S; f.Reserve = st.Runway; st.WaitSince = double.NaN; continue; }
                if (f.S >= st.S - 3) WaitAtCrossing(f, st, why);
            }
            hold = st;
            break;
        }
        if (hold is not null) target = Math.Min(target, Math.Sqrt(2 * 0.8 * Math.Max(0, hold.S - f.S)));
        // Ease into stops (a sudden stop rocks the aircraft on its nose gear).
        target = Math.Min(target, Math.Sqrt(2 * (f.Phase == "landing roll" ? 1.2 : f.Reverse ? 0.3 : 0.7) * Math.Max(0, path.Length - f.S)) + 0.12);
        if ((f.Phase is not ("landing roll" or "lining up") || (f.Phase == "landing roll" && f.OnRunway < 0)) && Blocked(f, v)) target = 0;
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
        // The main gear on the line, not the nose (user: big jets' main gear cut the corners): the aircraft's
        // reference point sits ahead of its main gear, so the gear follows the path a little behind it, the aircraft
        // points the way the gear is rolling, and the reference point swings wide in a turn — the "judgmental
        // oversteer" taxi pilots of long aircraft use. On a straight line nothing changes.
        var gearBack = f.Reverse ? 0 : Math.Clamp(0.2 * f.P.LengthM - 4, 0, 12);
        if (gearBack > 0 && f.S > gearBack)
        {
            var (gp, gdir, _) = path.At(f.S - gearBack);
            p = gp + V2.FromBearing(gdir) * gearBack;
            dir = gdir;
        }
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
        // Never stopped on a runway while taxiing (a crossing, a turn-off): after 45 s it moves on past everything on
        // the ground in its way, and the log says so.
        if (f.OnGround && f.OnRunway >= 0 && f.Gs < 0.5 && f.Phase is "taxi in" or "taxi")
        {
            f.RunwayStopFor += dt;
            if (f.RunwayStopFor > 45 && Time > f.IgnoreUntil)
            {
                f.IgnoreAll = Flights.Where(o => o != f && o.OnGround && !o.Done).ToHashSet();
                f.IgnoreUntil = Time + 60;
                Log($"{f.Callsign}: WARNING stopped on runway {Apt.Ends.FirstOrDefault(e => e.Runway == f.OnRunway)?.Ident} for {f.RunwayStopFor:0} s{(f.BlockedBy is { } bb ? $" behind {bb.Callsign}" : "")}; moving on off it");
                f.RunwayStopFor = 0;
            }
        }
        else f.RunwayStopFor = 0;
        var atHold = f.Stops.Any(st => !st.Cleared && Math.Abs(st.S - f.S) < 3);
        if (f.Gs > 0.5) { f.GoFirstAt = -1; f.GoFirstFailed = false; }
        if (f.Gs > 0.5 || atHold || f.JunctionHeld || InQueue(f)) { f.StuckFor = 0; return; }
        f.StuckFor += dt;
        // After 2 minutes on the way in, another gate comes first (it used to wait for a minute without a reroute,
        // which the reroutes every minute never allowed: DAL2225 was removed at MCO instead of going elsewhere).
        if (f.StuckFor > 120 && f.Phase == "taxi in" && Time - f.GateTryTime > 60) { f.GateTryTime = Time; if (TryOtherGate(f)) { f.StuckFor = 0; return; } }
        // Round the user only when the user is parked (hasn't moved for 3 minutes): taxiing or waiting in line, the user
        // keeps their place and LAIA's traffic waits behind (21 Nov, KORD: UAL2142 "took another way round" the user
        // waiting in the departure queue — across the grass, and ahead of the user in the sequence).
        if (f.StuckFor > 20 && f.BlockedByUser && Time - _userMovedAt > 180 && UserPos is { } u && Time - f.RerouteTime > 60)
        {
            f.RerouteTime = Time;
            // A new way doesn't reset the clock: only moving does (DAL1885 rerouted every minute for 10 minutes,
            // never reaching the "another gate" step, while the user's aircraft sat in its way).
            if (Reroute(f, u, "your aircraft")) return;
        }
        // Waiting behind the user isn't stuck: the user will move (20 Nov, KLAS: SWA2107 removed behind the user in the
        // departure queue). The user can remove one from the map if it's really stuck.
        // ... while the user is moving: a user parked at a gate for the night is a fixed obstacle (21 Nov, KATL: DAL2978
        // waited an hour behind the parked user, and six behind it were removed).
        if (f.StuckFor > 300 && !(f.BlockedByUser && Time - _userMovedAt < 180))
        {
            Log($"{f.Callsign}: stuck for 5 minutes at ({f.Pos.X:0}, {f.Pos.Y:0}), blocked by {f.BlockedBy?.Callsign ?? (f.BlockedByUser ? "your aircraft" : "nothing")}; removed");
            f.Done = true;
        }
    }

    /// <summary>Removed by the user from the map (user, 20 Nov: "we need a way for a user to remove stuck aircraft like
    /// SWA2617"), with what it was doing logged so the cause can be found.</summary>
    public string? RemoveByUser(Flight f)
    {
        if (f == UserArr || f.Done) return null;
        var why = f.HoldWhy is { } hw && f.Phase == "holding short" ? $", waiting: {hw}" : "";
        var blk = f.BlockedBy is { } b ? $", blocked by {b.Callsign}" : f.BlockedByUser ? ", blocked by your aircraft" : "";
        Log($"{f.Callsign}: removed by the user — {f.Kind} {f.Phase} for {Time - f.PhaseTime:0} s at ({f.Pos.X:0}, {f.Pos.Y:0}){(f.Rwy is { } rr ? ", runway " + rr.Ident : "")}{blk}{why}{(f.StuckFor > 0 ? $", not moving for {f.StuckFor:0} s" : "")}");
        f.Done = true;
        return f.Callsign;
    }

    /// <summary>An arriving aircraft that can't get to its gate takes another free gate it can reach.</summary>
    /// <summary>
    /// The user's aircraft in this one's way: along the route it will taxi (not straight ahead of its nose — 22 Nov,
    /// KLGA: the taxilane behind gate 48 points straight at the gate before it bends off to 45–47, so ten Uniteds stopped
    /// 78 m short of the user parked on 48, though their route passed 30–48 m from the user's nose, and were removed).
    /// The user's aircraft is its fuselage (from the stand's heading when parked on one) and wings; parked on a stand
    /// for a minute it is part of the scenery like LAIA's parked aircraft (a small margin), otherwise a wider one.
    /// </summary>
    private bool UserInTheWay(Flight f, V2 u, double v)
    {
        var up = Perf.For(string.IsNullOrEmpty(UserType) ? "B738" : UserType);
        var parked = Time - _userMovedAt > 60 ? Apt.Spots.Where(sp => (sp.P - u).Length < sp.RadiusM + 5).OrderBy(sp => (sp.P - u).Length).FirstOrDefault() : null;
        var udir = V2.FromBearing(parked?.Heading ?? UserTrack);
        var nose = u + udir * (up.LengthM * 0.45); var tail = u - udir * (up.LengthM * 0.55);
        var margin = parked is not null ? 4.0 : 12.0;
        var look = f.P.LengthM / 2 + 60 + v * v / 2.0;
        if (f.Path is not { } path)
        {
            var r = u - f.Pos; var mdir = V2.FromBearing(f.Reverse ? Geo2.Norm(f.Heading + 180) : f.Heading);
            return V2.Dot(r, mdir) > 0 && V2.Dot(r, mdir) < look && Math.Abs(V2.Cross(mdir, r)) < f.P.SpanM / 2 + up.SpanM / 2 + margin;
        }
        for (var d = 5.0; d <= look; d += 5)
        {
            var sp = Math.Min(f.S + d, path.Length);
            var p = path.At(sp).p;
            // Its wing past the user's fuselage, or wing to wing where the user's wings are.
            if (SegDist(p, tail, nose) < f.P.SpanM / 2 + margin || (p - u).Length < (f.P.SpanM + up.SpanM) / 2 + margin) return true;
            if (sp >= path.Length) break;
        }
        return false;
    }

    /// <summary>Getting off a runway: on it, or its tail still within the runway's protected area (about 80 m from the
    /// centreline). Only that far: once its tail is past the hold line it is ordinary taxi traffic (user, 22 Nov: "Traffic
    /// coming immediately off a runway should be given priority. But just immediately off a runway").</summary>
    private bool Vacating(Flight x)
    {
        if (!x.OnGround || x.Phase is "parked" or "boarding" or "pushback") return false;
        if (x.OnRunway >= 0 && x.Phase is "taxi" or "taxi in" or "landing roll") return true;
        if (x.Phase != "taxi in" && x.Phase != "landing roll") return false;
        var tail = x.Pos - V2.FromBearing(x.Heading) * (x.P.LengthM / 2);
        return Apt.Ends.Any(e => e.Contains(tail, 60));
    }

    /// <summary>
    /// Hold short of the next taxiway junction on its route when the aircraft ahead beyond it, stopped or crawling,
    /// would leave it across the junction: the room past the junction (to that aircraft's tail) must take its whole
    /// length plus 10 m. Not for one getting off a runway, or one already in the junction. Checked once a second.
    /// </summary>
    private (string name, Flight ahead)? JunctionHold(Flight f, double v)
    {
        if (f.Phase is not ("taxi" or "taxi in") || f.Reverse || f.Path is not { } path || f.TaxiRoute is not { Count: > 1 } route) return null;
        if (Vacating(f)) return null;
        if (Time - f.JhCheckAt < 1) return f.JhLast;
        f.JhCheckAt = Time; f.JhLast = null;
        var nose = f.S + f.P.LengthM / 2;
        var lead = 30 + v * v / 2.0;
        // The next junction ahead: a node on its route where three or more taxi lines meet, off the runways.
        var ne = NextEdge(f);
        var start = ne is { } e0 ? Math.Max(0, route.IndexOf(e0.v)) : 0;
        for (var i = start; i < route.Count && i < start + 12; i++)
        {
            var n = route[i];
            if (Apt.NodeRunway[n] >= 0) continue;
            if (Apt.Adj[n].Count(x => x.B < Apt.TaxiPointCount && x.Type is 1 or 4) < 3) continue;
            var jp = Apt.Nodes[n];
            if ((jp - f.Pos).Length > nose - f.S + lead + 40) return null;
            // Where it is along the path.
            double sJ = double.NaN; var bd = 12.0;
            for (var sp = f.S; sp <= Math.Min(path.Length, f.S + 200); sp += 4) { var d = (path.At(sp).p - jp).Length; if (d < bd) { bd = d; sJ = sp; } }
            if (double.IsNaN(sJ)) return null;
            var edgeIn = sJ - 15;
            if (nose > edgeIn) continue;                 // already in it: through it
            if (edgeIn - nose > lead) return null;       // not yet
            // The nearest aircraft on its path beyond the junction.
            Flight? ahead = null; double sAhead = double.MaxValue;
            foreach (var o in Flights)
            {
                if (o == f || o.Done || !o.OnGround || o.Phase is "parked" or "boarding" or "pushback") continue;
                if ((o.Pos - jp).Length > f.P.LengthM + o.P.LengthM + 60) continue;
                for (var sp = sJ; sp <= Math.Min(path.Length, sJ + f.P.LengthM + o.P.LengthM + 40); sp += 4)
                {
                    var (pp, pd, _) = path.At(sp);
                    if ((pp - o.Pos).Length >= o.P.SpanM * 0.25 + 6) continue;
                    // Only one going the same way — the line it would join — never one coming the other way (22 Nov, KMSP:
                    // DAL2151 and DAL258 each held short of B for the other, nose to nose, and nothing moved for an hour).
                    if (Math.Abs(Geo2.Diff(o.Heading, pd)) < 60 && sp < sAhead) { sAhead = sp; ahead = o; }
                    break;
                }
            }
            // Not when that one is waiting for this one (it would never move up), nor for it rolling along.
            if (ahead is null || ahead.Gs > 3 || ahead.BlockedBy == f) return null;
            var room = sAhead - ahead.P.LengthM / 2 - (sJ + 15);
            if (room >= f.P.LengthM + 10) return null;
            var name = Apt.Adj[n].Where(x => x.Name.Length > 0 && !route.Contains(x.B)).Select(x => x.Name).FirstOrDefault()
                       ?? Apt.Adj[n].Where(x => x.Name.Length > 0).Select(x => x.Name).FirstOrDefault() ?? "the junction";
            f.JhLast = (name, ahead);
            return f.JhLast;
        }
        return null;
    }

    private static double SegDist(V2 p, V2 a, V2 b)
    {
        var ab = b - a; var l2 = V2.Dot(ab, ab);
        var t = l2 < 1e-9 ? 0 : Math.Clamp(V2.Dot(p - a, ab) / l2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    private bool TryOtherGate(Flight f)
    {
        var old = f.Spot;
        f.Spot = null;
        var gate = FindGate(f.P, f.Airline, f.Brand, f.Callsign);
        if (gate is not null && gate != old)
        {
            f.Spot = gate;
            // Round what is in the way: the user's aircraft when it's the user (22 Nov, KLGA: the point 40 m ahead was the
            // taxilane itself, so no new way was found and ten were removed instead).
            var avoid = f.BlockedByUser && UserPos is { } uu ? uu : f.Pos + V2.FromBearing(f.Heading) * 40;
            if (Reroute(f, avoid, f.BlockedByUser ? "your aircraft" : "the blockage")) { Log($"{f.Callsign}: going to {gate.Label} instead"); return true; }
        }
        f.Spot = old;
        return false;
    }

    private bool CrossingAllowed(int runway, Flight f) => CrossingBlock(new Stop { Runway = runway, S = f.S, ExitS = f.S }, f) is null;

    private string RunwayName(int runway) => string.Join("/", Apt.Ends.Where(e => e.Runway == runway).Select(e => e.Ident));

    /// <summary>
    /// Why it may not cross the runway now (null: it may). Besides the runway itself (an aircraft or the user on it, an
    /// arrival within a minute, a departure lined up), the far side must be clear: it doesn't go onto a runway it
    /// can't get off again (KORD, 15 Nov: "everyone seems to be holding short of 9R" — one crossing could stop
    /// half-way, on the runway, behind an aircraft waiting on the far side, keeping the runway reserved and every other
    /// crossing waiting behind it).
    /// </summary>
    private string? CrossingBlock(Stop st, Flight f)
    {
        var runway = st.Runway;
        if (UserOnRunway(runway)) return "your aircraft on the runway";
        foreach (var o in Flights)
            if (o != f && !o.Done && (o.Reserve == runway || (o.OnRunway == runway && !(o.Kind == "departure" && !o.OnGround))) && !PassedCrossing(o, f, st))
                return $"{o.Callsign} {(o.Reserve == runway && o.OnRunway != runway ? "crossing" : "on the runway")}";
        // Time to be across and clear of it (taxi speed about 15 kt from here to 60 m past the far side), plus 30 s.
        var crossSec = f.Path is not null ? Math.Max(0, st.ExitS + 60 - f.S) / Math.Max(4, f.Gs * Kt) + 15 : 45;
        if (ArrivalEta(runway) is var eta && eta < Math.Max(60, crossSec + 30)) return $"an arrival {eta:0} s out";
        if (Flights.FirstOrDefault(o => o.Rwy?.Runway == runway && o.Phase is "lined up" or "takeoff roll" && !PassedCrossing(o, f, st)) is { } dep) return $"{dep.Callsign} departing";
        // Room on the far side: nobody on its way between the runway and 60 m past where it's clear of it.
        if (f.Path is { } path && st.ExitS > st.S)
        {
            // Room for all of it past the runway, and the gap to the one ahead (20 Nov, KLAS: crossing 26R/8L behind the
            // queue for 01R, aircraft stopped with their tails over the runway).
            var end = Math.Min(path.Length, st.ExitS + f.P.LengthM + 60);
            foreach (var o in Flights)
            {
                if (o == f || o.Done || !o.OnGround || o.Phase is "parked" or "boarding") continue;
                for (var d = st.S; d <= end; d += 15)
                    if ((path.At(d).p - o.Pos).Length < 25) return $"{o.Callsign} in the way on the far side";
            }
        }
        return null;
    }

    /// <summary>
    /// Waiting at a hold line: logged after a minute with the reason. Two aircraft waiting for each other across the
    /// same runway (each in the other's way on the far side) can't both wait: after a minute the one waiting longer
    /// takes another way round.
    /// </summary>
    private void WaitAtCrossing(Flight f, Stop st, string why)
    {
        if (double.IsNaN(st.WaitSince)) st.WaitSince = Time;
        var waited = Time - st.WaitSince;
        if (waited > 60 && !st.WaitLogged) { st.WaitLogged = true; Log($"{f.Callsign}: holding short of {RunwayName(st.Runway)} for {waited:0} s: {why}"); }
        if (waited > 60 && !st.Rerouted && why.EndsWith("in the way on the far side"))
        {
            var name = why[..why.IndexOf(' ')];
            if (Flights.FirstOrDefault(o => o.Callsign == name && !o.Done) is { } other
                && other.Stops.Any(s2 => s2.Runway == st.Runway && !s2.Cleared && !double.IsNaN(s2.WaitSince) && s2.WaitSince >= st.WaitSince))
            {
                st.Rerouted = true;
                if (Reroute(f, other)) Log($"{f.Callsign}: {other.Callsign} waiting across {RunwayName(st.Runway)} the other way: taking another way round");
            }
        }
    }

    /// <summary>Another aircraft (or the user) ahead and close: wait. Breaks head-on standoffs after a while.</summary>
    /// <summary>The taxi edge the aircraft is on or about to enter (from node u to node v) and how far it is to u.</summary>
    private (int u, int v, double toU)? NextEdge(Flight f)
    {
        if (f.TaxiRoute is not { Count: > 1 } r) return null;
        var fwd = V2.FromBearing(f.Heading);
        for (var i = 0; i + 1 < r.Count; i++)
        {
            var a = Apt.Nodes[r[i]]; var b = Apt.Nodes[r[i + 1]];
            var ab = b - a; var len = ab.Length;
            if (len < 1) continue;
            var t = V2.Dot(f.Pos - a, ab) / (len * len);
            var off = (f.Pos - (a + ab * Math.Clamp(t, 0, 1))).Length;
            if (off < 30 && t is >= -0.2 and < 1) return t < 0 ? (r[i], r[i + 1], (a - f.Pos).Length) : (r[i], r[i + 1], 0);
        }
        return null;
    }

    /// <summary>
    /// A taxiway with no parallel: an aircraft about to enter a segment that another is already on, coming the other
    /// way, waits at the junction until it's through, rather than meeting it nose to nose (user, MCO).
    /// </summary>
    private Flight? SingleLaneTraffic(Flight f)
    {
        if (f.Phase is not ("taxi" or "taxi in") || NextEdge(f) is not { } me || me.toU <= 0 || me.toU > 90) return null;
        foreach (var o in Flights)
        {
            if (o == f || o.Done || !o.OnGround || o.Phase is not ("taxi" or "taxi in")) continue;
            if ((f.Ignore == o || f.IgnoreAll?.Contains(o) == true) && Time < f.IgnoreUntil) continue;   // a standoff this one was told to go first in
            if (NextEdge(o) is not { } it) continue;
            // It's on (or entering first) the same segment the other way round.
            if (it.u == me.v && it.v == me.u && it.toU < me.toU) return o;
        }
        return null;
    }

    private bool Blocked(Flight f, double v)
    {
        var mdir = V2.FromBearing(f.Reverse ? Geo2.Norm(f.Heading + 180) : f.Heading);
        Flight? by = null;
        var userBlock = false;
        // Waiting at a junction for traffic on a single taxiway: a block like any other, so a ring of aircraft waiting
        // for each other is found and broken (MCO: SWA6325 waited for ASA301 to come off the single lane while ASA301
        // waited behind SWA6325 — both sat 5 minutes until SWA6325 was removed).
        by = SingleLaneTraffic(f);
        if (by is null)
        foreach (var o in Flights)
        {
            if (o == f || !o.OnGround || o.Done || o.Phase is "parked" or "boarding" or "takeoff roll") continue;
            if ((f.Ignore == o || f.IgnoreAll?.Contains(o) == true) && Time < f.IgnoreUntil) continue;
            // A pushback has the right of way over taxiing traffic (the taxiing aircraft holds for it), so the two
            // never wait for each other.
            if (f.Phase == "pushback" && o.Phase is "taxi" or "taxi in") continue;
            var r = o.Pos - f.Pos;
            var dist = r.Length;
            if (dist > 250) continue;
            var ahead = V2.Dot(r, mdir);
            var lat = Math.Abs(V2.Cross(mdir, r));
            var need = f.P.LengthM / 2 + o.P.LengthM / 2 + 25 + v * v / 2.0;
            if (ahead > 0 && ahead < need && lat < (f.P.SpanM + o.P.SpanM) / 2 * 0.6 + 6) { by = o; break; }
            // Paths crossing (intersections): look a few seconds ahead; whoever gets there second waits — except that an
            // aircraft on a runway (crossing it, or turning off it) always goes first, to clear it (KORD, 18 Nov: four
            // arrivals went around for aircraft that stopped on a runway they were crossing).
            if (ahead > -10 && dist < 160 && !(Vacating(f) && !Vacating(o)))
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
                    if ((o.Pos - meet).Length < (f.Pos - meet).Length || (Vacating(o) && !Vacating(f))) { by = o; }
                    break;
                }
                if (by is not null) break;
            }
        }
        if (by is null && UserOnGround && UserPos is { } u && UserInTheWay(f, u, v)) userBlock = true;
        f.BlockedByUser = userBlock;
        // Don't block the intersection (user, 22 Nov: "have traffic hold short of a taxiway if traffic before it would
        // make it block an intersection — a tactic real ATC use"): KLGA 07:23, three aircraft each stopped part-way
        // through a junction another needed, a ring that never cleared.
        // At most 90 s, then it goes on for 3 minutes whatever is ahead (the line may not move for a long time).
        if (f.JunctionHeld && Time - f.JunctionHeldSince > 90) { f.JunctionHeld = false; f.JunctionFreeUntil = Time + 180; }
        if (by is null && !userBlock && Time > f.JunctionFreeUntil && JunctionHold(f, v) is { } jh)
        {
            if (!f.JunctionHeld) f.JunctionHeldSince = Time;
            if (!f.JunctionHeld && Time - f.JunctionHeldSince < 1) Log($"{f.Callsign}: holding short of {jh.name} — {jh.ahead.Callsign} ahead would leave it stopped across the junction");
            f.JunctionHeld = true; f.BlockedBy = null; f.BlockedFor = 0;
            return true;
        }
        f.JunctionHeld = false;
        if (by is null) { f.BlockedBy = null; f.BlockedFor = 0; return userBlock; }
        if (f.BlockedBy == by) f.BlockedFor += 0.25; else { f.BlockedBy = by; f.BlockedFor = 0; }
        // Standoff: aircraft all waiting for each other — two head-on on a taxilane, or three or four in a ring at an
        // intersection (Orlando: a long pushback into the taxiway jammed three 737s). The newer flights look for
        // another way round; if there is none, the oldest one in the ring eventually goes.
        var ring = Ring(f);
        if (ring is not null)
        {
            // One on a runway always goes first (21 Nov, KATL: DAL330 crossing 08R after landing and DAL2952 waiting to
            // depart from it each waited for the other; DAL2952, the older, "went first" but couldn't line up with DAL330
            // on the runway — 08R had no departures for nearly three hours).
            // ... and one that was given the go but couldn't move for a minute hands it on to the next (KLGA, 22 Nov: DAL969
            // "going first" every 20 s for six minutes without moving; ACA713 and AAL1608 were removed).
            if (ring.All(x => x.GoFirstFailed)) foreach (var x in ring) x.GoFirstFailed = false;
            var able = ring.Where(x => !x.GoFirstFailed).ToList();
            var oldest = able.FirstOrDefault(Vacating) ?? able.FirstOrDefault(x => x.OnRunway >= 0) ?? able.OrderBy(x => x.Id, StringComparer.Ordinal).First();
            if (f != oldest && f.BlockedFor > 8 && Time - f.RerouteTime > 60)
            {
                f.RerouteTime = Time;
                if (Reroute(f, by)) { f.BlockedBy = null; f.BlockedFor = 0; return false; }
            }
            if (f == oldest && f.BlockedFor > 20)
            {
                if (f.GoFirstAt < 0) f.GoFirstAt = Time;
                else if (Time - f.GoFirstAt > 60)
                {
                    f.GoFirstFailed = true; f.GoFirstAt = -1;
                    Log($"{f.Callsign}: can't go first in the standoff with {string.Join(", ", ring.Where(x => x != f).Select(x => x.Callsign))}; looking for another way, the next one goes");
                    f.RerouteTime = Time;
                    if (Reroute(f, by)) { f.BlockedBy = null; f.BlockedFor = 0; return false; }
                    return true;
                }
                if (f.BlockedFor < 20.3) Log($"{f.Callsign}: standoff with {string.Join(", ", ring.Where(x => x != f).Select(x => x.Callsign))}; going first");
                // Past every one of them, not just the one it saw first: in a ring of three it only ever passed one,
                // then waited for the next (KORD, 19 Nov: UAL590 "going first" for 7 minutes between UAL2059 and
                // AAL2636 while eleven aircraft behind them were removed as stuck).
                f.Ignore = by;
                f.IgnoreAll = ring.Where(x => x != f).ToHashSet();
                f.IgnoreUntil = Time + 45;
                return userBlock;
            }
        }
        return true;
    }

    /// <summary>
    /// This airport's own arrivals kept apart en route, whatever runway they're for (user, 19 Nov: DAL2879 and UAL2211
    /// both at FL360 a few miles apart on VEECK5, one for 28C and one for 27C — the landing sequence only spaces arrivals
    /// to the same runway). Beyond 30 nm and above 10,000 ft, every 2 s each pair is projected 5 minutes ahead along
    /// their routes; where they'd come within 5 nm and 1,000 ft, the one later in its landing order is given the level
    /// 1,000 ft under the other (whole thousands) — an early descent, as en-route ATC would give — until clear.
    /// </summary>
    private void SeparateOwnArrivals()
    {
        var nm = Geo2.MPerNm;
        var arr = Flights.Where(f => f.Kind == "arrival" && !f.Done && !f.OnGround && f.Path is not null && f.Rwy is not null
            && f != UserArr && (f.ThrS - f.S) > 30 * nm && f.AltFt > 10000 && f.Phase != "go-around").ToList();
        foreach (var f in arr) if (!double.IsNaN(f.SepCeiling) && Time > f.SepUntil) { f.SepCeiling = double.NaN; f.SepFrom = null; }
        for (var i = 0; i < arr.Count; i++)
            for (var j = i + 1; j < arr.Count; j++)
            {
                var a = arr[i]; var b = arr[j];
                // Arrivals to the same runway are already spaced in its landing sequence (speed, real tracks).
                if (a.Rwy!.Runway == b.Rwy!.Runway) continue;
                if ((a.Pos - b.Pos).Length > 60 * nm || Math.Abs(a.AltFt - b.AltFt) >= 1000 && Math.Sign(a.Vs) == Math.Sign(b.Vs)) continue;
                var meet = false;
                for (var t = 0.0; t <= 300 && !meet; t += 20)
                {
                    var pa = a.Path!.At(Math.Min(a.Path.Length, a.S + a.Gs * Kt * t)).p;
                    var pb = b.Path!.At(Math.Min(b.Path.Length, b.S + b.Gs * Kt * t)).p;
                    var za = a.AltFt + Math.Clamp(a.Vs, -3000, 3000) * Math.Min(t, 120) / 60;
                    var zb = b.AltFt + Math.Clamp(b.Vs, -3000, 3000) * Math.Min(t, 120) / 60;
                    meet = (pa - pb).Length < 5 * nm && Math.Abs(za - zb) < 1000;
                }
                if (!meet) continue;
                // The later one in the landing order (by its unhindered landing time) goes under.
                var (lead, under) = (double.IsNaN(a.EtaNominal) ? double.MaxValue : a.EtaNominal) <= (double.IsNaN(b.EtaNominal) ? double.MaxValue : b.EtaNominal) ? (a, b) : (b, a);
                var level = Math.Floor((lead.AltFt - 1000) / 1000) * 1000;
                if (level < 11000) continue;
                if (double.IsNaN(under.SepCeiling) || level < under.SepCeiling)
                {
                    if (under.SepFrom != lead.Callsign) Log($"{under.Callsign}: descending early to {level:N0} ft below {lead.Callsign} (en route, both for {Apt.Icao}, {(a.Pos - b.Pos).Length / nm:0} nm apart)");
                    under.SepCeiling = level; under.SepFrom = lead.Callsign;
                }
                under.SepUntil = Time + 30;
            }
    }

    private static bool tooCloseNow(double gapNm, double needNm) => gapNm < needNm - 0.5;

    /// <summary>
    /// A neighbouring field's arrival over the main airport's traffic (user, 13 Nov: MLB's arrivals on BITHO7 cross
    /// MCO's east side): the satellite's traffic is kept clear of the primary's, as an approach control would — where,
    /// in the next 2 minutes, descending would bring it within 3.5 nm and 1,000 ft of the main airport's traffic below,
    /// it stops 1,000 ft above the highest that traffic gets while near (in whole thousands). NaN when nothing's there.
    /// </summary>
    private double ProbeDescent(Flight f)
    {
        if (f.Path is null || f.Phase is "final" or "flare" or "landing roll" or "go-around" || (f.ThrS - f.S) / Geo2.MPerNm < 8) return double.NaN;
        double floor = double.NaN; string? who = null;
        foreach (var o in OthersAndUser())
        {
            // A neighbour's arrival over the main airport's traffic; and (22 Nov, KLGA/KJFK) the main airport's arrival
            // over a neighbour's departure climbing below it — LaGuardia's arrivals kept descending onto JFK's departures
            // whose climbs had been stopped (JBU625 at 4,829 ft, DAL2140 down through 5,204 ft 2.9 nm away).
            if (!(o.Main || (!RunwayOnly && o.Kind == "departure")) || o.AltFt > f.AltFt + 500 || (o.Pos - f.Pos).Length > 20 * Geo2.MPerNm) continue;
            // An arrival behind the user in the same runway's sequence is spaced by distance, not kept above (KATL,
            // 15 Nov: DAL2266, behind the user for 08L, "descent stopped at 5,000 ft for DAL1196 (you) below" — it would
            // end up high on the approach).
            if (o.Phase == "user" && UserArr is { Rwy: { } ur } && f.Rwy?.Runway == ur.Runway && string.Equals(UserDestination, Apt.Icao, StringComparison.OrdinalIgnoreCase)) continue;
            double highest = double.MinValue; var conflict = false;
            for (var t = 0.0; t <= 120; t += 10)
            {
                var fp = f.Path.At(Math.Min(f.Path.Length, f.S + f.Gs * 0.5144 * t)).p;
                var op = Ahead(o, t);
                var oAlt = o.AltFt + (o.Kind == "departure" ? Math.Max(0, o.Vs) : Math.Min(0, o.Vs)) * t / 60;
                var fAlt = f.AltFt + Math.Min(0, f.Vs) * t / 60;
                var lat = (fp - op).Length;
                if (lat < 5 * Geo2.MPerNm) highest = Math.Max(highest, Math.Max(o.AltFt, oAlt));
                if (lat < 3.5 * Geo2.MPerNm && Math.Abs(fAlt - oAlt) < 1000) conflict = true;
            }
            if (!conflict || highest == double.MinValue) continue;
            var level = Math.Ceiling((highest + 1000) / 1000) * 1000;
            if (level > f.AltFt + 200) continue;   // it's below already: the other side's rules keep them apart
            if (double.IsNaN(floor) || level > floor) { floor = level; who = $"{o.Callsign} ({o.Airport})"; }
        }
        // This airport's own departures climbing out below (KORD, 18 Nov: departures stopped their climb 1,000 ft
        // below an arrival, which then kept descending onto them — 0.7 nm and 860 ft apart). The same probe along
        // both paths: it stops 1,000 ft above the highest the departure gets while near.
        foreach (var o in Flights)
        {
            if (o.Kind != "departure" || o.OnGround || o.Done || o.Path is null || o.AltFt > f.AltFt + 500 || (o.Pos - f.Pos).Length > 20 * Geo2.MPerNm) continue;
            double highest = double.MinValue; var conflict = false;
            for (var t = 0.0; t <= 120; t += 10)
            {
                var fp = f.Path.At(Math.Min(f.Path.Length, f.S + f.Gs * 0.5144 * t)).p;
                var op = o.Path.At(Math.Min(o.Path.Length, o.S + o.Gs * 0.5144 * t)).p;
                var cap = !double.IsNaN(o.ClimbStop) ? o.ClimbStop : 40000;
                var oAlt = Math.Min(cap, o.AltFt + Math.Max(0, o.Vs) * t / 60);
                var fAlt = f.AltFt + Math.Min(0, f.Vs) * t / 60;
                var lat = (fp - op).Length;
                if (lat < 5 * Geo2.MPerNm) highest = Math.Max(highest, Math.Max(o.AltFt, oAlt));
                if (lat < 3.5 * Geo2.MPerNm && Math.Abs(fAlt - oAlt) < 1000) conflict = true;
            }
            if (!conflict || highest == double.MinValue) continue;
            var level = Math.Ceiling((highest + 1000) / 1000) * 1000;
            if (level > f.AltFt + 200) continue;   // already below it: the departure's climb stop keeps them apart
            if (double.IsNaN(floor) || level > floor) { floor = level; who = o.Callsign; }
        }
        if (who is not null && f.FloorFor != who)
        {
            f.FloorFor = who; _descentStops++;
            Log($"{f.Callsign}: descent stopped at {floor:N0} ft for {who} below");
        }
        if (who is null) f.FloorFor = null;
        return floor;
    }

    /// <summary>
    /// Waiting in a line, not stuck (KORD, 16 Nov: UAL416, UAL938 and UAL1819 were removed after 5 minutes in the queue
    /// behind UAL1779, which was waiting its turn for runway 10C): the aircraft it's waiting for — or the one that one
    /// waits for, and so on — is moving, holding short, lining up or at a hold line for a crossing. A ring of aircraft
    /// each waiting for the next is a real jam and still counts.
    /// </summary>
    private bool InQueue(Flight f)
    {
        var seen = new HashSet<Flight> { f };
        var x = f.BlockedBy;
        for (var i = 0; i < 12 && x is not null; i++)
        {
            if (!seen.Add(x) || x.Done) return false;
            if (!x.OnGround || x.Gs > 0.5 || x.Phase is "holding short" or "lining up" or "lined up" or "takeoff roll" or "landing roll") return true;
            if (x.Stops.Any(st => !st.Cleared && Math.Abs(st.S - x.S) < 3)) return true;
            x = x.BlockedBy;
        }
        return false;
    }

    /// <summary>The ring of ground aircraft each waiting for the next, back to this one (null if there's no ring).</summary>
    private static List<Flight>? Ring(Flight f)
    {
        var ring = new List<Flight> { f };
        var x = f.BlockedBy;
        for (var i = 0; i < 5 && x is not null; i++)
        {
            if (x == f) return ring;
            if (ring.Contains(x)) return null;
            ring.Add(x);
            x = x.BlockedBy;
        }
        return null;
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
            if (f.Rwy is not null) _lastDep[f.Rwy.Runway] = f;
            f.Reserve = -1;
            f.Vs = 600;
            f.SetPhase("climbing", Time);
            Log($"{f.Callsign}: airborne from runway {f.Rwy!.Ident}{(f.Route.Length > 0 ? " on the " + f.Route + " departure" : "")}{f.DepReport}");
        }
    }

    private void Climb(Flight f, double dt)
    {
        var rwy = f.Rwy!;
        var agl = f.AltFt - rwy.ElevationFt;
        f.Lights = Nav | Beacon | Logo | Strobes | (f.AltFt < 10000 ? Landing : 0);
        if (agl > 100 && Time - f.LiftoffTime > 3) f.Gear = false;
        double targetIas = agl < 1500 ? f.P.V2 + 15 : f.AltFt < 10000 ? 250 : 290;
        if (!double.IsNaN(f.DepSpeedCap) && agl > 1500) targetIas = Math.Min(targetIas, Math.Max(f.P.V2 + 20, f.DepSpeedCap));
        // 91.117(b): 200 knots at or below 2,500 ft within 4 nm of a Class C or D airport.
        if (agl <= 2500 && f.Pos.Length < 4 * Geo2.MPerNm && Airspace.Has200KnotArea(Apt.Icao)) targetIas = Math.Min(targetIas, 200);
        // The SID's published speed limits.
        targetIas = Math.Max(f.P.V2 + 10, Math.Min(targetIas, ProcedureLimit(f, true)));
        f.Ias += Math.Clamp(targetIas - f.Ias, -1.0 * dt, 1.6 * dt);
        var targetVs = agl < 1500 ? f.P.ClimbFpm : f.P.ClimbFpm * (f.Ias < targetIas - 5 ? 0.7 : 0.9);
        // The SID's "at or below" restrictions ahead: level off at them until the fix is passed.
        var cap = f.AltLimits.Where(c => c.Max > 0 && c.S > f.S - 0.2 * Geo2.MPerNm).Select(c => c.Max).DefaultIfEmpty(double.MaxValue).Min();
        // Departure control: stop the climb below an arrival it would otherwise climb into (radar vectors or not).
        if (Time - f.ProbeAt >= 2) { f.ProbeAt = Time; f.ClimbStop = ProbeArrivals(f, f.P.ClimbFpm * 0.8); }
        // A stop below where it already is holds the altitude it had when the stop came — not "where it is now" every
        // 2 s, which crept up with the climb still easing off (22 Nov, KLGA: UAL2485 "climb stopped at" 3,852, 3,973,
        // 4,053, 4,075 ft while UAL1499 and UAL1826 came down to 778 ft above it).
        if (double.IsNaN(f.ClimbStop)) f.ClimbHoldFt = double.NaN;
        else
        {
            if (f.ClimbStop < f.AltFt && double.IsNaN(f.ClimbHoldFt)) f.ClimbHoldFt = f.AltFt;
            cap = Math.Min(cap, Math.Max(f.ClimbStop, double.IsNaN(f.ClimbHoldFt) ? Math.Min(f.AltFt, cap) : f.ClimbHoldFt));
        }
        // The SID's top altitude (MCO: 7,000 ft, under the arrivals' "at or above 8,000"): level there; the climb is
        // given early once it's clear of the arrivals, and in any case 10 minutes after take-off.
        if (!f.ClimbReleased)
        {
            if (f.AltFt > f.TopAltFt - 1500 && Time - f.ReleaseAt >= 4)
            {
                f.ReleaseAt = Time;
                if (double.IsNaN(f.TopReachedAt) && f.AltFt > f.TopAltFt - 100) f.TopReachedAt = Time;
                var clear = ClearToClimb(f, f.P.ClimbFpm * 0.8);
                if (clear || Time - f.LiftoffTime > 600)
                {
                    f.ClimbReleased = true;
                    _releases++;
                    var held = double.IsNaN(f.TopReachedAt) ? 0 : Time - f.TopReachedAt;
                    if (held > 20) { _heldAtTop++; _heldSec += held; }
                    Log($"{f.Callsign}: climb above {f.TopAltFt:N0} ft ({f.TopSource}) given at {f.AltFt:N0} ft, {f.Pos.Length / Geo2.MPerNm:0.0} nm out" +
                        (clear ? held > 20 ? $", clear of the arrivals after {held:0} s level" : ", nothing in the way" : ", 10 minutes after take-off"));
                }
            }
            if (!f.ClimbReleased) cap = Math.Min(cap, f.TopAltFt);
        }
        if (cap < double.MaxValue) targetVs = Math.Min(targetVs, Math.Max(0, (cap - f.AltFt) * 3));
        f.Vs += Math.Clamp(targetVs - f.Vs, -500 * dt, 600 * dt);
        f.AltFt += f.Vs / 60 * dt;
        MoveAir(f, dt);
        var fpa = Math.Atan2(f.Vs, Math.Max(60, f.Gs) * 101.27) * 180 / Math.PI;
        var alpha = Math.Clamp(2.5 + (250 - f.Ias) / 30, 2.5, 6.5);
        f.Pitch += Math.Clamp(fpa + alpha - f.Pitch, -2 * dt, 2 * dt);
        if (f.Pos.Length > 28 * Geo2.MPerNm || f.AltFt > 14000)
        {
            f.Done = true;
            if (f.ChecksMissed.Count > 0) Log($"{f.Callsign}: climbed out on {f.Route}{ChecksReport(f)}");
        }
    }

    /// <summary>
    /// Where a climbing departure must stop so it never climbs into an arrival: both are projected along their routes
    /// for the next 2 minutes (every 15 s); if they'd come within 3.5 nm and 1,000 ft, the departure is told to
    /// maintain 1,000 ft below the lowest the arrival will be (in whole thousands), as departure control would —
    /// whatever its SID, radar vectors included. NaN when nothing's in the way.
    /// </summary>
    private double ProbeArrivals(Flight d, double climbFpm)
    {
        if (d.Path is null) return double.NaN;
        double stop = double.NaN; string? who = null; double whoAlt = 0, whoNm = 0;
        void Stop(double level, string name, double alt, V2 pos)
        {
            if (level < Apt.ElevationFt + 2000) return;
            if (double.IsNaN(stop) || level < stop) { stop = level; who = name; whoAlt = alt; whoNm = (pos - d.Pos).Length / Geo2.MPerNm; }
        }
        foreach (var a in Flights)
        {
            // Arrivals on the approach or final are the runway's business (landing behind a departure is normal),
            // not a reason to stop a climb; arrivals still being vectored (downwind, base) are (CYUL: departures off 06R
            // climbed through 2,200 ft 1.9 nm from arrivals at 3,100 ft being vectored for 06L).
            if (a.Kind != "arrival" || a.OnGround || a.Done || a.Path is null || a.Phase is "flare" or "landing roll" or "final" or "approach" or "go-around") continue;
            if ((a.Pos - d.Pos).Length > 20 * Geo2.MPerNm || a.AltFt < d.AltFt - 1500) continue;
            for (var t = 0.0; t <= 120; t += 15)
            {
                var dp = d.Path.At(Math.Min(d.Path.Length, d.S + d.Gs * 0.5144 * t)).p;
                var ap = a.Path.At(Math.Min(a.Path.Length, a.S + a.Gs * 0.5144 * t)).p;
                var dAlt = d.AltFt + climbFpm * t / 60;
                var aAlt = Math.Max(a.AltFt + Math.Min(0, a.Vs) * t / 60, (a.Rwy?.ElevationFt ?? 0));
                if ((dp - ap).Length < 3.5 * Geo2.MPerNm && Math.Abs(dAlt - aAlt) < 1000 && d.AltFt < aAlt)
                {
                    // The lowest the arrival gets while it's still near (it keeps descending after the first conflict:
                    // MCO, DAL328 descending through 9,100 ft 3 nm from SWA660 stopped at 8,177 ft).
                    var lowest = Math.Min(a.AltFt, aAlt);
                    for (var t2 = t; t2 <= 120; t2 += 15)
                    {
                        var dp2 = d.Path.At(Math.Min(d.Path.Length, d.S + d.Gs * 0.5144 * t2)).p;
                        var ap2 = a.Path.At(Math.Min(a.Path.Length, a.S + a.Gs * 0.5144 * t2)).p;
                        if ((dp2 - ap2).Length < 5 * Geo2.MPerNm)
                            lowest = Math.Min(lowest, Math.Max(a.AltFt + Math.Min(0, a.Vs) * t2 / 60, a.Rwy?.ElevationFt ?? 0));
                    }
                    var level = Math.Floor((lowest - 1000) / 1000) * 1000;
                    // Never raised while the same arrival is still in the way (a level-off isn't lifted early).
                    if (d.StoppedFor == a.Callsign && !double.IsNaN(d.ClimbStop)) level = Math.Min(level, d.ClimbStop);
                    // Never stopped below 2,000 ft above the field (departure control's lowest level-off); below that
                    // it's the arrival that must be kept clear.
                    Stop(level, a.Callsign, a.AltFt, a.Pos);
                    break;
                }
            }
        }
        // The departure ahead (same airport, earlier take-off), along both routes: a follower climbing faster or
        // flying faster stays 1,000 ft below it, and no faster than it while closing in behind (FATHE4 from 36L and
        // 36R, 25 s apart, came within 1.7 nm and 900 ft).
        d.DepSpeedCap = double.NaN;
        foreach (var o in Flights)
        {
            if (o == d || o.Kind != "departure" || o.OnGround || o.Done || o.Path is null || o.LiftoffTime < 0 || o.LiftoffTime > d.LiftoffTime) continue;
            if ((o.Pos - d.Pos).Length > 15 * Geo2.MPerNm || d.AltFt > o.AltFt + 500) continue;
            var oClimb = Math.Max(0, o.Vs);
            double lowest = double.MaxValue; var conflict = false; double minLat = double.MaxValue;
            for (var t = 0.0; t <= 120; t += 10)
            {
                var dp = d.Path.At(Math.Min(d.Path.Length, d.S + d.Gs * 0.5144 * t)).p;
                var op = o.Path.At(Math.Min(o.Path.Length, o.S + o.Gs * 0.5144 * t)).p;
                var lat = (dp - op).Length;
                minLat = Math.Min(minLat, lat);
                var oAlt = Math.Min(o.CruiseFt > 0 ? o.CruiseFt : 40000, o.AltFt + oClimb * t / 60);
                if (lat < 5 * Geo2.MPerNm) lowest = Math.Min(lowest, Math.Min(o.AltFt, oAlt));
                if (lat < 3.5 * Geo2.MPerNm && Math.Abs(d.AltFt + climbFpm * t / 60 - oAlt) < 1000) conflict = true;
            }
            if (conflict && lowest < double.MaxValue) Stop(Math.Floor((lowest - 1000) / 1000) * 1000, o.Callsign, o.AltFt, o.Pos);
            // Closing in behind on the same way out: no faster than the one ahead.
            if (minLat < 5 * Geo2.MPerNm && Math.Abs(Geo2.Diff(o.Heading, d.Heading)) < 60 && (o.Pos - d.Pos).Length < 6 * Geo2.MPerNm)
                d.DepSpeedCap = double.IsNaN(d.DepSpeedCap) ? o.Ias : Math.Min(d.DepSpeedCap, o.Ias);
        }
        // Another airport's traffic (MCO's arrivals for an MLB departure, MLB's for MCO's), and the user: projected straight ahead.
        foreach (var o in OthersAndUser())
        {
            if (o.AltFt < d.AltFt - 1500 || (o.Pos - d.Pos).Length > 20 * Geo2.MPerNm || o.Phase is "flare" or "landing roll") continue;
            // Another airport's traffic is only known where it is now and projected straight on — its turns aren't — so
            // looked at further ahead (3 minutes) and wider (5 nm) than this airport's own (22 Nov, KLGA/KJFK: ten losses,
            // 2.1–3.0 nm and 850–950 ft, most with the climb stopped only once the other was 3 nm away).
            double lowest = double.MaxValue; var conflict = false;
            for (var t = 0.0; t <= 180; t += 10)
            {
                var dp = d.Path.At(Math.Min(d.Path.Length, d.S + d.Gs * 0.5144 * t)).p;
                var op = Ahead(o, t);
                var oAlt = Math.Max(o.AltFt + (o.Phase == "user" ? o.Vs * Math.Min(t, 60) / 60 : (o.Kind == "arrival" ? Math.Min(0, o.Vs) : Math.Max(0, o.Vs)) * t / 60), 0);
                var lat = (dp - op).Length;
                if (lat < 6 * Geo2.MPerNm) lowest = Math.Min(lowest, Math.Min(o.AltFt, oAlt));
                if (lat < 5 * Geo2.MPerNm && Math.Abs(d.AltFt + climbFpm * t / 60 - oAlt) < 1000 && d.AltFt < oAlt) conflict = true;
            }
            if (conflict && lowest < double.MaxValue) Stop(Math.Floor((lowest - 1000) / 1000) * 1000, $"{o.Callsign} ({o.Airport})", o.AltFt, o.Pos);
        }
        if (who is not null && d.StoppedFor != who)
        {
            d.StoppedFor = who;
            _climbStops++;
            Log($"{d.Callsign}: climb stopped at {Math.Max(stop, d.AltFt):N0} ft for {who} ({whoAlt:N0} ft, {whoNm:0.0} nm)");
        }
        if (who is null) d.StoppedFor = null;
        return stop;
    }

    /// <summary>Another airport's airborne traffic, in this airport's frame (set by the server every second): the main
    /// airport's for a neighbour, a neighbour's for the main airport. Projected on a straight line.</summary>
    public sealed record Foreign(string Callsign, string Airport, string Kind, V2 Pos, double AltFt, double Vs, double GsKt, double Track, string Phase, bool Main);
    public List<Foreign> Others { get; set; } = new();
    /// <summary>Neighbouring airports' landing runways in use (threshold in this frame, heading), for the approach choice.</summary>
    public List<(V2 thr, double hdg, string who)> NeighbourFinals { get; set; } = new();

    /// <summary>
    /// How much of an approach (its transitions and final, below 6,000 ft) runs through a neighbour's final in use —
    /// within 2 nm of its extended centreline from 1 to 15 nm out: metres of the approach in there. 0: clear of them.
    /// </summary>
    private double NeighbourConflict(Approach a, RunwayEnd rwy, out string who)
    {
        who = "";
        if (NeighbourFinals.Count == 0) return 0;
        var legs = a.Transitions.SelectMany(t => t.Legs).Concat(a.FinalLegs).ToList();
        var pts = Apt.LegsToPoints(a.FinalLegs, rwy.Threshold - rwy.Dir * (15 * Geo2.MPerNm), 4000, out _).Select(q => q.P).ToList();
        foreach (var t in a.Transitions)
            pts.AddRange(Apt.LegsToPoints(t.Legs, rwy.Threshold - rwy.Dir * (20 * Geo2.MPerNm), 6000, out _).Select(q => q.P));
        double total = 0; var worst = 0.0;
        foreach (var (thr, hdg, name) in NeighbourFinals)
        {
            var dir = V2.FromBearing(hdg);
            double m = 0;
            for (var i = 1; i < pts.Count; i++)
            {
                var seg = pts[i] - pts[i - 1]; var len = seg.Length;
                for (var d = 0.0; d < len; d += 200)
                {
                    var p = pts[i - 1] + seg * (d / Math.Max(1, len));
                    var along = -V2.Dot(p - thr, dir);                      // miles out on its final
                    var off = Math.Abs(V2.Dot(p - thr, dir.Perp));
                    if (along > 1 * Geo2.MPerNm && along < 15 * Geo2.MPerNm && off < 2 * Geo2.MPerNm) m += Math.Min(200, len - d);
                }
            }
            total += m;
            if (m > worst) { worst = m; who = name; }
        }
        return total;
    }
    private static V2 Ahead(Foreign o, double t) => o.Pos + V2.FromBearing(o.Track) * (o.GsKt * 0.5144 * t);

    /// <summary>
    /// The user, as traffic LAIA's aircraft keep clear of (user, 14 Nov: after a TA/RA at cruise, "when is that fix
    /// scheduled?" for the airports' own traffic): departures stop their climb 1,000 ft below the user, arrivals stop
    /// their descent 1,000 ft above, and an arrival level with the user starts down early — the same rules as for
    /// another airport's traffic. Not on the ground, and not while the user is on this airport's final (then it's the
    /// runway's business, as for LAIA's own arrivals).
    /// </summary>
    private Foreign? UserTraffic()
    {
        if (UserOnGround || UserPos is not { } u) return null;
        if (UserArr is { } ua && ua.Rwy is not null && (ua.ThrS - ua.S) / Geo2.MPerNm < 8) return null;
        var cs = UserCallsign.Length > 0 ? UserCallsign : "your aircraft";
        return new Foreign(cs, "you", UserVs < -300 ? "arrival" : "departure", u, UserAltFt, UserVs, UserGs, UserTrack, "user", true);
    }

    /// <summary>Other airports' traffic and the user.</summary>
    private IEnumerable<Foreign> OthersAndUser() => UserTraffic() is { } me ? Others.Append(me) : Others;

    /// <summary>
    /// An arrival level with the user, closing (cruise, or the early descent): it starts down to 1,000 ft below the
    /// user's level before they meet, as ATC would give one of them a lower level. NaN when not needed.
    /// </summary>
    private double ProbeUserLevel(Flight f)
    {
        if (UserTraffic() is not { } me || f.Path is null) return double.NaN;
        // Only up high (cruise, early descent): lower down the departures' climb stops and the descent probes keep
        // arrivals and the user apart (16 Nov: UAL12 on a 5 nm final was told to descend "early to 0 ft" below the user
        // climbing out at 1,211 ft).
        if (me.AltFt < 10000 || f.AltFt < 10000) return double.NaN;
        if ((me.Pos - f.Pos).Length > 60 * Geo2.MPerNm || Math.Abs(f.AltFt - me.AltFt) >= 1000) return double.NaN;
        if (f.AltFt < me.AltFt - 200) return double.NaN;   // already below and descending away
        for (var t = 0.0; t <= 300; t += 15)
        {
            var fp = f.Path.At(Math.Min(f.Path.Length, f.S + f.Gs * 0.5144 * t)).p;
            if ((fp - Ahead(me, t)).Length < 6 * Geo2.MPerNm)
            {
                var level = Math.Floor((me.AltFt - 1000) / 1000) * 1000;
                if (f.UserCeiling != level) Log($"{f.Callsign}: descending early to {level:N0} ft below {me.Callsign} (you, {me.AltFt:N0} ft, {(me.Pos - f.Pos).Length / Geo2.MPerNm:0} nm)");
                return level;
            }
        }
        return double.NaN;
    }

    private int _climbStops, _losCount, _releases, _heldAtTop, _descentStops, _losBetween;
    private double _heldSec;

    /// <summary>Whether a callsign is flying at another airport LAIA runs (set by the server).</summary>
    public Func<string, bool>? CallsignInUse { get; set; }

    /// <summary>
    /// A callsign already in use (user: never two aircraft called JBU1728 at once — AIG gives one flight number to
    /// several legs, and two airframes could fly it at the same time): the next leg waits until the first is done.
    /// </summary>
    private bool CallsignBusy(string cs) => cs.Length > 0 && (Flights.Any(f => !f.Done && f.Kind != "parked" && f.Callsign == cs) || (CallsignInUse?.Invoke(cs) ?? false));

    /// <summary>For a neighbouring field: the main airport's STARs, whose restrictions apply to a shared STAR's fixes.</summary>
    public List<Procedure>? SharedArrivals { get; set; }

    /// <summary>The "expect" restrictions from the FAA STAR charts, by STAR name: fix, at or above, at or below, speed.</summary>
    /// <summary>Whether a chart's "expect" applies to an arrival landing this runway (ident, true heading): when is ""
    /// (always), "rwy:18" (18, 18L, 18C, 18R) or "dir:S" (landing within 60° of south; N, E, W alike).</summary>
    public static bool ExpectApplies(string when, string rwyIdent, double rwyHeading)
    {
        if (string.IsNullOrEmpty(when)) return true;
        if (when.StartsWith("rwy:"))
        {
            var r = when[4..];
            return rwyIdent.Equals(r, StringComparison.OrdinalIgnoreCase) || (r.All(char.IsDigit) && rwyIdent.StartsWith(r) && (rwyIdent.Length == r.Length || !char.IsDigit(rwyIdent[r.Length])));
        }
        if (when.StartsWith("dir:"))
        {
            var h = when[4..] switch { "N" => 0.0, "E" => 90.0, "S" => 180.0, _ => 270.0 };
            return Math.Abs(Geo2.Diff(rwyHeading, h)) < 60;
        }
        return true;
    }

    /// <summary>The STAR charts' "expect" restrictions, by STAR: when is "" (always), "dir:S" (landing south) or "rwy:18".</summary>
    public Dictionary<string, List<(string fix, double min, double max, double kt, string when, string how)>>? StarExpects { get; set; }

    /// <summary>The SID top altitudes from the FAA departure charts, by SID name (set by the server; null: none known).</summary>
    public Dictionary<string, int>? SidTops { get; set; }
    private double? _corridor;

    /// <summary>
    /// A departure's top altitude: its SID chart's ("maintain 7,000"), else 1,000 ft below the lowest "at or above"
    /// on the airport's STARs within 35 nm (the arrivals' floor over the departure corridor: MCO's DORII "at or above
    /// 8,000" → 7,000). NaN when neither is known.
    /// </summary>
    private (double ft, string source) TopAltitude(string route)
    {
        var sid = route.Split('.')[0];
        if (SidTops is { } t && t.TryGetValue(sid, out var ft)) return (ft, $"{sid} top altitude, chart");
        _corridor ??= CorridorTop();
        return double.IsNaN(_corridor.Value) ? (double.NaN, "") : (_corridor.Value, "under the arrivals' lowest \"at or above\"");
    }

    private double CorridorTop()
    {
        var lowest = double.MaxValue;
        foreach (var star in Apt.Data.Arrivals)
            foreach (var l in star.Legs.Concat(star.RunwayTransitions.SelectMany(x => x.Legs)).Concat(star.EnrouteTransitions.SelectMany(x => x.Legs)))
            {
                var floor = l.AltDesc == 2 ? l.Alt1Ft : l.AltDesc == 4 ? Math.Min(l.Alt1Ft, l.Alt2Ft) : 0;
                if (floor < Apt.ElevationFt + 3000 || floor > 12000 || (l.Lat == 0 && l.Lon == 0)) continue;
                if (Apt.Map.ToXY(l.Lat, l.Lon).Length > 35 * Geo2.MPerNm) continue;
                lowest = Math.Min(lowest, floor);
            }
        return lowest == double.MaxValue ? double.NaN : Math.Floor((lowest - 1000) / 1000) * 1000;
    }

    /// <summary>
    /// Can a departure levelled at its top altitude be given its climb now? It must not come within 5 nm and 1,000 ft
    /// of an arrival, nor climb through one within 5 nm, in the next 4 minutes (both projected along their routes).
    /// </summary>
    private bool ClearToClimb(Flight d, double climbFpm)
    {
        if (d.Path is null) return true;
        // Another airport's traffic too (straight ahead), and the departure ahead of it on its way out.
        foreach (var o in Others)
        {
            if ((o.Pos - d.Pos).Length > 30 * Geo2.MPerNm || o.AltFt < d.AltFt - 500 || o.Phase is "flare" or "landing roll") continue;
            var below0 = d.AltFt < o.AltFt;
            for (var t = 0.0; t <= 240; t += 10)
            {
                var dp = d.Path.At(Math.Min(d.Path.Length, d.S + d.Gs * 0.5144 * t)).p;
                var oAlt = o.AltFt + o.Vs * t / 60;
                var dAlt = d.AltFt + climbFpm * t / 60;
                if ((dp - Ahead(o, t)).Length < 5 * Geo2.MPerNm && (Math.Abs(dAlt - oAlt) < 1000 || (below0 && dAlt > oAlt))) return false;
                below0 = dAlt < oAlt;
            }
        }
        foreach (var o in Flights)
        {
            if (o == d || o.Kind != "departure" || o.OnGround || o.Done || o.Path is null || o.LiftoffTime > d.LiftoffTime || d.AltFt > o.AltFt + 500) continue;
            for (var t = 0.0; t <= 240; t += 10)
            {
                var dp = d.Path.At(Math.Min(d.Path.Length, d.S + d.Gs * 0.5144 * t)).p;
                var op = o.Path.At(Math.Min(o.Path.Length, o.S + o.Gs * 0.5144 * t)).p;
                if ((dp - op).Length < 5 * Geo2.MPerNm && Math.Abs(d.AltFt + climbFpm * t / 60 - (o.AltFt + Math.Max(0, o.Vs) * t / 60)) < 1000) return false;
            }
        }
        foreach (var a in Flights)
        {
            if (a.Kind != "arrival" || a.OnGround || a.Done || a.Path is null || a.Phase is "flare" or "landing roll" or "final" or "approach" or "go-around") continue;
            if ((a.Pos - d.Pos).Length > 30 * Geo2.MPerNm || a.AltFt < d.AltFt - 500) continue;
            var below = d.AltFt < a.AltFt;
            for (var t = 0.0; t <= 240; t += 10)
            {
                var dp = d.Path.At(Math.Min(d.Path.Length, d.S + d.Gs * 0.5144 * t)).p;
                var ap = a.Path.At(Math.Min(a.Path.Length, a.S + a.Gs * 0.5144 * t)).p;
                var dAlt = d.AltFt + climbFpm * t / 60;
                var aAlt = Math.Max(a.AltFt + Math.Min(0, a.Vs) * t / 60, a.Rwy?.ElevationFt ?? 0);
                var near = (dp - ap).Length < 5 * Geo2.MPerNm;
                if (near && (Math.Abs(dAlt - aAlt) < 1000 || (below && dAlt > aAlt))) return false;
                below = dAlt < aAlt;
            }
        }
        return true;
    }

    /// <summary>Departures held at their top altitude and given the climb (for the status page).</summary>
    public string TopAltitudeSummary => _releases == 0 ? "" :
        $"{_releases} departures given the climb above their top altitude, {_heldAtTop} after waiting level for arrivals (average {(_heldAtTop == 0 ? 0 : _heldSec / _heldAtTop):0} s)";
    private readonly Dictionary<(string, string), double> _losSeen = new();

    /// <summary>
    /// Watches separation in the air between departures and everything else (3 nm / 1,000 ft, the terminal radar
    /// minimum), logging each loss once — to show whether the rules above work. Arrivals following each other to the
    /// same or parallel runways are the arrival manager's (and parallel approaches are legal), so not counted here.
    /// </summary>
    private void WatchSeparation()
    {
        var air = Flights.Where(x => !x.OnGround && !x.Done && x.Kind is "arrival" or "departure" && x.AltFt - Apt.ElevationFt > 800).ToList();
        foreach (var d in air.Where(x => x.Kind == "departure"))
            foreach (var o in air)
            {
                if (o == d || (o.Kind == "departure" && string.CompareOrdinal(o.Id, d.Id) < 0)) continue;
                if ((o.Pos - d.Pos).Length >= 3 * Geo2.MPerNm || Math.Abs(o.AltFt - d.AltFt) >= 900) continue;
                // A departure off one parallel while an arrival is on final to another, near the field, is legal.
                if (o.Kind == "arrival" && o.Phase is "final" or "approach" && d.Pos.Length < 6 * Geo2.MPerNm) continue;
                // Two departures off different parallel runways at once, near the field: legal (diverging SIDs).
                if (o.Kind == "departure" && o.Rwy is not null && d.Rwy is not null && o.Rwy.Runway != d.Rwy.Runway && d.Pos.Length < 6 * Geo2.MPerNm && o.Pos.Length < 6 * Geo2.MPerNm) continue;
                var key = (d.Callsign, o.Callsign);
                if (_losSeen.TryGetValue(key, out var at) && Time - at < 120) continue;
                _losSeen[key] = Time; _losCount++;
                Log($"SEPARATION: {d.Callsign} ({d.AltFt:N0} ft) and {o.Callsign} ({o.AltFt:N0} ft) {(o.Pos - d.Pos).Length / Geo2.MPerNm:0.0} nm apart");
            }
        // Between airports (logged by the one whose name comes first, so once): any of this airport's airborne traffic
        // against another's, outside the last few miles of either's final.
        foreach (var f in air)
            foreach (var o in Others)
            {
                if (string.CompareOrdinal(Apt.Icao, o.Airport) > 0 || o.AltFt < 800 || o.Phase is "final" or "flare" or "landing roll") continue;
                if (f.Phase is "final" or "flare") continue;
                if ((o.Pos - f.Pos).Length >= 3 * Geo2.MPerNm || Math.Abs(o.AltFt - f.AltFt) >= 900) continue;
                var key = (f.Callsign, o.Callsign);
                if (_losSeen.TryGetValue(key, out var at) && Time - at < 120) continue;
                _losSeen[key] = Time; _losBetween++;
                Log($"SEPARATION (between airports): {f.Callsign} {Apt.Icao} ({f.AltFt:N0} ft) and {o.Callsign} {o.Airport} ({o.AltFt:N0} ft) {(o.Pos - f.Pos).Length / Geo2.MPerNm:0.0} nm apart");
            }
    }

    /// <summary>For the control page.</summary>
    public string SeparationSummary => $"{_climbStops} departure climb{(_climbStops == 1 ? "" : "s")} stopped below traffic; {_descentStops} neighbouring-field descent{(_descentStops == 1 ? "" : "s")} stopped above the main airport's; {_losCount} loss{(_losCount == 1 ? "" : "es")} of separation involving departures; {_losBetween} between airports";

    private double _lastSepWatch = -99;

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
            // Predict the landing time all the time (every 5 s), and keep what was predicted 30, 10 and 5 minutes out.
            if (Time - f.EtaAt >= 5)
            {
                f.EtaAt = Time;
                f.EtaSec = EtaToThreshold(f, dth);
                f.EtaNominal = Time + (f.Slowed ? EtaToThreshold(f, dth, nominal: true) : f.EtaSec);
                PredictFixes(f, dth);
                var land = Time + f.EtaSec;
                if (!f.WentAround)
                {
                    if (double.IsNaN(f.Pred30) && f.EtaSec is <= 1800 and > 1500) f.Pred30 = land;
                    if (double.IsNaN(f.Pred10) && f.EtaSec is <= 600 and > 480) f.Pred10 = land;
                    if (double.IsNaN(f.Pred5) && f.EtaSec is <= 300 and > 240) f.Pred5 = land;
                }
            }
            if (dth < 5 && f.Phase == "approach") f.SetPhase("final", Time);
            var target = ArrivalSpeed(f, dth);
            // Speed restrictions on the procedure just ahead.
            // Spacing behind the arrival ahead on the same runway.
            Flight? leader = null;
            var leaderD = double.MinValue;
            // The one ahead in the landing order (the arrival manager's sequence), not merely the one nearer the
            // runway along its path: once a downwind is extended, the aircraft on it is further from the runway along
            // its path than the one behind it, and it must stay ahead (the trombone would otherwise swap them).
            var pool = UserArr is { } uaf0 ? Flights.Append(uaf0) : Flights;
            if (f.Seq > 1)
            {
                leader = pool.FirstOrDefault(o => o != f && o.Kind == "arrival" && o.Rwy?.Runway == rwy.Runway && o.Seq == f.Seq - 1 && !o.OnGround && !o.Done && o.Phase != "go-around");
                if (leader is not null && !double.IsNaN(leader.EtaSec) && !double.IsNaN(f.EtaSec) && leader.EtaSec > f.EtaSec + 240) leader = null;
                if (leader is not null) leaderD = (leader.ThrS - leader.S) / Geo2.MPerNm;
            }
            if (leader is null)
            foreach (var o in pool)
            {
                if (o == f || o.Kind != "arrival" || o.Rwy?.Runway != rwy.Runway || o.Phase == "go-around") continue;
                // Never one that's after it in the landing order (user, 19 Nov, UAL538 and DAL1390 each spacing on the
                // other: DAL1390, first of the two, took UAL538 as its leader because it was nearer along its path).
                if (f.Seq > 0 && o.Seq > f.Seq && !o.OnGround) continue;
                var od = o.OnGround ? -1 : (o.ThrS - o.S) / Geo2.MPerNm;
                if (od < dth && od > leaderD) { leaderD = od; leader = o; }
            }
            // Arrivals to a crossing runway or a close parallel (sequenced together; user, 27 Nov: "crossing runways needs
            // proper approach sequencing as well"): close in, the base turn and speed also keep the stagger with the one
            // this arrival lands after there — from both aircraft's live predicted times, as for the one ahead.
            var (depAdd, depWho) = dth > 4 && dth < 45 && Time - f.LastTrombone > 8 ? DependentAdd(f, dth) : (0.0, (string?)null);
            if (depAdd > 15 && dth > 5)
            {
                var jet = f.P.Family != "turboprop";
                var floor = dth > 20 ? (jet ? 210 : 200) : (jet ? 170 : 150);
                target = Math.Min(target, Math.Round(Math.Max(floor, f.P.Vapp) / 5) * 5);
            }
            if (depAdd > 10 && (leader is null || leader.OnGround || dth >= 45) && Time - f.LastVector > 30)
            {
                f.LastTrombone = Time;
                f.TromboneAdd = depAdd;
                f.TromboneLeft = Trombone(f, depAdd, $"landing after {depWho}, {depAdd:0} s to lose");
                if (double.IsNaN(f.TromboneLeft) && f.Fan is not null) f.TromboneLeft = FanPick(f, depAdd, $"landing after {depWho}, {depAdd:0} s to lose");
                // Nothing to stretch (straight in): vectors while there's room, else an early break-off rather than a
                // go-around at half a mile.
                if (double.IsNaN(f.TromboneLeft) || f.TromboneLeft > 40)
                {
                    var onFin = Math.Abs(rwy.Offset(f.Pos)) < 0.6 * Geo2.MPerNm && rwy.Along(f.Pos) < 0;
                    if (dth > 13 && depAdd > 30 && Time - f.LastVector > 240 && Vector(f, depAdd + 15, $"landing after {depWho}, {depAdd:0} s short")) return;
                    if (dth is > 5 and <= 12 && depAdd > 20 && onFin && Time - f.LastOrbit > 240) { BreakOff(f, $"landing after {depWho}, {depAdd:0} s short"); return; }
                }
            }
            f.LeaderCs = leader?.Callsign;
            if (leader is not null && !leader.OnGround && dth < 45)
            {
                // Measured and predicted all the time: the gap now, and the predicted gap when each crosses the
                // threshold (compression: the leader slows to its approach speed first), against the gap needed there.
                // The gap in miles: along the final when both are on it; otherwise how far apart they really are (on
                // different routes the difference in miles to go says nothing about radar separation).
                bool OnFinal(Flight x, double d) => d < 15 && x.Rwy is { } xr && Math.Abs(xr.Offset(x.Pos)) < 0.6 * Geo2.MPerNm && xr.Along(x.Pos) < 0;
                var gap = OnFinal(f, dth) && OnFinal(leader, leaderD) ? dth - leaderD
                    : Math.Abs(f.AltFt - leader.AltFt) >= 1000 ? double.PositiveInfinity : (f.Pos - leader.Pos).Length / Geo2.MPerNm;
                var gapText = double.IsInfinity(gap) ? $"1,000 ft or more from {leader.Callsign}" : $"{gap:0.0} nm behind {leader.Callsign}";
                var gapSec = EtaToThreshold(f, dth) - (leader == UserArr ? leader.EtaSec : EtaToThreshold(leader, leaderD));
                var needSec = ThresholdGapNeeded(leader, f);
                var needNm = OnFinal(f, dth) && OnFinal(leader, leaderD) ? Cwt.MilesBehind(leader.Wake, f.Wake) : 3;
                // Speed first (7110.65 5-7-1), never below what a controller may assign (5-7-3: jets 210 kt below
                // 10,000 ft, 170 kt within 20 miles; turboprops 200 / 150), never inside 5 miles (5-7-2), in 5-kt steps.
                if ((gapSec < needSec || gap < needNm + 0.5) && dth > 5)
                {
                    var jet = f.P.Family != "turboprop";
                    var floor = dth > 20 ? (jet ? 210 : 200) : (jet ? 170 : 150);
                    var want = Math.Max(Math.Max(floor, f.P.Vapp), Math.Min(target, leader.Ias - 10));
                    target = Math.Min(target, Math.Round(want / 5) * 5);
                }
                // Speed can't make it: hold (far out), a 360 (12–15 nm) or an early break-off (5–12 nm), as a controller would.
                // Only for real spacing (wake, radar, runway occupancy): gaps for departures are made with speed, never
                // by breaking someone off.
                var short_ = ThresholdGapNeeded(leader, f, forDepartures: false) - gapSec;
                // The stagger with an arrival to a crossing runway or close parallel counts as spacing too.
                if (depAdd > 0) short_ = Math.Max(short_, depAdd);
                // What speed control can still absorb before 20 nm (250 → 210 kt) and inside it (down to the 170 kt
                // floor): a hold only when the shortfall is more than that.
                if (!(gap > 0 && tooCloseNow(gap, needNm))) short_ -= Math.Max(0, dth - 20) * (3600 / 210.0 - 3600 / 250.0) + (dth > 12 ? 10 : 0);
                var tooClose = gap > 0 && tooCloseNow(gap, needNm);
                // The "one ahead" predicted to land well after this one is a mix-up in the order for the sequence to sort
                // out, not a reason to stretch, vector or break off (22 Nov, KMSP: one pass put the user on final 279 s
                // behind SWA4367 as its leader; its downwind went from a 10.6 to a 22.2 nm base turn, then back, and it
                // looped). Radar spacing still counts.
                // (Two minutes or more: a little less is a faster follower catching up, which is real spacing work.)
                var inverted = gapSec < -120 && !tooClose;
                if (inverted) short_ = 0;
                f.GapNm = double.IsInfinity(gap) ? double.NaN : gap; f.GapSec = gapSec;
                // The trombone first: with a downwind and base ahead, the base turn moves so it lands the gap needed
                // (plus 10 s) behind the one ahead — further out, or earlier when the gap is bigger than it needs to be.
                // Looked at every 8 s, so the downwind keeps following how the traffic ahead (and the user) actually
                // flies. While it can absorb the shortfall, no vectors off, holds, 360s or break-offs.
                if (dth > 4 && Time - f.LastTrombone > 8 && Time - f.LastVector > 30)
                {
                    f.LastTrombone = Time;
                    var wantGap = ThresholdGapNeeded(leader, f, forDepartures: false) + 10;
                    var add = Math.Max(wantGap - gapSec, depAdd);
                    // Never catching up on a leader that isn't on its plan — broken off, rejoining, on vectors or just
                    // gone around: its predicted time is far too late (21 Nov, KATL: SWA727 took a shorter real track to
                    // close "405 s behind DAL3127" while DAL3127 was on its second break-off loop; DAL3127 landed first
                    // and SWA727 was broken off 215 s short behind the next one).
                    // And on the landing schedule (its slot from the arrival manager), not only the leader's live time:
                    // a shortcut never takes it more than a minute ahead of its slot, where the one ahead of its leader
                    // may be.
                    var staAdd = f.Frozen && f.FrozenSlot > 0 ? f.FrozenSlot - (Time + EtaToThreshold(f, dth)) : double.NaN;
                    if (add < 0 && (leader.Mgr is "vectored around" or "vectored" || leader.Rejoin || leader.WentAround || leader.DetourSec > 0)) add = 0;
                    else if (add < 0 && !double.IsNaN(staAdd)) add = Math.Max(add, Math.Min(0, staAdd - 60));
                    if (inverted) add = 0;
                    f.TromboneAdd = add;
                    f.TromboneLeft = Trombone(f, add > 10 || add < -25 ? add : 0, $"{gapSec:0} s behind {leader.Callsign} at the runway, {wantGap:0} wanted");
                    if (double.IsNaN(f.TromboneLeft) && f.Fan is not null)
                        f.TromboneLeft = FanPick(f, add > 10 || add < -25 ? add : 0, $"{gapSec:0} s behind {leader.Callsign} at the runway, {wantGap:0} wanted");
                }
                if (dth <= 4) f.TromboneLeft = double.NaN;
                var trombone = !double.IsNaN(f.TromboneLeft) && f.TromboneLeft < 40 && !tooClose;
                // Vectors before a hold or a 360 (path stretching): a detour as long as the time to lose, when there's
                // route left to do it; a hold only for long delays (over 4 minutes) or when no detour fits.
                // No holds and no 360s (user, 18 Nov, KORD: "I am taking away the ability to put holds on aircraft"): the
                // arrival manager plans the delay far out with speed, a runway with less delay, delay vectors on the
                // way in and the trombone. Close in, vectors only with real route left (beyond 20 nm), and a break-off
                // only when radar spacing would actually be lost or the gap is far too short to make up.
                // Another landing runway it's already nearer to, with room there (user, 19 Nov, UAL415 broken off at
                // KORD: "could have easily been sequenced for 28C instead") — before vectoring off or breaking off.
                if (!trombone && (short_ > (dth > 20 ? 45 : 20) || tooClose) && dth > 6 && !OnFinal(f, dth)
                    && OtherRunway(f, Math.Max(short_, 0), $"{gapText}, {short_:0} s short at the runway")) return;
                if (!trombone && (short_ > (dth > 20 ? 45 : 20) || tooClose) && dth > 13 && short_ <= 300 && Time - f.LastVector > 240
                    && Vector(f, Math.Max(short_, 45) + 15, $"{gapText}, {short_:0} s short at the runway")) return;
                // Short only because the one ahead was just turned off on a detour while this one is on the final, well
                // behind it: it'll be put ahead in the next sequence pass, so no break-off for that (AAL2740, 19 Nov).
                var leaderDetour = leader is { } ld && ((Time - ld.LastVector < 120 && OnFinal(f, dth)) || ld.Rejoin) && !tooClose;
                // (Or the one ahead just went around or was broken off — waiting to be put back in behind those nearer in:
                // "429 s short" behind it says nothing, and the next pass makes this one the leader's leader.)
                if (!trombone && !leaderDetour && (tooClose || (short_ > 20 && OnFinal(f, dth))) && dth > 5 && dth <= 12 && Time - f.LastOrbit > 240) { BreakOff(f, $"{gapText}, {short_:0} s short at the runway"); return; }
            }
            if (f.Phase == "flare") target = f.P.Vapp - 5;
            // Energy: it slows only as fast as its configuration and descent allow (descend OR slow, not both).
            if (target < f.Ias) f.Ias -= Math.Min(f.Ias - target, DecelRate(f, target, agl) * dt);
            else f.Ias += Math.Min(target - f.Ias, 1.5 * dt);
            if (dth < 6.5 || agl < 2200) f.Gear = true;

            double vsTarget;
            double glideVs = -(f.Gs * 101.27 * Math.Tan(3 * Math.PI / 180));
            if (f.Phase == "flare" || (agl < 30 && dth < 0.4))
            {
                if (f.Phase != "flare") f.SetPhase("flare", Time);
                vsTarget = Math.Max(-650, -140 - agl * 14);
                f.Vs += Math.Clamp(vsTarget - f.Vs, -900 * dt, 900 * dt);
            }
            else
            {
                var plan = f.CruiseFt > 0 ? Math.Min(Profile(rwy, dth), f.CruiseFt) : Profile(rwy, dth);
                var want = Restricted(f, plan, out var floorAhead);
                if (Time - f.DescentProbeAt >= 2)
                {
                    f.DescentProbeAt = Time;
                    f.DescentFloor = ProbeDescent(f);
                    f.UserCeiling = ProbeUserLevel(f);
                }
                if (!double.IsNaN(f.UserCeiling)) want = Math.Min(want, f.UserCeiling);
                // Never below a published floor still ahead (user: "STAR altitude restrictions trump"; 20 Nov, KATL: DAL1285
                // sent down early to 11,000 ft under another KATL arrival en route, then crossed CHPPR "at 13,000" low).
                if (!double.IsNaN(f.SepCeiling)) want = Math.Min(want, double.IsNaN(floorAhead) ? f.SepCeiling : Math.Max(f.SepCeiling, floorAhead));
                f.DbgWant = want; f.DbgFloor = floorAhead; f.DbgPlan = plan;
                // Follow the slope of the wanted altitude itself (the descent path bent by the restrictions), not
                // always the 3° path: level where a restriction holds it ("at 7,000" at BANYA), 300 ft/nm towards an
                // "at or below". Following the 3° slope while held at a level let it sink ~600 ft below "at" fixes.
                {
                    // The slope over the last half mile (not the next): looking ahead made it start down before an
                    // "at" fix and cross it low (offline FF27 "at 2,000" crossed at 1,780 ft).
                    var plan2 = f.CruiseFt > 0 ? Math.Min(Profile(rwy, dth + 0.5), f.CruiseFt) : Profile(rwy, dth + 0.5);
                    var want2 = Restricted(f, plan2, f.S - 0.5 * Geo2.MPerNm);
                    // Always: the plan's own level pieces (slowing 250→210, 210→180, 180→170) are level too.
                    glideVs = Math.Min(0, (want - want2) / 0.5 * Math.Max(60, f.Gs) / 60);
                    // Already below what it wants: follow the slope ahead, not the one behind (19 Nov, the cause of the
                    // Midway misses: "aiming for 6,004" at 5,584 ft and still descending 812 ft/min — the steep slope
                    // over the last half mile carried it down through a level-off at STKNY, HALIE, JILLZ, GERMN).
                    if (f.AltFt < want - 30)
                    {
                        var plan3 = f.CruiseFt > 0 ? Math.Min(Profile(rwy, Math.Max(0, dth - 0.5)), f.CruiseFt) : Profile(rwy, Math.Max(0, dth - 0.5));
                        var want3 = Restricted(f, plan3, f.S + 0.5 * Geo2.MPerNm);
                        glideVs = Math.Max(glideVs, Math.Min(0, (want3 - want) / 0.5 * Math.Max(60, f.Gs) / 60));
                    }
                }
                var err = f.AltFt - want;
                // Below the descent path (after a go-around, or vectored in low): level off until the path comes down
                // to meet the aircraft, as pilots do, rather than carry on descending.
                vsTarget = Math.Clamp(glideVs - Math.Clamp(err * 1.5, -6000, 1500), -3000, 0);
                // Told to slow (or still too fast) outside the final approach: shallow the descent so it can slow
                // ("descend OR slow down"); it catches up with the path afterwards.
                // Not when an "at or below" restriction ahead is what's bringing it down: that has to be met too.
                if (f.Ias - target > 5 && dth > 6.5 && !f.Gear && want >= plan - 50 && err < 2000) vsTarget = Math.Max(vsTarget, -400);   // well above the path: get down first
                // Levelling at a published floor ("at 6,000", "at or above 24,000"): ease the descent as it gets close,
                // as an autopilot captures an altitude — coming down at 2,500–3,000 ft/min it used to sink 400–650 ft
                // through it (Midway STKNY/HALIE at 5,550, JNSNN at 23,470, O'Hare KRENA at 10,340; 19 Nov).
                if (!double.IsNaN(floorAhead) && f.AltFt > floorAhead && f.AltFt - floorAhead < 1500)
                    vsTarget = Math.Max(vsTarget, -Math.Max(150, (f.AltFt - floorAhead) * 4));
                // And levelling at what it wants when that's level (an "at" fix with no floor left in view): the same
                // capture (21 Nov, Midway STKNY "at 6,000" at 5,827 ft and JILLZ "at 10,000" at 9,844 ft, both "aiming
                // for 6,002 / 10,001" — sinking 150–170 ft through the level, just outside the allowance).
                if (glideVs > -100 && f.AltFt > want && f.AltFt - want < 1500)
                    vsTarget = Math.Max(vsTarget, -Math.Max(150, (f.AltFt - want) * 4));
                // Kept above the main airport's traffic (a satellite's arrival crossing the big airport's flows): level
                // at the floor until clear.
                if (!double.IsNaN(f.DescentFloor) && f.AltFt > f.DescentFloor - 100) vsTarget = Math.Max(vsTarget, Math.Min(0, (f.DescentFloor - f.AltFt) * 2));
                // Not below 10,000 ft until slowed to 250 kt (14 CFR 91.117(a)): level off at 10,000 if need be.
                if (f.Ias > 252 && f.AltFt > 9900 && f.AltFt < 12000) vsTarget = Math.Max(vsTarget, Math.Clamp((10000 - f.AltFt) * 1.5, -1500, 0));
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
                // Anticipating separation (7110.65 3-10-3): a departure that will be airborne, or a landing aircraft
                // that will have turned off, before this one crosses the threshold doesn't need a go-around.
                bool Clear(Flight o) => (o.Phase == "takeoff roll" && (o.P.Vr + 7 - o.Ias) / o.P.RollAccel < toThreshold)
                    || (o.Phase == "landing roll" && o.Rwy?.Runway == rwy.Runway && TimeToClear(o) < toThreshold);
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
                    // An arrival to the crossing runway — LAIA's or the user's — that crosses its threshold first and won't
                    // have passed the intersection (or turned off before it) by the time this one crosses its threshold
                    // (3-10-4). The approach sequencing staggers the two streams; this is the safety net.
                    if (CrossingArrivalAhead(f, rwy, end, x.Value.alongB, toThreshold) is { } who) { GoAround(f, $"{who} landing the crossing runway {end.Ident}"); return; }
                }
            }
            if (f.Phase == "flare" && f.AltFt <= rwy.ElevationFt)
            {
                f.AltFt = rwy.ElevationFt;
                f.MaxKt = 30;
                MoveAir(f, dt);
                StartRollout(f);
                Log($"{f.Callsign}: landed runway {rwy.Ident}{(f.Spot is not null ? ", taxiing to " + f.Spot.Label : "")}{PredictionReport(f)}{ChecksReport(f)}{LandingGapReport(f)}{SlotReport(f)}{f.ExitNote}");
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
        var air = f.OnGround || f.Phase == "takeoff roll" ? Wind : WindAt(f.AltFt);
        var (_, dir0, _) = f.Path!.At(f.S);
        var (_, gs0) = air.Solve(dir0, tas);
        f.S += gs0 * Kt * dt;
        var (p, dir, k) = f.Path.At(f.S);
        var (hdg, gs) = air.Solve(dir, tas);
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
