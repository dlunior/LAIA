// Offline check of LAIA's traffic simulation on a made-up airport: runs an hour of busy traffic much
// faster than real time and checks that aircraft push back, taxi, take off, land, vacate and park, that the
// runway is never used by two aircraft at once, that aircraft keep their distance on the ground, and that
// movement is smooth (no jumps). Run in CI on every build.
using Laia.Common.Airports;
using Laia.Server.TrafficCore;

var failures = new List<string>();
void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures.Add(what); }

Console.WriteLine("== Path geometry");
{
    var pts = new List<V2> { new(0, 0), new(0, 500), new(500, 500), new(500, 0) };
    var path = new PathGeom(pts, _ => 60, 1.0);
    double maxJump = 0, prevDir = path.At(0).dir;
    var prev = path.At(0).p;
    double maxTurn = 0;
    for (double s = 1; s <= path.Length; s += 1)
    {
        var (p, d, _) = path.At(s);
        maxJump = Math.Max(maxJump, (p - prev).Length);
        maxTurn = Math.Max(maxTurn, Math.Abs(Geo2.Diff(prevDir, d)));
        prev = p; prevDir = d;
    }
    Check(maxJump < 1.05, $"path is continuous (largest step {maxJump:0.00} m per metre)");
    Check(maxTurn < 1.5, $"direction changes smoothly (largest {maxTurn:0.00}° per metre)");
    var expectLen = 1500 - 2 * 120 + 2 * (60 * Math.PI / 2);
    Check(Math.Abs(path.Length - expectLen) < 2, $"length is right ({path.Length:0} m, expected {expectLen:0})");
}

Console.WriteLine("== Eased taxi turns");
{
    var pts = new List<V2> { new(0, 0), new(0, 300), new(300, 300), new(300, 120), new(420, 0) };
    var plain = new PathGeom(pts, _ => 40, 1.0);
    var eased = new PathGeom(pts, _ => 40, 1.0, ease: true);
    (double jump, double turnRate, double kJump, V2 end) Measure(PathGeom g)
    {
        double jump = 0, turn = 0, kj = 0; var (p0, d0, k0) = g.At(0);
        for (double s = 0.5; s <= g.Length; s += 0.5)
        {
            var (p, d, k) = g.At(s);
            jump = Math.Max(jump, (p - p0).Length); turn = Math.Max(turn, Math.Abs(Geo2.Diff(d0, d)) / 0.5); kj = Math.Max(kj, Math.Abs(k - k0));
            p0 = p; d0 = d; k0 = k;
        }
        return (jump, turn, kj, g.At(g.Length).p);
    }
    var mp = Measure(plain); var me = Measure(eased);
    Console.WriteLine($"  plain: curvature jump {mp.kJump:0.0000}/m, eased: {me.kJump:0.0000}/m; eased largest step {me.jump:0.00} m");
    Check(me.jump < 0.6, $"eased path is continuous (largest step {me.jump:0.00} m per 0.5 m)");
    Check(me.kJump < mp.kJump * 0.35, "turns tighten and unwind gradually instead of all at once");
    Check((me.end - new V2(420, 0)).Length < 0.5, "eased path still ends where it should");
    // Like LGA's alleys: a taxiway with nodes every 15 m and a 90° turn into an alley through short 10 m pieces.
    var dense = new List<V2>();
    for (var x = 0; x <= 300; x += 15) dense.Add(new V2(x, 0));
    dense.Add(new V2(305, -5)); dense.Add(new V2(310, -10)); dense.Add(new V2(310, -20));
    for (var y = -30; y >= -250; y -= 10) dense.Add(new V2(310, y));
    var dpath = new PathGeom(dense, _ => 40, 1.0, ease: true);
    double maxK = 0, maxOff = 0;
    for (double sd = 0; sd <= dpath.Length; sd += 0.5)
    {
        var (q, _, k) = dpath.At(sd);
        maxK = Math.Max(maxK, Math.Abs(k));
        var off = q.Y > -1 ? Math.Abs(q.Y) : Math.Abs(q.X - 310);
        if (q.X < 220 || q.Y < -90) maxOff = Math.Max(maxOff, off);   // on the straights, away from the turn
    }
    Console.WriteLine($"  alley turn: tightest radius {1 / Math.Max(1e-9, maxK):0} m, furthest off the line on the straights {maxOff:0.0} m");
    Check(1 / Math.Max(1e-9, maxK) >= 20, $"a turn into an alley through short scenery pieces stays a proper curve (tightest {1 / Math.Max(1e-9, maxK):0} m radius)");
    Check(maxOff < 6.5, "smoothing never strays more than a few metres off the taxi line");
}

Console.WriteLine("== Wind");
{
    var w = new Wind(270, 20);
    var (hdg, gs) = w.Solve(0, 200);
    Check(hdg > 350 || hdg < 1, $"crabs into a west wind when flying north (heading {hdg:0.0})");
    Check(Math.Abs(gs - Math.Sqrt(200 * 200 - 400)) < 1, $"ground speed with a crosswind ({gs:0.0} kt)");
    var (_, gs2) = w.Solve(270, 200);
    Check(Math.Abs(gs2 - 180) < 0.5, $"headwind slows the ground speed ({gs2:0.0} kt)");
    Check(Math.Abs(w.Headwind(270) - 20) < 0.01, "headwind component on runway 27");
}

foreach (var swapped in new[] { false, true })
{
    Console.WriteLine($"== Airport model (offsets {(swapped ? "with axes swapped" : "as documented")})");
    var data = TestAirport.Build(swapped);
    var apt = new AirportModel(data);
    Console.WriteLine("  calibration: " + apt.Calibration);
    Check(apt.HasGround, "has a taxi network and parking");
    Check(apt.Spots.Count == 16, $"16 gates found ({apt.Spots.Count})");
    var onRunway = Enumerable.Range(0, apt.TaxiPointCount).Count(i => apt.NodeRunway[i] == 0);
    Check(onRunway == 11, $"runway taxi points recognised ({onRunway} of 11)");
    var gate = apt.Spots[0];
    Check(Math.Abs(gate.P.X - (-600)) < 1 && Math.Abs(gate.P.Y - (-350)) < 1, $"gate position decoded ({gate.P.X:0}, {gate.P.Y:0})");
    var r = apt.Route(gate.Access, apt.NearestNode(new V2(1500, -90)));
    Check(r is not null && r.Count > 3, $"taxi route found ({r?.Count ?? 0} nodes)");
}

