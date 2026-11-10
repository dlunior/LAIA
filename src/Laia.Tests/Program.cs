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
    double minSep = double.MaxValue, maxGroundTurnRate = 0, maxBankRate = 0;
    var seenPhases = new HashSet<string>();
    var stuckFlights = new HashSet<string>();
    var prevBank = new Dictionary<string, double>();
    // Rule checks (7110.65, 14 CFR 91.117, AIM).
    int slowFar = 0, slowNear = 0, speedUpInside5 = 0, fastLow = 0, wakeShort = 0;
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
                if (jet && dth > 5.5 && dth <= 20 && f.Ias < Math.Min(169, f.P.Vapp + 30)) { slowNear++; if (slowNear <= 3) log.Add($"SLOW {f.Callsign} {f.Ias:0} kt at {dth:0.0} nm"); }
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