Console.WriteLine("== One hour of busy traffic, runway 27 in use (wind 270/12)");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var log = new List<string>();
    var world = new TrafficWorld(apt, new TestSource(), seed: 42) { Level = TrafficLevel.Busy, Wind = new Wind(270, 12) };
    world.Log = s => log.Add($"{TimeSpan.FromSeconds(world.Time):hh\\:mm\\:ss} {s}");
    world.UserPos = new V2(-1200, -600); // user parked off to the side
    world.UserOnGround = true;
    const double dt = 0.25;
    var last = new Dictionary<string, (V2 p, double hdg, double gs, string phase, bool ground)>();
    var phaseSince = new Dictionary<string, (string phase, double t)>();
    int runwayConflicts = 0, jumps = 0, closeCalls = 0, stuck = 0, sharpTurns = 0;
    double minSep = double.MaxValue, maxGroundTurnRate = 0, maxBankRate = 0, maxGateTurn = 0;
    var gateTurnWho = "";
    var seenPhases = new HashSet<string>();
    var stuckFlights = new HashSet<string>();
    var prevBank = new Dictionary<string, double>();
    // Rule checks (7110.65, 14 CFR 91.117, AIM).
    int slowFar = 0, slowNear = 0, speedUpInside5 = 0, fastLow = 0, wakeShort = 0, slowDive = 0;
    var prevIas = new Dictionary<string, double>();
    var liftoffs = new List<(double t, int rwy, char wake, string cs)>();
    var airborneSeen = new HashSet<string>();
    for (var step = 0; step < 3600 / dt; step++)
    {
        world.Step(dt);
        var fl = world.Flights;
        foreach (var f in fl)
        {
            if (f.Kind == "arrival" && !f.OnGround && !f.WentAround && f.Phase is "arriving" or "approach" or "final" && world.Time - f.LastHold > 400)
            {
                var dth = (f.ThrS - f.S) / Geo2.MPerNm;
                var jet = f.P.Family != "turboprop";
                if (jet && dth > 20.5 && f.AltFt < 10000 && f.Ias < 209) { slowFar++; if (slowFar <= 3) log.Add($"SLOW {f.Callsign} {f.Ias:0} kt at {dth:0.0} nm"); }
                if (jet && dth > 5.5 && dth <= 20 && f.Ias < Math.Min(169, f.P.Vapp + 10 * (dth - 3)) - 3) { slowNear++; if (slowNear <= 3) log.Add($"SLOW {f.Callsign} {f.Ias:0} kt at {dth:0.0} nm"); }
                // Energy: never slowing while descending steeply in a clean configuration (it can't, in reality).
                if (prevIas.TryGetValue(f.Id, out var pv) && !f.Gear && f.Vs < 0 && -f.Vs * 60 / Math.Max(60, f.Gs) > 300 && pv - f.Ias > 0.15 * dt * 4)
                { slowDive++; if (slowDive <= 3) log.Add($"SLOWING IN A DIVE {f.Callsign} {f.Ias:0} kt, {f.Vs:0} fpm at {dth:0.0} nm"); }
                if (dth < 5 && prevIas.TryGetValue(f.Id, out var pi) && f.Ias > pi + 0.01) speedUpInside5++;
            }
            if (f.Kind == "departure" && !f.OnGround && f.AltFt < 9500 && f.Ias > 251) fastLow++;
            if (f.Kind == "departure" && !f.OnGround && f.Rwy is not null && airborneSeen.Add(f.Id)) liftoffs.Add((world.Time, f.Rwy.Runway, f.Wake, f.Callsign));
            prevIas[f.Id] = f.Ias;
        }
        // Runway: at most one aircraft on it (departures that have lifted off don't count).
        var onRwy = fl.Where(f => f.OnRunway >= 0 && !(f.Kind == "departure" && !f.OnGround)).GroupBy(f => f.OnRunway).Where(g => g.Count() > 1).ToList();
        if (onRwy.Count > 0)
        {
            runwayConflicts++;
            if (runwayConflicts <= 3) log.Add($"{TimeSpan.FromSeconds(world.Time):hh\\:mm\\:ss} RUNWAY CONFLICT: " + string.Join(", ", onRwy.SelectMany(g => g).Select(f =>
                $"{f.Callsign} {f.Phase} since {TimeSpan.FromSeconds(f.PhaseTime):hh\\:mm\\:ss} at ({f.Pos.X:0},{f.Pos.Y:0}) gs {f.Gs:0} reserve {f.Reserve}")));
        }
        // Ground separation between moving aircraft.
        var moving = fl.Where(f => f.OnGround && f.Phase is "taxi" or "taxi in" or "pushback" or "holding short" or "lining up" or "engine start").ToList();
        for (var i = 0; i < moving.Count; i++)
            for (var j = i + 1; j < moving.Count; j++)
            {
                var d = (moving[i].Pos - moving[j].Pos).Length;
                minSep = Math.Min(minSep, d);
                if (d < 12) { closeCalls++; if (closeCalls <= 4) log.Add($"{TimeSpan.FromSeconds(world.Time):hh\\:mm\\:ss} CLOSE {moving[i].Callsign} {moving[i].Phase} / {moving[j].Callsign} {moving[j].Phase}: {d:0} m at ({moving[i].Pos.X:0},{moving[i].Pos.Y:0})"); }
            }
        foreach (var f in fl)
        {
            seenPhases.Add(f.Phase);
            if (last.TryGetValue(f.Id, out var l))
            {
                var expected = Math.Max(l.gs, f.Gs) * Geo2.MpsPerKt * dt;
                var jump = (f.Pos - l.p).Length;
                if (jump > expected * 1.6 + 2 && l.phase == f.Phase)
                {
                    jumps++;
                    if (jumps <= 5) log.Add($"{TimeSpan.FromSeconds(world.Time):hh\\:mm\\:ss} JUMP {f.Callsign} {f.Phase}: moved {jump:0.0} m, expected {expected:0.0}; s {f.S:0.0} of {f.Path?.Length:0.0}, from ({l.p.X:0.0},{l.p.Y:0.0}) to ({f.Pos.X:0.0},{f.Pos.Y:0.0})");
                }
                if (f.OnGround && l.ground && f.Gs > 1)
                {
                    var rate = Math.Abs(Geo2.Diff(l.hdg, f.Heading)) / dt;
                    if (f.Phase != "pushback") maxGroundTurnRate = Math.Max(maxGroundTurnRate, rate);
                    if (f.Phase == "taxi in" && f.Spot is not null && (f.Pos - f.Spot.P).Length < 120 && rate > maxGateTurn) { maxGateTurn = rate; gateTurnWho = $"{f.Callsign} at {f.Gs:0} kt"; }
                    if (rate > 40 && f.Phase != "pushback") { sharpTurns++; if (sharpTurns <= 5) log.Add($"{TimeSpan.FromSeconds(world.Time):hh\\:mm\\:ss} SHARP TURN {f.Callsign} {l.phase}->{f.Phase}: {rate:0}°/s at {f.Gs:0} kt at ({f.Pos.X:0},{f.Pos.Y:0}), heading {l.hdg:0}->{f.Heading:0}, s {f.S:0}/{f.Path?.Length:0}, path " + string.Join(" ", f.Path?.Points.Select(q => $"({q.X:0},{q.Y:0})") ?? Array.Empty<string>())); }
                }
            }
            if (prevBank.TryGetValue(f.Id, out var pb)) maxBankRate = Math.Max(maxBankRate, Math.Abs(f.Bank - pb) / dt);
            prevBank[f.Id] = f.Bank;
            last[f.Id] = (f.Pos, f.Heading, f.Gs, f.Phase, f.OnGround);
            if (!phaseSince.TryGetValue(f.Id, out var ps) || ps.phase != f.Phase) phaseSince[f.Id] = (f.Phase, world.Time);
            else if (f.Phase is "taxi" or "taxi in" or "holding short" or "pushback" or "lined up" or "landing roll" && world.Time - ps.t > 900 && stuckFlights.Add(f.Id))
            {
                stuck++;
                log.Add($"{TimeSpan.FromSeconds(world.Time):hh\\:mm\\:ss} STUCK {f.Callsign} in {f.Phase} for 15 min at ({f.Pos.X:0}, {f.Pos.Y:0}), blocked by {f.BlockedBy?.Callsign ?? "-"}");
            }
        }
    }
    foreach (var line in log.Take(160)) Console.WriteLine("    " + line);
    if (log.Count > 160) Console.WriteLine($"    … {log.Count - 160} more log lines");
    var takeoffs = log.Count(l => l.Contains("airborne"));
    var landings = log.Count(l => l.Contains(": landed"));
    var goArounds = log.Count(l => l.Contains("going around"));
    var parkedArrivals = log.Count(l => l.Contains("taxiing to"));
    Console.WriteLine($"  takeoffs {takeoffs}, landings {landings}, go-arounds {goArounds}, landings with a gate {parkedArrivals}");
    Console.WriteLine($"  phases seen: {string.Join(", ", seenPhases.OrderBy(x => x))}");
    Console.WriteLine($"  closest moving aircraft on the ground: {minSep:0} m; fastest ground turn {maxGroundTurnRate:0}°/s; fastest bank change {maxBankRate:0}°/s");
    Check(takeoffs >= 6, $"departures take off ({takeoffs})");
    Check(landings >= 6, $"arrivals land ({landings})");
    Check(parkedArrivals >= 3, $"arrivals taxi to gates ({parkedArrivals})");
    Check(goArounds <= 2, $"few go-arounds ({goArounds})");
    Check(!log.Any(l => l.Contains("not stable")), "every arrival is stable by 1,000 ft (AIM 5-4)");
    Check(slowFar == 0, $"no jet slowed below 210 kt beyond 20 miles (7110.65 5-7-3) ({slowFar})");
    Check(slowNear == 0, $"no jet slowed below 170 kt between 5 and 20 miles (7110.65 5-7-3) ({slowNear})");
    Check(slowDive == 0, $"no arrival slows while diving clean (energy: descend OR slow) ({slowDive})");
    Check(speedUpInside5 == 0, $"no speed changes upward inside 5 miles (7110.65 5-7-2) ({speedUpInside5})");
    Check(fastLow == 0, $"departures stay at or below 250 kt below 10,000 ft (91.117) ({fastLow})");
    for (var i = 1; i < liftoffs.Count; i++)
    {
        var a = liftoffs[i - 1]; var b = liftoffs[i];
        if (a.rwy != b.rwy) continue;
        if (b.t - a.t < Cwt.DepartureSeconds(a.wake, b.wake) - 1) { wakeShort++; log.Add($"WAKE {b.cs} ({b.wake}) {b.t - a.t:0} s behind {a.cs} ({a.wake})"); }
        if (b.t - a.t < 25) { wakeShort++; log.Add($"TOO CLOSE {b.cs} {b.t - a.t:0} s behind {a.cs}"); }
    }
    Check(wakeShort == 0, $"departures spaced for wake turbulence and the runway (7110.65 3-9-6) ({wakeShort})");
    Check(runwayConflicts == 0, $"never two aircraft on the runway ({runwayConflicts} moments)");
    Check(jumps == 0, $"no jumps in movement ({jumps})");
    Check(closeCalls < 20, $"aircraft keep their distance on the ground ({closeCalls} moments closer than 12 m)");
    Check(stuck == 0, $"no aircraft stuck for 15 minutes ({stuck})");
    Check(sharpTurns == 0, $"no sudden turns on the ground ({sharpTurns}, fastest {maxGroundTurnRate:0}°/s)");
    Check(maxGateTurn <= 20, $"turns into the gates are gentle, like a lead-in line (fastest {maxGateTurn:0}°/s, {gateTurnWho})");
    Check(maxBankRate <= 5.5, $"bank changes gently ({maxBankRate:0.0}°/s)");
    Check(!log.Any(l => l.Contains("removed")), "no errors in the traffic logic");
    Check(seenPhases.Contains("pushback") && seenPhases.Contains("lined up") && seenPhases.Contains("flare") && seenPhases.Contains("at gate") || seenPhases.Contains("parked"),
        "all phases happen (pushback, line-up, flare, gate)");
}

Console.WriteLine("== Rules: wake categories and crossing runways");
{
    Check(Cwt.For("B77W", Perf.For("B77W")) == 'B' && Cwt.For("B738", Perf.For("B738")) == 'F' && Cwt.For("E75L", Perf.For("E75L")) == 'G' && Cwt.For("B752", Perf.For("B752")) == 'E' && Cwt.For("A388", Perf.For("A388")) == 'A',
        "aircraft get their CWT wake categories (777 B, 737 F, E175 G, 757 E, A380 A)");
    Check(Cwt.MilesBehind('B', 'F') == 5 && Cwt.MilesBehind('A', 'I') == 8 && Cwt.MilesBehind('F', 'F') == 3 && Cwt.MilesBehind('C', 'E') == 3.5,
        "spacing on final follows the CWT table (heavy→large 5 nm, A380→small 8, large→large 3)");
    Check(Cwt.DepartureSeconds('B', 'F') == 120 && Cwt.DepartureSeconds('A', 'B') == 180 && Cwt.DepartureSeconds('F', 'F') == 0,
        "departure wake times follow 7110.65 3-9-6 (2 min behind a heavy, 3 behind an A380)");
    Check(Airspace.Has200KnotArea("KISP") && !Airspace.Has200KnotArea("KLGA") && !Airspace.Has200KnotArea("EGLL"),
        "91.117(b) 200-knot area: Class C/D airports in the US, not Class B");

    var data = TestAirport.Build(false);
    var map = new LocalMap(40, -75);
    var (clat, clon) = map.ToLatLon(new V2(0, 0));
    data.Runways.Add(new AirportRunway { Lat = clat, Lon = clon, ElevationM = 10, Heading = 0, LengthM = 2500, WidthM = 45, PrimaryNumber = 36, SecondaryNumber = 18 });
    var apt = new AirportModel(data);
    var w = new TrafficWorld(apt, new TestSource(), seed: 3) { Level = TrafficLevel.Busy, Wind = new Wind(300, 15) };
    var lines = new List<string>(); w.Log = l => lines.Add(l);
    w.SelectRunways(true);
    Console.WriteLine("  " + string.Join(" | ", lines));
    Check(w.ArrRwy?.Ident == "27" && w.DepRwy?.Ident == "36", $"busy: land 27, depart the crossing runway 36 ({w.ArrRwy?.Ident}/{w.DepRwy?.Ident})");
    // 7110.65 3-10-4 / 3-9-8: a departure on the crossing runway must be through the intersection before an
    // arrival crosses its landing threshold.
    var dep = new Flight { Id = "D1", Callsign = "TST1", Airline = "DAL", Type = "B738", Kind = "departure", P = Perf.For("B738"), Rwy = w.DepRwy, Pos = w.DepRwy!.Threshold };
    var close = w.SpawnArrival(1.5, true)!;
    Check(!w.CrossingRunwayClearForTakeoff(dep), "no takeoff on the crossing runway with an arrival 1.5 nm from its threshold");
    Check(!w.CrossingRunwayClearForTakeoff(dep, extraSec: 30), "no lining up to wait on the runway either");
    close.Done = true; w.Flights.Remove(close);
    var far = w.SpawnArrival(7, true)!;
    Check(w.CrossingRunwayClearForTakeoff(dep), "takeoff on the crossing runway allowed with the next arrival 7 nm out");
    Check(w.CrossingRunwayClearForTakeoff(dep, extraSec: 30), "lining up allowed too when the takeoff can follow it");
    far.Done = true; w.Flights.Remove(far);
    w.Level = TrafficLevel.Light;
    Check(w.DepRwy?.Ident == "27", $"turning traffic down re-picks the runways: one runway for both ({w.DepRwy?.Ident})");
    w.Level = TrafficLevel.Busy;
    Check(w.DepRwy?.Ident == "36", $"turning traffic up re-picks the runways: the crossing runway again ({w.DepRwy?.Ident})");
    w.Level = TrafficLevel.Light;
    w.Level = TrafficLevel.Busy; w.Wind = new Wind(120, 12); w.SelectRunways(true);
    Check(w.ArrRwy?.Ident == "09" || w.ArrRwy?.Ident == "18", $"wind change: landing runway turns into the wind ({w.ArrRwy?.Ident})");
}

Console.WriteLine("== Runway-only airport (test buttons without a layout)");
{
    var model = AirportModel.RunwayOnly("KTST", 40, -75, 20, 40, 7000, "04", 40, -75);
    var world = new TrafficWorld(model, new TestSource(), seed: 7) { Automatic = false, Wind = new Wind(40, 8) };
    var (ok, msg) = world.TestDeparture();
    Console.WriteLine("  " + msg);
    Check(ok, "test departure accepted");
    var dep = world.Flights[0];
    double airborneAt = -1;
    for (var i = 0; i < 90 * 4; i++) { world.Step(0.25); if (airborneAt < 0 && !dep.OnGround) airborneAt = world.Time; }
    Check(airborneAt > 0 && airborneAt < 70, $"test departure airborne within 70 s ({airborneAt:0} s)");
    var (ok2, msg2) = world.TestArrival();
    Console.WriteLine("  " + msg2);
    var arr = world.Flights.First(f => f.Kind == "arrival");
    double landedAt = -1;
    for (var i = 0; i < 400 * 4 && !arr.Done; i++) { world.Step(0.25); if (landedAt < 0 && arr.OnGround) landedAt = world.Time; }
    Check(ok2 && landedAt > 0, $"test arrival lands ({(landedAt > 0 ? (landedAt - 90).ToString("0") + " s after starting" : "never")})");
}

Console.WriteLine("== Pushback plans at every gate of the test airport");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var world = new TrafficWorld(apt, new TestSource(), seed: 5) { Wind = new Wind(270, 10) };
    var survey = world.PushbackSurvey();
    Console.WriteLine("  " + survey);
    Check(survey.Contains("16 clean"), "a clean pushback at every gate");
    var planner = new PushbackPlanner(apt);
    var p = planner.Plan(apt.Spots[5], Perf.Narrowbody, -1);
    Console.WriteLine("  gate 6: " + p.Describe());
    Check(p.TurnStart >= 25, $"pushes straight back before turning ({p.TurnStart:0} m)");
    Check(Math.Abs(Geo2.Diff(p.EndHeading, 90)) < 5 || Math.Abs(Geo2.Diff(p.EndHeading, 270)) < 5, "ends lined up on the taxilane");
}

Console.WriteLine("== Traffic from a real schedule");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var sched = new Laia.Common.Airports.ScheduleData { Airport = "TEST", Source = "AIG" };
    const int start = 2 * 1440 + 600; // Wednesday 10:00 UTC
    for (var i = 0; i < 10; i++)
    {
        // Each airframe arrives, then leaves again 50 minutes later.
        sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = $"N{i}DL", Flight = $"{100 + i}", Dep = "KXXX", Arr = "TEST", DepMin = start - 100 + i * 7, ArrMin = start + 6 + i * 7 });
        sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = $"N{i}DL", Flight = $"{200 + i}", Dep = "TEST", Arr = "KYYY", DepMin = start + 56 + i * 7, ArrMin = start + 150 + i * 7 });
        // And some aircraft already on the ground leave in the first half hour.
        sched.Legs.Add(new() { Airline = "UAL", Operator = "UAL", Title = "U", Type = "A320", Reg = $"N{i}UA", Flight = $"{300 + i}", Dep = "TEST", Arr = "KZZZ", DepMin = start + 8 + i * 3, ArrMin = start + 120 });
    }
    var log = new List<string>();
    var world = new TrafficWorld(apt, new TestSource(), seed: 9) { Level = TrafficLevel.Busy, Wind = new Wind(270, 8), Schedule = sched, WeekMinute = start };
    world.Log = s => log.Add($"{start + world.Time / 60 - start,5:0.0} min {s}");
    var landedAt = new Dictionary<string, double>();
    var airborneAt = new Dictionary<string, double>();
    for (var i = 0; i < 150 * 60 * 4; i++)
    {
        world.WeekMinute = start + world.Time / 60;
        world.Step(0.25);
        foreach (var l in log.Skip(0)) { }
        foreach (var f in world.Flights)
        {
            if (f.Kind == "arrival" && f.OnGround && !landedAt.ContainsKey(f.Callsign)) landedAt[f.Callsign] = world.Time / 60;
            if (f.Kind == "departure" && !f.OnGround && !airborneAt.ContainsKey(f.Callsign)) airborneAt[f.Callsign] = world.Time / 60;
        }
    }
    foreach (var line in log.Where(l => l.Contains("schedule") || l.Contains("skipped") || l.Contains("Couldn't")).Take(20)) Console.WriteLine("    " + line);
    var arrivalErrors = sched.Legs.Where(l => l.Arr == "TEST" && landedAt.ContainsKey(l.Callsign)).Select(l => landedAt[l.Callsign] - (l.ArrMin - start)).ToList();
    var departureDelays = sched.Legs.Where(l => l.Dep == "TEST" && airborneAt.ContainsKey(l.Callsign)).Select(l => airborneAt[l.Callsign] - (l.DepMin - start)).ToList();
    Console.WriteLine($"  arrivals landed: {arrivalErrors.Count} of 10, off schedule by {string.Join(", ", arrivalErrors.Select(e => e.ToString("+0;-0")))} min");
    Console.WriteLine($"  departures airborne: {departureDelays.Count} of 20, minutes after the scheduled time: {string.Join(", ", departureDelays.Select(e => e.ToString("0")))}");
    Check(arrivalErrors.Count >= 9, "scheduled arrivals land");
    Check(arrivalErrors.Count > 0 && arrivalErrors.Average(Math.Abs) < 6, "arrivals land close to their scheduled time");
    Check(departureDelays.Count >= 18, "scheduled departures leave (including the arrived aircraft turning around)");
    Check(departureDelays.Count > 0 && departureDelays.Min() > -1, "nobody leaves before their scheduled time");
    var dl = sched.Legs.Where(l => l.Dep == "TEST" && l.Airline == "DAL").Count(l => airborneAt.ContainsKey(l.Callsign));
    Check(dl >= 9, $"arrived airframes leave again on their next flight ({dl} of 10)");
}

Console.WriteLine("== Real gates override the scenery's gate assignments");
{
    var apt = new AirportModel(TestAirport.Build(false));
    // The scenery (wrongly) gives gates A10-A12 to Delta.
    foreach (var sp in apt.Spots.Where(sp => sp.Label is "Gate A10" or "Gate A11" or "Gate A12")) sp.Airlines = new() { "DAL" };
    var real = new RealGates { Airport = "TEST", Source = "AirLabs" };
    foreach (var gnum in new[] { "2", "3", "4" }) real.Add("DAL", "", "2026-11-11", "A", gnum);
    foreach (var gnum in new[] { "A10", "A11", "A12" }) real.Add("UAL", "", "2026-11-11", "", gnum);
    real.Add("DAL", "DAL150", "2026-11-11", "A", "15");
    real.Add("DAL", "", "2026-11-12", "C", "99");
    var plan = new GatePlan(apt, real);
    Console.WriteLine("  " + plan.Describe());
    Check(RealGates.Match(apt, "A", "7").Select(x => x.Label).SequenceEqual(new[] { "Gate A7" }), "terminal letter + gate number finds the stand");
    Check(RealGates.Match(apt, "", "A7").Select(x => x.Label).SequenceEqual(new[] { "Gate A7" }), "gate with its own letter finds the stand");
    Check(RealGates.Match(apt, "B", "7").Count == 0, "a gate in another terminal isn't matched to the wrong letter");
    Check(plan.Unmatched.Contains("C|99"), "gates not in the scenery are reported");

    var sched = new Laia.Common.Airports.ScheduleData { Airport = "TEST", Source = "AIG" };
    const int start = 2 * 1440 + 600;
    for (var i = 0; i < 5; i++)
    {
        sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = $"N{i}DL", Flight = $"{150 + i}", Dep = "TEST", Arr = "KYYY", DepMin = start + 30 + i * 5, ArrMin = start + 150 });
        sched.Legs.Add(new() { Airline = "UAL", Operator = "UAL", Title = "U", Type = "A320", Reg = $"N{i}UA", Flight = $"{300 + i}", Dep = "TEST", Arr = "KZZZ", DepMin = start + 32 + i * 5, ArrMin = start + 120 });
    }
    // An aircraft that landed three hours ago and leaves tomorrow morning is at its gate overnight.
    sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = "N900DL", Flight = "900", Dep = "KYYY", Arr = "TEST", DepMin = start - 330, ArrMin = start - 180 });
    sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = "N900DL", Flight = "901", Dep = "TEST", Arr = "KYYY", DepMin = start + 600, ArrMin = start + 750 });
    // Two arrivals due the same minute don't appear on top of each other.
    sched.Legs.Add(new() { Airline = "UAL", Operator = "UAL", Title = "U", Type = "A320", Reg = "N800UA", Flight = "800", Dep = "KZZZ", Arr = "TEST", DepMin = start - 120, ArrMin = start + 3 });
    sched.Legs.Add(new() { Airline = "UAL", Operator = "UAL", Title = "U", Type = "A320", Reg = "N801UA", Flight = "801", Dep = "KZZZ", Arr = "TEST", DepMin = start - 125, ArrMin = start + 3 });
    var world = new TrafficWorld(apt, new TestSource(), seed: 5) { Level = TrafficLevel.Busy, Wind = new Wind(270, 8), Schedule = sched, WeekMinute = start, Gates = real };
    var log = new List<string>();
    world.Log = s => log.Add(s);
    for (var i = 0; i < 40; i++) { world.WeekMinute = start + world.Time / 60; world.Step(0.25); }
    int Num(string label) => int.Parse(new string(label.Where(char.IsDigit).ToArray()));
    var at = world.Flights.Where(f => f.Spot is not null).Select(f => (f.Callsign, f.Airline, gate: f.Spot!.Label)).ToList();
    Console.WriteLine("  parked: " + string.Join(", ", at.Select(a => $"{a.Callsign} {a.gate}")));
    Check(at.Any(a => a.Callsign == "DAL150" && a.gate == "Gate A15"), "a flight with a known real gate parks at exactly that gate");
    Check(at.Where(a => a.Airline == "DAL" && a.Callsign != "DAL150").All(a => Num(a.gate) is <= 5 or >= 14), "Delta parks at its real gates (or next to them), not the scenery's");
    Check(at.Where(a => a.Airline == "UAL").All(a => Num(a.gate) is >= 9 and <= 13), "United parks at its real gates (or next to them)");
    Check(log.Any(l => l.Contains("real gates from AirLabs")), "the real-gate plan is logged");
    Check(world.Flights.Any(f => f.Reg == "N900DL" && f.Kind == "parked"), "an aircraft that landed earlier waits at its gate overnight for its morning flight");
    var two = world.Flights.Where(f => f.Callsign is "UAL800" or "UAL801").ToList();
    Check(two.Count == 2 && (two[0].Pos - two[1].Pos).Length > 4 * 1852, "two arrivals due at the same time start at least 4 nm apart");
}

Console.WriteLine("== A STAR that ends over the airport is vectored off it, not flown across the field");
{
    // Like LaGuardia's VADDR1: from the north, ending at a fix right over the airport, "expect vectors".
    var data = TestAirport.Build(false);
    var map = new LocalMap(data.Lat, data.Lon);
    var nm = Geo2.MPerNm;
    Laia.Common.Airports.ProcedureLeg Fix(string name, double x, double y, int type = 18)
    {
        var (la, lo) = map.ToLatLon(new V2(x, y));
        return new() { Type = type, Fix = name, Lat = la, Lon = lo };
    }
    data.Arrivals.Clear(); data.Approaches.Clear();
    data.Arrivals.Add(new() { Name = "OVRHD1", Legs = { Fix("NORTH", 0, 35 * nm, 15), Fix("MIDDL", 2 * nm, 20 * nm), Fix("OVERF", 0, 0.4 * nm) } });
    data.Approaches.Add(new() { Type = 4, Runway = "27", FinalLegs = { Fix("FINAL", 10 * nm, 0, 15), Fix("RW27", 1500, 0, 4) } });
    var apt = new AirportModel(data);
    var world = new TrafficWorld(apt, new TestSource(), seed: 4) { Automatic = false, Wind = new Wind(270, 10) };
    world.SelectRunways(true);
    var bad = 0; string route = "";
    for (var k = 0; k < 8; k++)
    {
        var f = world.SpawnArrival(38, true);
        if (f?.Path is null) continue;
        route = f.Route;
        // Anything within 3 nm of the field must be on the final approach course (east of the runway, on the centreline).
        foreach (var q in f.Path.Points)
            if (q.Length < 3 * nm && !(Math.Abs(q.Y) < 600 && q.X > -1600)) bad++;   // the runway itself and its final are fine
        f.Done = true;
    }
    Console.WriteLine($"  route: {route}");
    Check(bad == 0, $"arrivals don't cross the airport on the way to final ({bad} points over the field)");
    Check(route.Contains("vectors to the ILS 27"), "the arrival is vectored to the approach, as controllers do");
}

Console.WriteLine("== Vectored to an approach's own first fix, not a straight-in final through a neighbour");
{
    // Like LaGuardia's RNAV Y 31: its final comes in from a fix off to the side, while the extended centreline
    // points at a neighbouring airport. A STAR that doesn't lead into it from the other side of the field.
    var data = TestAirport.Build(false);
    data.Arrivals.Clear(); data.Approaches.Clear();
    var map = new LocalMap(data.Lat, data.Lon);
    var nm = Geo2.MPerNm;
    Laia.Common.Airports.ProcedureLeg Fix(string name, double x, double y, int type = 18)
    {
        var (la, lo) = map.ToLatLon(new V2(x, y));
        return new() { Type = type, Fix = name, Lat = la, Lon = lo };
    }
    data.Arrivals.Add(new() { Name = "WEST1", Legs = { Fix("WESTA", -35 * nm, 6 * nm, 15), Fix("WESTB", -20 * nm, 8 * nm) } });
    // Runway 27 lands westbound; this approach starts north-east (GEMKX) and curves onto a short final.
    data.Approaches.Add(new() { Type = 10, Runway = "27", Suffix = "Y", FinalLegs = { Fix("GEMKX", 7 * nm, 7 * nm, 15), Fix("NYJXT", 5 * nm, 1.5 * nm), Fix("MANLX", 3.5 * nm, 0), Fix("RW27", 1500, 0) } });
    var apt = new AirportModel(data);
    var world = new TrafficWorld(apt, new TestSource(), seed: 5) { Automatic = false, Wind = new Wind(270, 10) };
    world.SelectRunways(true);
    var f = world.SpawnArrival(40, true)!;
    var pts = f.Path!.Points;
    // With a published transition starting near the end of the STAR (like FEDEM for LGA's southwest arrivals),
    // that transition is flown as published.
    var data2 = TestAirport.Build(false);
    data2.Arrivals.Clear(); data2.Approaches.Clear();
    data2.Arrivals.Add(new() { Name = "WEST1", Legs = { Fix("WESTA", -35 * nm, 6 * nm, 15), Fix("WESTB", -20 * nm, 8 * nm) } });
    data2.Approaches.Add(new() { Type = 10, Runway = "27", Suffix = "Y",
        Transitions = { new() { Name = "FEDXM", Legs = { Fix("FEDXM", -14 * nm, 9 * nm, 15), Fix("DRIXS", 0, 9 * nm), Fix("GEMKX", 7 * nm, 7 * nm) } } },
        FinalLegs = { Fix("GEMKX", 7 * nm, 7 * nm, 15), Fix("NYJXT", 5 * nm, 1.5 * nm), Fix("MANLX", 3.5 * nm, 0), Fix("RW27", 1500, 0) } });
    var world2 = new TrafficWorld(new AirportModel(data2), new TestSource(), seed: 5) { Automatic = false, Wind = new Wind(270, 10) };
    world2.SelectRunways(true);
    var f2 = world2.SpawnArrival(40, true)!;
    Console.WriteLine($"  with a transition: {f2.Route}");
    Check(f2.Route.Contains("via FEDXM"), "the published approach transition nearest the end of the STAR is flown");
    Check(f2.Path!.Points.Count(q => q.X > 6.5 * nm && Math.Abs(q.Y) < 2 * nm) == 0, "the transition keeps it out of the neighbour's airspace too");
    // The "neighbour" sits on the extended centreline 8–12 nm east: nothing should go there.
    var intoNeighbour = pts.Count(q => q.X > 6.5 * nm && Math.Abs(q.Y) < 2 * nm);
    var overField = pts.Count(q => q.Length < 3 * nm && !(Math.Abs(q.Y) < 600 && q.X > -1600));
    Console.WriteLine($"  route: {f.Route}");
    Check(f.Route.Contains("vectors to GEMKX for the RNAV 27 Y"), "vectored to the approach's first fix, then flies the approach");
    Check(intoNeighbour == 0, $"never on a long straight-in through the neighbour's airspace ({intoNeighbour} points)");
    Check(overField == 0, $"doesn't cross the field on the way ({overField} points)");
}

Console.WriteLine("== One approach at a time: the ATIS's, or a fixed choice — never a mix (LGA RNAV X vs Y 31)");
{
    var atis = AtisParse.Approaches("LGA ATIS INFO K 0551Z. 31010KT 10SM FEW250 08/M03 A3012 (THREE ZERO ONE TWO). RNAV X RWY 31 APCH IN USE. DEPG RWY 4. NOTICE TO AIR MISSIONS. ILS RWY 22 OTS. ...ADVS YOU HAVE INFO K.");
    Check(atis.Count == 1 && atis[0] == ("RNAV", "X", "31"), $"reads the approach in use from the ATIS, not the out-of-service one ({string.Join(", ", atis)})");
    var atis2 = AtisParse.Approaches("EXPECT ILS OR LOC RWY 4 APPROACH. SIMUL APCHS IN USE");
    Check(atis2.Count == 1 && atis2[0] == ("ILS", "", "04"), $"\"ILS or LOC runway 4\" is the ILS to 04 ({string.Join(", ", atis2)})");
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    data.Approaches.Clear();
    var map = new LocalMap(data.Lat, data.Lon);
    Laia.Common.Airports.ProcedureLeg Fix(string name, double x, double y, int type = 18)
    {
        var (la, lo) = map.ToLatLon(new V2(x, y));
        return new() { Type = type, Fix = name, Lat = la, Lon = lo };
    }
    // Like the RNAV X 31: curves in from the side and turns onto a final only 1.6 nm long.
    data.Approaches.Add(new() { Type = 10, Runway = "27", Suffix = "X",
        Transitions = { new() { Name = "VARAX", Legs = { Fix("VARAX", 14 * nm, -12 * nm, 15), Fix("PACHX", 8 * nm, -8 * nm) } } },
        FinalLegs = { Fix("PACHX", 8 * nm, -8 * nm, 15), Fix("SHAYX", 6 * nm, -5 * nm), Fix("KEYTX", 4.5 * nm, -2.5 * nm), Fix("DCTRX", 1500 + 1.6 * nm, 0), Fix("RW27", 1500, 0) } });
    // Like the RNAV Y 31: more transitions, from the north-east.
    data.Approaches.Add(new() { Type = 10, Runway = "27", Suffix = "Y",
        Transitions = { new() { Name = "WASBX", Legs = { Fix("WASBX", 15 * nm, 12 * nm, 15), Fix("GEMKY", 10 * nm, 6 * nm) } },
                        new() { Name = "JABAX", Legs = { Fix("JABAX", 20 * nm, 5 * nm, 15), Fix("GEMKY", 10 * nm, 6 * nm) } } },
        FinalLegs = { Fix("GEMKY", 10 * nm, 6 * nm, 15), Fix("MANLY", 6 * nm, 0), Fix("RW27", 1500, 0) } });
    List<string> Routes(TrafficWorld w) { var r = new List<string>(); for (var i = 0; i < 6; i++) { var f = w.SpawnArrival(40, true); if (f is null) continue; r.Add(f.Route); f.Done = true; } return r; }
    var w1 = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 8) { Automatic = false, Wind = new Wind(270, 10) };
    w1.SelectRunways(true);
    var r1 = Routes(w1);
    Console.WriteLine($"  standard choice: {w1.ApproachInUse}; {string.Join(" | ", r1.Distinct())}");
    Check(w1.ApproachInUse == "RNAV Y 27" && r1.All(r => r.Contains("RNAV 27 Y")), "without an ATIS, every arrival flies the same approach (the one with the most transitions)");
    var w2 = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 8) { Automatic = false, Wind = new Wind(270, 10), AtisApproaches = { ("RNAV", "X", "27") } };
    w2.SelectRunways(true);
    var f2 = w2.SpawnArrival(40, true)!;
    var r2 = Routes(w2).Append(f2.Route).ToList();
    Console.WriteLine($"  ATIS says RNAV X: {string.Join(" | ", r2.Distinct())}");
    Check(w2.ApproachInUse == "RNAV X 27" && r2.All(r => r.Contains("RNAV 27 X")), "with the ATIS saying RNAV X, every arrival flies the X");
    var keyth = new V2(4.5 * nm, -2.5 * nm);
    var nearKeyth = f2.Path!.Points.Min(q => (q - keyth).Length) / nm;
    Check(nearKeyth < 0.6 && !f2.Route.StartsWith("vectors to the "), $"the X is flown along its curved final, not replaced by a straight-in ({nearKeyth:0.00} nm from its fix)");
}

Console.WriteLine("== The real ATIS decides the runways; the wind is only the fallback");
{
    string L(IEnumerable<string> l) => string.Join(",", l.OrderBy(x => x));
    // Real texts that broke the FlightSim Route Generator's parser (its lessons learned), plus LaGuardia's.
    var cases = new (string name, string text, string dep, string arr, string closed)[]
    {
        ("LaGuardia", "LGA ATIS INFO B. 18008KT. ILS RWY 22 APCH IN USE. DEPG RWY 13. NOTAMS... RWY 4 CLSD.", "13", "22", ""),
        ("Denver", "DEN ARR INFO X. DEPG RWY8, RWY17L, RWY25. LDG RWY 3 4 LEFT, RWY 35R.", "08,17L,25", "34L,35R", ""),
        ("Boston", "BOS ATIS INFO C. RNAV 4L, DEP 4R.", "04R", "04L", ""),
        ("Charlotte", "CLT DEP INFO D. SIMUL DEPS IN USE, EXPECT 1R, 36.", "01R,36", "", ""),
        ("Cleveland", "CLE ATIS INFO E. ILS RWY 24R APCH IN USE. DEPG RWY 24L. TWY G2 CLSD BTN RWY, 24L AND RWY 24R.", "24L", "24R", ""),
        ("Atlanta", "ATL ARR INFO F. APCHS IN USE VIS 8L, ILS 9R, VIS 10. NOTAMS... RWY 9L CLSD.", "", "08L,09R,10", ""),
        ("San Francisco", "SFO ARR INFO G. VISUAL, RWY 28L, SIMUL, VISUAL, RWY 28R APCH IN USE. RWY 1L/19R CLSD.", "", "28L,28R", "01L,19R"),
        ("lists", "SIMUL ILS APCHS IN USE. LANDING RWYS 4R, 4L. DEPARTING RWY 4L AND 31L.", "04L,31L", "04L,04R", ""),
        ("both", "LDG AND DEPG RWY 31.", "31", "31", ""),
    };
    foreach (var (name, text, dep, arr, closed) in cases)
    {
        var (d, a, c) = AtisParse.Runways(text);
        Check(L(d) == dep && L(a) == arr && L(c) == closed, $"{name}: departing {L(d)}, landing {L(a)}{(c.Count > 0 ? ", closed " + L(c) : "")}");
    }
    var combined = AtisParse.Combine(new[] { ("arr", "K", "RDU ARR INFO K. ILS RWY 23L APCH IN USE."), ("dep", "L", "RDU DEP INFO L. RWY 23R IN USE.") });
    Check(L(combined.Landing) == "23L" && L(combined.Departing) == "23R" && combined.CodeArr == "K" && combined.CodeDep == "L",
        $"arrival and departure broadcasts combine (landing {L(combined.Landing)}, departing {L(combined.Departing)}, info {combined.CodeArr}/{combined.CodeDep})");
    var one = AtisParse.FromText("RDU ATIS INFO M. ILS RWY 5L APCH IN USE.");
    Check(L(one.Departing) == "05L", "a combined ATIS naming only arrivals uses them for departures too");
    var world = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 3) { Automatic = false, Wind = new Wind(270, 12) };
    world.SelectRunways(true);
    var byWind = world.ArrRwy?.Ident;
    world.AtisText = "TEST ATIS INFO C. 27012KT. VISUAL APCH RWY 9 IN USE. DEPG RWY 9.";
    var byAtis = world.ArrRwy?.Ident;
    world.AtisText = "";
    world.SelectRunways(true);
    Check(byWind == "27" && byAtis == "09" && world.ArrRwy?.Ident == "27", $"the ATIS's runway is used even against the wind, and the wind decides without an ATIS ({byWind} → {byAtis} → {world.ArrRwy?.Ident})");
}

Console.WriteLine("== Several runways from the ATIS, and STARs/SIDs chosen by where the flight comes from or goes");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var map = new LocalMap(data.Lat, data.Lon);
    // A parallel 1,500 m north: 09L/27R (the existing runway stays 09/27).
    var (pla, plo) = map.ToLatLon(new V2(0, 1500));
    data.Runways.Add(new AirportRunway { Lat = pla, Lon = plo, ElevationM = 10, Heading = 90, LengthM = 3000, WidthM = 45, PrimaryNumber = 9, PrimaryDesignator = 1, SecondaryNumber = 27, SecondaryDesignator = 2 });
    Laia.Common.Airports.ProcedureLeg Fix(string name, double x, double y, int type = 18)
    {
        var (la, lo) = map.ToLatLon(new V2(x, y));
        return new() { Type = type, Fix = name, Lat = la, Lon = lo };
    }
    data.Arrivals.Add(new() { Name = "NORTH1", Legs = { Fix("NORRR", 0, 40 * nm, 15), Fix("NORMD", 8 * nm, 20 * nm) } });
    data.Arrivals.Add(new() { Name = "SOUTH1", Legs = { Fix("SOURR", 0, -40 * nm, 15), Fix("SOUMD", 8 * nm, -20 * nm) } });
    var far = new Dictionary<string, (double, double)?> { ["KNOR"] = map.ToLatLon(new V2(0, 300 * nm)), ["KSOU"] = map.ToLatLon(new V2(0, -300 * nm)) };
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 4)
    {
        Automatic = false, Wind = new Wind(270, 10), Locate = c => far.TryGetValue(c, out var p) ? p : null,
    };
    world.SelectRunways(true);
    world.AtisText = "TEST ARR INFO A. ILS RWY 27 AND RWY 27R APCHS IN USE. DEPG RWY 27.";
    Check(string.Join(",", world.ArrRwys.Select(e => e.Ident).OrderBy(x => x)) == "27,27R", $"both landing runways the ATIS names are used ({string.Join(", ", world.ArrRwys.Select(e => e.Ident))})");
    Laia.Common.Airports.ScheduleLeg Leg(string from, int n) => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = "N" + n, Flight = n.ToString(), Dep = from, Arr = "TEST" };
    var fromNorth = Enumerable.Range(0, 4).Select(i => world.SpawnArrival(30 + i * 6, false, Leg("KNOR", 100 + i))!).ToList();
    var fromSouth = Enumerable.Range(0, 4).Select(i => world.SpawnArrival(30 + i * 6, false, Leg("KSOU", 200 + i))!).ToList();
    Console.WriteLine($"  from the north: {string.Join(" | ", fromNorth.Select(f => f.Rwy!.Ident + " " + f.Route))}");
    Console.WriteLine($"  from the south: {string.Join(" | ", fromSouth.Select(f => f.Rwy!.Ident + " " + f.Route))}");
    Check(fromNorth.All(f => f.Route.StartsWith("NORTH1")) && fromSouth.All(f => f.Route.StartsWith("SOUTH1")), "each arrival gets the STAR from its own direction");
    Check(fromNorth.Count(f => f.Rwy!.Ident == "27R") >= 2 && fromSouth.Count(f => f.Rwy!.Ident == "27") >= 2, "arrivals land on the runway on their side, shared out between the two");
    Check(world.ApproachInUse is { } ai && ai.Contains("27R") && ai.Contains("ILS 27"), $"an approach for each landing runway ({world.ApproachInUse})");
    // Departures: the gates here are on a ramp south of both runways, so they leave from the southern runway
    // even when heading north (no crossing the airport), as cargo and FBO ramps do.
    world.AtisText = "TEST ATIS INFO B. ILS RWY 27 AND RWY 27R APCHS IN USE. DEPG RWYS 27, 27R.";
    Laia.Common.Airports.ScheduleLeg Out(int n) => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = "D" + n, Flight = n.ToString(), Dep = "TEST", Arr = "KNOR" };
    var outs = Enumerable.Range(0, 3).Select(i => world.StartDeparture(null, 20, Out(300 + i))).Where(f => f is not null).ToList();
    Console.WriteLine($"  departures from the south ramp: {string.Join(", ", outs.Select(f => f!.Callsign + " " + f.Rwy!.Ident))}");
    Check(outs.Count > 0 && outs.All(f => f!.Rwy!.Ident == "27"), "a ramp outside the runways departs from the runway on its own side");
}

Console.WriteLine("== Vectors to final: downwind, base, a 20–30° intercept 8–10 nm out (no hairpins, no needless long finals)");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    data.Arrivals.Clear(); data.Approaches.Clear();
    var map = new LocalMap(data.Lat, data.Lon);
    Laia.Common.Airports.ProcedureLeg Fix(string name, double x, double y, int type = 18)
    {
        var (la, lo) = map.ToLatLon(new V2(x, y));
        return new() { Type = type, Fix = name, Lat = la, Lon = lo };
    }
    // Like Orlando's ILS 36L "via EXBAN": a downwind transition that hooks 180° onto a 14 nm final.
    data.Arrivals.Add(new() { Name = "WEST1", Legs = { Fix("WESTA", -35 * nm, 8 * nm, 15), Fix("WESTB", -14 * nm, 4 * nm) } });
    data.Approaches.Add(new() { Type = 4, Runway = "27",
        Transitions = { new() { Name = "HOOKX", Legs = { Fix("HOOKX", -10 * nm, 4 * nm, 15), Fix("HOOKY", 10 * nm, 4 * nm), Fix("IF27", 1500 + 14 * nm, 0) } } },
        FinalLegs = { Fix("IF27", 1500 + 14 * nm, 0, 15), Fix("FF27", 1500 + 5 * nm, 0), Fix("RW27", 1500, 0) } });
    // And plain vectors from the far side of the field (no approach transition reaches).
    var data2 = TestAirport.Build(false);
    data2.Arrivals.Clear();
    data2.Arrivals.Add(new() { Name = "EAST1", Legs = { Fix("EASTA", 40 * nm, 20 * nm, 15), Fix("EASTB", 16 * nm, 12 * nm) } });
    foreach (var (name, d) in new[] { ("hooking transition", data), ("vectors", data2) })
    {
        var world = new TrafficWorld(new AirportModel(d), new TestSource(), seed: 6) { Automatic = false, Wind = new Wind(270, 10) };
        world.SelectRunways(true);
        var f = world.SpawnArrival(45, true)!;
        var rwy = f.Rwy!;
        // Walk back from the threshold along the final course to where the aircraft joins it.
        var sJoin = f.ThrS;
        while (sJoin > 200)
        {
            var (p, dir, _) = f.Path!.At(sJoin - 100);
            if (Math.Abs(rwy.Offset(p)) > 0.15 * nm || Math.Abs(Geo2.Diff(dir, rwy.Heading)) > 3) break;
            sJoin -= 100;
        }
        var joinNm = -rwy.Along(f.Path!.At(sJoin).p) / nm;
        var before = f.Path.At(Math.Max(0, sJoin - 0.8 * nm)).dir;
        var cut = Math.Abs(Geo2.Diff(before, rwy.Heading));
        double maxTurn = 0, prev = double.NaN;
        for (var s2 = Math.Max(0, sJoin - 6 * nm); s2 < f.ThrS; s2 += 100)
        {
            var dd = f.Path.At(s2).dir;
            if (!double.IsNaN(prev)) maxTurn = Math.Max(maxTurn, Math.Abs(Geo2.Diff(dd, prev)));
            prev = dd;
        }
        Console.WriteLine($"  {name}: {f.Route}; joins {joinNm:0.0} nm out at {cut:0}°");
        Check(joinNm is >= 7.5 and <= 10.5, $"{name}: joins the final 8–10 nm out ({joinNm:0.0} nm)");
        Check(cut <= 32, $"{name}: intercepts the final at no more than 30° ({cut:0}°)");
    }
}

Console.WriteLine("== Airport rules read from the FAA's AAUP text (any airport, no airport-specific code)");
{
    // The text of Orlando's RNAV Departure AAUP (AL-571), as a PDF text extractor gives it.
    var text = string.Join("\n", new[]
    {
        "5. SPECIFIC INFORMATION: 0700-2300 local runway 36L/R, 35L/R RNAV simultaneous",
        "departures, all RNAV equipped aircraft departing should expect to fly an MCO RNAV DEPARTURE",
        "• Final runway assignments will be issued on initial contact with Ground Control",
        "Departing Runways 35L/R 17L/R",
        "DDANY, MZULO, JEEMY, FATHE - Expect to Depart Runway 35L",
        "Departing Runways 36L/R 18L/R",
        "RDSOX, OSPRY, FSHUN - Expect to Depart Runway 36R",
        "RDSOX, OSPRY,", "FSHUN", "DDANY, MZULO,", "JEEMY, FATHE", "18R 18L", "36L 36R 35L",
    });
    var sids = new[] { "CITR1", "DDANY3", "FATHE4", "FSHUN4", "JEEMY4", "MZULO3", "ORLA6", "OSPRY1", "RDSOX1" };
    var stars = new[] { "GRNCH5", "PRICY5", "SNFLD3", "MUNGI1" };
    var rules = AirportRules.Parse(text, sids, stars, "RNAV DEPARTURE AAUP");
    Console.WriteLine("  " + rules.Describe());
    var east = rules.ForSid("JEEMY4"); var west = rules.ForSid("OSPRY1");
    Check(rules.Rules.Count == 2 && east is not null && string.Join(" ", east.Runways) == "35L 35R 17L 17R" && west is not null && string.Join(" ", west.Runways) == "36L 36R 18L 18R"
        && rules.ForSid("CITR1") is null && rules.ForStar("GRNCH5") is null,
        "Orlando: JEEMY/FATHE/DDANY/MZULO → the east runways, RDSOX/OSPRY/FSHUN → the west (read from the AAUP text)");
    Check(AaupReader_Cycle(new DateTime(2026, 11, 10)) == "2611" && AaupReader_Cycle(new DateTime(2026, 11, 26)) == "2612" && AaupReader_Cycle(new DateTime(2027, 1, 5)) == "2613" && AaupReader_Cycle(new DateTime(2027, 1, 25)) == "2701",
        "the FAA chart cycle is worked out from the date (29 OCT 2026 = 2611)");
    static string AaupReader_Cycle(DateTime d)
    {
        // Same arithmetic as AaupReader.Cycle (the server class isn't compiled into these checks).
        var refStart = new DateTime(2026, 10, 29);
        var n = (int)Math.Floor((d.Date - refStart).TotalDays / 28);
        var start = refStart.AddDays(28 * n);
        var first = start;
        while (first.AddDays(-28).Year == start.Year) first = first.AddDays(-28);
        return $"{start:yy}{(int)((start - first).TotalDays / 28) + 1:00}";
    }
}

Console.WriteLine("== Hand-over: a scheduled arrival taken over from the whole sky ~200 nm out, descending from cruise to land");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var map = new LocalMap(data.Lat, data.Lon);
    var origin = map.ToLatLon(new V2(450 * nm, -150 * nm));   // ~475 nm east-southeast
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 9)
    {
        Automatic = false, Wind = new Wind(270, 10), WeekMinute = 2000,
        Locate = c => c == "KFAR" ? origin : null,
    };
    world.SelectRunways(true);
    // A 475 nm flight: find the departure time that has it about 180 nm out now.
    Laia.Common.Airports.ScheduleLeg Leg(int dep) => new() { Airline = "DAL", Operator = "DAL", Type = "B738", Reg = "N900DL", Flight = "900", Dep = "KFAR", Arr = "TEST", DepMin = dep, ArrMin = dep + 85 };
    Laia.Common.Airports.ScheduleLeg? leg = null; NetPos? np = null;
    for (var dep = 2000 - 80; dep <= 2000; dep++)
    {
        var l = Leg(dep); var p = world.NetworkPosition(l);
        if (p is not null && Geo2.DistanceNm(data.Lat, data.Lon, p.Lat, p.Lon) is > 170 and < 195) { leg = l; np = p; break; }
    }
    Check(leg is not null && np is not null, "the whole sky has the arrival about 180 nm out");
    var f = world.SpawnFromNetwork(leg!, np!)!;
    var startNm = f.ThrS / nm; var startAlt = f.AltFt;
    var startPos = map.ToLatLon(f.Pos);
    Console.WriteLine($"  taken over {Geo2.DistanceNm(data.Lat, data.Lon, startPos.lat, startPos.lon):0} nm out at {startAlt:0} ft, {startNm:0} track nm to go: {f.Route}");
    Check(Geo2.DistanceNm(startPos.lat, startPos.lon, np!.Lat, np.Lon) < 1 && startAlt > 20000, "it starts exactly where the whole sky had it, at its level");
    double over250Low = 0, maxDescent = 0, maxClimb = 0, t = 0; var holds = 0; var landed = false; var prevAlt = f.AltFt;
    var lastHold = f.LastHold;
    while (t < 3 * 3600 && !landed)
    {
        world.Step(0.5); t += 0.5;
        if (f.LastHold != lastHold) { holds++; lastHold = f.LastHold; }
        if (!f.OnGround && f.AltFt < 9800 && f.Ias > 255) over250Low = Math.Max(over250Low, f.Ias);
        var vs = (f.AltFt - prevAlt) / 0.5 * 60; prevAlt = f.AltFt;
        maxDescent = Math.Min(maxDescent, vs); maxClimb = Math.Max(maxClimb, vs);
        if (f.OnGround) landed = true;
    }
    Console.WriteLine($"  landed after {t / 60:0} min; steepest descent {-maxDescent:0} ft/min, highest climb {maxClimb:0} ft/min");
    Check(landed, "it flies all the way in and lands");
    Check(over250Low == 0, $"250 kt or less below 10,000 ft ({over250Low:0} kt)");
    Check(maxClimb < 300 && -maxDescent < 4000, "it only descends, at airliner rates");
    Check(holds == 0, "no holding far out on its own");
    var landAt = world.Time;
    Console.WriteLine($"  landing time predicted 10 min out: {landAt - f.Pred10:+0;-0} s off; 5 min out: {landAt - f.Pred5:+0;-0} s off; first prediction {(double.IsNaN(f.Pred30) ? "after 30 min out" : $"30 min out: {landAt - f.Pred30:+0;-0} s off")}");
    Check(!double.IsNaN(f.Pred10) && Math.Abs(landAt - f.Pred10) < 45 && !double.IsNaN(f.Pred5) && Math.Abs(landAt - f.Pred5) < 30,
        "the landing time is predicted within 45 s from 10 minutes out and 30 s from 5 minutes out (no other traffic)");
}

Console.WriteLine("== Neighbouring fields: runway traffic only, on the main airport's clock; satellite STARs are a last resort");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var map = new LocalMap(data.Lat, data.Lon);
    var far = map.ToLatLon(new V2(-300 * nm, 60 * nm));
    var sched = new Laia.Common.Airports.ScheduleData { Airport = "TEST", Source = "AIG" };
    sched.Legs.Add(new() { Airline = "JBU", Operator = "JBU", Type = "A320", Reg = "N501JB", Flight = "501", Dep = "TEST", Arr = "KFAR", DepMin = 1990, ArrMin = 2060 });
    var clock = 2000.0;
    var nb = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 2)
    {
        RunwayOnly = true, Level = TrafficLevel.Busy, Wind = new Wind(270, 10), WeekMinute = 2000, ClockFrom = () => clock,
        Locate = c => c == "KFAR" ? far : null, Schedule = sched,
    };
    nb.SelectRunways(true);
    Laia.Server.TrafficCore.Flight? dep = null; var airborne = false; var gone = false;
    for (var t = 0.0; t < 1800 && !gone; t += 0.5)
    {
        clock = 2000 + t / 60 + 3;                        // 13–16 minutes after its gate time: it appears lined up
        nb.WeekMinute = clock;
        nb.Step(0.5);
        dep ??= nb.Flights.FirstOrDefault(f => f.Callsign == "JBU501");
        if (dep is not null && !dep.OnGround) airborne = true;
        if (dep is not null && airborne && !nb.Flights.Contains(dep)) gone = true;
    }
    Check(dep is not null && dep.Spot is null, "a neighbour's departure appears lined up on the runway (no gate, no taxi)");
    Check(airborne && gone, "it takes off and climbs away into the whole sky");
    // Satellite STARs: an arrival from the south-east would pick ARRVL1, but if a neighbour also publishes it, it's a last resort.
    var data2 = TestAirport.Build(false);
    var map2 = new LocalMap(data2.Lat, data2.Lon);
    Laia.Common.Airports.ProcedureLeg Fx(string name, double x, double y, int type = 18) { var (la, lo) = map2.ToLatLon(new V2(x, y)); return new() { Type = type, Fix = name, Lat = la, Lon = lo }; }
    data2.Arrivals.Add(new() { Name = "OWNED2", Legs = { Fx("OWNAA", 20 * nm, -40 * nm, 15), Fx("OWNBB", 12 * nm, -18 * nm) } });
    var se = map2.ToLatLon(new V2(300 * nm, -300 * nm));
    var w2 = new TrafficWorld(new AirportModel(data2), new TestSource(), seed: 3) { Automatic = false, Wind = new Wind(270, 10), Locate = c => c == "KSEE" ? se : null };
    w2.SelectRunways(true);
    Laia.Common.Airports.ScheduleLeg L2(int n) => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = "N" + n, Flight = n.ToString(), Dep = "KSEE", Arr = "TEST" };
    var before = w2.SpawnArrival(30, false, L2(1))!.Route;
    w2.NeighbourStars = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ARRVL" };
    var after = w2.SpawnArrival(36, false, L2(2))!.Route;
    Console.WriteLine($"  from the south-east: {before}  |  with ARRVL1 also published at a neighbour: {after}");
    Check(before.StartsWith("ARRVL1") && after.StartsWith("OWNED2"), "a STAR a neighbouring field also publishes is only used when nothing else fits");
}

Console.WriteLine("== After a go-around, the aircraft comes back around and lands normally");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var world = new TrafficWorld(apt, new TestSource(), seed: 12) { Automatic = false, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s => log.Add($"{TimeSpan.FromSeconds(world.Time):mm\\:ss} {s}");
    world.SelectRunways(true);
    var f = world.SpawnArrival(4, true)!;
    while ((f.ThrS - f.S) / Geo2.MPerNm > 0.6 && world.Time < 300) world.Step(0.25);
    world.GoAround(f, "test");
    var trace = new List<string>();
    for (var i = 0; i < 25 * 60 * 4 && !f.OnGround && !f.Done; i++)
    {
        world.Step(0.25);
        if (i % 240 == 0) trace.Add($"{f.Phase} {(f.ThrS - f.S) / Geo2.MPerNm:0.0} nm {f.AltFt:0} ft {f.Ias:0} kt");
    }
    var again = log.Count(l => l.Contains("going around")) - 1;
    Console.WriteLine("  " + string.Join(" | ", trace.Take(14)));
    foreach (var l in log.Where(l => l.Contains("going around") || l.Contains("landed"))) Console.WriteLine("    " + l);
    Check(again == 0, $"no second go-around ({again})");
    Check(f.OnGround, "it lands after coming back around");
}

Console.WriteLine("== Peak-hour test setting replays the busiest hours now");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var sched = new Laia.Common.Airports.ScheduleData { Airport = "TEST", Source = "AIG" };
    const int now = 3 * 1440 + 180;                 // a quiet night
    const int rush = 4 * 1440 + 900;                // the next afternoon's rush
    for (var i = 0; i < 8; i++)
        sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = $"N{i}PK", Flight = $"{700 + i}", Dep = "TEST", Arr = "KYYY", DepMin = rush + 10 + i * 6, ArrMin = rush + 150 });
    var world = new TrafficWorld(apt, new TestSource(), seed: 9) { Level = TrafficLevel.Peak, Wind = new Wind(270, 8), Schedule = sched, WeekMinute = now };
    var log = new List<string>(); world.Log = s => log.Add(s);
    for (var i = 0; i < 40 * 60 * 4; i++) { world.WeekMinute = now + world.Time / 60; world.Step(0.25); }
    var airborne = log.Count(l => l.Contains("airborne"));
    Console.WriteLine("  " + (world.PeakInfo ?? "(no peak info)") + $"; {airborne} departures in 40 min");
    Check(world.PeakInfo is not null && log.Any(l => l.Contains("busiest 3 hours")), "the peak test finds the busiest hours and says so");
    Check(airborne >= 4, $"the rush is replayed now, at night ({airborne} departures in 40 min)");
}

Console.WriteLine("== Traffic starts even when no installed model fits the small gates");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var world = new TrafficWorld(apt, new BigOnlySource(), seed: 3) { Level = TrafficLevel.Busy, Wind = new Wind(270, 10) };
    var errors = new List<string>();
    world.Log = s => { if (s.Contains("Couldn't") || s.Contains("removed")) errors.Add(s); };
    Exception? crash = null;
    try { for (var i = 0; i < 600; i++) world.Step(0.25); } catch (Exception ex) { crash = ex; }
    Check(crash is null, "no crash" + (crash is null ? "" : ": " + crash.Message));
    Check(world.Flights.Count > 0, $"aircraft were created ({world.Flights.Count})");
}

Console.WriteLine("== The whole sky: scheduled flights fly logically between their airports");
{
    var apts = new Dictionary<string, (double lat, double lon, double elevFt)?>
    {
        ["KJFK"] = (40.6398, -73.7789, 13), ["KLAX"] = (33.9425, -118.4081, 125), ["KLGA"] = (40.7769, -73.8740, 21),
        ["KBOS"] = (42.3643, -71.0052, 20), ["EGLL"] = (51.4706, -0.4619, 83),
    };
    NetLeg Leg(string dep, string arr, string type, int depMin, int block) => new()
    {
        Airline = "DAL", Operator = "DAL", Type = type, Reg = "N" + depMin, Flight = "0123", Dep = dep, Arr = arr,
        DepMin = depMin, ArrMin = (depMin + block) % 10080, Title = "AIG Delta",
    };
    var west = Leg("KJFK", "KLAX", "A321", 600, 390);
    var east = Leg("KLAX", "KJFK", "A321", 600, 330);
    var hop = Leg("KLGA", "KBOS", "DH8D", 600, 75);
    var wrap = Leg("KJFK", "EGLL", "B763", 10020, 420);
    var orphan = Leg("KJFK", "ZZZZ", "A320", 600, 100);
    var legs = new List<NetLeg> { west, east, hop, wrap, orphan };
    var placed = NetworkModel.Locate(legs, a => apts.TryGetValue(a, out var p) ? p : null);
    Check(placed == 4 && !orphan.Located, $"flights between known airports are placed, the rest left out ({placed} of 5)");
    Check(west.Callsign == "DAL123", "callsign from operator and flight number (DAL123)");

    var mid = NetworkModel.Intermediate(0, 0, 0, 90, 0.5);
    Check(Math.Abs(mid.lat) < 1e-6 && Math.Abs(mid.lon - 45) < 1e-6, "great-circle midpoint is right");

    Check(NetworkModel.At(west, 590) is null && NetworkModel.At(west, 600 + 391) is null, "not in the sky before departure or after arrival");
    Check(NetworkModel.At(west, 605) is null && NetworkModel.At(west, 605, includeGround: true)?.Phase == "taxiing out", "taxiing out first, not airborne at the gate time");
    var wc = NetworkModel.CruiseFt(west); var ec = NetworkModel.CruiseFt(east); var hc = NetworkModel.CruiseFt(hop);
    Check(wc % 2000 == 0 && ec % 2000 == 1000, $"cruise levels on the right semicircle (westbound FL{wc / 100}, eastbound FL{ec / 100})");
    Check(hc is >= 9000 and <= 25000, $"turboprop on a short hop cruises low (FL{hc / 100})");
    var c = NetworkModel.At(west, 600 + 195)!;
    Check(c.Phase == "cruising" && c.AltFt == wc && c.GsKt is > 330 and < 560, $"mid-flight: cruising at FL{c.AltFt / 100}, {c.GsKt:0} kt");

    // Fly the whole leg a quarter-minute at a time: smooth, starting and ending at the airports.
    double maxJump = 0, maxRate = 0, endNm = 99, startAlt = 0; NetPos? prev = null; var first = true;
    for (var t = 600.0; t <= 990; t += 0.25)
    {
        var p = NetworkModel.At(west, t);
        if (p is null) { prev = null; continue; }
        if (first) { startAlt = p.AltFt; first = false; }
        if (prev is not null)
        {
            maxJump = Math.Max(maxJump, Geo2.DistanceNm(prev.Lat, prev.Lon, p.Lat, p.Lon));
            maxRate = Math.Max(maxRate, Math.Abs(p.AltFt - prev.AltFt) / 0.25);
        }
        endNm = Geo2.DistanceNm(p.Lat, p.Lon, 33.9425, -118.4081);
        prev = p;
    }
    Check(maxJump < 2.6, $"no jumps along the route (largest {maxJump:0.00} nm in 15 s)");
    Check(maxRate < 5000, $"climbs and descends at airliner rates (steepest {maxRate:0} ft/min)");
    Check(startAlt < 1500 && endNm < 3, $"takes off from JFK ({startAlt:0} ft) and lands at LAX ({endNm:0.0} nm out at the end)");
    var w = NetworkModel.At(wrap, 30);
    Check(w is not null && w.Phase == "cruising", "a flight leaving late Sunday is still flying after the week rolls over");

    using var ms = new MemoryStream();
    NetworkModel.Write(ms, legs, new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));
    ms.Position = 0;
    var (back, read) = NetworkModel.Parse(ms);
    Check(ms.CanRead, "reading the schedule file leaves the upload open (it's saved afterwards)");
    Check(back.Count == 5 && back[3].DepMin == 10020 && back[3].Type == "B763" && back[0].Flight == "0123" && read.Year == 2026,
        $"the schedule file reads back the same ({back.Count} flights)");
}

Console.WriteLine(failures.Count == 0 ? "TRAFFIC CHECKS PASSED" : $"TRAFFIC CHECKS FAILED: {failures.Count}");
return failures.Count == 0 ? 0 : 1;

/// <summary>Only offers widebodies, and ignores the gate size it's asked for (like a catalog with nothing small).</summary>
sealed class BigOnlySource : IFlightSource
{
    public AircraftIdentity NewAircraft(string airport, int maxGateSize, Random rng, IReadOnlyCollection<string>? airlines = null) => new("UAL", "B772", "UAL" + rng.Next(100, 999), "KXXX");
    public (string callsign, string other) NewFlight(string airport, string airline, bool departure, Random rng, string type = "") => (airline + rng.Next(100, 999), "KYYY");
}

sealed class TestSource : IFlightSource
{
    private static readonly (string a, string t)[] Types = { ("DAL", "B739"), ("UAL", "A320"), ("AAL", "A321"), ("SKW", "E75L"), ("DAL", "A333"), ("UAL", "B772"), ("SWA", "B737") };
    public AircraftIdentity NewAircraft(string airport, int maxGateSize, Random rng, IReadOnlyCollection<string>? airlines = null)
    {
        var ok = Types.Where(x => Perf.For(x.t).GateSize <= maxGateSize).ToArray();
        var (a, t) = ok[rng.Next(ok.Length)];
        return new AircraftIdentity(a, t, a + rng.Next(100, 3000), "KXXX");
    }
    public (string callsign, string other) NewFlight(string airport, string airline, bool departure, Random rng, string type = "") => (airline + rng.Next(100, 3000), "KYYY");
}

/// <summary>A made-up airport: runway 09/27 (3,000 m), a parallel taxiway, six connectors, an apron with 16 gates.</summary>
static class TestAirport
{
    public static AirportData Build(bool swapAxes)
    {
        var map = new LocalMap(40, -75);
        var d = new AirportData { Icao = "TEST", Name = "Test Field", Lat = 40, Lon = -75, ElevationM = 10, MagVar = -12, ScannedUtc = DateTime.UtcNow };
        var (clat, clon) = map.ToLatLon(new V2(0, 0));
        d.Runways.Add(new AirportRunway { Lat = clat, Lon = clon, ElevationM = 10, Heading = 90, LengthM = 3000, WidthM = 45, PrimaryNumber = 9, SecondaryNumber = 27 });
        int Pt(double x, double y, int type = 1)
        {
            d.Points.Add(swapAxes ? new TaxiPoint { Type = type, BiasX = y, BiasZ = x } : new TaxiPoint { Type = type, BiasX = x, BiasZ = y });
            return d.Points.Count - 1;
        }
        void Path(int a, int b, int type = 1) => d.Paths.Add(new TaxiPath { Type = type, Start = a, End = b, WidthM = 23 });
        // Runway centreline points every 300 m.
        var rw = new List<int>();
        for (var x = -1500; x <= 1500; x += 300) rw.Add(Pt(x, 0));
        for (var i = 0; i + 1 < rw.Count; i++) Path(rw[i], rw[i + 1], 2);
        // Parallel taxiway at y = -200, every 300 m.
        var tw = new Dictionary<int, int>();
        for (var x = -1500; x <= 1500; x += 300) tw[x] = Pt(x, -200);
        for (var x = -1500; x < 1500; x += 300) Path(tw[x], tw[x + 300]);
        // Connectors with hold-short points.
        foreach (var x in new[] { -1500, -900, -300, 300, 900, 1500 })
        {
            var hold = Pt(x, -95, 2);
            Path(rw[(x + 1500) / 300], hold);
            Path(hold, tw[x]);
        }
        // Apron lane at y = -280 linked to the taxiway at x = -600, 0, 600 (via the taxiway points nearby).
        var lane = new Dictionary<int, int>();
        for (var x = -600; x <= 600; x += 80) lane[x] = Pt(x, -280);
        for (var x = -600; x < 600; x += 80) Path(lane[x], lane[x + 80]);
        Path(tw[-600], lane[-600]);
        Path(tw[600], lane[600]);
        Path(lane[-40], tw[0]);
        // 16 gates south of the lane, noses pointing south toward the terminal.
        var g = 0;
        for (var x = -600; x <= 600 && g < 16; x += 80, g++)
        {
            d.Parkings.Add(swapAxes
                ? new Parking { Type = 9, Name = 12, Number = g + 1, Heading = 180, RadiusM = 22, BiasX = -350, BiasZ = x }
                : new Parking { Type = 9, Name = 12, Number = g + 1, Heading = 180, RadiusM = 22, BiasX = x, BiasZ = -350 });
            d.Paths.Add(new TaxiPath { Type = 3, Start = lane[x], End = d.Parkings.Count - 1 });
        }
        // Procedures: a SID for both runways and a STAR, plus ILS approaches for both runway ends.
        ProcedureLeg Fix(string name, V2 p, int type = 18, double alt = 0, int desc = 0)
        {
            var (lat, lon) = map.ToLatLon(p);
            return new ProcedureLeg { Type = type, Fix = name, Lat = lat, Lon = lon, Alt1Ft = alt, AltDesc = desc };
        }
        var nm = 1852.0;
        var sid = new Procedure { Name = "TEST1" };
        sid.RunwayTransitions.Add(new Transition { Name = "27", Legs = { new ProcedureLeg { Type = 19, Course = 282, Alt1Ft = 1500, AltDesc = 2 }, Fix("WESTY", new V2(-12 * nm, 6 * nm), 7) } });
        sid.RunwayTransitions.Add(new Transition { Name = "09", Legs = { new ProcedureLeg { Type = 19, Course = 102, Alt1Ft = 1500, AltDesc = 2 }, Fix("EASTY", new V2(12 * nm, 6 * nm), 7) } });
        sid.Legs.Add(Fix("NORTY", new V2(0, 25 * nm)));
        d.Departures.Add(sid);
        var star = new Procedure { Name = "ARRVL1" };
        star.Legs.Add(Fix("FARRR", new V2(30 * nm, -30 * nm), 15, 11000, 2));
        star.Legs.Add(Fix("MIDDL", new V2(18 * nm, -12 * nm), 18, 6000, 2));
        d.Arrivals.Add(star);
        foreach (var (ident, sign) in new[] { ("27", 1.0), ("09", -1.0) })
        {
            var app = new Approach { Type = 4, Runway = ident };
            var thrX = sign * 1500;
            app.Transitions.Add(new Transition { Name = "MIDDL", Legs = { Fix("MIDDL", new V2(18 * nm, -12 * nm), 15), Fix("IF" + ident, new V2(thrX + sign * 11 * nm, 0)) } });
            app.FinalLegs.Add(Fix("IF" + ident, new V2(thrX + sign * 11 * nm, 0), 15, 3000, 2));
            app.FinalLegs.Add(Fix("FF" + ident, new V2(thrX + sign * 6 * nm, 0), 18, 2000, 1));
            app.FinalLegs.Add(Fix("RW" + ident, new V2(thrX, 0), 18));
            d.Approaches.Add(app);
        }
        return d;
    }
}
