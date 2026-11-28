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

Console.WriteLine("== A runway change while departures taxi: they take off along the new runway (CYYZ ACA836, 17 Nov)");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var log = new List<string>();
    var world = new TrafficWorld(apt, new TestSource(), seed: 7) { Level = TrafficLevel.Busy, Wind = new Wind(270, 12) };
    world.Log = s2 => log.Add(s2);
    var changed = false; var checkedDeps = new HashSet<string>(); var bad = new List<string>(); var movedAndFlown = 0;
    var taxiingAtChange = new HashSet<string>();
    for (var t = 0.0; t < 3600; t += 0.5)
    {
        world.Step(0.5);
        if (!changed && t > 900 && world.Flights.Count(f => f.Kind == "departure" && f.Phase is "taxi" or "holding short") >= 1)
        {
            changed = true;
            foreach (var f in world.Flights.Where(f => f.Kind == "departure" && f.Phase is "taxi" or "holding short" or "pushback" or "engine start")) taxiingAtChange.Add(f.Callsign);
            world.Wind = new Wind(90, 15); world.SelectRunways(true);
        }
        foreach (var f in world.Flights)
        {
            if (f.Kind != "departure" || f.OnGround || f.Rwy is not { } r || checkedDeps.Contains(f.Callsign)) continue;
            if (f.AltFt - apt.ElevationFt < 400) continue;
            checkedDeps.Add(f.Callsign);
            var off = Math.Abs(r.Offset(f.Pos)); var along = r.Along(f.Pos);
            if (taxiingAtChange.Contains(f.Callsign)) movedAndFlown++;
            if (off > 250 || along < r.LengthM * 0.5) bad.Add($"{f.Callsign} off {r.Ident}: {off:0} m aside, {along:0} m along at {f.AltFt - apt.ElevationFt:0} ft");
        }
    }
    var warn = log.Where(l => l.Contains("WARNING starting the take-off roll")).ToList();
    Console.WriteLine($"  runway change at {(changed ? "yes" : "no")}; {taxiingAtChange.Count} departures on the ground then, {movedAndFlown} of them flown; {checkedDeps.Count} departures checked; {string.Join("; ", bad.Concat(warn).Take(4))}");
    Check(changed && movedAndFlown > 0, "departures caught taxiing by the runway change took off");
    Check(bad.Count == 0 && warn.Count == 0, "every departure climbs out along its own runway's centreline");
}

Console.WriteLine("== One hour of busy traffic, runway 27 in use (wind 270/12)");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var log = new List<string>();
    var world = new TrafficWorld(apt, new TestSource(), seed: 42) { Level = TrafficLevel.Busy, Wind = new Wind(270, 12), SafetyLogic = true };
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
        // Runway: at most one aircraft on it (departures that have lifted off don't count) — except one lining up or
        // waiting behind a take-off roll or a landing rolling out ahead of it, the same way (line up and wait, 3-9-4).
        bool LuawPair(IGrouping<int, Flight> g)
        {
            var waiting = g.Where(f => f.Kind == "departure" && f.Phase is "lining up" or "lined up").ToList();
            if (waiting.Count != 1) return false;
            var w = waiting[0];
            return g.Where(f => f != w).All(o => o.Phase is "takeoff roll" or "landing roll" && o.Rwy is { } r && w.Rwy is { } wr
                && Math.Abs(Geo2.Diff(r.Heading, wr.Heading)) < 90 && wr.Along(o.Pos) > wr.Along(w.Pos) + 200);
        }
        var onRwy = fl.Where(f => f.OnRunway >= 0 && !(f.Kind == "departure" && !f.OnGround)).GroupBy(f => f.OnRunway).Where(g => g.Count() > 1 && !LuawPair(g)).ToList();
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
    var luawGoArounds = log.Count(l => l.Contains("going around") && l.Contains("lined up"));
    Console.WriteLine($"  long waits lined up: {log.Count(l => l.Contains(": lined up on "))}");
    Check(luawGoArounds == 0, $"no arrival goes around for a departure lined up on the runway (CYYZ, 17 Nov) ({luawGoArounds})");
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
    Check(Cwt.MilesBehind('A', 'B') == 5 && Cwt.MilesBehind('A', 'H') == 8 && Cwt.MilesBehind('C', 'H') == 5 && Cwt.For("CRJ9", Perf.For("CRJ9")) == 'G' && Cwt.For("DH8D", Perf.For("DH8D")) == 'G' && Cwt.MilesBehind('H', 'I') == 3,
        "the 7110.65 table as published (A→B 5, A→H 8, C→H 5; F–I behind anything small: radar minimum); CRJ900 and Q400 Lower Large");
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

Console.WriteLine("== Crossing runways at Busy for an hour: land 27, depart 36 across it (LaGuardia-style)");
{
    var data = TestAirport.Build(false);
    var map = new LocalMap(40, -75);
    // Runway 18/36 crossing 27 300 m past 27's threshold, with a taxiway to 36's start and hold lines both sides of
    // it on the parallel taxiway.
    var (clat, clon) = map.ToLatLon(new V2(1200, 0));
    data.Runways.Add(new AirportRunway { Lat = clat, Lon = clon, ElevationM = 10, Heading = 0, LengthM = 2500, WidthM = 45, PrimaryNumber = 36, SecondaryNumber = 18 });
    int Find(double x, double y) => data.Points.FindIndex(q => Math.Abs(q.BiasX - x) < 1 && Math.Abs(q.BiasZ - y) < 1);
    int Pt(double x, double y, int type = 1) { data.Points.Add(new TaxiPoint { Type = type, BiasX = x, BiasZ = y }); return data.Points.Count - 1; }
    void Path(int a, int b, int type = 1) => data.Paths.Add(new TaxiPath { Type = type, Start = a, End = b, WidthM = 23 });
    int tw900 = Find(900, -200), tw1200 = Find(1200, -200), tw1500 = Find(1500, -200), rw1200 = Find(1200, 0);
    data.Paths.RemoveAll(q => q.Type == 1 && ((q.Start == tw900 && q.End == tw1200) || (q.Start == tw1200 && q.End == tw1500)));
    int hW = Pt(1140, -200, 2), hE = Pt(1260, -200, 2), t1 = Pt(1350, -200);
    Path(tw900, hW); Path(hW, tw1200); Path(tw1200, hE); Path(hE, t1); Path(t1, tw1500);
    var south = new[] { -500.0, -800, -1100, -1250 }.Select(y => Pt(1350, y)).ToList();
    Path(t1, south[0]); for (var i = 0; i + 1 < south.Count; i++) Path(south[i], south[i + 1]);
    var hold36 = Pt(1270, -1250, 2);
    var r36 = new[] { -1250.0, -1000, -750, -500 }.Select(y => Pt(1200, y)).Concat(new[] { tw1200, rw1200 }).Concat(new[] { 300.0, 600, 900, 1250 }.Select(y => Pt(1200, y))).ToList();
    for (var i = 0; i + 1 < r36.Count; i++) Path(r36[i], r36[i + 1], 2);
    Path(south[^1], hold36); Path(hold36, r36[0]);
    var apt = new AirportModel(data);
    var world = new TrafficWorld(apt, new TestSource(), seed: 21) { Level = TrafficLevel.Busy, Wind = new Wind(300, 15), SafetyLogic = true };
    var log = new List<string>(); world.Log = l => log.Add($"{world.Time:0} {l}");
    world.SelectRunways(true);
    var e27 = apt.Ends.First(e => e.Ident == "27"); var e36 = apt.Ends.First(e => e.Ident == "36");
    var xing = new V2(1200, 0);
    var conflicts = new List<string>(); var deps36 = new HashSet<string>(); var maxHold = 0.0; string maxHoldWho = "";
    var holdSince = new Dictionary<string, double>();
    for (var t = 0.0; t < 3600; t += 0.5)
    {
        world.Step(0.5);
        foreach (var f in world.Flights)
        {
            if (f.Kind == "departure" && f.Rwy?.Ident == "36")
            {
                if (f.Phase == "takeoff roll") deps36.Add(f.Callsign);
                if (f.Phase is "holding short" or "lined up") { holdSince.TryAdd(f.Callsign, world.Time); var w0 = world.Time - holdSince[f.Callsign]; if (w0 > maxHold) { maxHold = w0; maxHoldWho = f.Callsign; } }
                else holdSince.Remove(f.Callsign);
                // 3-10-4: a departure rolling through the intersection with an arrival over the 27 threshold or rolling
                // towards the intersection.
                if (f.Phase == "takeoff roll" && Math.Abs(e36.Along(f.Pos) - e36.Along(xing)) < 60)
                    foreach (var a in world.Flights.Where(a => a.Kind == "arrival" && a.Rwy?.Ident == "27" && !a.Done))
                    {
                        var dA = (a.Pos - xing).Length;
                        if ((a.OnGround && a.OnRunway == e27.Runway && e27.Along(a.Pos) < e27.Along(xing) + 30) || (!a.OnGround && dA < 1500 && a.AltFt - apt.ElevationFt < 600))
                            conflicts.Add($"{world.Time:0} {f.Callsign} through the intersection with {a.Callsign} {(a.OnGround ? "rolling" : "over the threshold")} {dA:0} m away");
                    }
            }
        }
    }
    var gaXing = log.Count(l => l.Contains("departing the crossing runway"));
    var landed = log.Count(l => l.Contains(": landed runway 27"));
    Console.WriteLine($"  runways {world.ArrRwy?.Ident}/{world.DepRwy?.Ident}; {landed} landed on 27, {deps36.Count} took off from 36; longest wait at 36 {maxHold:0} s ({maxHoldWho}); go-arounds for a crossing departure {gaXing}; conflicts {conflicts.Count}");
    foreach (var c in conflicts.Take(3)) Console.WriteLine("    " + c);
    if (conflicts.Count > 0)
    {
        var who = conflicts[0].Split(' ');
        foreach (var l in log.Where(l => l.Contains(who[1]) || l.Contains(who[6]) || l.Contains("letting")).Take(30)) Console.WriteLine("    D " + l);
    }
    Check(world.ArrRwy?.Ident == "27" && world.DepRwy?.Ident == "36", "land 27, depart the crossing runway 36");
    Check(deps36.Count >= 4 && landed >= 6, $"both runways work: departures go between the arrivals ({deps36.Count} departures, {landed} arrivals)");
    Check(conflicts.Count == 0, "no departure through the intersection while an arrival is over the threshold or rolling towards it");
    Check(maxHold < 600, $"no departure held at 36 for 10 minutes or more ({maxHold:0} s)");
    Check(gaXing <= 1, $"go-arounds for a crossing departure stay rare ({gaXing})");
    // Traffic management is for overload only (user, 26 Nov: "how can we make it so LAIA doesn't abuse it?"): an hour at
    // Busy sets no miles-in-trail, and gives few vectors.
    var tmi = log.Count(l => l.Contains("Traffic management:") && !l.Contains("lifted"));
    var vecs = log.Count(l => l.Contains("vectors for spacing"));
    var landings = Math.Max(1, log.Count(l => l.Contains(": landed runway")));
    Console.WriteLine($"  traffic management restrictions {tmi}; vectors for spacing {vecs} for {landings} landings");
    Check(tmi == 0, "an hour at Busy needs no miles-in-trail");
    Check(vecs * 10.0 / landings <= 2, "and at most 2 vectors for spacing per 10 landings");
}

Console.WriteLine("== The learned ways in: a model trained by learning/train_routes.py, run by LAIA (Step 6, part 2)");
if (Environment.GetEnvironmentVariable("LAIA_TEST_MODEL") is { } mpath && RouteModel.Load(mpath) is { } rm)
{
    var nm = Geo2.MPerNm;
    var apt = new AirportModel(TestAirport.Build(false));
    var e27 = apt.Ends.First(e => e.Ident == "27");
    var calm = new Wind(0, 0);
    // Straight in from the east: the way it gives stays on the centreline.
    var eastAt = RouteModel.FromFrame(e27, 40, 0);
    var east = rm.Predict("TEST", e27, RouteModel.Features(e27, eastAt, 12000, e27.Heading, calm, 4, 8))[0];
    var eastOff = east.Points.Max(q => Math.Abs(e27.Offset(q))) / nm;
    var eastEnd = -e27.Along(east.Points[^1]) / nm;
    Console.WriteLine($"  from the east, 40 nm out: {rm.K} ways, the likeliest {100 * east.Prob:0}%, at most {eastOff:0.0} nm off the centreline, ending {eastEnd:0.0} nm out");
    Check(eastOff < 2.5 && Math.Abs(eastEnd) < 2, "straight in from the east, it stays on the centreline to the runway");
    // From the west, beyond the field: round the downwind (4 nm left) and base at about 12 nm.
    var westAt = RouteModel.FromFrame(e27, -30, 5);
    var west = rm.Predict("TEST", e27, RouteModel.Features(e27, westAt, 12000, e27.Heading + 180, calm, 4, 8))[0];
    var downwind = west.Points.Any(q => -e27.Along(q) / nm is > 6 and < 15 && e27.Offset(q) / nm > 2);
    Console.WriteLine($"  from the west, 30 nm beyond the field: the likeliest {100 * west.Prob:0}%, downwind and base {(downwind ? "flown" : "missing")}, ending {-e27.Along(west.Points[^1]) / nm:0.0} nm out");
    Check(downwind, "from the west, it flies the downwind and base the examples did");
    // Flown by LAIA: the way in from the model, through the route check, to the runway.
    var w = new TrafficWorld(apt, new TestSource(), seed: 5) { Automatic = false, Wind = calm, LearnedRoutes = rm };
    var wlog = new List<string>(); w.Log = l => wlog.Add(l);
    w.SelectRunways(true);
    var fw = w.SpawnArrival(0, false, null, (e27, null), entry: (westAt, 12000, 12000));
    var fe = w.SpawnArrival(0, false, null, (e27, null), entry: (eastAt, 12000, 12000));
    Console.WriteLine($"  {fw?.Callsign}: {fw?.Route}");
    Console.WriteLine($"  {fe?.Callsign}: {fe?.Route}");
    Check(fw?.Route.Contains("learned way in") == true && fe?.Route.Contains("learned way in") == true, "LAIA flies the learned way in");
    for (var t = 0.0; t < 2400 && !(fw!.OnGround && fe!.OnGround); t += 0.5) w.Step(0.5);
    Check(fw!.OnGround && fe!.OnGround, $"both reach the runway ({(fw.OnGround ? "west landed" : "west didn't land")}, {(fe.OnGround ? "east landed" : "east didn't land")})");
    foreach (var l in wlog.Where(l => l.Contains("refused") || l.Contains("going around") || l.Contains("breaking off")).Take(4)) Console.WriteLine("    " + l);
}
else Console.WriteLine("  (no trained test model — LAIA_TEST_MODEL not set; skipped)");

Console.WriteLine("== The scoreboard: real arrivals read from their tracks and flown again by LAIA (Step 6)");
{
    var nm = Geo2.MPerNm;
    var apt = new AirportModel(TestAirport.Build(false));
    var world = new TrafficWorld(apt, new TestSource(), seed: 41) { Level = TrafficLevel.Busy, Wind = new Wind(270, 8) };
    world.SelectRunways(true);
    // "Real" tracks: LAIA's own arrivals for 90 minutes, as the Collector keeps them (45 nm, under 18,000 ft, every 5 s).
    var t0 = 1795800000.0;
    var tracks = new Dictionary<string, List<TrackPoint>>(); var lastT = new Dictionary<string, double>();
    for (var t = 0.0; t < 5400; t += 0.5)
    {
        world.Step(0.5);
        foreach (var f in world.Flights.Where(f => f.Kind == "arrival" && !f.Done && (!f.OnGround || f.Gs > 40)))
        {
            if (f.Pos.Length > 45 * nm || f.AltFt - apt.ElevationFt > 18000) continue;
            if (lastT.TryGetValue(f.Id, out var lt) && world.Time - lt < 5) continue;
            lastT[f.Id] = world.Time;
            var (la, lo) = apt.Map.ToLatLon(f.Pos);
            if (!tracks.TryGetValue(f.Id, out var tl)) tracks[f.Id] = tl = new();
            tl.Add(new TrackPoint(t0 + world.Time, la, lo, f.OnGround ? apt.ElevationFt : f.AltFt, f.Heading));
        }
    }
    var real = RealReplay.Arrivals(apt, tracks.Values.Cast<IReadOnlyList<TrackPoint>>().ToList());
    var landedReal = world.Flights.Count(f => f.Kind == "arrival" && (f.OnGround || f.Done));
    Console.WriteLine($"  {tracks.Count} tracks, {real.Count} read as arrivals ({string.Join(", ", real.GroupBy(a => a.Runway).Select(g => $"{g.Key}: {g.Count()}"))}); {landedReal} landed in the run");
    Check(real.Count >= 8, $"the arrivals are read from their tracks ({real.Count})");
    Check(real.All(a => a.TrackNm > 15 && a.ThrT > a.EntryT), "each with where it came in, its threshold time and its track miles");
    var (start, count) = RealReplay.BusiestHour(real);
    var sc = RealReplay.Replay(apt, null, new Wind(270, 8), real, start, start + 3600, new TestSource());
    Console.WriteLine("  " + sc.Line());
    foreach (var wl in sc.Worst) Console.WriteLine("    " + wl);
    Check(sc.Scored >= 6 && sc.Landed >= sc.Scored - 1, $"flown again, they reach the runway ({sc.Landed} of {sc.Scored})");
    Check(sc.OrderSame >= 0.75, $"LAIA flying its own traffic again puts most in the same order ({100 * sc.OrderSame:0}%)");
    Check(sc.TimeErr < 150 && sc.PathNm < 4, $"and lands them about when and where they flew ({sc.TimeErr:0} s, {sc.PathNm:0.0} nm)");
}

Console.WriteLine("== Learned ATC (Release 4b): speeds, landing order and gaps learned by learning/train_atc.py, flown by LAIA");
if (Environment.GetEnvironmentVariable("LAIA_TEST_ATC") is { } apath && AtcModel.Load(apath) is { } atc)
{
    // The habits in the synthetic data: 250 kt beyond 20 nm, 210 to 12; 75 % of that with time to lose; heavies
    // landing 2 minutes after their own time would put them; 90 s behind a narrowbody when pressed.
    var v30 = atc.NominalIas("TEST", 1, 30); var v15 = atc.NominalIas("TEST", 1, 15); var vg = atc.NominalIas("ZZZZ", 1, 30);
    Console.WriteLine($"  model {atc.Id}: unimpeded {v30:0} kt at 30 nm, {v15:0} at 15 (general {vg:0}); gap behind a narrowbody {atc.TargetGap("TEST", 1, 1):0} s");
    Check(Math.Abs(v30 - 250) < 15 && Math.Abs(v15 - 210) < 15 && Math.Abs(vg - 250) < 15, "the unimpeded speeds are learned, per airport and across the hubs");
    Check(Math.Abs(atc.TargetGap("TEST", 1, 1) - 90) < 10, "the gap real controllers leave behind a narrowbody when pressed is learned");
    var rSlow = atc.SpeedRatio("TEST", 1, 30, 9000, 240, 10); var rFree = atc.SpeedRatio("TEST", 1, 30, 9000, 0, 10);
    Console.WriteLine($"  speed control at 30 nm: {100 * rSlow:0}% of the unimpeded speed with 4 minutes to lose, {100 * rFree:0}% with none");
    Check(rSlow < rFree - 0.08, "with time to lose, it's flown slower");
    var oH = atc.OrderOffset("TEST", 2, 35, 10000, 300, 0, 1, 0, 10); var oN = atc.OrderOffset("TEST", 1, 35, 10000, 300, 0, 1, 0, 10);
    Console.WriteLine($"  landing order: a heavy {oH:+0;-0} s on its own time, a narrowbody {oN:+0;-0} s");
    Check(oH - oN > 45, "heavies are put later in the order, as the real controllers did");
    // Flown: an hour at Busy with the learned controller, against the same hour by the rules.
    var apt = new AirportModel(TestAirport.Build(false));
    (int landed, int ga, int bo, List<string> log) Run(AtcModel? m)
    {
        var w = new TrafficWorld(apt, new TestSource(), seed: 23) { Level = TrafficLevel.Busy, Wind = new Wind(270, 8) };
        var l = new List<string>(); w.Log = x => l.Add(x);
        w.Atc = m; w.AtcAirport = "TEST";
        w.SelectRunways(true);
        for (var t = 0.0; t < 3600; t += 0.5) w.Step(0.5);
        return (l.Count(x => x.Contains(": landed runway")), l.Count(x => x.Contains("going around")), l.Count(x => x.Contains("breaking off")), l);
    }
    var byRules = Run(null); var learned = Run(atc);
    Console.WriteLine($"  an hour at Busy: by the rules {byRules.landed} landings, {byRules.ga} go-arounds, {byRules.bo} break-offs; learned ATC {learned.landed}, {learned.ga}, {learned.bo}");
    Check(learned.log.Any(x => x.Contains("learned ATC") && x.Contains("in use")), "the world says the learned ATC is in use");
    Check(learned.landed >= byRules.landed * 0.8, "the learned controller lands about as many");
    Check(learned.ga + learned.bo <= byRules.ga + byRules.bo + 2, "with no more go-arounds and break-offs to speak of");
    foreach (var x in learned.log.Where(x => x.Contains("going around") || x.Contains("breaking off") || x.Contains("SEPARATION")).Take(4)) Console.WriteLine("    " + x);
}
else Console.WriteLine("  (no trained test model — LAIA_TEST_ATC not set; skipped)");

Console.WriteLine("== The route check (27 Nov, KSFO: UAL436 flew a circle by the 28R final; loops every release)");
{
    var nm = Geo2.MPerNm;
    var apt = new AirportModel(TestAirport.Build(false));
    var e27 = apt.Ends.First(e => e.Ident == "27");
    var thr = e27.Threshold; var d = e27.Dir; var left = new V2(-d.Y, d.X);
    V2 P(double alongNm, double leftNm) => thr + d * (alongNm * nm) + left * (leftNm * nm);
    // A normal way in from the east: downwind beside the runway going east, base, final 10 nm out.
    var normal = new List<V2> { P(-40, 15), P(-20, 5), P(0, 5), P(8, 5), P(12, 3), P(-11, 0), P(-6, 0) };
    // (the downwind runs the opposite way to landing: along grows going east for a runway landing west)
    normal = new List<V2> { P(25, 20), P(10, 5), P(-5, 5), P(-12, 5), P(-12, 0), P(-6, 0) };
    var ti = normal.Count; normal.Add(thr);
    var r1 = TrafficWorld.RouteProblem(normal, ti, e27);
    Check(r1 is null, $"a downwind, base and final passes ({r1 ?? "ok"})");
    // UAL436's: in towards the final, 4 nm out on it, then north across, east, south across its own way in, back on final 13 nm out.
    var loop = new List<V2> { P(-30, -8), P(-8, -2), P(-4, 0), P(-4, 4), P(-12, 4), P(-14, -5), P(-13, 0), P(-6, 0) };
    var t2 = loop.Count; loop.Add(thr);
    var r2 = TrafficWorld.RouteProblem(loop, t2, e27);
    Check(r2 is not null, $"UAL436's circle by the final is refused ({r2})");
    // On the final, then off it and back.
    var leave = new List<V2> { P(-24, 0), P(-15, 0), P(-12, 2.5), P(-9, 0), P(-5, 0) };
    var t3 = leave.Count; leave.Add(thr);
    var r3 = TrafficWorld.RouteProblem(leave, t3, e27);
    Check(r3 is not null && r3.Contains("leaves its final"), $"leaving the final and coming back is refused ({r3})");
    // Far too long: 30 nm out, 120 nm of zigzag.
    var zig = new List<V2> { P(-30, 0) };
    for (var k = 1; k <= 8; k++) zig.Add(P(-30 - (k % 2) * 14, (k % 2 == 0 ? 1 : -1) * 0.2 + k * 0.01));
    zig.Add(P(-8, 0));
    var t4 = zig.Count; zig.Add(thr);
    var r4 = TrafficWorld.RouteProblem(zig, t4, e27);
    Check(r4 is not null, $"a way in far longer than it needs is refused ({r4})");
}

Console.WriteLine("== Landing on two crossing runways (27 Nov, KPIT: AAL814 on 32 crossed its threshold 4 s after the user passed the 28L/32 intersection)");
{
    var data = TestAirport.Build(false);
    var map = new LocalMap(40, -75);
    // Runway 18/36 crossing 27 300 m past 27's threshold (as 32 crosses 28L at Pittsburgh); both used for landings.
    var (clat, clon) = map.ToLatLon(new V2(1200, 0));
    data.Runways.Add(new AirportRunway { Lat = clat, Lon = clon, ElevationM = 10, Heading = 0, LengthM = 2500, WidthM = 45, PrimaryNumber = 36, SecondaryNumber = 18 });
    var apt = new AirportModel(data);
    var world = new TrafficWorld(apt, new TestSource(), seed: 27) { Level = TrafficLevel.Busy, Wind = new Wind(320, 8) };
    var log = new List<string>(); world.Log = l => log.Add($"{world.Time:0} {l}");
    world.AtisText = "TEST ARR INFO Q. ILS RWY 27 AND RWY 36 APCHS IN USE. DEPG RWY 27.";
    world.SelectRunways(true);
    var e27 = apt.Ends.First(e => e.Ident == "27"); var e36 = apt.Ends.First(e => e.Ident == "36");
    var xing = new V2(1200, 0);
    Console.WriteLine($"  landing {string.Join(", ", world.ArrRwys.Select(e => e.Ident))}");
    // Each arrival's threshold time, and when it passed the intersection (or left the runway before it).
    var thr = new Dictionary<string, (string rwy, double t)>(); var passed = new Dictionary<string, double>();
    var prevAlong = new Dictionary<string, double>();
    for (var t = 0.0; t < 3600; t += 0.5)
    {
        world.Step(0.5);
        foreach (var f in world.Flights.Where(f => f.Kind == "arrival" && f.Rwy is not null && (f.Rwy == e27 || f.Rwy == e36)))
        {
            var along = f.Rwy!.Along(f.Pos);
            var key = f.Callsign + "#" + (f.WentAround ? "2" : "1");
            if (prevAlong.TryGetValue(key, out var pa) && pa < 0 && along >= 0 && Math.Abs(f.Rwy.Offset(f.Pos)) < 200 && f.AltFt - apt.ElevationFt < 200) thr[key] = (f.Rwy.Ident, world.Time);
            var x = f.Rwy == e27 ? e27.Along(xing) : e36.Along(xing);
            if (thr.ContainsKey(key) && !passed.ContainsKey(key) && (along > x + 30 || (f.OnGround && f.OnRunway != f.Rwy.Runway))) passed[key] = world.Time;
            prevAlong[key] = along;
        }
    }
    // 3-10-4: when an arrival crosses its threshold, every arrival that crossed the other runway's threshold before it
    // has passed the intersection (or turned off before it).
    var conflicts = new List<string>();
    foreach (var (k, a) in thr)
        foreach (var (k2, b) in thr)
            if (a.rwy != b.rwy && b.t <= a.t && (!passed.TryGetValue(k2, out var pt) || pt > a.t))
                conflicts.Add($"{k.Split('#')[0]} over the {a.rwy} threshold at {a.t:0} with {k2.Split('#')[0]} ({b.rwy}, threshold {b.t:0}) not yet through the intersection");
    var on27 = thr.Count(x => x.Value.rwy == "27"); var on36 = thr.Count(x => x.Value.rwy == "36");
    var gaX = log.Count(l => l.Contains("landing the crossing runway"));
    var refused = log.Count(l => l.Contains("route refused"));
    Console.WriteLine($"  {on27} landed on 27, {on36} on 36; conflicts {conflicts.Count}; go-arounds for a crossing arrival {gaX}; routes refused {refused}");
    foreach (var l in log.Where(l => l.Contains("route refused")).Take(5)) Console.WriteLine("    " + l);
    foreach (var c in conflicts.Take(4)) Console.WriteLine("    " + c);
    Check(on27 >= 6 && on36 >= 4, $"both runways take landings ({on27} on 27, {on36} on 36)");
    Check(conflicts.Count == 0, "no arrival crosses its threshold before one landing the crossing runway ahead of it is through the intersection");
    Check(gaX <= 1, $"the approach sequencing staggers the two streams: go-arounds for a crossing arrival stay rare ({gaX})");
}

Console.WriteLine("== The user landing 27 with LAIA's arrivals for the crossing runway 36 timed to meet them (the KPIT case)");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var map = new LocalMap(40, -75);
    var (clat, clon) = map.ToLatLon(new V2(1200, 0));
    data.Runways.Add(new AirportRunway { Lat = clat, Lon = clon, ElevationM = 10, Heading = 0, LengthM = 2500, WidthM = 45, PrimaryNumber = 36, SecondaryNumber = 18 });
    var apt = new AirportModel(data);
    var world = new TrafficWorld(apt, new TestSource(), seed: 31) { Automatic = false, Wind = new Wind(320, 8) };
    var log = new List<string>(); world.Log = l => log.Add($"{world.Time:0} {l}");
    world.AtisText = "TEST ARR INFO Q. ILS RWY 27 AND RWY 36 APCHS IN USE. DEPG RWY 27.";
    world.SelectRunways(true);
    var e27 = apt.Ends.First(e => e.Ident == "27"); var e36 = apt.Ends.First(e => e.Ident == "36");
    var xing = new V2(1200, 0);
    var u = e27.Threshold - e27.Dir * (38 * nm);
    world.UserCallsign = "UAL1196"; world.UserType = "B738"; world.UserDestination = "TEST";
    world.UserOnGround = false; world.UserTrack = e27.Heading; world.UserGs = 160; world.UserAltFt = 6000; world.UserPos = u;
    var ais = new List<Flight>();
    foreach (var d in new[] { 40.0, 46, 52, 58, 64 })
        if (world.SpawnArrival(d, false, null, (e36, null)) is { } a) ais.Add(a);
    double userThr = -1, userPass = -1;
    var thr = new Dictionary<string, double>(); var pass = new Dictionary<string, double>(); var prev = new Dictionary<string, double>();
    for (var t = 0.0; t < 2400; t += 0.5)
    {
        if (userThr < 0 || userPass < 0)
        {
            var left = -e27.Along(u) / nm;
            var kt = left > 20 ? 230 : left > 6 ? 170 : left > 0 ? 135 : 120;
            u += e27.Dir * (kt * Geo2.MpsPerKt * 0.5);
            world.UserGs = kt; world.UserAltFt = Math.Max(apt.ElevationFt, Math.Min(6000, apt.ElevationFt + left * 318));
            if (userThr < 0 && e27.Along(u) >= 0) userThr = world.Time;
            if (userPass < 0 && e27.Along(u) > e27.Along(xing) + 30) { userPass = world.Time; world.UserOnGround = true; }
        }
        world.UserPos = u;
        world.Step(0.5);
        foreach (var f in ais.Where(f => !f.Done && f.Rwy == e36))
        {
            var key = f.Callsign + (f.WentAround ? "#2" : "");
            var al = e36.Along(f.Pos);
            if (prev.TryGetValue(key, out var pa) && pa < 0 && al >= 0 && f.AltFt - apt.ElevationFt < 200) thr[key] = world.Time;
            if (thr.ContainsKey(key) && !pass.ContainsKey(key) && (al > e36.Along(xing) + 30 || (f.OnGround && f.OnRunway != e36.Runway))) pass[key] = world.Time;
            prev[key] = al;
        }
        if (userPass > 0 && ais.All(f => f.Done || f.OnGround)) break;
    }
    var bad = new List<string>();
    foreach (var (k, t36) in thr)
    {
        // It went first: through the intersection before the user crossed the 27 threshold; or after: the user was through first.
        var p36 = pass.TryGetValue(k, out var pp) ? pp : double.PositiveInfinity;
        if (!(p36 <= userThr || (userPass > 0 && userPass <= t36))) bad.Add($"{k}: 36 threshold {t36:0}, through {p36:0}; you: 27 threshold {userThr:0}, through {userPass:0}");
    }
    var ga = log.Count(l => l.Contains("landing the crossing runway"));
    Console.WriteLine($"  you over the 27 threshold at {userThr:0}; 36 thresholds: {string.Join(", ", thr.Select(x => $"{x.Key} {x.Value:0}"))}; go-arounds for you {ga}");
    foreach (var l in log.Where(l => l.Contains("landing after") || l.Contains("crossing runway") || l.Contains("later on 36")).Take(12)) Console.WriteLine("    " + l);
    foreach (var b in bad) Console.WriteLine("    " + b);
    foreach (var gl in log.Where(l => l.Contains("landing the crossing runway")).Take(2))
    {
        var who = gl.Split(' ')[1].TrimEnd(':'); var other = gl.Split('(')[1].Split(' ')[0];
        foreach (var l in log.Where(l => (l.Contains(who) || l.Contains(other)) && !l.Contains("Performance")).TakeLast(25)) Console.WriteLine("    D " + l);
    }
    Check(userThr > 0 && thr.Count >= 2, $"you and LAIA's arrivals land ({thr.Count} on 36)");
    Check(bad.Count == 0, "none of LAIA's arrivals to 36 meets you at the intersection: each is through it before you reach the 27 threshold, or crosses its threshold after you're through");
    Check(ga == 0, $"it's done on the approach, not by go-arounds ({ga})");
}

Console.WriteLine("== Close parallels under 2,500 ft: one runway for wake (7110.65 3-9-6 f)");
{
    var data = TestAirport.Build(false);
    var map = new LocalMap(40, -75);
    var (pla, plo) = map.ToLatLon(new V2(0, 300));    // 300 m (about 1,000 ft) north
    data.Runways.Add(new AirportRunway { Lat = pla, Lon = plo, ElevationM = 10, Heading = 90, LengthM = 3000, WidthM = 45, PrimaryNumber = 9, PrimaryDesignator = 1, SecondaryNumber = 27, SecondaryDesignator = 2 });
    var (fla, flo) = map.ToLatLon(new V2(0, 1500));   // and one 1,500 m north
    data.Runways.Add(new AirportRunway { Lat = fla, Lon = flo, ElevationM = 10, Heading = 90, LengthM = 3000, WidthM = 45, PrimaryNumber = 8, SecondaryNumber = 26 });
    var apt = new AirportModel(data);
    var w = new TrafficWorld(apt, new TestSource(), seed: 5) { Automatic = false, Wind = new Wind(270, 10) };
    var e27 = apt.Ends.First(e => e.Ident == "27"); var e27R = apt.Ends.First(e => e.Ident == "27R"); var e26 = apt.Ends.First(e => e.Ident == "26");
    Check(TrafficWorld.CloseParallel(e27, e27R) && !TrafficWorld.CloseParallel(e27, e26), "27/27R 300 m apart are close parallels; 26, 1,500 m away, is not");
    var heavy = new Flight { Id = "H1", Callsign = "HVY1", Airline = "DAL", Type = "B77W", Kind = "departure", P = Perf.For("B77W"), Rwy = e27R, Pos = e27R.Threshold, OnGround = false, LiftoffTime = w.Time - 30 };
    w.NoteDeparture(heavy);
    var next = new Flight { Id = "N1", Callsign = "NXT1", Airline = "DAL", Type = "B738", Kind = "departure", P = Perf.For("B738"), Rwy = e27, Pos = e27.Threshold };
    var far = new Flight { Id = "N2", Callsign = "NXT2", Airline = "DAL", Type = "B738", Kind = "departure", P = Perf.For("B738"), Rwy = e26, Pos = e26.Threshold };
    var wk = w.OtherRunwayWake(next);
    Check(wk is > 80 and <= 90, $"a 737 off 27 waits the heavy's 2 minutes after it lifted off the close parallel 27R ({wk:0} s left)");
    Check(w.OtherRunwayWake(far) == 0, "not off the parallel 1,500 m away");
}

Console.WriteLine("== Crossing-runway wake (7110.65 3-9-8): 2 minutes behind a heavy departing a crossing runway");
{
    var data = TestAirport.Build(false);
    var map = new LocalMap(40, -75);
    var (clat, clon) = map.ToLatLon(new V2(0, 0));
    data.Runways.Add(new AirportRunway { Lat = clat, Lon = clon, ElevationM = 10, Heading = 0, LengthM = 2500, WidthM = 45, PrimaryNumber = 36, SecondaryNumber = 18 });
    var apt = new AirportModel(data);
    var w = new TrafficWorld(apt, new TestSource(), seed: 6) { Automatic = false, Wind = new Wind(300, 10) };
    var e27 = apt.Ends.First(e => e.Ident == "27"); var e36 = apt.Ends.First(e => e.Ident == "36");
    var heavy = new Flight { Id = "H2", Callsign = "HVY2", Airline = "DAL", Type = "B744", Kind = "departure", P = Perf.For("B744"), Rwy = e36, Pos = e36.Threshold, OnGround = false, LiftoffTime = w.Time - 60 };
    w.NoteDeparture(heavy);
    var next = new Flight { Id = "N3", Callsign = "NXT3", Airline = "DAL", Type = "A320", Kind = "departure", P = Perf.For("A320"), Rwy = e27, Pos = e27.Threshold };
    var wk = w.OtherRunwayWake(next);
    Check(wk is > 50 and <= 60, $"an A320 off 27 waits out 2 minutes behind a 747 that lifted off the crossing runway 36 ({wk:0} s left)");
    var med = new Flight { Id = "M1", Callsign = "MED1", Airline = "DAL", Type = "B738", Kind = "departure", P = Perf.For("B738"), Rwy = e36, Pos = e36.Threshold, OnGround = false, LiftoffTime = w.Time - 10 };
    w.NoteDeparture(med);
    Check(w.OtherRunwayWake(next) == 0, "no wake time behind a 737 on the crossing runway");
}

Console.WriteLine("== Crossing a runway between departures (23 Nov, KATL: JBU1798 held short of 27R for 12+ minutes behind line up and wait)");
{
    var data = TestAirport.Build(false);
    int Find(double x, double y) => data.Points.FindIndex(q => Math.Abs(q.BiasX - x) < 1 && Math.Abs(q.BiasZ - y) < 1);
    int Pt(double x, double y, int type = 1) { data.Points.Add(new TaxiPoint { Type = type, BiasX = x, BiasZ = y }); return data.Points.Count - 1; }
    void Path(int a, int b, int type = 1) => data.Paths.Add(new TaxiPath { Type = type, Start = a, End = b, WidthM = 23 });
    // A north apron across the runway: a taxiway at y = +200, two crossings with hold lines both sides, three gates.
    var n = new[] { -300, 0, 300 }.ToDictionary(x => x, x => Pt(x, 200));
    Path(n[-300], n[0]); Path(n[0], n[300]);
    foreach (var x in new[] { -300, 300 })
    {
        var hs = Pt(x, -95, 2); var hn = Pt(x, 95, 2);
        Path(Find(x, -200), hs); Path(hs, Find(x, 0)); Path(Find(x, 0), hn); Path(hn, n[x]);
    }
    foreach (var x in new[] { -300, 0, 300 })
    {
        data.Parkings.Add(new Parking { Type = 9, Name = 12, Number = 60 + x / 300, Heading = 0, RadiusM = 22, BiasX = x, BiasZ = 330 });
        data.Paths.Add(new TaxiPath { Type = 3, Start = n[x], End = data.Parkings.Count - 1 });
    }
    var apt = new AirportModel(data);
    foreach (var sp in apt.Spots) if (sp.P.Y > 0) sp.Airlines = new() { "JBU" };
    var sched = new Laia.Common.Airports.ScheduleData { Airport = "TEST", Source = "AIG" };
    const int start = 2 * 1440 + 600;
    for (var i = 0; i < 16; i++)
        sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = $"N{i}XD", Flight = $"{500 + i}", Dep = "TEST", Arr = "KYYY", DepMin = start + 4 + i / 2, ArrMin = start + 150 });
    sched.Legs.Add(new() { Airline = "JBU", Operator = "JBU", Title = "J", Type = "A320", Reg = "N798JB", Flight = "1798", Dep = "KXXX", Arr = "TEST", DepMin = start - 120, ArrMin = start + 8 });
    var world = new TrafficWorld(apt, new TestSource(), seed: 4) { Level = TrafficLevel.RealWorld, Wind = new Wind(270, 8), Schedule = sched, WeekMinute = start, SafetyLogic = true, LocalMinuteOverride = 12 * 60 };
    var log = new List<string>(); world.Log = l => log.Add($"{world.Time:0} {l}");
    double maxWait = 0; var parked = false; var crossed = false;
    for (var t = 0.0; t < 30 * 60; t += 0.5)
    {
        world.WeekMinute = start + world.Time / 60;
        world.Step(0.5);
        if (world.Flights.FirstOrDefault(f => !f.Done && f.Reg == "N798JB") is { } jb)
        {
            foreach (var st in jb.Stops.Where(st => st.Runway >= 0 && !double.IsNaN(st.WaitSince) && !st.Cleared)) maxWait = Math.Max(maxWait, world.Time - st.WaitSince);
            if (jb.OnGround && jb.Pos.Y > 100) crossed = true;
            if (jb.Phase is "at gate" or "parked" && jb.Pos.Y > 100) parked = true;
            if (jb.Stops.Any(st => st.Runway >= 0 && !double.IsNaN(st.WaitSince) && !st.Cleared) && world.Time % 30 < 0.5)
                Console.WriteLine($"    {world.Time:0} JBU1798 waiting to cross; on the runway: {string.Join(", ", world.Flights.Where(f => !f.Done && f.OnRunway >= 0).Select(f => f.Callsign + " " + f.Phase))}");
        }
    }
    foreach (var l in log.Where(l => l.Contains("JBU1798") || l.Contains("letting")).Take(8)) Console.WriteLine("    " + l);
    var luaw = log.Count(l => l.Contains("lining up runway"));
    Console.WriteLine($"  {luaw} departures lined up; JBU1798 crossed {crossed}, at its gate {parked}; longest wait to cross {maxWait:0} s");
    Check(crossed && parked, "the arrival crosses the runway and reaches its gate across it");
    Check(maxWait < 180, $"it waits no more than one departure and a bit to cross ({maxWait:0} s)");
    Check(luaw >= 12, $"the departures still go ({luaw})");
}

Console.WriteLine("== Peak runways: the ATIS first, real pairs, and kept until a tailwind over 5 kt (KLGA, 22 Nov)");
{
    var data = TestAirport.Build(false);
    var map = new LocalMap(40, -75);
    var (clat, clon) = map.ToLatLon(new V2(0, 0));
    data.Runways.Add(new AirportRunway { Lat = clat, Lon = clon, ElevationM = 10, Heading = 0, LengthM = 2500, WidthM = 45, PrimaryNumber = 36, SecondaryNumber = 18 });
    var apt = new AirportModel(data);
    // Learned days: landing 27 (60), departing 27 more than anything overall (90) — but at other times; with the
    // landings on 27, real take-offs were off 36 (40).
    var prof = new AirportProfile { Icao = "TEST", Version = AirportProfile.CurrentVersion, Day = "test days" };
    prof.Arrivals["27"] = new AirportProfile.RunwayArrivals { Count = 60 };
    prof.Departures["27"] = new AirportProfile.RunwayDepartures { Count = 90 };
    prof.Departures["36"] = new AirportProfile.RunwayDepartures { Count = 40 };
    for (var k = 0; k < 60; k++) prof.Samples.Add(new AirportProfile.SampleTrack { Kind = "arrival", Runway = "27", T = 1_000_000 + k * 120 });
    for (var k = 0; k < 40; k++) prof.Samples.Add(new AirportProfile.SampleTrack { Kind = "departure", Runway = "36", T = 1_000_000 + k * 170 + 40 });
    for (var k = 0; k < 90; k++) prof.Samples.Add(new AirportProfile.SampleTrack { Kind = "departure", Runway = "27", T = 2_000_000 + k * 120 });
    var w = new TrafficWorld(apt, new TestSource(), seed: 3) { Automatic = false, Wind = new Wind(270, 5), Learned = prof };
    var lines = new List<string>(); w.Log = l => lines.Add(l);
    w.Level = TrafficLevel.Peak; w.SelectRunways(true);
    Console.WriteLine("  " + lines.LastOrDefault(l => l.StartsWith("Runways in use")));
    Check(w.ArrRwy?.Ident == "27" && w.DepRwy?.Ident == "36", $"learned days: departing the runway used together with the landings, not the busiest overall ({w.ArrRwy?.Ident}/{w.DepRwy?.Ident})");
    var changes = 0; w.Log = l => { lines.Add(l); if (l.StartsWith("Runways in use")) changes++; };
    foreach (var (d, kt) in new[] { (300, 9), (360, 0), (230, 11), (180, 3), (330, 12), (10, 4) }) { w.Wind = new Wind(d, kt); w.SelectRunways(); }
    Check(changes == 0, $"Peak: calm, a few knots more or the wind swinging round don't change the runways ({changes} changes)");
    w.Wind = new Wind(90, 8); w.SelectRunways();
    Check(changes == 1 && w.ArrRwy?.Ident != "27", $"a tailwind over 5 kt on the landing runway does ({w.ArrRwy?.Ident})");
    // A field with an ATIS: it decides at Peak too.
    var w2 = new TrafficWorld(apt, new TestSource(), seed: 3) { Automatic = false, Wind = new Wind(270, 5), Learned = prof };
    var l2 = new List<string>(); w2.Log = l => l2.Add(l);
    w2.AtisText = "TEST ATIS INFO K 0151Z. LDG RWY 27. DEPG RWY 36. NOTAMS...";
    w2.Level = TrafficLevel.Peak; w2.SelectRunways(true);
    Console.WriteLine("  " + l2.LastOrDefault(l => l.StartsWith("Runways in use")));
    Check(w2.ArrRwy?.Ident == "27" && w2.DepRwy?.Ident == "36" && l2.Any(l => l.Contains("from the real ATIS")), "Peak follows the real ATIS when the field has one");
}

Console.WriteLine("== The user parked on a gate is part of the scenery (KLGA gate 48, 22 Nov)");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var log = new List<string>();
    var world = new TrafficWorld(apt, new TestSource(), seed: 11) { Level = TrafficLevel.Busy, Wind = new Wind(270, 12) };
    world.Log = l => log.Add(l);
    // Parked on the westernmost gate: the way in from the taxiway at x = -600 points straight at it before turning
    // along the apron lane.
    var gate = apt.Spots.OrderBy(sp => sp.P.X).First();
    world.UserPos = gate.P; world.UserOnGround = true; world.UserType = "B738"; world.UserGs = 0; world.UserTrack = 0;
    var up = Perf.For("B738");
    var nose = gate.P + V2.FromBearing(gate.Heading) * (up.LengthM * 0.45); var tail = gate.P - V2.FromBearing(gate.Heading) * (up.LengthM * 0.55);
    double SegD(V2 p, V2 a, V2 b) { var ab = b - a; var t = Math.Clamp(V2.Dot(p - a, ab) / V2.Dot(ab, ab), 0, 1); return (p - (a + ab * t)).Length; }
    var closest = double.MaxValue; string who = "";
    for (var t = 0.0; t < 3600; t += 0.5)
    {
        world.Step(0.5);
        foreach (var f in world.Flights.Where(f => !f.Done && f.OnGround && f.Gs > 1))
        {
            var d = SegD(f.Pos, tail, nose) - f.P.SpanM / 2;
            if (d < closest) { closest = d; who = f.Callsign; }
        }
    }
    var removed = log.Count(l => l.Contains("blocked by your aircraft; removed"));
    var waited = world.Flights.Count(f => f.BlockedByUser);
    Console.WriteLine($"  removed behind the user {removed}; nearest wingtip to the user's fuselage {closest:0} m ({who})");
    Check(removed == 0, "nobody removed as blocked by the user parked on a gate");
    Check(closest > 0, "and nobody's wing passes over the user's aircraft");
}

Console.WriteLine("== LAIA's own track recording: a frame and an instruction, read back for the replay");
{
    var dir = Path.Combine(Path.GetTempPath(), "laia-rec-" + Guid.NewGuid().ToString("N")[..8]);
    Environment.SetEnvironmentVariable("LAIA_DATA", dir);
    var apt = new AirportModel(TestAirport.Build(false));
    var w = new TrafficWorld(apt, new TestSource(), seed: 4) { Level = TrafficLevel.Busy, Wind = new Wind(270, 10) };
    for (var k = 0; k < 2400; k++) w.Step(0.5);
    var rec = new TrackRecorder();
    rec.Frame(new[] { w });
    var someone = w.Flights.First(f => !f.Done && f.Kind != "parked" && f.Phase is not ("parked" or "boarding"));
    rec.Event(w, $"{someone.Callsign}: turn base earlier, base turn now 9.1 nm out (was 10.7) — test");
    var now = DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;
    var json = rec.Range(now - 60, now + 60, 5);
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var frames = doc.RootElement.EnumerateArray().Count(e => e.TryGetProperty("f", out _));
    var events = doc.RootElement.EnumerateArray().Where(e => e.TryGetProperty("e", out _)).ToList();
    var rows = doc.RootElement.EnumerateArray().Where(e => e.TryGetProperty("f", out _)).Sum(e => e.GetProperty("f").GetArrayLength());
    Console.WriteLine($"  {frames} frame(s), {rows} aircraft, {events.Count} event(s): {(events.Count > 0 ? events[0].GetProperty("e")[4].GetString() : "-")}");
    Check(frames == 1 && rows > 0 && events.Count == 1 && events[0].GetProperty("e")[0].GetString() == someone.Callsign, "recorded and read back: every flying aircraft, and the instruction where it was given");
    Environment.SetEnvironmentVariable("LAIA_DATA", null);
}

Console.WriteLine("== Filed-route points still ahead (KBOS, 22–23 Nov: JBU734's route rebuilt back to points 100+ nm behind)");
{
    var nmM = Geo2.MPerNm;
    // Airways west to east, a point every 30 nm, then a dogleg north.
    var fv = new List<V2> { new(-300 * nmM, 0), new(-270 * nmM, 0), new(-240 * nmM, 0), new(-210 * nmM, 0), new(-180 * nmM, 0), new(-150 * nmM, 20 * nmM), new(-120 * nmM, 20 * nmM) };
    var a1 = TrafficWorld.ViaAhead(fv, new V2(-195 * nmM, 0.5 * nmM));     // past four points, on the 4th leg
    var a2 = TrafficWorld.ViaAhead(fv, new V2(-310 * nmM, 0));             // before the first
    var a3 = TrafficWorld.ViaAhead(fv, new V2(-110 * nmM, 21 * nmM));      // beyond the last
    var a4 = TrafficWorld.ViaAhead(fv, new V2(-165 * nmM, 12 * nmM));      // on the dogleg, off its line by a little
    Console.WriteLine($"  ahead: past four {a1.Count} (first {a1.FirstOrDefault().X / nmM:0} nm), before the first {a2.Count}, beyond the last {a3.Count}, on the dogleg {a4.Count}");
    Check(a1.Count == 3 && Math.Abs(a1[0].X / nmM + 180) < 1, "well past several points: only the ones after the leg it's on (none behind it)");
    Check(a2.Count == fv.Count && a3.Count == 0 && a4.Count == 2, "before the route all of it; beyond its end none; part-way along a dogleg the rest");
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
        // Each airframe arrives, then leaves again 90 minutes later.
        sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = $"N{i}DL", Flight = $"{100 + i}", Dep = "KXXX", Arr = "TEST", DepMin = start - 100 + i * 7, ArrMin = start + 6 + i * 7 });
        sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = $"N{i}DL", Flight = $"{200 + i}", Dep = "TEST", Arr = "KYYY", DepMin = start + 96 + i * 7, ArrMin = start + 190 + i * 7 });
        // And some aircraft already on the ground leave in the first half hour.
        sched.Legs.Add(new() { Airline = "UAL", Operator = "UAL", Title = "U", Type = "A320", Reg = $"N{i}UA", Flight = $"{300 + i}", Dep = "TEST", Arr = "KZZZ", DepMin = start + 8 + i * 3, ArrMin = start + 120 });
    }
    var log = new List<string>();
    var world = new TrafficWorld(apt, new TestSource(), seed: 9) { Level = TrafficLevel.RealWorld, Wind = new Wind(270, 8), Schedule = sched, WeekMinute = start, LocalMinuteOverride = 12 * 60 };
    world.Log = s => log.Add($"{start + world.Time / 60 - start,5:0.0} min {s}");
    var landedAt = new Dictionary<string, double>();
    var airborneAt = new Dictionary<string, double>();
    // At a gate only 20 minutes before push-back and 10 after arriving (user, 23 Nov).
    var earlyAtGate = new HashSet<string>(); var stayed = new HashSet<string>(); var goneBetween = new HashSet<string>(); var maxEarly = 0.0;
    var atStart = 0;
    for (var i = 0; i < 180 * 60 * 4; i++)
    {
        world.WeekMinute = start + world.Time / 60;
        world.Step(0.25);
        if (i == 4) atStart = world.Flights.Count(f => !f.Done && f.Kind == "parked");
        var nowMin = world.Time / 60;
        foreach (var f in world.Flights.Where(f => !f.Done && f.Kind == "parked" && f.Phase == "parked" && f.Reg.Length > 0))
        {
            var nextDep = sched.Legs.Where(l => l.Dep == "TEST" && l.Reg == f.Reg && l.DepMin - start > nowMin - 15).Select(l => l.DepMin - start - nowMin).DefaultIfEmpty(9999).Min();
            if (double.IsNaN(f.ArrivedAt)) { if (nextDep > 45.5) { earlyAtGate.Add(f.Reg); maxEarly = Math.Max(maxEarly, nextDep); } }
            else if (world.Time - f.ArrivedAt > 601 && nextDep > 45.5) stayed.Add(f.Reg);
        }
        // An airframe that turned round (landed, then leaves 50 minutes later) is away from its gate in between.
        foreach (var r in sched.Legs.Where(l => l.Arr == "TEST").Select(l => l.Reg))
            if (landedAt.ContainsKey(sched.Legs.First(l => l.Arr == "TEST" && l.Reg == r).Callsign) && !world.Flights.Any(f => !f.Done && f.Reg == r)) goneBetween.Add(r);
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
    Check(departureDelays.Count > 0 && departureDelays.Min() > -6, "nobody leaves more than 5 minutes early (early only when the runway is quiet)");
    var dl = sched.Legs.Where(l => l.Dep == "TEST" && l.Airline == "DAL").Count(l => airborneAt.ContainsKey(l.Callsign));
    Check(dl >= 9, $"arrived airframes leave again on their next flight ({dl} of 10)");
    Console.WriteLine($"  at the gates at the start: {atStart} (10 leave in the first 35 minutes, none of the 90-minute turnarounds); early at a gate: {earlyAtGate.Count} (up to {maxEarly:0} min before); stayed past 10 min: {stayed.Count}; away between flights: {goneBetween.Count} of 10");
    Check(atStart <= 10, "at the start, only aircraft pushing back within 45 minutes are at the gates");
    Check(earlyAtGate.Count == 0, "no aircraft at a gate more than 45 minutes before its push-back");
    Check(stayed.Count == 0, "no aircraft at its gate more than 10 minutes after arriving (unless it leaves within 45)");
    Check(goneBetween.Count >= 9, "an airframe with 90 minutes between flights is taken away, then back 45 minutes before");
}

Console.WriteLine("== Overnight: an aircraft landing after 22:00 local stays at its gate for its morning flight (user, 24 Nov)");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var sched = new Laia.Common.Airports.ScheduleData { Airport = "TEST", Source = "AIG" };
    const int start = 2 * 1440 + 600;
    sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = "N950DL", Flight = "950", Dep = "KXXX", Arr = "TEST", DepMin = start - 100, ArrMin = start + 6 });
    sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = "N950DL", Flight = "951", Dep = "TEST", Arr = "KYYY", DepMin = start + 420, ArrMin = start + 500 });
    sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = "N960DL", Flight = "960", Dep = "KXXX", Arr = "TEST", DepMin = start - 98, ArrMin = start + 8 });
    var world = new TrafficWorld(apt, new TestSource(), seed: 9) { Level = TrafficLevel.RealWorld, Wind = new Wind(270, 8), Schedule = sched, WeekMinute = start, LocalMinuteOverride = 22 * 60 + 20 };
    for (var i = 0; i < 45 * 60 * 4; i++) { world.WeekMinute = start + world.Time / 60; world.Step(0.25); }
    var n950 = world.Flights.FirstOrDefault(f => !f.Done && f.Reg == "N950DL");
    var n960 = world.Flights.FirstOrDefault(f => !f.Done && f.Reg == "N960DL");
    Console.WriteLine($"  after 45 min at 22:20 local: N950DL (leaves in 7 h) {n950?.Phase ?? "gone"}, N960DL (no flight out) {n960?.Phase ?? "gone"}");
    Check(n950 is { Kind: "parked", Overnight: true }, "landed at night with a morning flight: still at its gate");
    Check(n960 is { Kind: "parked" }, "landed at night with no flight out: still at its gate before 06:00");
    world.LocalMinuteOverride = 6 * 60 + 5;
    for (var i = 0; i < 40; i++) { world.WeekMinute = start + world.Time / 60; world.Step(0.25); }
    Check(world.Flights.Any(f => !f.Done && f.Reg == "N950DL") && !world.Flights.Any(f => !f.Done && f.Reg == "N960DL"), "at 06:00 the one with no flight out goes; the one with a flight stays");
    // Lights (user, 24 Nov): parked and nobody aboard — dark; night and day; wing lights in cloud near freezing.
    Check(n950 is not null && world.ShownLights(n950) == 0, "parked long after arriving, nobody aboard: all lights off");
    world.LightClock = new DateTime(2026, 11, 24, 17, 0, 0, DateTimeKind.Utc);
    var day = world.Dark;
    world.LightClock = new DateTime(2026, 11, 24, 5, 0, 0, DateTimeKind.Utc);
    var night = world.Dark;
    Console.WriteLine($"  sun at noon local {world.SunElevationDeg():0}° → dark at noon {day}, at midnight {night}");
    Check(!day && night, "night lighting by the sun at the airport");
    var air = world.SpawnArrival(20, false, new Laia.Common.Airports.ScheduleLeg { Airline = "DAL", Operator = "DAL", Type = "B739", Reg = "N970DL", Flight = "970", Dep = "KXXX", Arr = "TEST" })!;
    air.AltFt = world.Apt.ElevationFt + 4000;
    world.CloudBaseFt = 2500; world.SurfaceTempC = 6;
    var inCloud = (world.ShownLights(air) & TrafficWorld.Wing) != 0;
    world.SurfaceTempC = 20;
    var warm = (world.ShownLights(air) & TrafficWorld.Wing) != 0;
    Check(inCloud && !warm, "wing lights in cloud near freezing, not in warm cloud");
}

Console.WriteLine("== Changing the traffic level while traffic runs refits it at once (15 Nov: Medium and Real world seemed to do nothing)");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var sched = new Laia.Common.Airports.ScheduleData { Airport = "TEST", Source = "AIG" };
    const int start = 2 * 1440 + 600;
    for (var i = 0; i < 16; i++)
        sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = $"N{i}LV", Flight = $"{400 + i}", Dep = "TEST", Arr = "KYYY", DepMin = start + 4 + i, ArrMin = start + 200 });
    var log = new List<string>();
    var world = new TrafficWorld(apt, new TestSource(), seed: 9) { Level = TrafficLevel.RealWorld, Wind = new Wind(270, 8), Schedule = sched, WeekMinute = start };
    world.Log = s2 => log.Add(s2);
    int Parked() => world.Flights.Count(f => !f.Done && f.Kind == "parked");
    void Run(double secs) { for (var t = 0.0; t < secs; t += 0.5) { world.WeekMinute = start + world.Time / 60; world.Step(0.5); } }
    Run(5);
    var real = Parked();
    world.Level = TrafficLevel.Light; Run(2);
    var light = Parked();
    world.Level = TrafficLevel.Medium; Run(2);
    var medium = Parked();
    world.Level = TrafficLevel.RealWorld; Run(2);
    var back = Parked();
    foreach (var x in log.Where(x => x.Contains("traffic level now"))) Console.WriteLine("  " + x);
    Console.WriteLine($"  aircraft at the gates: Real world {real} → Light {light} → Medium {medium} → Real world {back}");
    Check(light < real, "Light removes the aircraft it leaves out at once");
    Check(medium > light, "Medium adds aircraft at once");
    Check(back >= medium && back == real, "Real world brings them all back at once");
}

Console.WriteLine("== A bank of departures at once: held at the gate in order, released one by one (KORD, 15 Nov)");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var sched = new Laia.Common.Airports.ScheduleData { Airport = "TEST", Source = "AIG" };
    const int start = 2 * 1440 + 600;
    for (var i = 0; i < 12; i++)
        sched.Legs.Add(new() { Airline = "AAL", Operator = "AAL", Title = "T", Type = "B738", Reg = $"N{i}BK", Flight = $"{500 + i}", Dep = "TEST", Arr = "KYYY", DepMin = start + 6 + (i % 3), ArrMin = start + 200 });
    var log = new List<string>();
    var world = new TrafficWorld(apt, new TestSource(), seed: 9) { Level = TrafficLevel.RealWorld, Wind = new Wind(270, 8), Schedule = sched, WeekMinute = start };
    world.Log = s2 => log.Add($"{world.Time / 60,5:0.0} min {s2}");
    var airborne = new Dictionary<string, double>();
    for (var t = 0.0; t < 50 * 60; t += 0.5)
    {
        world.WeekMinute = start + world.Time / 60;
        world.Step(0.5);
        foreach (var f in world.Flights) if (f.Kind == "departure" && !f.OnGround && !airborne.ContainsKey(f.Callsign)) airborne[f.Callsign] = world.Time / 60;
    }
    var times = airborne.Values.OrderBy(x => x).ToList();
    var gaps = times.Zip(times.Skip(1), (x, y) => y - x).ToList();
    foreach (var x in log.Where(x => x.Contains("gate hold") || x.Contains("held at the gate")).Take(4)) Console.WriteLine("  " + x);
    Console.WriteLine($"  {times.Count} of 12 airborne, at {string.Join(", ", times.Select(x => x.ToString("0.0")))} min");
    Check(times.Count == 12, "the whole bank departs");
    Check(gaps.Count > 0 && gaps.Max() < 3, $"one after another, no long gaps (longest {(gaps.Count > 0 ? gaps.Max() : 0):0.0} min)");
    Check(!log.Any(x => x.Contains("released from the gate hold after 15.0")), "nobody waits out the 15-minute limit");
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
        sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = $"N{i}DL", Flight = $"{150 + i}", Dep = "TEST", Arr = "KYYY", DepMin = start + 6 + i * 3, ArrMin = start + 150 });
        sched.Legs.Add(new() { Airline = "UAL", Operator = "UAL", Title = "U", Type = "A320", Reg = $"N{i}UA", Flight = $"{300 + i}", Dep = "TEST", Arr = "KZZZ", DepMin = start + 7 + i * 3, ArrMin = start + 120 });
    }
    // An aircraft that landed three hours ago and leaves tomorrow morning isn't at a gate (user, 23 Nov: at a gate only
    // 20 minutes before push-back and 10 after arriving).
    sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = "N900DL", Flight = "900", Dep = "KYYY", Arr = "TEST", DepMin = start - 330, ArrMin = start - 180 });
    sched.Legs.Add(new() { Airline = "DAL", Operator = "DAL", Title = "T", Type = "B739", Reg = "N900DL", Flight = "901", Dep = "TEST", Arr = "KYYY", DepMin = start + 600, ArrMin = start + 750 });
    // Two arrivals due the same minute don't appear on top of each other.
    sched.Legs.Add(new() { Airline = "UAL", Operator = "UAL", Title = "U", Type = "A320", Reg = "N800UA", Flight = "800", Dep = "KZZZ", Arr = "TEST", DepMin = start - 120, ArrMin = start + 3 });
    sched.Legs.Add(new() { Airline = "UAL", Operator = "UAL", Title = "U", Type = "A320", Reg = "N801UA", Flight = "801", Dep = "KZZZ", Arr = "TEST", DepMin = start - 125, ArrMin = start + 3 });
    var world = new TrafficWorld(apt, new TestSource(), seed: 5) { Level = TrafficLevel.RealWorld, Wind = new Wind(270, 8), Schedule = sched, WeekMinute = start, Gates = real };
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
    Check(!world.Flights.Any(f => !f.Done && f.Reg == "N900DL"), "an aircraft that landed hours ago and leaves tomorrow isn't at a gate");
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
    {
        var dtw = AtisParse.Approaches("DTW ARR INFO I 0453Z. 03006KT 9SM SCT040 OVC250 M01/M07 A3057 (THREE ZERO FIVE SEVEN) RMK AO2 SLP361 T10111072 400331044. VISUAL RY 4L USE Z LOC FREQ 111.95 APCH IN USE. EOR OPERATIONS IN PROGRESS, RNAV RNP CAPABLE AIRCRAFT MAY BE ASSIGNED RNAV RNP W. NOTICE TO AIRMEN. RWY 22L, 4R CLSD, RWY 21R, 3L CLSD. PILOTS USE CTN FOR BIRD ACTIVITY. ...ADVS YOU HAVE INFO I.");
        Console.WriteLine($"  KDTW info I: {string.Join(", ", dtw.Select(a => $"{a.kind} {a.suffix} {a.runway}"))}");
        Check(dtw.Count == 1 && dtw[0] == ("ILS", "Z", "04L"), "\"VISUAL RY 4L USE Z LOC\" is flown as the ILS Z 04L (the RNAV RNP W that may be assigned isn't the one in use)");
        // KATL info B 2252Z, 25 Nov: "ILS PRM 9R" — the PRM between ILS and the runway hid two of the three landing runways.
        var atl = AtisParse.FromText("ATL ARR INFO B 2252Z. 06010KT 8SM -RA OVC020 08/06 A3005 (THREE ZERO ZERO FIVE) RMK AO2 SLP177 P0004 T00780056. SIMULTANEOUS APCHS IN USE, ILS 8L, ILS PRM 9R, ILS PRM 10. NOTAMS... RWY 9L PAPI OTS. IM RWY 8L OTS, IM RWY 9R OTS, IM RWY 10 OTS, DME PORTION OF ILS RWY 9L OTS. WIND SHEAR ADZYS IN EFCT. ...ADVS YOU HAVE INFO B.");
        Console.WriteLine($"  KATL info B: {atl.Describe()}");
        Check(atl.Landing.OrderBy(x => x).SequenceEqual(new[] { "08L", "09R", "10" }) && atl.Approaches.Count == 3, "\"ILS 8L, ILS PRM 9R, ILS PRM 10\" lands all three (PRM read)");
        var vis2 = AtisParse.Approaches("VIS APCH RWY 27L BACKED BY ILS Y. DEPG RWY 27R.");
        Check(vis2.Count == 1 && vis2[0] == ("ILS", "Y", "27L"), "\"VIS APCH RWY 27L BACKED BY ILS Y\" is the ILS Y 27L");
    }
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

Console.WriteLine("== Three landing runways: each side's arrivals shared among that side's runways (KATL, 15 Nov: none for 9R)");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var map = new LocalMap(data.Lat, data.Lon);
    // 09L/27R 1,500 m north, 10/28 1,500 m south of the existing 09/27 (like Atlanta's 8L, 9R and 10).
    var (pla, plo) = map.ToLatLon(new V2(0, 1500));
    data.Runways.Add(new AirportRunway { Lat = pla, Lon = plo, ElevationM = 10, Heading = 90, LengthM = 3000, WidthM = 45, PrimaryNumber = 9, PrimaryDesignator = 1, SecondaryNumber = 27, SecondaryDesignator = 2 });
    var (psa, pso) = map.ToLatLon(new V2(0, -1500));
    data.Runways.Add(new AirportRunway { Lat = psa, Lon = pso, ElevationM = 10, Heading = 90, LengthM = 3000, WidthM = 45, PrimaryNumber = 10, SecondaryNumber = 28 });
    Laia.Common.Airports.ProcedureLeg Fx(string name, double x, double y, int type = 18) { var (la, lo) = map.ToLatLon(new V2(x, y)); return new() { Type = type, Fix = name, Lat = la, Lon = lo }; }
    data.Arrivals.Add(new() { Name = "NORTH1", Legs = { Fx("NORRR", 0, 40 * nm, 15), Fx("NORMD", 8 * nm, 20 * nm) } });
    data.Arrivals.Add(new() { Name = "SOUTH1", Legs = { Fx("SOURR", 0, -40 * nm, 15), Fx("SOUMD", 8 * nm, -20 * nm) } });
    var far = new Dictionary<string, (double, double)?> { ["KNOR"] = map.ToLatLon(new V2(0, 300 * nm)), ["KSOU"] = map.ToLatLon(new V2(0, -300 * nm)) };
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 4) { Automatic = false, Wind = new Wind(270, 10), Locate = c => far.TryGetValue(c, out var p) ? p : null };
    world.SelectRunways(true);
    world.AtisText = "TEST ARR INFO A. ILS RWY 27 AND RWY 27R AND RWY 28 APCHS IN USE. DEPG RWY 27.";
    Console.WriteLine($"  landing {string.Join(", ", world.ArrRwys.Select(e => e.Ident))}");
    Laia.Common.Airports.ScheduleLeg Leg(string from, int n) => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = "N" + n, Flight = n.ToString(), Dep = from, Arr = "TEST" };
    var south = Enumerable.Range(0, 6).Select(i => world.SpawnArrival(30 + i * 6, false, Leg("KSOU", 300 + i))!).ToList();
    var north = Enumerable.Range(0, 4).Select(i => world.SpawnArrival(30 + i * 6, false, Leg("KNOR", 400 + i))!).ToList();
    Console.WriteLine($"  from the south: {string.Join(", ", south.Select(f => f.Rwy!.Ident))}; from the north: {string.Join(", ", north.Select(f => f.Rwy!.Ident))}");
    if (world.ArrRwys.Count == 3)
    {
        Check(south.Select(f => f.Rwy!.Ident).Distinct().Count() >= 2, "the south arrivals are shared between the south runway and the middle one");
        Check(!south.Any(f => f.Rwy!.Ident == "27R") && !north.Any(f => f.Rwy!.Ident == "28"), "nobody crosses the airport to the far side");
    }
    else Check(false, "the three landing runways the ATIS names are used");
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
    // Real traffic decides (user, 19 Nov: "ADSB can tell us which arrivals land on which runways"): here the learned
    // tracks say arrivals from the north land on 27 nine times in ten, though 27R is on their side.
    var learned = new AirportProfile { Icao = "TEST", Version = AirportProfile.CurrentVersion };
    var n27 = new int[12]; n27[0] = 45; var n27R = new int[12]; n27R[0] = 5;
    learned.Arrivals["27"] = new AirportProfile.RunwayArrivals { Count = 45, FromBearing = n27 };
    learned.Arrivals["27R"] = new AirportProfile.RunwayArrivals { Count = 5, FromBearing = n27R };
    foreach (var f in world.Flights.ToList()) world.Flights.Remove(f);
    world.Learned = learned;
    var north2 = Enumerable.Range(0, 10).Select(i => world.SpawnArrival(30 + i * 6, false, Leg("KNOR", 400 + i))!).ToList();
    var on27 = north2.Count(f => f.Rwy!.Ident == "27");
    Console.WriteLine($"  from the north, by real use (27: 90%): {string.Join(" ", north2.Select(f => f.Rwy!.Ident))}");
    Check(on27 >= 7 && on27 <= 10, $"arrivals shared between the runways as real traffic from their direction used them ({on27} of 10 on 27)");

    // The runway planner: a rush from the north, one every 20 s 60–110 nm out, more than one runway takes. It plans them
    // together — where real traffic goes first, the overflow to the other — without anyone changing back and forth.
    // Run with the planner off first, the same rush, to compare.
    (int landed, int total, double span, int on27, int on27R, int changes, int maxSw, int breakoffs, double avgDelay) Rush(bool planner, double farNm = 110)
    {
        foreach (var f in world.Flights.ToList()) world.Flights.Remove(f);
        world.Planner = planner;
        var log2 = new List<string>();
        world.Log = l => log2.Add($"{world.Time:0} {l}");
        var rush2 = new List<Flight>();
        var spawnAt = new Dictionary<string, double>();
        for (var k = 0; k < 16; k++)
        {
            // Beyond its STAR's start, as taken over from the whole sky: from the north at cruise.
            var f = farNm > 150
                ? world.SpawnArrival(0, false, Leg("KNOR", 700 + k), entry: (new V2(0, (farNm - k * 3) * Geo2.MPerNm), 35000, 37000))
                : world.SpawnArrival(farNm - k * 3, false, Leg("KNOR", (planner ? 600 : 500) + k));
            if (f is not null) { rush2.Add(f); spawnAt[f.Callsign] = world.Time; }
            for (var t = 0.0; t < 20; t += 0.5) world.Step(0.5);
        }
        var landed2 = new Dictionary<string, (double t, string rwy)>();
        for (var t = 0.0; t < 3 * 3600 && landed2.Count < rush2.Count; t += 0.5)
        {
            world.Step(0.5);
            foreach (var f in rush2) if (f.OnGround && !landed2.ContainsKey(f.Callsign)) landed2[f.Callsign] = (world.Time, f.Rwy!.Ident);
        }
        var span = landed2.Count > 1 ? (landed2.Values.Max(x => x.t) - landed2.Values.Min(x => x.t)) / 60 : 0;
        var avg = landed2.Count > 0 ? landed2.Average(kv => kv.Value.t - spawnAt[kv.Key]) / 60 : 0;
        var r = (landed2.Count, rush2.Count, span, landed2.Values.Count(x => x.rwy == "27"), landed2.Values.Count(x => x.rwy == "27R"),
            log2.Count(l => l.Contains("planned with the others")), rush2.Max(f => f.RwySwitches), log2.Count(l => l.Contains("breaking off")), avg);
        Console.WriteLine($"  {farNm:0} nm out, planner {(planner ? "on " : "off")}: {r.Item1} of {r.Item2} landed in {span:0} min, {avg:0.0} min each on average from appearing ({r.Item4} on 27, {r.Item5} on 27R); {r.Item6} planned changes, at most {r.Item7} per aircraft; break-offs {r.Item8}");
        foreach (var l in log2.Where(l => l.Contains("breaking off")).Take(4)) Console.WriteLine("    " + l);
        if (planner && Environment.GetEnvironmentVariable("LAIA_DIAG") is not null)
            foreach (var bl in log2.Where(l => l.Contains("breaking off") || l.Contains("going around (runway not clear")).Take(3))
            {
                var parts = bl.Split(' ');
                var cs = parts[1].TrimEnd(':');
                var ldr = System.Text.RegularExpressions.Regex.Match(bl, @"(?:behind|not clear: )(\w+)").Groups[1].Value;
                Console.WriteLine($"  DIAG history of {cs} and {ldr}:");
                foreach (var l in log2.Where(l => l.Contains(cs + ":") || l.Contains(ldr + ":"))) Console.WriteLine("    D " + l);
            }
        return r;
    }
    // Close in (65–110 nm): 8–9 minutes of delay for the last ones, more than speed and delay vectors can take from there —
    // a controller would hold. An overload check: it all lands, the planner no worse than without it.
    var off = Rush(false);
    var on = Rush(true);
    // From 200+ nm, as the 400 nm hand-over and metering give in real use: the delay is planned far out, no break-offs.
    var farOn = Rush(true, 230);
    Check(farOn.landed == farOn.total, "the rush from 200+ nm all lands");
    Check(farOn.breakoffs == 0, $"the rush from 200+ nm needs no break-offs ({farOn.breakoffs})");
    Check(on.landed == on.total, "the planner's rush all lands");
    Check(on.maxSw <= 2, "nobody changes runway more than twice");
    Check(on.on27 >= on.on27R, "at least half go where real traffic from their direction goes");
    Check(on.breakoffs <= off.breakoffs && on.avgDelay <= off.avgDelay + 0.5, $"the planner does no worse than without it (break-offs {on.breakoffs} vs {off.breakoffs}, {on.avgDelay:0.0} vs {off.avgDelay:0.0} min to land)");
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

Console.WriteLine("== An ILS fix coded 'at or above' the intercept altitude, with the glideslope altitude second (Calgary ULBUD)");
{
    // CYYC ILS 17L in MSFS: VIKTI "at or above 6,500", ULBUD (3.8 nm out) "at or above 6,500" with 4,890 (the glideslope's
    // altitude there) as the second altitude. Read as 6,500 at 3.8 nm, every arrival arrived 500 ft high at 1,000 ft
    // above the field and went around (CYYC, 14 Nov: 15 go-arounds in 45 minutes; MCO had them too).
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    data.Arrivals.Clear(); data.Approaches.Clear();
    var map = new LocalMap(data.Lat, data.Lon);
    Laia.Common.Airports.ProcedureLeg Fix(string name, double x, int type, int desc, double a1, double a2)
    {
        var (la, lo) = map.ToLatLon(new V2(x, 0));
        return new() { Type = type, Fix = name, Lat = la, Lon = lo, Course = 270, AltDesc = desc, Alt1Ft = a1, Alt2Ft = a2 };
    }
    var elev = 10 * 3.28084;
    data.Approaches.Add(new() { Type = 4, Runway = "27", FinalLegs = {
        Fix("VIKTX", 1500 + 10.9 * nm, 15, 2, elev + 2950, elev + 2950),
        Fix("ULBUX", 1500 + 3.8 * nm, 4, 2, elev + 2950, elev + 1330),
        Fix("RW27", 1500, 4, 1, elev + 105, 0) } });
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 31) { Automatic = false, Wind = new Wind(270, 5) };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    var f = world.SpawnArrival(40, true)!;
    double offAt1000 = double.NaN;
    var rwy = f.Rwy!;
    for (var t = 0.0; t < 2400 && !f.OnGround && !f.WentAround; t += 0.5)
    {
        world.Step(0.5);
        var dth = (f.ThrS - f.S) / nm;
        if (double.IsNaN(offAt1000) && !f.OnGround && f.AltFt - elev < 1000 && dth < 6) offAt1000 = f.AltFt - (elev + 50 + dth * 318.4);
    }
    foreach (var x in log.Where(x => x.Contains(f.Callsign) && (x.Contains("going around") || x.Contains("landed")))) Console.WriteLine("  " + x);
    Console.WriteLine($"  {f.Route}: {(f.WentAround ? "went around" : f.OnGround ? "landed" : "still flying")}; {offAt1000:+0;-0} ft from the 3° path at 1,000 ft");
    Check(f.OnGround && !f.WentAround, "lands off the first approach: the glideslope altitude at the fix is the restriction, not the intercept altitude");
    Check(Math.Abs(offAt1000) < 300, "on the glidepath at 1,000 ft above the field");
}

// The old behaviour (real tracks stitched into ways in), kept behind a switch since 0.1.333 (user, 28 Nov: "no flight
// should have a hard coded route") — tested with the switch on.
TrafficWorld.CopyRealTracks = true;
Console.WriteLine("== Vectors off the STAR where real traffic is turned off it (KORD, 15 Nov: arrivals from the NW 'blindly following the STAR')");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    data.Arrivals.Clear();
    var map = new LocalMap(data.Lat, data.Lon);
    Laia.Common.Airports.ProcedureLeg Fx(string name, double xNm, double yNm, int type)
    {
        var (la, lo) = map.ToLatLon(new V2(xNm * nm, yNm * nm));
        return new() { Type = type, Fix = name, Lat = la, Lon = lo };
    }
    // A STAR from the northwest that runs on to a point 8 nm north-west of the field.
    data.Arrivals.Add(new() { Name = "NWEST1", Legs = { Fx("NWAAA", -30, 30, 15), Fx("NWBBB", -14, 14, 18), Fx("NWCCC", -7, 9, 18), Fx("NWDDD", -5, 6, 18) } });
    // A STAR from the north no real track flew, ending beside where the real ones pass (AAL2867 on SHAIN2, 19 Nov).
    data.Arrivals.Add(new() { Name = "NORTH1", Legs = { Fx("NOAAA", -6, 40, 15), Fx("NOBBB", -6, 13, 18) } });
    // Real arrivals to 27 (landing west, from the east): on the STAR to NWBBB, then turned east onto a downwind
    // 5 nm north of the field, base 12 nm east, final 10 nm out.
    double[] P(double x, double y) { var (la, lo) = map.ToLatLon(new V2(x * nm, y * nm)); return new[] { la, lo, 6000 }; }
    var prof = new AirportProfile { Icao = "TEST", Source = "test", Day = "test" };
    for (var k = 0; k < 5; k++)
    {
        var pts = new List<double[]>();
        for (var t = 0.0; t <= 1; t += 0.05) pts.Add(P(-30 + 16 * t, 30 - 16 * t));                 // NWAAA → NWBBB
        for (var x = -13.0; x <= 6; x += 1) pts.Add(P(x, 14 - (x + 13) / 19 * (9 + k * 0.2)));       // turned off: to the downwind
        for (var x = 7.0; x <= 12; x += 1) pts.Add(P(x, 5));                                        // downwind
        for (var y = 4.0; y >= 1; y -= 1) pts.Add(P(12, y));                                        // base
        for (var x = 11.0; x >= 1.5; x -= 1) pts.Add(P(x, 0));                                       // final
        prof.Samples.Add(new AirportProfile.SampleTrack { Kind = "arrival", Runway = "27", Points = pts });
    }
    // Traffic from another arrival, from the north, that runs beside the STAR's last legs for 12 nm, then goes on to a
    // downwind south of the field (DAL2646 on WYNDE3, 19 Nov): it didn't fly this STAR, and more of them than of the
    // real ones mustn't make their downwind the "typical" way in.
    for (var k = 0; k < 7; k++)
    {
        var pts = new List<double[]>();
        for (var y = 48.0; y >= 16; y -= 1) pts.Add(P(-14, y));                                     // from the north
        for (var t = 0.0; t <= 1; t += 0.1) pts.Add(P(-14 + 7 * t, 14 - 5 * t));                    // beside NWBBB → NWCCC
        for (var t = 0.1; t <= 1; t += 0.2) pts.Add(P(-7 + 2 * t, 9 - 3 * t));                      // → NWDDD
        for (var t = 0.1; t <= 1; t += 0.1) pts.Add(P(-5 + 2 * t, 6 - (11 + k * 0.2) * t));          // turned south
        for (var x = -2.0; x <= 12; x += 1) pts.Add(P(x, -5));                                      // a south downwind
        for (var y = -4.0; y <= -1; y += 1) pts.Add(P(12, y));                                      // base
        for (var x = 11.0; x >= 1.5; x -= 1) pts.Add(P(x, 0));                                       // final
        prof.Samples.Add(new AirportProfile.SampleTrack { Kind = "arrival", Runway = "27", Points = pts });
    }
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 41) { Automatic = false, Wind = new Wind(270, 5), Learned = prof };
    world.SelectRunways(true);
    var f = world.SpawnArrival(45, true)!;
    var downwind = new V2(9 * nm, 5 * nm);
    var minToDownwind = double.MaxValue;
    for (var d = 0.0; d < f.Path!.Length; d += 200) minToDownwind = Math.Min(minToDownwind, (f.Path.At(d).p - downwind).Length);
    var nearDddd = double.MaxValue;
    for (var d = 0.0; d < f.Path.Length; d += 200) nearDddd = Math.Min(nearDddd, (f.Path.At(d).p - new V2(-5 * nm, 6 * nm)).Length);
    Console.WriteLine($"  {f.Route}; passes {minToDownwind / nm:0.0} nm from the real downwind, {nearDddd / nm:0.0} nm from the STAR's last fix");
    Check(f.Route.Contains("real traffic"), "flies the vectors real traffic flew");
    Check(minToDownwind < 1.5 * nm, "out to the real downwind");
    Check(nearDddd > 2 * nm, "turned off the STAR where real traffic is, not flown to its end");
    var southDw = double.MaxValue;
    for (var d = 0.0; d < f.Path.Length; d += 200) southDw = Math.Min(southDw, (f.Path.At(d).p - new V2(6 * nm, -5 * nm)).Length);
    Check(southDw > 4 * nm, $"not out to the downwind of traffic that only ran beside the STAR ({southDw / nm:0.0} nm from it)");
    // Started close in, already past where real traffic turns off the STAR (UAL1664 at KORD when traffic started, 19 Nov):
    // it joins the real track passing nearest, not a made-up downwind.
    var g = world.SpawnArrival(20, true, entry: (new V2(-6 * nm, 10.5 * nm), 6000, 6000))!;
    var gDw = double.MaxValue;
    for (var d = 0.0; d < g.Path!.Length; d += 200) gDw = Math.Min(gDw, (g.Path.At(d).p - downwind).Length);
    Console.WriteLine($"  started close in: {g.Route}; passes {gDw / nm:0.0} nm from the real downwind");
    Check(g.Route.Contains("real traffic") && gDw < 1.5 * nm, "an arrival started past the turn-off joins the nearest real track");
    var north = data.Arrivals.First(a => a.Name == "NORTH1");
    var hN = world.SpawnArrival(45, true, planned: (world.ArrRwy, (north, (Laia.Common.Airports.Transition?)null)))!;
    var hDw = double.MaxValue;
    for (var d = 0.0; d < hN.Path!.Length; d += 200) hDw = Math.Min(hDw, (hN.Path.At(d).p - downwind).Length);
    Console.WriteLine($"  on a STAR no real track flew: {hN.Route}; passes {hDw / nm:0.0} nm from the real downwind");
    Check(hN.Route.Contains("NORTH1") && hN.Route.Contains("real traffic") && hDw < 1.5 * nm, "from the end of a STAR no real track flew, the way real arrivals passing there flew on");
}

Console.WriteLine("== Real tracks chosen for the need: an empty sky gets the short way real controllers give when quiet (21 Nov, AMZ3838)");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    data.Arrivals.Clear();
    var map = new LocalMap(data.Lat, data.Lon);
    Laia.Common.Airports.ProcedureLeg Fx(string name, double xNm, double yNm, int type)
    {
        var (la, lo) = map.ToLatLon(new V2(xNm * nm, yNm * nm));
        return new() { Type = type, Fix = name, Lat = la, Lon = lo };
    }
    data.Arrivals.Add(new() { Name = "NEAST1", Legs = { Fx("NEAAA", 20, 30, 15), Fx("NEBBB", 12, 12, 18), Fx("NECCC", 14, 6, 18) } });
    double[] P(double x, double y) { var (la, lo) = map.ToLatLon(new V2(x * nm, y * nm)); return new[] { la, lo, 6000 }; }
    var prof = new AirportProfile { Icao = "TEST", Source = "test", Day = "test" };
    // Six short ways in, flown at quiet times (2 h apart): off at NEBBB, straight down to a short base, final 10 nm out.
    // Six long ones, flown in a rush (2 min apart): off at NEBBB onto a downwind 5 nm north, base 20 nm east.
    for (var k = 0; k < 12; k++)
    {
        var quiet = k < 6; var v = (k % 6) * 0.1;
        var pts = new List<double[]>();
        for (var t = 0.0; t <= 1; t += 0.05) pts.Add(P(20 - 8 * t, 30 - 18 * t));
        if (quiet)
        {
            for (var y = 11.0; y >= 4; y -= 1) pts.Add(P(12 + v, y));
            pts.Add(P(11, 1.5)); pts.Add(P(10, 0.2));
            for (var x = 9.0; x >= 1.5; x -= 1) pts.Add(P(x, 0));
        }
        else
        {
            for (var t = 0.1; t <= 1; t += 0.1) pts.Add(P(12 + 8 * t, 12 - (7 + v) * t));
            for (var y = 4.0; y >= 1; y -= 1) pts.Add(P(20, y));
            for (var x = 19.0; x >= 1.5; x -= 1) pts.Add(P(x, 0));
        }
        prof.Samples.Add(new AirportProfile.SampleTrack { Kind = "arrival", Runway = "27", Points = pts, T = quiet ? 1_000_000 + k * 7200 : 2_000_000 + k * 120 });
    }
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 43) { Automatic = false, Wind = new Wind(270, 5), Learned = prof };
    world.SelectRunways(true);
    var f = world.SpawnArrival(40, true)!;
    double Near(V2 at) { var d = double.MaxValue; for (var s0 = 0.0; s0 < f.Path!.Length; s0 += 200) d = Math.Min(d, (f.Path.At(s0).p - at).Length); return d / nm; }
    Console.WriteLine($"  alone: {f.Route}; {Near(new V2(12 * nm, 4 * nm)):0.0} nm from the quiet short base, {Near(new V2(20 * nm, 5 * nm)):0.0} nm from the rush downwind");
    Check(f.Route.Contains("real traffic") && Near(new V2(12 * nm, 4 * nm)) < 1.5 && Near(new V2(20 * nm, 5 * nm)) > 4, "alone, it takes the short way real traffic was given when quiet");
    // Smoothing: no turn sharper than an airliner flies on it.
    var maxRate = 0.0; double? pb = null;
    for (var s0 = 0.0; s0 + 200 < f.Path!.Length; s0 += 200)
    {
        var b = (f.Path.At(s0 + 200).p - f.Path.At(s0).p).Bearing;
        if (pb is { } p0) maxRate = Math.Max(maxRate, Math.Abs(Geo2.Diff(b, p0)));
        pb = b;
    }
    Console.WriteLine($"  sharpest change of direction along its path: {maxRate:0}° per 200 m");
    Check(maxRate < 20, "its real-traffic path turns smoothly");
    // One right behind it with time to lose takes a longer real way in (before vectors).
    var logR = new List<string>(); world.Log = l => logR.Add(l);
    var g2 = world.SpawnArrival(37, true)!;
    for (var t = 0.0; t < 240; t += 0.5) world.Step(0.5);
    Console.WriteLine($"  two together: {string.Join(" | ", logR.Where(l => l.Contains("real track") || l.Contains("for the sequence")).Take(3).Select(l => l.Length > 140 ? l[..140] : l))}");
    var second = f.Seq > g2.Seq ? f : g2;
    Check(logR.Any(l => l.StartsWith(second.Callsign + ":") && l.Contains("longer real track")) && second.FanIdx > 0, $"the one behind ({second.Callsign}), with time to lose, is put on a longer real way in");
    // Simplify and TotalTurn (used for the departures' loop check).
    var zig = new List<V2>(); for (var i = 0; i < 20; i++) zig.Add(new V2(i * nm, (i % 2) * 0.1 * nm));
    Check(TrafficWorld.Simplify(zig, 0.25 * nm).Count == 2, "a wiggling line within a quarter mile simplifies to a straight leg");
    var loop = new List<V2>(); for (var a = 0; a <= 380; a += 20) loop.Add(V2.FromBearing(a, 3 * nm) + new V2(0, 0));
    Check(TrafficWorld.TotalTurn(loop, 25 * nm) > 300, "a loop is measured as one");
}

TrafficWorld.CopyRealTracks = false;
Console.WriteLine("== No copied routes (user, 28 Nov): with real tracks learned, the way in is LAIA's own downwind, base and final, shaped as real controllers fly it");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var map = new LocalMap(data.Lat, data.Lon);
    double[] P(double x, double y) { var (la, lo) = map.ToLatLon(new V2(x * nm, y * nm)); return new[] { la, lo, 6000 }; }
    var prof = new AirportProfile { Icao = "TEST", Source = "test", Day = "test" };
    prof.Arrivals["27"] = new AirportProfile.RunwayArrivals { Count = 40, JoinP25 = 13, JoinP50 = 14, JoinP75 = 15, BaseNm = 17, DownwindNm = 5, InterceptDeg = 25 };
    for (var k = 0; k < 6; k++)
    {
        var pts = new List<double[]>();
        for (var x = -20.0; x <= 6; x += 1) pts.Add(P(x, 9));
        for (var x = 7.0; x <= 12; x += 1) pts.Add(P(x, 5));
        for (var x = 11.0; x >= 1.5; x -= 1) pts.Add(P(x, 0));
        prof.Samples.Add(new AirportProfile.SampleTrack { Kind = "arrival", Runway = "27", Points = pts });
    }
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 41) { Automatic = false, Wind = new Wind(270, 5), Learned = prof };
    world.SelectRunways(true);
    var f = world.SpawnArrival(45, true)!;
    var e27 = world.Apt.Ends.First(e => e.Ident == "27");
    // Where it's established on the final: the last point of its way in that's off the centreline.
    double joinOut = double.NaN;
    for (var d = f.Path!.Length; d > 0; d -= 200) { var q = f.Path.At(d).p; if (e27.Along(q) < 0 && Math.Abs(e27.Offset(q)) > 0.3 * nm) { joinOut = -e27.Along(q) / nm; break; } }
    Console.WriteLine($"  {f.Route}; joins the final about {joinOut:0.0} nm out (real controllers here: 13–15)");
    Check(!f.Route.Contains("real traffic"), "no copied real track in its way in");
    Check(joinOut is > 10 and < 19, "its final is joined where real controllers here join it (the learned shape)");
    var g0 = TrafficWorld.GeneralShape;
    TrafficWorld.GeneralShape = new VectorShape(14, 18, 5, 25);
    var w2 = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 41) { Automatic = false, Wind = new Wind(270, 5) };
    w2.SelectRunways(true);
    var f2 = w2.SpawnArrival(45, true)!;
    double join2 = double.NaN;
    for (var d = f2.Path!.Length; d > 0; d -= 200) { var q = f2.Path.At(d).p; if (e27.Along(q) < 0 && Math.Abs(e27.Offset(q)) > 0.3 * nm) { join2 = -e27.Along(q) / nm; break; } }
    TrafficWorld.GeneralShape = g0;
    Console.WriteLine($"  an airport with nothing learned, the pattern learned across the hubs (final 14 nm): {f2.Route}; joins about {join2:0.0} nm out");
    Check(join2 is > 10 and < 19, "an airport with nothing of its own learned flies the pattern learned across the hubs");
}

Console.WriteLine("== KATL's ATIS info I (21 Nov): three landing runways, and how old a broadcast is");
{
    var txt = "ATL ARR INFO I 1952Z. 07014G20KT 8SM -RA FEW065 BKN095 OVC150 14/11 A3005 (THREE ZERO ZERO FIVE). SIMULTANEOUS APCHS IN USE ILS 8L, ILS 9R, ILS 10. NOTAMS... RWY 9L PAPI OTS. IM RWY 8L OTS, IM RWY 9R OTS, IM RWY 10 OTS, DME PORTION OF ILS RWY 9L OTS. BIRD ACTIVITY VC OF ARPT. CRANE MIDFIELD BETWEEN RWY 9R AND RWY 10 AT 80 FT. CRANE OVER E CONCOURSE 80 FT. OPERATE TRANSPONDER WITH MODE C ON ALL RAMPS, TWYS AND RWYS. ...ADVS YOU HAVE INFO I.";
    var (dep, arr, closed) = AtisParse.Runways(txt);
    Console.WriteLine($"  landing {string.Join(", ", arr)}; departing {string.Join(", ", dep)}; closed {string.Join(", ", closed)}");
    Check(arr.SequenceEqual(new[] { "08L", "09R", "10" }) && closed.Count == 0, "KATL info I: landing 08L, 09R, 10 (the inner markers out of service aren't runways)");
    var age = AtisParse.AgeMinutes(txt, new DateTime(2026, 11, 21, 20, 13, 0, DateTimeKind.Utc));
    var age2 = AtisParse.AgeMinutes("ATL ARR INFO Z 2350Z. ...", new DateTime(2026, 11, 22, 0, 5, 0, DateTimeKind.Utc));
    Console.WriteLine($"  issued 1952Z, read at 2013Z: {age} min old; 2350Z read at 0005Z: {age2} min");
    Check(age == 21 && age2 == 15, "a broadcast's age from its issue time, across midnight too");
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
    Check(east is not null && east.Hours == (7 * 60, 23 * 60) && rules.ForSid("JEEMY4", 12 * 60) is not null && rules.ForSid("JEEMY4", 0) is null,
        "the rules apply 0700-2300 local only (at midnight the general logic and ATIS decide)");
    // The same page with the text out of reading order (as PDF readers often give it): headings after the rules.
    var jumbled = string.Join("\n", new[]
    {
        "DDANY, MZULO, JEEMY, FATHE - Expect to Depart Runway 35L", "RDSOX, OSPRY, FSHUN - Expect to Depart Runway 36R",
        "5. SPECIFIC INFORMATION: 0700-2300 local runway 36L/R, 35L/R RNAV simultaneous", "Departing Runways 35L/R 17L/R", "Departing Runways 36L/R 18L/R",
    });
    var r2 = AirportRules.Parse(jumbled, sids, stars, "RNAV DEPARTURE AAUP");
    Console.WriteLine("  out of order: " + r2.Describe());
    Check(r2.ForSid("FATHE4") is { } fe && string.Join(" ", fe.Runways) == "35L 35R 17L 17R" && fe.Hours is not null && r2.ForSid("OSPRY1") is { } ow && string.Join(" ", ow.Runways) == "36L 36R 18L 18R",
        "out of reading order it still finds each rule's runway group (both flows) and the hours");
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

Console.WriteLine("== A rush of arrivals far out: planned with speed, delay vectors and the trombone — never a hold (KORD, 18 Nov)");
{
    var data = TestAirport.Build(false);
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 21) { Automatic = false, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s2 => log.Add($"{world.Time:0} {s2}");
    world.SelectRunways(true);
    Laia.Common.Airports.ScheduleLeg L(string reg, string fl) => new() { Airline = "UAL", Operator = "UAL", Type = "B738", Reg = reg, Flight = fl, Dep = "KXXX", Arr = "TEST" };
    var rush = new List<Flight>();
    var rng = new Random(3);
    for (var k = 0; k < 12; k++)
    {
        var f = world.SpawnArrival(70 + rng.NextDouble() * 40, false, L($"N{k:00}", $"{100 + k}"));
        if (f is not null) rush.Add(f);
        for (var t = 0; t < 120; t++) world.Step(0.5);   // one a minute: half as many again as the runway takes
    }
    var lands = new Dictionary<string, double>();
    var lastHdg = new Dictionary<Flight, double>();
    var sharp = new List<string>();
    for (var t = 0.0; t < 3 * 3600 && lands.Count < rush.Count; t += 0.5)
    {
        world.Step(0.5);
        foreach (var f in rush) if (f.OnGround && !lands.ContainsKey(f.Callsign)) lands[f.Callsign] = world.Time;
        // Turns an airliner can fly (user, 19 Nov, UAL415: "a sharp turn that's not even possible"): no more than
        // 8°/s anywhere in the air (25° of bank is about 2.5°/s at 200 kt, 4°/s at 140).
        foreach (var f in rush)
        {
            if (f.OnGround || f.Path is null || f.Done) { lastHdg.Remove(f); continue; }
            var h = f.Path.At(f.S).dir;
            if (lastHdg.TryGetValue(f, out var h0) && Math.Abs(Geo2.Diff(h0, h)) > 4 && sharp.Count < 6)
            {
                var tNow = world.Time;
                var near = log.LastOrDefault(l => l.Contains(f.Callsign + ":") && double.TryParse(l.Split(' ')[0], out var lt) && lt >= tNow - 2) ?? "(nothing logged)";
                sharp.Add($"{f.Callsign} {Math.Abs(Geo2.Diff(h0, h)) * 2:0}°/s at {tNow:0} s, {(f.ThrS - f.S) / Geo2.MPerNm:0.0} nm to go, {f.Phase}, S {f.S:0} m; {near}");
            }
            lastHdg[f] = h;
        }
    }
    var order = lands.OrderBy(x => x.Value).ToList();
    var gaps = order.Zip(order.Skip(1), (a, b) => b.Value - a.Value).ToList();
    int C(string k) => log.Count(l => l.Contains(k));
    var negative = log.Count(l => l.Contains("breaking off") && System.Text.RegularExpressions.Regex.IsMatch(l, @"\(-\d+(\.\d)? nm behind"));
    Console.WriteLine("  landings: " + string.Join(", ", order.Select(o => $"{o.Key} {o.Value:0}")));
    Console.WriteLine($"  {rush.Count} arrivals, {lands.Count} landed in {(order.Count > 0 ? (order[^1].Value - order[0].Value) / 60 : 0):0} min; gaps min {(gaps.Count > 0 ? gaps.Min() : 0):0} s, average {(gaps.Count > 0 ? gaps.Average() : 0):0} s, max {(gaps.Count > 0 ? gaps.Max() : 0):0} s");
    Console.WriteLine($"  speed {C("reduce speed")}, delay vectors {C("delay for the sequence")}, vectors for spacing {C("vectors for spacing")}, downwind changes {C("base turn now")}, holds {C("holding")}, 360s {C("making a 360")}, break-offs {C("breaking off")}, go-arounds {C("going around")}, spaced behind one behind it {negative}");
    foreach (var l in log.Where(l => l.Contains("breaking off") || l.Contains("going around") || l.Contains("now ahead of")).Take(8)) Console.WriteLine("    " + l);
    foreach (var l in log.Where(l => l.Contains("delay for the sequence")).Take(4)) Console.WriteLine("    " + l);
    foreach (var cs in log.Where(l => l.Contains("breaking off")).Select(l => l.Split(' ')[1].TrimEnd(':')).Distinct().Take(2))
        foreach (var l in log.Where(l => l.Contains(cs + ":")).Take(14)) Console.WriteLine("      " + l);
    Check(lands.Count == rush.Count, "every arrival lands");
    Check(gaps.Count > 0 && gaps.Min() >= 75, "spaced at the runway (75 s or more between landings)");
    Check(C("holding") == 0 && C("making a 360") == 0, "no holds and no 360s");
    Check(C("failed") == 0, "no step of the arrival manager fails");
    // Half as many again as the runway can take: a break-off or two at the end of the rush is what a controller would
    // do too; more means the delay isn't being planned far out.
    Check(C("breaking off") <= 2 && C("going around") == 0, "at most two break-offs in the whole rush, no go-arounds");
    Check(negative == 0, "nobody is broken off for an aircraft that's behind it");
    // Nobody spaces on an aircraft that is at the same time spacing on it (UAL538/DAL1390 at KORD, 19 Nov).
    var spacing = log.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^(\d+) (\w+): .*?(?:\d nm behind|from|s behind) (\w+)[ ,]"))
        .Where(m => m.Success).Select(m => (t: double.Parse(m.Groups[1].Value), a: m.Groups[2].Value, b: m.Groups[3].Value)).ToList();
    var mutual = spacing.Where(x => spacing.Any(y => y.a == x.b && y.b == x.a && Math.Abs(y.t - x.t) < 60)).Select(x => $"{x.a}/{x.b} at {x.t:0}").Distinct().Take(4).ToList();
    foreach (var x in mutual) Console.WriteLine("    each spacing on the other: " + x);
    Check(mutual.Count == 0, "no two arrivals each spacing on the other");
    foreach (var x in sharp) Console.WriteLine("    turned too sharply: " + x);
    Check(sharp.Count == 0, "no turn sharper than 8°/s in the air (vectors and break-offs start with a real turn)");
}

Console.WriteLine("== The trombone: arrivals 3.5 nm apart on the downwind are spaced by moving their base turns — no holds, no 360s (CYYZ, 17 Nov)");
{
    var data = TestAirport.Build(false);
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 5) { Automatic = false, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s2 => log.Add($"{world.Time:0} {s2}");
    world.SelectRunways(true);
    var rwy = world.ArrRwy!;
    var left = new V2(-rwy.Dir.Y, rwy.Dir.X);
    var nm = Geo2.MPerNm;
    Laia.Common.Airports.ScheduleLeg L(string reg, string fl) => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = reg, Flight = fl, Dep = "KXXX", Arr = "TEST" };
    var fl3 = new List<Flight>();
    foreach (var (k, along) in new[] { (1, 11.0), (2, 14.5), (3, 18.0) })
    {
        var pos = rwy.Threshold + rwy.Dir * (along * nm) + left * (6 * nm);
        var f = world.SpawnArrival(30, false, L($"N{k}", $"{k}{k}"), entry: (pos, 7000, 7000));
        if (f is not null) fl3.Add(f);
    }
    var lands = new Dictionary<string, double>();
    for (var t = 0.0; t < 3600 && lands.Count < fl3.Count; t += 0.5)
    {
        world.Step(0.5);
        foreach (var f in fl3) if (f.OnGround && !lands.ContainsKey(f.Callsign)) lands[f.Callsign] = world.Time;
    }
    var order = lands.OrderBy(x => x.Value).ToList();
    var gaps = order.Zip(order.Skip(1), (a, b) => b.Value - a.Value).ToList();
    var holds = log.Count(l => l.Contains("holding for spacing")); var orbits = log.Count(l => l.Contains("360")); var breaks = log.Count(l => l.Contains("breaking off"));
    var vecs = log.Count(l => l.Contains("vectors for spacing")); var tromb = log.Where(l => l.Contains("base turn now")).ToList();
    var gas = log.Count(l => l.Contains("going around"));
    Console.WriteLine($"  {fl3.Count} spawned, {lands.Count} landed; gaps {string.Join(", ", gaps.Select(g => $"{g:0} s"))}; trombone moves {tromb.Count}, vectors {vecs}, holds {holds}, 360s {orbits}, break-offs {breaks}, go-arounds {gas}");
    foreach (var l in tromb.Take(10)) Console.WriteLine("    " + l);
    foreach (var f in fl3.Where(f => !lands.ContainsKey(f.Callsign))) Console.WriteLine($"    not landed: {f.Callsign} {f.Phase} seq {f.Seq} {(f.ThrS - f.S) / Geo2.MPerNm:0.0} nm to go, {f.AltFt:0} ft, {f.Ias:0} kt");
    foreach (var l in log.Where(l => l.Contains("vectors for spacing") || l.Contains("going around") || l.Contains("landed")).Take(8)) Console.WriteLine("    " + l);
    Check(fl3.Count == 3 && lands.Count == 3, "all three land");
    Check(gaps.All(g => g >= 80) && gas == 0, "spaced at the runway (at least 80 s apart), no go-around");
    Check(tromb.Count > 0 && holds == 0 && orbits == 0 && breaks == 0, "the downwind is extended for them: no hold, 360 or break-off");
    Check(gaps.All(g => g <= 200), "and not spread out more than needed (under 200 s apart)");
}

Console.WriteLine("== Winds aloft: the FD forecast read, and the user's own sim wind is the reference near them (17 Nov)");
{
    var fd = string.Join("\n", new[]
    {
        "000", "FBUS31 KWNO 171359", "FD1US1", "DATA BASED ON 171200Z", "VALID 171800Z   FOR USE 1400-2100Z. TEMPS NEG ABV 24000", "",
        "FT  3000    6000    9000   12000   18000   24000  30000  34000  39000",
        "ABI      2523+11 2622+06 2523+00 2533-14 2546-27 255042 255452 256062",
        "BHM 3010 2711+10 2717+05 2725+00 2747-12 2766-24 279739 790846 780950",
        "DEN" + new string(' ', 14) + "2715+02 2826-04 2846-16 2864-28 760041 761151 760761",
        "MIA 9900 0505+20 0610+15 0715+10 0820-05 0925-17 103033 103543 104055",
    });
    var st = WindsAloft.ParseFd(fd);
    var bhm = st.FirstOrDefault(x => x.id == "BHM").winds; var den = st.FirstOrDefault(x => x.id == "DEN").winds; var abi = st.FirstOrDefault(x => x.id == "ABI").winds;
    var mia = st.FirstOrDefault(x => x.id == "MIA").winds;
    string W(V2? v) => v is { } x ? $"{WindsAloft.ToWind(x).FromDeg:000}/{WindsAloft.ToWind(x).Kt:0}" : "-";
    Console.WriteLine($"  {st.Count} stations; BHM 3k {W(bhm?[0])} 39k {W(bhm?[8])}; DEN 3k {W(den?[0])} 9k {W(den?[2])}; ABI 3k {W(abi?[0])} 6k {W(abi?[1])}; MIA 3k {W(mia?[0])}");
    Check(st.Count == 4, "four stations read (header lines skipped)");
    bool Is(V2? v, double dir, double kt) => v is { } x && Math.Abs(Geo2.Diff(WindsAloft.ToWind(x).FromDeg, dir)) < 0.5 && Math.Abs(x.Length - kt) < 0.5;
    Check(Is(bhm?[0], 300, 10) && Is(bhm?[8], 280, 109) && Is(bhm?[7], 290, 108), "direction and speed decoded, 100 kt and over too (780950 = 280° at 109 kt)");
    Check(den?[0] is null && den?[1] is null && Is(den?[2], 270, 15) && abi?[0] is null && Is(abi?[1], 250, 23), "levels below the station are blank");
    Check(mia?[0] is { } m0 && m0.Length < 0.5, "9900 is light and variable");

    var field = new WindsAloft();
    field.SetForecast(new[] { new WindsAloft.Station("BHM", 33.56, -86.75, 650, bhm!) }, DateTime.UtcNow);
    var sfc = new Wind(180, 10);
    var now = DateTime.UtcNow;
    var at21 = field.At(now, 33.56, -86.75, 21000, sfc, 650);
    var at1 = field.At(now, 33.56, -86.75, 650, sfc, 650);
    var at18 = field.At(now, 33.56, -86.75, 1800, sfc, 650);
    Console.WriteLine($"  over BHM: 21,000 ft {at21.FromDeg:000}/{at21.Kt:0.0}; surface {at1.FromDeg:000}/{at1.Kt:0}; 1,800 ft {at18.FromDeg:000}/{at18.Kt:0}");
    Check(Math.Abs(at21.FromDeg - 270) < 1 && Math.Abs(at21.Kt - 56.5) < 1, "between levels: interpolated (270° at 47 and 66 kt → 56)");
    Check(Math.Abs(at1.FromDeg - 180) < 1 && Math.Abs(at1.Kt - 10) < 0.5, "at the ground: the surface wind");
    // The user at FL340 over BHM finds 300° at 80 kt (the forecast said 290° at 108).
    field.AddUserSample(now, 33.56, -86.75, 34000, 300, 80, sfc, 650);
    var here = field.At(now, 33.56, -86.75, 34000, sfc, 650);
    var far = field.At(now, 33.56 + 3.33, -86.75, 34000, sfc, 650);   // 200 nm north
    var low = field.At(now, 33.56, -86.75, 18000, sfc, 650);
    Console.WriteLine($"  after the user's reading: here {here.FromDeg:000}/{here.Kt:0}; 200 nm away {far.FromDeg:000}/{far.Kt:0}; here at 18,000 ft {low.FromDeg:000}/{low.Kt:0}");
    Check(Math.Abs(Geo2.Diff(here.FromDeg, 300)) < 2 && Math.Abs(here.Kt - 80) < 3, "where the user is, LAIA's aircraft get the user's wind");
    Check(Math.Abs(far.Kt - 108) < 15 && Math.Abs(Geo2.Diff(far.FromDeg, 290)) < 6, "200 nm away, mostly the forecast");
    Check(Math.Abs(low.Kt - 47) < 3, "far below the user's level, the forecast for that level");
    var prof = field.Profile(now, 33.56, -86.75, sfc, 650);
    var p34 = WindsAloft.FromProfile(prof, 34000);
    Check(prof.Length == WindsAloft.ProfileSteps && Math.Abs(p34.Kt - here.Kt) < 1, "an airport's profile matches the field");
    var none = new WindsAloft().At(now, 40, -100, 30000, sfc, 0);
    Check(none.FromDeg == 180 && none.Kt == 10, "with no forecast and no readings: the surface wind (as before)");
}

Console.WriteLine("== Real flights from ADS-B (the LAIA Collector) and the real timetable built from them (user, 24 Nov)");
{
    var aptA = new TraceLegs.Apt("KAAA", 40.0, -75.0, 100);
    var aptB = new TraceLegs.Apt("KBBB", 41.0, -77.0, 600);
    IEnumerable<TraceLegs.Apt> Near(double la, double lo) => new[] { aptA, aptB };
    // One aircraft's day: on the ground at KAAA, up, across to KBBB (the receivers lose it at 1,400 ft), on the ground
    // there, then back to KAAA, seen to the ground.
    var t0 = new DateTimeOffset(2026, 11, 23, 14, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    var pts = new List<string>();
    void P(double dt, double la, double lo, string alt, string? cs = null) =>
        pts.Add($"[{dt},{la},{lo},{alt},250,270,0,0{(cs is null ? "" : $",{{\"flight\":\"{cs}   \"}}")}]");
    P(0, 40.0, -75.0, "\"ground\"", "DAL123"); P(300, 40.0, -75.01, "\"ground\"");
    for (var k = 0; k <= 40; k++) P(400 + k * 90, 40.0 + k * 0.025, -75.01 - k * 0.05, Math.Min(35000, Math.Min(400 + k * 2000, 3000 + (40 - k) * 2000)).ToString(), k == 5 ? "DAL123" : null);
    P(400 + 41 * 90, 40.99, -76.99, "1400");
    P(9000, 41.0, -77.0, "\"ground\"", "DAL124"); P(9600, 41.0, -77.0, "\"ground\"");
    for (var k = 0; k <= 40; k++) P(9700 + k * 90, 41.0 - k * 0.025, -77.0 + k * 0.05, Math.Min(33000, Math.Min(1200 + k * 1800, 900 + (40 - k) * 1800)).ToString());
    P(9700 + 41 * 90, 40.0, -75.0, "\"ground\"");
    var json = $"{{\"icao\":\"a1b2c3\",\"r\":\"N123DL\",\"t\":\"B739\",\"timestamp\":{t0},\"trace\":[{string.Join(",", pts)}]}}";
    var legs = TraceLegs.Extract(System.Text.Encoding.UTF8.GetBytes(json), Near);
    Console.WriteLine("  " + string.Join("; ", legs.Select(l => $"{l.Callsign} {l.Type} {l.Reg} {l.Dep}→{l.Arr} {DateTimeOffset.FromUnixTimeSeconds((long)l.DepT):HH:mm}–{DateTimeOffset.FromUnixTimeSeconds((long)l.ArrT):HH:mm}")));
    Check(legs.Count == 2 && legs[0].Dep == "KAAA" && legs[0].Arr == "KBBB" && legs[1].Dep == "KBBB" && legs[1].Arr == "KAAA", "both flights, each from its ground stop to the next");
    Check(legs.Count == 2 && legs[0].Callsign == "DAL123" && legs[1].Callsign == "DAL124" && legs[0].Reg == "N123DL" && legs[0].Type == "B739", "callsign, registration and type from the trace");
    Check(legs.Count == 2 && Math.Abs(legs[0].DepT - (t0 + 300)) < 5 && legs[0].ArrT > t0 + 400 + 41 * 90, "take-off from the last ground point; a landing lost at 1,400 ft worked on from there");
    // A timetable from two weeks of the same flights, plus one with no installed livery and one with an end not seen.
    var day1 = legs.ToList();
    var day2 = legs.Select(l => l with { DepT = l.DepT - 7 * 86400, ArrT = l.ArrT - 7 * 86400 }).ToList();
    day1.Add(new TraceLegs.Leg("XYZ900", "A320", "N900XY", "KAAA", "KBBB", t0 + 3600, t0 + 7200));
    day1.Add(new TraceLegs.Leg("DAL777", "B739", "N777DL", "", "KBBB", double.NaN, t0 + 20000));
    var aigByReg = new Dictionary<string, RealTimetable.AigInfo> { ["N123DL"] = new("DAL", "AIG Delta 739 N123DL", "DAL") };
    var aigRoute = new Dictionary<string, (string, string)> { ["DAL777"] = ("KAAA", "KBBB") };
    var tt = RealTimetable.Build(new[] { day1, day2 }, (a, t) => a == "DAL", aigByReg, aigRoute, c => c == "KAAA" ? (40.0, -75.0) : (41.0, -77.0));
    Console.WriteLine("  " + tt.Summary + ": " + string.Join("; ", tt.Legs.Select(l => $"{l.Callsign} {l.Dep}→{l.Arr} {l.DepMin / 1440}d{l.DepMin % 1440 / 60:00}:{l.DepMin % 60:00} \"{l.Title}\"")));
    Check(tt.Legs.Count(l => l.Callsign == "DAL123") == 1, "the same flight on the same weekday two weeks running: once (the newest)");
    Check(tt.Legs.FirstOrDefault(l => l.Callsign == "DAL123") is { Title: "AIG Delta 739 N123DL" } d1 && d1.DepMin == RealTimetable.WeekMinute(t0 + 300 - 12 * 60), "the AIG airframe's exact model, and push-back 12 minutes before take-off");
    Check(!tt.Legs.Any(l => l.Callsign == "XYZ900") && tt.NoLivery >= 1, "no installed livery: the flight is skipped");
    Check(tt.Legs.FirstOrDefault(l => l.Callsign == "DAL777") is { Dep: "KAAA" } d7 && d7.DepMin < d7.ArrMin, "an end the receivers didn't see comes from the AIG flight, its time from the distance");
    var good = RealTimetable.GoodAirports(tt, c => c == "KAAA" ? 2 : 200);
    Check(!good.Contains("KAAA") && !good.Contains("KBBB"), "used only where it has 14+ flights a week and half the AIG timetable's");
}

Console.WriteLine("== A vectored SID heading skips a fix left behind it (24 Nov, KMSP SNOWZ1 off 17 zigzagged 'turning 383°')");
{
    var apt = new AirportModel(TestAirport.Build(false));
    Laia.Common.Airports.ProcedureLeg Fix(int type, string name, V2 at, double course = 0)
    {
        var (la, lo) = apt.Map.ToLatLon(at);
        return new() { Type = type, Fix = name, Lat = la, Lon = lo, Course = course, TrueCourse = true };
    }
    var nm = Geo2.MPerNm;
    var a = new V2(0, -3 * nm);                                     // RUBRB, south of the runway end
    var b = a + new V2(-1.75 * nm, 1.0 * nm);                       // MANCY, 2 nm north-west of it (behind the heading)
    var c = a + new V2(-6.5 * nm, -6.5 * nm);                       // SNOWZ, 9 nm south-west
    var legs = new List<Laia.Common.Airports.ProcedureLeg> { Fix(4, "RUBRB", a, 220), Fix(11, "RUBRB", a, 215), Fix(18, "MANCY", b), Fix(15, "MANCY", b), Fix(18, "SNOWZ", c) };
    var pts = apt.LegsToPoints(legs, new V2(0, 0), 1000, out _, hybrid: true);
    double turned = 0;
    var all = new[] { new V2(0, 0) }.Concat(pts.Select(p => p.P)).ToList();
    for (var i = 2; i < all.Count; i++) turned += Math.Abs(Geo2.Diff((all[i - 1] - all[i - 2]).Bearing, (all[i] - all[i - 1]).Bearing));
    Console.WriteLine($"  route: {string.Join(" → ", pts.Select(p => p.Fix.Length > 0 ? p.Fix : "·"))}; turns {turned:0}° in all");
    Check(!pts.Any(p => p.Fix == "MANCY") && pts.Any(p => p.Fix == "SNOWZ"), "the fix behind the heading is left out, the next one ahead flown to");
    Check(turned < 120, "no zigzag back to it");
}

Console.WriteLine("== Winds aloft from Open-Meteo (the FD tables were discontinued, Nov 2026): pressure levels to the forecast levels");
{
    var json = """
    [{"latitude":42.0,"longitude":-71.0,"elevation":30.0,"hourly":{"time":["2026-11-23T13:00","2026-11-23T14:00","2026-11-23T15:00"],
      "wind_speed_925hPa":[5,20,5],"wind_direction_925hPa":[180,200,180],"wind_speed_850hPa":[5,30,5],"wind_direction_850hPa":[180,220,180],
      "wind_speed_700hPa":[5,40,5],"wind_direction_700hPa":[180,250,180],"wind_speed_600hPa":[5,50,5],"wind_direction_600hPa":[180,260,180],
      "wind_speed_500hPa":[5,60,5],"wind_direction_500hPa":[180,270,180],"wind_speed_400hPa":[5,80,5],"wind_direction_400hPa":[180,270,180],
      "wind_speed_300hPa":[5,110,5],"wind_direction_300hPa":[180,270,180],"wind_speed_250hPa":[5,120,5],"wind_direction_250hPa":[180,270,180],
      "wind_speed_200hPa":[5,100,5],"wind_direction_200hPa":[180,270,180]}},
     {"latitude":39.75,"longitude":-105.0,"elevation":1650.0,"hourly":{"time":["2026-11-23T14:00"],
      "wind_speed_925hPa":[10],"wind_direction_925hPa":[90],"wind_speed_850hPa":[10],"wind_direction_850hPa":[90],
      "wind_speed_700hPa":[25],"wind_direction_700hPa":[270],"wind_speed_600hPa":[35],"wind_direction_600hPa":[270],
      "wind_speed_500hPa":[50],"wind_direction_500hPa":[270],"wind_speed_400hPa":[70],"wind_direction_400hPa":[270],
      "wind_speed_300hPa":[90],"wind_direction_300hPa":[270],"wind_speed_250hPa":[95],"wind_direction_250hPa":[270],
      "wind_speed_200hPa":[80],"wind_direction_200hPa":[270]}}]
    """;
    var st = WindsAloft.ParseOpenMeteo(json, new DateTime(2026, 11, 23, 14, 10, 0, DateTimeKind.Utc));
    string W(V2? v) => v is { } x ? $"{WindsAloft.ToWind(x).FromDeg:000}/{WindsAloft.ToWind(x).Kt:0}" : "-";
    var bos = st.FirstOrDefault(x => x.Lat > 41)?.Winds; var den = st.FirstOrDefault(x => x.Lat < 40)?.Winds;
    Console.WriteLine($"  {st.Count} points; Boston 3k {W(bos?[0])} 9k {W(bos?[2])} 30k {W(bos?[6])} 39k {W(bos?[8])}; Denver 3k {W(den?[0])} 6k {W(den?[1])} 9k {W(den?[2])}");
    Check(st.Count == 2 && bos is not null && den is not null, "every point read (a list of locations)");
    Check(bos![8] is { } b39 && Math.Abs(b39.Length - 100) < 1 && bos[6] is { } b30 && Math.Abs(b30.Length - 110) < 3, "the hour nearest now, and high levels from the nearest pressure levels");
    Check(bos[0] is { } b3 && WindsAloft.ToWind(b3).FromDeg is > 200 and < 215 && WindsAloft.ToWind(b3).Kt is > 20 and < 28, "3,000 ft between 925 and 850 hPa");
    Check(den![0] is null && den[1] is null && den[2] is { } d9 && Math.Abs(WindsAloft.ToWind(d9).FromDeg - 270) < 1, "none within 1,500 ft of the ground (Denver at 5,400 ft)");
}

Console.WriteLine("== Winds aloft: arrivals spaced and timed right in strong winds at altitude (tailwind, then headwind)");
foreach (var (aloftFrom, label) in new[] { (90.0, "tailwind"), (270.0, "headwind") })
{
    var data = TestAirport.Build(false);
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 11) { Automatic = false, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    var winds = new V2?[WindsAloft.Levels.Length];
    for (var k = 0; k < winds.Length; k++) winds[k] = WindsAloft.FromWind(aloftFrom, 40 + 10 * k);   // 40 kt at 3,000 ft … 120 kt at 39,000
    var field = new WindsAloft();
    field.SetForecast(new[] { new WindsAloft.Station("TST", data.Lat, data.Lon, 0, winds) }, DateTime.UtcNow);
    world.Aloft = field.Profile(DateTime.UtcNow, data.Lat, data.Lon, world.Wind, world.Apt.ElevationFt);
    world.SelectRunways(true);
    Laia.Common.Airports.ScheduleLeg L(string reg, string fl) => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = reg, Flight = fl, Dep = "KXXX", Arr = "TEST" };
    var a = world.SpawnArrival(30, false, L("N1", "11"))!;
    var b = world.SpawnArrival(36, false, L("N2", "22"))!;
    double landA = -1, landB = -1; var holds = 0; var hA = a.LastHold; var hB = b.LastHold;
    double gsAt5k = double.NaN, iasAt5k = double.NaN, trkAt5k = double.NaN, altAt5k = double.NaN;
    for (var t = 0.0; t < 3600 && (landA < 0 || landB < 0); t += 0.5)
    {
        world.Step(0.5);
        if (a.LastHold != hA || b.LastHold != hB) { holds++; hA = a.LastHold; hB = b.LastHold; }
        if (double.IsNaN(gsAt5k) && !a.OnGround && a.AltFt < 5000 && a.AltFt > 4000) { gsAt5k = a.Gs; iasAt5k = a.Ias; trkAt5k = a.Path!.At(a.S).dir; altAt5k = a.AltFt; }
        if (landA < 0 && a.OnGround) landA = world.Time;
        if (landB < 0 && b.OnGround) landB = world.Time;
    }
    var errs = log.Where(x => x.Contains("landing time as predicted"))
        .SelectMany(x => System.Text.RegularExpressions.Regex.Matches(x, @"(\d+) min out ([+-]\d+) s").Select(m => (min: int.Parse(m.Groups[1].Value), s: int.Parse(m.Groups[2].Value)))).ToList();
    Console.WriteLine($"  {label} aloft: DAL11 at about 4,500 ft {iasAt5k:0} kt indicated, {gsAt5k:0} kt over the ground; landed {Math.Abs(landB - landA):0} s apart; holds {holds}; predictions {string.Join(", ", errs.Select(e => $"{e.min} min {e.s:+0;-0} s"))}");
    var expect = double.IsNaN(gsAt5k) ? double.NaN : world.WindAt(altAt5k).Solve(trkAt5k, Wind.Tas(iasAt5k, altAt5k)).groundSpeed;
    var still = double.IsNaN(gsAt5k) ? double.NaN : Wind.Tas(iasAt5k, altAt5k);
    Console.WriteLine($"    expected {expect:0} kt with the wind at that height (no wind: {still:0})");
    Check(!double.IsNaN(gsAt5k) && Math.Abs(gsAt5k - expect) < 3 && Math.Abs(world.WindAt(altAt5k).Kt - world.Wind.Kt) > 20, $"{label}: ground speed is the airspeed plus the wind at that height, not the surface wind");
    Check(landA > 0 && landB > 0 && Math.Abs(landB - landA) >= 60 && holds == 0 && !a.WentAround && !b.WentAround, $"{label}: both land, properly spaced, with no hold and no go-around");
    Check(errs.Count > 0 && errs.All(e => Math.Abs(e.s) <= 30), $"{label}: landing times predicted within 30 s (the wind at each height is in the prediction)");
}

Console.WriteLine("== Whole sky: a flight with a real route flies along it, not the great circle");
{
    var leg = new NetLeg { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = "N-RTE", Flight = "88", Dep = "KATL", Arr = "KORD",
        DepMin = 4000, ArrMin = 4000 + 140, DepLat = 33.64, DepLon = -84.43, ArrLat = 41.98, ArrLon = -87.90, DepElev = 1026, ArrElev = 680, Located = true };
    leg.DistNm = Geo2.DistanceNm(leg.DepLat, leg.DepLon, leg.ArrLat, leg.ArrLon);
    // A dog-leg via Nashville-ish and Indianapolis-ish.
    leg.SetRoute(new() { (33.64, -84.43), (36.14, -86.68), (39.73, -86.27), (41.98, -87.90) }, "test");
    var mid = NetworkModel.At(leg, 4000 + 70);
    double off = double.MaxValue;
    for (var i = 1; i < leg.RouteLat!.Length; i++)
        for (var f = 0.0; f <= 1; f += 0.01)
        {
            var (la, lo) = NetworkModel.Intermediate(leg.RouteLat[i - 1], leg.RouteLon![i - 1], leg.RouteLat[i], leg.RouteLon[i], f);
            if (mid is not null) off = Math.Min(off, Geo2.DistanceNm(mid.Lat, mid.Lon, la, lo));
        }
    var line = NetworkModel.Line(leg, 0, 1);
    Console.WriteLine($"  great circle {leg.DistNm:0} nm, route {leg.PathNm:0} nm; halfway it's {off:0.0} nm off the route; line {line.Count} points");
    Check(leg.PathNm > leg.DistNm && mid is not null && off < 2, "it flies along its route");
    Check(line.Any(p => Math.Abs(p[0] - 36.14) < 0.01 && Math.Abs(p[1] + 86.68) < 0.01), "the map line keeps the route's corners");
}

Console.WriteLine("== Whole sky: a metered flight runs late by its metering, landing in its slot");
{
    var leg = new NetLeg { Airline = "UAL", Operator = "UAL", Type = "B39M", Reg = "N-MTR", Flight = "77", Dep = "KIAH", Arr = "KORD",
        DepMin = 3000, ArrMin = 3000 + 180, DepLat = 29.98, DepLon = -95.34, ArrLat = 41.98, ArrLon = -87.90, DepElev = 97, ArrElev = 680, Located = true };
    leg.DistNm = Geo2.DistanceNm(leg.DepLat, leg.DepLon, leg.ArrLat, leg.ArrLon);
    var a = NetworkModel.At(leg, 3100);
    leg.MeterMin = 12;
    var b = NetworkModel.At(leg, 3112);
    var landedLate = NetworkModel.At(leg, 3000 + 180 - 6 + 5) is not null;
    Console.WriteLine($"  unmetered at 3100: {a?.Lat:0.000},{a?.Lon:0.000}; metered 12 min at 3112: {b?.Lat:0.000},{b?.Lon:0.000}; still flying 5 min after its scheduled landing: {landedLate}");
    Check(a is not null && b is not null && Math.Abs(a.Lat - b.Lat) < 1e-6 && Math.Abs(a.Lon - b.Lon) < 1e-6, "12 minutes of metering puts it where it was 12 minutes earlier");
    Check(landedLate, "it lands later by its metering");
}

Console.WriteLine("== Whole sky: a padded schedule doesn't make a jet crawl (UAL2458, 17 Nov)");
{
    // About 380 nm (Houston–Dallas-ish is shorter; this is IAH–MEM-like) with a padded 150-minute block.
    var leg = new NetLeg { Airline = "UAL", Operator = "UAL", Type = "B39M", Reg = "N-UAL", Flight = "2458", Dep = "KIAH", Arr = "KMEM",
        DepMin = 3000, ArrMin = 3000 + 150, DepLat = 29.98, DepLon = -95.34, ArrLat = 35.04, ArrLon = -89.98, DepElev = 97, ArrElev = 341, Located = true };
    leg.DistNm = Geo2.DistanceNm(leg.DepLat, leg.DepLon, leg.ArrLat, leg.ArrLon);
    double cruiseGs = 0, firstAir = double.NaN, lastAir = double.NaN;
    for (var m = 3000.0; m <= 3150; m += 0.25)
    {
        var p = NetworkModel.At(leg, m);
        if (p is null) continue;
        if (double.IsNaN(firstAir)) firstAir = m;
        lastAir = m;
        if (p.Phase == "cruising") cruiseGs = Math.Max(cruiseGs, p.GsKt);
    }
    Console.WriteLine($"  {leg.DistNm:0} nm in a 150-minute block: cruise {cruiseGs:0} kt, airborne {firstAir - 3000:0} to {lastAir - 3000:0} min");
    Check(cruiseGs > 400, "cruises at a jet's speed (over 400 kt), not the schedule's padded average");
    Check(lastAir - 3000 >= 143, "still lands on schedule (taxi-in about 6 minutes before the block ends)");
}

Console.WriteLine("== Whole sky: descending flights are never above a 3:1 path to their destination");
{
    var leg = new NetLeg { Airline = "WJA", Operator = "WJA", Type = "B738", Reg = "C-WJA", Flight = "1272", Dep = "CYYZ", Arr = "KMCO",
        DepMin = 1000, ArrMin = 1000 + 170, DepLat = 43.68, DepLon = -79.63, ArrLat = 28.43, ArrLon = -81.31, ArrElev = 96, Located = true };
    leg.DistNm = Geo2.DistanceNm(leg.DepLat, leg.DepLon, leg.ArrLat, leg.ArrLon);
    double worst = double.MinValue, at52 = double.NaN;
    for (var m = 1000.0; m < 1170; m += 0.25)
    {
        var p = NetworkModel.At(leg, m);
        if (p is null || p.Phase != "descending") continue;
        var rem = leg.DistNm - p.FlownNm;
        worst = Math.Max(worst, p.AltFt - (leg.ArrElev + rem * 300 + 1500));
        if (double.IsNaN(at52) && rem < 52) at52 = p.AltFt;
    }
    Console.WriteLine($"  Toronto–Orlando: at 52 nm to go {at52:0} ft");
    Check(worst <= 20 && at52 < 18000, "at or below about 300 ft per nm to go (no more 26,000 ft at 52 nm)");
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
    var hdgs = new Queue<(double t, double h)>(); double farTurn = 0;
    while (t < 3 * 3600 && !landed)
    {
        world.Step(0.5); t += 0.5;
        // Far out (beyond 40 nm), never turned more than 90° in a minute (23 Nov, KBOS: sharp 180s when routes were rebuilt).
        if (f.Pos.Length > 40 * nm)
        {
            hdgs.Enqueue((t, f.Heading));
            while (hdgs.Count > 0 && t - hdgs.Peek().t > 60) hdgs.Dequeue();
            farTurn = Math.Max(farTurn, Math.Abs(Geo2.Diff(f.Heading, hdgs.Peek().h)));
        }
        else hdgs.Clear();
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
    Console.WriteLine($"  biggest turn in a minute beyond 40 nm: {farTurn:0}°");
    Check(farTurn <= 90, "far out, no turn of more than 90° in a minute");
    var landAt = world.Time;
    Console.WriteLine($"  landing time predicted 10 min out: {landAt - f.Pred10:+0;-0} s off; 5 min out: {landAt - f.Pred5:+0;-0} s off; first prediction {(double.IsNaN(f.Pred30) ? "after 30 min out" : $"30 min out: {landAt - f.Pred30:+0;-0} s off")}");
    Check(!double.IsNaN(f.Pred10) && Math.Abs(landAt - f.Pred10) < 45 && !double.IsNaN(f.Pred5) && Math.Abs(landAt - f.Pred5) < 30,
        "the landing time is predicted within 45 s from 10 minutes out and 30 s from 5 minutes out (no other traffic)");
    Console.WriteLine($"  predictions: {world.PredictionSummary}");
    Check(world.PredictionSummary?.Contains("STAR/approach fixes") == true, "times at the STAR and approach fixes are predicted and measured");
}

Console.WriteLine("== Hand-over of a short flight: one departing a field inside the 200 nm ring is taken over once climbed, not at take-off");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var map = new LocalMap(data.Lat, data.Lon);
    var near = map.ToLatLon(new V2(160 * nm, 40 * nm));   // ~165 nm away
    var sched = new Laia.Common.Airports.ScheduleData { Airport = "TEST", Source = "AIG" };
    sched.Legs.Add(new() { Airline = "AAL", Operator = "AAL", Type = "A321", Reg = "N2072A", Flight = "2072", Dep = "KNEA", Arr = "TEST", DepMin = 2001, ArrMin = 2041 });
    var clock = 2000.0;
    var w = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 4)
    {
        Level = TrafficLevel.RealWorld, Wind = new Wind(270, 10), WeekMinute = 2000, ClockFrom = () => clock,
        Locate = c => c == "KNEA" ? near : null, Schedule = sched,
    };
    w.SelectRunways(true);
    Laia.Server.TrafficCore.Flight? f = null; double firstAlt = 0;
    for (var t = 0.0; t < 1800 && f is null; t += 0.5)
    {
        clock = 2000 + t / 60; w.WeekMinute = clock;
        w.Step(0.5);
        f = w.Flights.FirstOrDefault(x => x.Callsign == "AAL2072");
        if (f is not null) firstAlt = f.AltFt;
    }
    Console.WriteLine($"  taken over at {firstAlt:0} ft");
    Check(f is not null && firstAlt >= 9000, "taken over at 10,000 ft or more, not on the other field's runway");
}

Console.WriteLine("== Vectors to final without a zigzag (MCO GRNCH5 for 36L: DORII, a keep-clear point inside the downwind, then back out)");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var rwy = apt.Ends.First(e => e.Ident == "27");
    var nm = Geo2.MPerNm;
    var left = new V2(-rwy.Dir.Y, rwy.Dir.X);
    V2 At(double along, double side) => rwy.Threshold + rwy.Dir * (along * nm) + left * (side * nm);
    // The STAR's last fix beyond the airport 5.1 nm to the side; a keep-clear point 4.1 nm out abeam; then the approach.
    var route = new List<V2> { At(20, 9), At(8.3, 5.1), At(-3.1, 4.1), At(-13, 0), At(-10, 0), At(-5, 0) };
    var speeds = route.Select(_ => 0.0).ToList();
    TrafficWorld.ShapeFinal(route, speeds, rwy, new VectorShape(7.6, 9, 5.2, 30));
    var offs = route.Select(p => Math.Abs(rwy.Offset(p)) / nm).ToList();
    Console.WriteLine("  route offsets from the final (nm): " + string.Join(", ", offs.Select(o => o.ToString("0.0"))));
    var zig = false;
    for (var i = 1; i + 1 < route.Count; i++)
        if (offs[i] < offs[i - 1] - 0.3 && offs[i + 1] > offs[i] + 0.5) zig = true;
    Check(!zig, "no cutting in towards the final and back out again");
}

Console.WriteLine("== Heading to intercept (VI) then a course to a fix (CF): the turn comes where the heading meets the course (MCO FATHE4 off 35L)");
{
    var nm = Geo2.MPerNm;
    var apt = new AirportModel(TestAirport.Build(false));
    // From (0, 0): heading 000 to intercept course 037 to a fix at (2.9, 5.1) nm — the course line crosses x = 0 at y = 1.25 nm.
    var fixPos = new V2(2.9 * nm, 5.1 * nm);
    var (flat, flon) = apt.Map.ToLatLon(fixPos);
    var legs = new List<ProcedureLeg>
    {
        new() { Type = 21, Course = 0, TrueCourse = true },
        new() { Type = 4, Fix = "JWOLF", Lat = flat, Lon = flon, Course = 37, TrueCourse = true },
    };
    var pts = apt.LegsToPoints(legs, new V2(0, 0), 500, out _, hybrid: true);
    var turn = pts[0].P;
    Console.WriteLine($"  turn point at ({turn.X / nm:0.00}, {turn.Y / nm:0.00}) nm, then {pts[1].Fix} — was 3 nm straight ahead, then direct");
    Check(Math.Abs(turn.X) < 0.1 * nm && Math.Abs(turn.Y / nm - 1.25) < 0.15, "it turns where the heading meets the 037 course, about 1.25 nm out");
    Check(pts.Count == 2 && pts[1].Fix == "JWOLF", "and then flies the course to JWOLF");
}

Console.WriteLine("== Hybrid SIDs: RNAV to a fix, radar vectors, then RNAV again (MCO's runway → KAAPE → vectors → UKAYY)");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var apt = new AirportModel(data);
    var map = new LocalMap(data.Lat, data.Lon);
    Laia.Common.Airports.ProcedureLeg L(string fix, double x, double y, int type)
    {
        var (lat, lon) = map.ToLatLon(new V2(x * nm, y * nm));
        return new() { Type = type, Fix = fix, Lat = lat, Lon = lon };
    }
    var legs = new List<Laia.Common.Airports.ProcedureLeg>
    {
        L("KAAPE", -4, 1, 7), new() { Type = 11, Fix = "KAAPE", Course = 270, TrueCourse = true }, L("UKAYY", -14, 10, 7), L("DDANY", -20, 25, 18),
    };
    var hy = apt.LegsToPoints(legs, new V2(-1500, 0), 400, out var vec1, hybrid: true);
    var old = apt.LegsToPoints(legs, new V2(-1500, 0), 400, out var vec2);
    Console.WriteLine($"  flown: {string.Join(" → ", hy.Select(p => p.Fix.Length > 0 ? p.Fix : "(heading)"))}");
    Check(hy.Any(p => p.Fix == "UKAYY") && hy.Any(p => p.Fix == "DDANY") && !vec1, "after the vectors it rejoins at UKAYY and flies on to DDANY");
    var h = hy.FindIndex(p => p.Fix.Length == 0);
    Check(h > 0 && Math.Abs(Geo2.Diff((hy[h].P - hy[h - 1].P).Bearing, 270)) < 1, "the vector leg is flown on its published heading first");
    Check(vec2 && !old.Any(p => p.Fix == "UKAYY"), "(an arrival or approach still ends at a vector leg)");
}

Console.WriteLine("== Hand-over spacing: two arrivals the whole sky has on top of each other close in are sequenced, not held");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var map = new LocalMap(data.Lat, data.Lon);
    var origin = map.ToLatLon(new V2(250 * nm, -100 * nm));
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 3)
    {
        Automatic = false, Wind = new Wind(270, 10), WeekMinute = 2000, Locate = c => c == "KFAR" ? origin : null,
    };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    Laia.Common.Airports.ScheduleLeg Leg(int dep, string fl, string reg) => new() { Airline = "DAL", Operator = "DAL", Type = "B738", Reg = reg, Flight = fl, Dep = "KFAR", Arr = "TEST", DepMin = dep, ArrMin = dep + 60 };
    NetPos? np = null; int depMin = 0;
    for (var dep = 2000 - 70; dep <= 2000; dep++)
    {
        var p = world.NetworkPosition(Leg(dep, "1", "N1"));
        if (p is not null && Geo2.DistanceNm(data.Lat, data.Lon, p.Lat, p.Lon) is > 28 and < 40) { np = p; depMin = dep; break; }
    }
    Check(np is not null, "the whole sky has an arrival about 30 nm out");
    var a = world.SpawnFromNetwork(Leg(depMin, "1", "N1"), np!)!;
    var b = world.SpawnFromNetwork(Leg(depMin, "2", "N2"), np!)!;
    var gap = (b.ThrS - b.S - (a.ThrS - a.S)) / nm;
    Console.WriteLine($"  first {(a.ThrS - a.S) / nm:0.0} track nm out, second {(b.ThrS - b.S) / nm:0.0} ({gap:0.0} nm behind)");
    Console.WriteLine("  " + log.LastOrDefault());
    Check(gap >= 4.9, "the second starts at least 5 nm behind the first");
    var aDist = Geo2.DistanceNm(data.Lat, data.Lon, np!.Lat, np.Lon);
    Console.WriteLine($"  the whole sky has it {aDist:0} nm out at {np.AltFt:0} ft");
    Check(np.AltFt <= (aDist * 1.3 + 10) * 300 + 1500 + 1000, "not handed over higher than it can get down from on the way it will fly in");
    // Injection gaps (user, 19 Nov): 5 track nm on the approach, 7 on the STAR, 10 before it.
    NetPos? far = null; int farDep = 0;
    for (var dep = 2000 - 70; dep <= 2000; dep++)
    {
        var p = world.NetworkPosition(Leg(dep, "7", "N7"));
        if (p is not null && Geo2.DistanceNm(data.Lat, data.Lon, p.Lat, p.Lon) is > 120 and < 180) { far = p; farDep = dep; break; }
    }
    if (far is not null)
    {
        var c = world.SpawnFromNetwork(Leg(farDep, "7", "N7"), far)!;
        var e = world.SpawnFromNetwork(Leg(farDep, "8", "N8"), far)!;
        var fg = Math.Abs((e.ThrS - e.S) - (c.ThrS - c.S)) / nm;
        Console.WriteLine($"  far out: {(c.ThrS - c.S) / nm:0.0} and {(e.ThrS - e.S) / nm:0.0} track nm ({fg:0.0} apart; STAR starts {c.StarNm:0} nm out)");
        Console.WriteLine("  " + log.LastOrDefault());
        Check(fg >= 9.9, "two taken over on top of each other before their STAR start 10 track nm or more apart");
        world.Flights.Remove(c); world.Flights.Remove(e);
    }
    var holds = 0; var landed = 0;
    for (var t = 0.0; t < 2400 && landed < 2; t += 0.5)
    {
        world.Step(0.5);
        landed = new[] { a, b }.Count(f => f.OnGround);
    }
    holds = log.Count(x => x.Contains("holding for spacing") || x.Contains("going around") || x.Contains("making a 360"));
    Check(landed == 2 && holds == 0, $"both land with no hold, 360 or go-around ({holds})");
}

Console.WriteLine("== Take-off times are predicted (taxi, queue, arrivals on the same runway) and measured");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var world = new TrafficWorld(apt, new TestSource(), seed: 21) { Automatic = true, Level = TrafficLevel.Busy, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    for (var t = 0.0; t < 5400; t += 0.5) world.Step(0.5);
    var offs = log.Where(x => x.Contains("take-off time as predicted")).ToList();
    Console.WriteLine($"  {offs.Count} take-offs with a prediction; {world.PredictionSummary}");
    foreach (var x in offs.Take(3)) Console.WriteLine("  " + x);
    Check(offs.Count >= 3, "departures get a predicted take-off time");
    var errs = offs.SelectMany(x => System.Text.RegularExpressions.Regex.Matches(x, @"5 min out ([+-]\d+) s").Select(m => Math.Abs(double.Parse(m.Groups[1].Value)))).ToList();
    Console.WriteLine($"  5 min out: average miss {(errs.Count > 0 ? errs.Average() : double.NaN):0} s over {errs.Count}");
    Check(errs.Count == 0 || errs.Average() < 120, "predicted within about 2 minutes on average from 5 minutes out");
}

Console.WriteLine("== Filed routes in the whole sky: an arrival is already flying to its STAR when it's taken over");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var map = new LocalMap(data.Lat, data.Lon);
    var origin = map.ToLatLon(new V2(300 * nm, 40 * nm));     // east: the great circle would come straight in from the east
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 6)
    {
        Automatic = false, Wind = new Wind(270, 10), WeekMinute = 2000, Locate = c => c == "KFAR" ? origin : null,
    };
    world.SelectRunways(true);
    Laia.Common.Airports.ScheduleLeg Leg(int dep) => new() { Airline = "DAL", Operator = "DAL", Type = "B738", Reg = "N55", Flight = "55", Dep = "KFAR", Arr = "TEST", DepMin = dep, ArrMin = dep + 55 };
    NetPos? np = null; Laia.Common.Airports.ScheduleLeg? leg = null;
    for (var dep = 2000 - 55; dep <= 2000; dep++)
    {
        var l = Leg(dep); var p = world.NetworkPosition(l);
        if (p is not null && Geo2.DistanceNm(data.Lat, data.Lon, p.Lat, p.Lon) is > 60 and < 80) { np = p; leg = l; break; }
    }
    Check(np is not null, "the whole sky has the arrival 60–80 nm out");
    var pos = map.ToXY(np!.Lat, np.Lon);
    var farrr = new V2(30 * nm, -30 * nm);
    var toFix = Math.Abs(Geo2.Diff((farrr - pos).Bearing, np.Heading));
    Console.WriteLine($"  at {pos.Length / nm:0} nm, {np.AltFt:0} ft, heading {np.Heading:000}; FARRR bears {(farrr - pos).Bearing:000}");
    Check(toFix < 10, "it is heading for its STAR's first fix (FARRR), not straight at the airport");
    var f = world.SpawnFromNetwork(leg!, np)!;
    double turn = 0, prev = double.NaN;
    for (var s2 = 0.0; s2 < 20 * nm; s2 += 200) { var d = f.Path!.At(s2).dir; if (!double.IsNaN(prev)) turn += Math.Abs(Geo2.Diff(d, prev)); prev = d; }
    Console.WriteLine($"  taken over: {f.Route}, turning {turn:0}° in its first 20 nm");
    Check(turn < 40, "the hand-over just carries on along the route (no turn to join)");
}

Console.WriteLine("== FAA NASR navigation data: fixes, navaids and airways, and route strings expanded point by point");
{
    var nav = new NavDb();
    nav.AddPoints(new[] {
        new[] { "EFF_DATE", "FIX_ID", "ICAO_REGION_CODE", "STATE_CODE", "LAT_DECIMAL", "LONG_DECIMAL" },
        new[] { "2026/10/29", "AAAAA", "K7", "FL", "30.0", "-82.0" },
        new[] { "2026/10/29", "BBBBB", "K7", "FL", "29.5", "-81.8" },
        new[] { "2026/10/29", "CCCCC", "K7", "FL", "29.0", "-81.6" },
        new[] { "2026/10/29", "DUPLI", "K7", "FL", "29.2", "-81.7" },
        new[] { "2026/10/29", "DUPLI", "K2", "WA", "47.0", "-122.0" },
    }, false, "FIX_BASE.csv");
    nav.AddPoints(new[] { new[] { "NAV_ID", "NAV_TYPE", "LAT_DECIMAL", "LONG_DECIMAL" }, new[] { "ODF", "VORTAC", "34.69", "-83.29" } }, true, "NAV_BASE.csv");
    nav.AddAirways(new[] { new[] { "EFF_DATE", "REGULATORY", "AWY_ID", "AWY_LOCATION", "AIRWAY_STRING" }, new[] { "2026/10/29", "Y", "J99", "C", "ODF AAAAA BBBBB CCCCC" } }, "AWY_BASE.csv");
    var pts = nav.Expand(new[] { "ATL", "DIRTY3", "ODF", "J99", "CCCCC", "DUPLI", "SNFLD3", "MCO" }, 33.6, -84.4);
    Console.WriteLine($"  {nav.Fixes} fixes, {nav.Navaids} navaids, {nav.Airways} airways; ODF J99 CCCCC DUPLI → {string.Join(" ", pts.Select(p => p.name))}; {string.Join(" | ", nav.Notes)}");
    Check(string.Join(" ", pts.Select(p => p.name)) == "ODF AAAAA BBBBB CCCCC DUPLI" && Math.Abs(pts[^1].lat - 29.2) < 0.01,
        "the airway is expanded between its fixes, a repeated name resolves to the nearest one, SID/STAR names are skipped");
    var segs = new NavDb();
    segs.AddAirways(new[] { new[] { "AWY_ID", "POINT_SEQ", "FROM_POINT" }, new[] { "Q1", "20", "BBBBB" }, new[] { "Q1", "10", "AAAAA" }, new[] { "Q1", "30", "CCCCC" } }, "AWY_SEG.csv");
    Check(string.Join(" ", segs.AirwayBetween("Q1", "CCCCC", "AAAAA") ?? new()) == "CCCCC BBBBB AAAAA", "airways given point by point work too, flown either way");
}

Console.WriteLine("== The whole sky flies the filed route string (airways), then the STAR");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var map = new LocalMap(data.Lat, data.Lon);
    var origin = map.ToLatLon(new V2(300 * nm, 0));
    var w1 = map.ToLatLon(new V2(220 * nm, -90 * nm));        // well south of the great circle
    var w2 = map.ToLatLon(new V2(120 * nm, -80 * nm));
    var farrr = map.ToLatLon(new V2(30 * nm, -30 * nm));
    var nav = new NavDb();
    nav.AddPoints(new[] { new[] { "FIX_ID", "LAT_DECIMAL", "LONG_DECIMAL" },
        new[] { "WAYPA", w1.lat.ToString(System.Globalization.CultureInfo.InvariantCulture), w1.lon.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        new[] { "WAYPB", w2.lat.ToString(System.Globalization.CultureInfo.InvariantCulture), w2.lon.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        new[] { "FARRR", farrr.lat.ToString(System.Globalization.CultureInfo.InvariantCulture), farrr.lon.ToString(System.Globalization.CultureInfo.InvariantCulture) } }, false, "FIX_BASE.csv");
    nav.AddAirways(new[] { new[] { "AWY_ID", "AIRWAY_STRING" }, new[] { "J7", "WAYPA WAYPB FARRR" } }, "AWY_BASE.csv");
    var routes = PreferredRoutes.Parse(new[] { "Orig,Route String,Dest,Hours1,Hours2,Hours3,Type,Area,Altitude,Aircraft,Direction,Seq,DCNTR,ACNTR",
        "FAR,FAR WAYPA J7 FARRR ARRVL1 TEST,TEST,,,,H,,,,,1,ZXX,ZYY" });
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 6)
    {
        Automatic = false, Wind = new Wind(270, 10), WeekMinute = 2000, Locate = c => c is "KFAR" or "FAR" ? origin : null, Routes = routes, NavData = nav,
    };
    world.SelectRunways(true);
    Laia.Common.Airports.ScheduleLeg Leg(int dep) => new() { Airline = "DAL", Operator = "DAL", Type = "B738", Reg = "N66", Flight = "66", Dep = "KFAR", Arr = "TEST", DepMin = dep, ArrMin = dep + 60 };
    // Halfway: the great circle would be due east of the field; the filed route is far to the south, near WAYPB.
    NetPos? np = null;
    for (var dep = 2000 - 60; dep <= 2000; dep++)
    {
        var p = world.NetworkPosition(Leg(dep));
        if (p is not null && p.FlownNm / p.Leg.DistNm is > 0.45 and < 0.6) { np = p; break; }
    }
    Check(np is not null, "the whole sky has the flight about halfway");
    var pos = map.ToXY(np!.Lat, np.Lon);
    double SegDist(V2 p, V2 a, V2 b) { var ab = b - a; var t = Math.Clamp(V2.Dot(p - a, ab) / Math.Max(1, ab.Length * ab.Length), 0, 1); return (p - (a + ab * t)).Length; }
    var A = new V2(300 * nm, 0); var B = new V2(220 * nm, -90 * nm); var C = new V2(120 * nm, -80 * nm); var D = new V2(30 * nm, -30 * nm);
    var off = new[] { SegDist(pos, A, B), SegDist(pos, B, C), SegDist(pos, C, D) }.Min() / nm;
    Console.WriteLine($"  halfway at ({pos.X / nm:0}, {pos.Y / nm:0}) nm, {off:0.0} nm off the filed route; {world.FiledRoutesFlown} filed route(s)");
    Check(off < 3 && pos.Y < -40 * nm, "it is on the airway route (well south of the great circle)");
}

Console.WriteLine("== Taken over beside its STAR's first fix: direct to the next fix ahead, no flying back and looping round");
{
    var nm = Geo2.MPerNm;
    var world = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 8) { Automatic = false, Wind = new Wind(270, 10) };
    world.SelectRunways(true);
    // ARRVL1 runs FARRR (30, -30) → MIDDL (18, -12); the aircraft is east-northeast of FARRR, beside it.
    var start = new V2(40 * nm, -15 * nm);
    Laia.Common.Airports.ScheduleLeg L() => new() { Airline = "JBU", Operator = "JBU", Type = "A320", Reg = "N828JB", Flight = "228", Dep = "KXXX", Arr = "TEST" };
    var f = world.SpawnArrival(0, false, L(), entry: (start, 10000, 10000))!;
    var farrr = new V2(30 * nm, -30 * nm);
    double nearest = double.MaxValue, turn = 0, prev = double.NaN;
    for (var s2 = 0.0; s2 < Math.Min(f.Path!.Length, 20 * nm); s2 += 200)
    {
        var (pp, dir, _) = f.Path.At(s2);
        nearest = Math.Min(nearest, (pp - farrr).Length / nm);
        if (!double.IsNaN(prev)) turn += Math.Abs(Geo2.Diff(dir, prev));
        prev = dir;
    }
    Console.WriteLine($"  {f.Route}; closest to FARRR {nearest:0.0} nm, turning {turn:0}° in the first 20 nm");
    Check(nearest > 5 && turn < 150, "it goes direct to MIDDL rather than back to FARRR and round");
}

Console.WriteLine("== Departures never climb into arrivals; freighters park on cargo ramps");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var world = new TrafficWorld(apt, new TestSource(), seed: 31) { Automatic = true, Level = TrafficLevel.Busy, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    for (var t = 0.0; t < 3600; t += 0.5) world.Step(0.5);
    var los = log.Where(x => x.StartsWith("SEPARATION")).ToList();
    Console.WriteLine($"  {world.SeparationSummary}");
    foreach (var x in los.Take(3)) Console.WriteLine("  " + x);
    Check(los.Count == 0, "no departure comes within 3 nm and 1,000 ft of other traffic");
    var low = log.Where(x => x.Contains("climb stopped at")).Select(x => double.Parse(System.Text.RegularExpressions.Regex.Match(x, @"climb stopped at ([0-9,]+) ft").Groups[1].Value.Replace(",", ""))).ToList();
    Check(low.All(v => v >= 2000), "a climb is never stopped below 2,000 ft above the field");
    Check(TrafficWorld.IsCargo("UPS") && TrafficWorld.IsCargo("FDX") && !TrafficWorld.IsCargo("SWA"), "UPS and FedEx are known as cargo airlines");
}

Console.WriteLine("== SID top altitude: departures level under the arrivals, the climb given once clear (MCO: 7,000 under 8,000)");
{
    var apt = new AirportModel(TestAirport.Build(false));
    var world = new TrafficWorld(apt, new TestSource(), seed: 31) { Automatic = true, Level = TrafficLevel.Busy, Wind = new Wind(270, 10) };
    // Every SID tops out at 5,000 ft in this test.
    world.SidTops = apt.Data.Departures.ToDictionary(d => d.Name, _ => 5000, StringComparer.OrdinalIgnoreCase);
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    for (var t = 0.0; t < 3600; t += 0.5) world.Step(0.5);
    var given = log.Where(x => x.Contains("climb above")).ToList();
    Console.WriteLine($"  {world.TopAltitudeSummary}");
    foreach (var x in given.Take(3)) Console.WriteLine("  " + x);
    Check(given.Count > 0, "departures are given the climb above their top altitude");
    Check(given.All(x => !x.Contains("given at") || double.Parse(System.Text.RegularExpressions.Regex.Match(x, @"given at ([0-9,]+) ft").Groups[1].Value.Replace(",", "")) <= 5100),
        "never above the top altitude before the climb is given");
    var los = log.Where(x => x.StartsWith("SEPARATION")).ToList();
    foreach (var x in los.Take(3)) Console.WriteLine("  " + x);
    Check(los.Count == 0, "no departure comes within 3 nm and 1,000 ft of other traffic");
}

Console.WriteLine("== Between airports: departures level under another airport's traffic in their way");
{
    var nm = Geo2.MPerNm;
    var apt = new AirportModel(TestAirport.Build(false));
    var world = new TrafficWorld(apt, new TestSource(), seed: 31) { Automatic = true, Level = TrafficLevel.Busy, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    // A neighbour's arrival holding still 6 nm west at 4,000 ft, right over the departures off 27.
    var obstacle = new V2(-6 * nm, 0);
    var bad = 0;
    for (var t = 0.0; t < 3600; t += 0.5)
    {
        world.Others = new() { new TrafficWorld.Foreign("NBR1", "KNBR", "arrival", obstacle, 4000, 0, 0, 90, "arriving", false) };
        world.Step(0.5);
        bad += world.Flights.Count(f => f.Kind == "departure" && !f.OnGround && !f.Done && (f.Pos - obstacle).Length < 3 * nm && Math.Abs(f.AltFt - 4000) < 900);
    }
    var stops = log.Where(x => x.Contains("NBR1 (KNBR)")).ToList();
    Console.WriteLine($"  {stops.Count} climb stops for the other airport's aircraft; {world.SeparationSummary}");
    foreach (var x in stops.Take(2)) Console.WriteLine("  " + x);
    Check(stops.Count > 0, "departures are stopped below the other airport's aircraft");
    Check(bad == 0, $"none comes within 3 nm and 900 ft of it ({bad} samples)");
}

Console.WriteLine("== The user as traffic: departures level below the user, arrivals stay above (14 Nov, after a TA/RA)");
{
    var nm = Geo2.MPerNm;
    // Departures: the user circling 6 nm west at 4,000 ft, right over the departures off 27.
    var world = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 33) { Automatic = true, Level = TrafficLevel.Busy, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    var me = new V2(-6 * nm, 0);
    world.UserCallsign = "UAL1196"; world.UserOnGround = false; world.UserPos = me; world.UserAltFt = 4000; world.UserGs = 0; world.UserTrack = 90;
    var bad = 0;
    for (var t = 0.0; t < 3600; t += 0.5)
    {
        world.Step(0.5);
        bad += world.Flights.Count(f => f.Kind == "departure" && !f.OnGround && !f.Done && (f.Pos - me).Length < 3 * nm && Math.Abs(f.AltFt - 4000) < 900);
    }
    var stops = log.Where(x => x.Contains("UAL1196") && x.Contains("climb stopped")).ToList();
    foreach (var x in stops.Take(2)) Console.WriteLine("  " + x);
    Check(stops.Count > 0, "departures are stopped below the user");
    Check(bad == 0, $"no departure comes within 3 nm and 900 ft of the user ({bad} samples)");

    // Arrivals: the user 1,500 ft below an arrival's descent path, 20 nm out on its route.
    var w2 = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 34) { Automatic = false, Wind = new Wind(270, 10) };
    var log2 = new List<string>(); w2.Log = s2 => log2.Add(s2);
    w2.SelectRunways(true);
    var a = w2.SpawnArrival(45, true)!;
    var spot = a.Path!.At(a.ThrS - 20 * nm).p;
    var userAlt = Math.Max(3000, (a.Rwy!.ElevationFt + 50 + 20 * 318.4) - 1500);
    w2.UserCallsign = "UAL1196"; w2.UserOnGround = false; w2.UserPos = spot; w2.UserAltFt = userAlt; w2.UserGs = 0; w2.UserTrack = 0;
    double minDz = double.MaxValue; var minAt = "";
    for (var t = 0.0; t < 1200 && !a.OnGround && !a.Done; t += 0.5)
    {
        w2.Step(0.5);
        if ((a.Pos - spot).Length < 3 * nm && Math.Abs(a.AltFt - userAlt) < minDz)
        { minDz = Math.Abs(a.AltFt - userAlt); minAt = $"t {w2.Time:0} {a.Phase} {a.AltFt:0} ft, {(a.Pos - spot).Length / nm:0.0} nm from the user, {(a.ThrS - a.S) / nm:0.0} nm to go, seq {a.Seq}, user arriving {(w2.UserArr is not null)}"; }
    }
    Console.WriteLine($"  closest: {minAt}");
    foreach (var x in log2.Where(x => x.Contains(a.Callsign)).Take(12)) Console.WriteLine("    " + x);
    foreach (var x in log2.Where(x => x.Contains("UAL1196")).Take(2)) Console.WriteLine("  " + x);
    Console.WriteLine($"  arrival over the user: closest {minDz:N0} ft vertically within 3 nm; {(a.OnGround ? "landed" : a.WentAround ? "went around" : "still flying")}");
    Check(minDz >= 900, "an arrival stays 1,000 ft above the user while near");
}

Console.WriteLine("== En-route separation of the whole-sky flights (RVSM: 1,000 ft or 5 nm)");
{
    Laia.Server.TrafficCore.NetLeg Leg(string reg, string fl, int dep, int arr) => new()
    {
        Airline = "DAL", Operator = "DAL", Type = "A320", Reg = reg, Flight = fl, Dep = "KAAA", Arr = "KBBB", Located = true,
        DepLat = 40, DepLon = -100, ArrLat = 40, ArrLon = -90, DepElev = 500, ArrElev = 500, DepMin = dep, ArrMin = arr,
        DistNm = Geo2.DistanceNm(40, -100, 40, -90),
    };
    double Worst(List<Laia.Server.TrafficCore.NetLeg> legs, List<EnRouteSeparation.Obstacle> obs, double from, double to, out int limits, out List<string> evs)
    {
        var sep = new EnRouteSeparation();
        evs = new List<string>();
        var worst = double.MaxValue;
        for (var c = from; c < to; c += 2.0 / 60)
        {
            foreach (var e in sep.Update(c, 40, -95, legs, obs)) evs.Add($"{e.Leg.Callsign} {e.Text} for {e.Who}");
            var ps = legs.Select(l => NetworkModel.At(l, c)).ToList();
            for (var i = 0; i < legs.Count; i++)
            {
                if (ps[i] is not { } a || a.GsKt < 60) continue;
                var ai = sep.Alt(legs[i], c, a.AltFt);
                for (var j = i + 1; j < legs.Count; j++)
                    if (ps[j] is { } b && b.GsKt >= 60 && Geo2.DistanceNm(a.Lat, a.Lon, b.Lat, b.Lon) < 5)
                        worst = Math.Min(worst, Math.Abs(ai - sep.Alt(legs[j], c, b.AltFt)));
                foreach (var o in obs)
                    if (Geo2.DistanceNm(a.Lat, a.Lon, o.Lat, o.Lon) < 5) worst = Math.Min(worst, Math.Abs(ai - o.AltFt));
            }
        }
        limits = sep.Limits;
        return worst;
    }
    // A faster flight catching a slower one up on the same route and level.
    var slow = Leg("N1", "100", 600, 700);
    var fast = Leg("N2", "200", 615, 690);
    var w0 = Worst(new() { slow, fast }, new(), 600, 700, out var n0, out var ev0);
    foreach (var e in ev0.Take(3)) Console.WriteLine("  " + e);
    Console.WriteLine($"  catching up at the same level: closest {w0:N0} ft within 5 nm, {n0} limits given");
    Check(n0 > 0 && w0 >= 950, "a flight catching another up at its level is moved 2,000 ft (keeps 1,000 ft within 5 nm)");
    // An aircraft (the user, an airport's) sitting at the cruise level on the route.
    var c0 = NetworkModel.At(slow, 650)!;
    var obstacle = new EnRouteSeparation.Obstacle("UAL1196 (you)", c0.Lat, c0.Lon + 1.5, 270, 0, c0.AltFt, 0, 6);
    var w1 = Worst(new() { slow }, new() { obstacle }, 600, 700, out var n1, out var ev1);
    foreach (var e in ev1.Take(2)) Console.WriteLine("  " + e);
    Console.WriteLine($"  an aircraft at its level on the route: closest {w1:N0} ft within 5 nm, {n1} limits given");
    Check(n1 > 0 && w1 >= 950, "a whole-sky flight moves 1,000 ft for an aircraft at its level in its way");
    // The user 1,000 ft below its level (read a few feet high, 20 ft above their level): RVSM separation already,
    // nothing to do (16 Nov: SWA3049 at FL370 was moved to FL380 for the user at FL360).
    var below = new EnRouteSeparation.Obstacle("UAL1196 (you)", c0.Lat, c0.Lon + 1.5, 270, 0, c0.AltFt - 1000 + 20, 0, 6);
    var w2 = Worst(new() { slow }, new() { below }, 600, 700, out var n2, out var ev2);
    foreach (var e in ev2.Take(2)) Console.WriteLine("  " + e);
    Console.WriteLine($"  the user 1,000 ft below its level (read 20 ft high): {n2} limits given, closest {w2:N0} ft within 5 nm");
    Check(n2 == 0, "1,000 ft from the user's level is left alone (RVSM), even with the user's altitude read a little off");
    // A little faster on the same route and level: kept in trail by speed — the one ahead a little faster first — no level
    // change (user, 25 Nov: speed up the one ahead, "that's usually the first thing they'll do").
    // A long route (1,400 nm) so the catching up happens in the cruise: an A320 and, 3 minutes behind it after take-off,
    // a 757 about 10 kt faster.
    Laia.Server.TrafficCore.NetLeg Long(string reg, string fl, string type, int dep) => new()
    {
        Airline = "DAL", Operator = "DAL", Type = type, Reg = reg, Flight = fl, Dep = "KAAA", Arr = "KCCC", Located = true,
        DepLat = 40, DepLon = -100, ArrLat = 40, ArrLon = -70, DepElev = 500, ArrElev = 500, DepMin = dep, ArrMin = dep + 260,
        DistNm = Geo2.DistanceNm(40, -100, 40, -70),
    };
    var slow2 = Long("N5", "500", "A320", 600);
    var quick = Long("N6", "600", "B752", 600);
    // Line the 757 up 3 minutes behind the A320 at take-off (its schedule padding differs).
    double TakeOff(Laia.Server.TrafficCore.NetLeg l) { for (double c = l.DepMin; c < l.DepMin + 200; c += 0.1) if (NetworkModel.At(l, c) is not null) return c; return double.NaN; }
    quick.DepMin += (int)Math.Round(TakeOff(slow2) + 3 - TakeOff(quick)); quick.ArrMin = quick.DepMin + 260;
    var ps0 = NetworkModel.At(slow2, 680); var pq0 = NetworkModel.At(quick, 680);
    Console.WriteLine($"  in trail: {slow2.Callsign} {ps0?.GsKt:0} kt, {quick.Callsign} {pq0?.GsKt:0} kt at FL{ps0?.AltFt / 100:0}/{pq0?.AltFt / 100:0}, {(ps0 is not null && pq0 is not null ? Geo2.DistanceNm(ps0.Lat, ps0.Lon, pq0.Lat, pq0.Lon) : 0):0} nm apart at 680");
    var w3 = Worst(new() { slow2, quick }, new(), 600, 860, out var n3, out var ev3);
    foreach (var e in ev3.Take(6)) Console.WriteLine("  " + e);
    Console.WriteLine($"  {ev3.Count} instructions; closest {w3:N0} ft within 5 nm");
    Check(ev3.Any(e => e.Contains("speeds up") || e.Contains("slows")) && !ev3.Any(e => e.Contains("level change")) && w3 >= 950,
        "a flight a little faster at the same level is kept in trail by speed (the one ahead faster first), not moved off its level");
    Check(ev3.FirstOrDefault(e => e.Contains("speeds up") || e.Contains("slows"))?.Contains("speeds up") == true, "the one ahead is sped up first");
    // The user ahead at its level, this one catching them up (24 Nov, DAL8948 at FL360): the user is asked first; after 90 s
    // the one behind is slowed — never moved above the user and then below (through the user's level).
    {
        var ai = Long("N7", "700", "A320", 600);
        var sep = new EnRouteSeparation();
        var p0 = NetworkModel.At(ai, 700)!;
        double uS = p0.FlownNm + 10, uGs = p0.GsKt - 25;   // the user 10 nm ahead on the same route
        var evs = new List<string>(); string? notice = null; double closest = double.MaxValue; var flips = 0; var lastSide = 0;
        for (var c = 700.0; c < 740; c += 2.0 / 60)
        {
            uS += uGs / 60 * (2.0 / 60);
            var (uLat, uLon, uHdg) = ai.PointAt(uS);
            var obs = new List<EnRouteSeparation.Obstacle> { new("UAL1196 (you)", uLat, uLon, uHdg, uGs, p0.AltFt, 0, 6) };
            foreach (var e in sep.Update(c, 40, -95, new[] { ai }, obs)) evs.Add($"{(c - 700) * 60:0} s {e.Leg.Callsign} {e.Text} for {e.Who}");
            notice ??= sep.UserNotice?.text;
            if (NetworkModel.At(ai, c) is { } a)
            {
                var alt = sep.Alt(ai, c, a.AltFt);
                var side = alt > p0.AltFt + 300 ? 1 : alt < p0.AltFt - 300 ? -1 : 0;
                if (side != 0 && lastSide != 0 && side != lastSide) flips++;
                if (side != 0) lastSide = side;
                var (uLat2, uLon2, _) = ai.PointAt(uS);
                var d = Geo2.DistanceNm(a.Lat, a.Lon, uLat2, uLon2);
                if (Math.Abs(alt - p0.AltFt) < 900) closest = Math.Min(closest, d);
            }
        }
        foreach (var e in evs.Take(4)) Console.WriteLine("  " + e);
        Console.WriteLine($"  notice: {notice ?? "none"}; closest at the user's level {closest:0.0} nm; side changes {flips}");
        Check(notice is not null && notice.Contains("catching you up"), "the user ahead is asked first, in the notices");
        Check(evs.Any(e => e.Contains("slows")) && closest >= 5, "after 90 s the one behind is slowed and stays 5 nm or more behind at the user's level");
        Check(flips == 0, "never moved above the user and then below (through the user's level)");
    }
}

Console.WriteLine("== M6: learning an airport from real tracks (synthetic adsb.lol-style traces), and flying what was learned");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var apt = new AirportModel(data);
    var map = apt.Map;
    var r27 = apt.Ends.First(e => e.Ident == "27");
    var left = new V2(-r27.Dir.Y, r27.Dir.X);
    V2 At(double along, double off) => r27.Threshold + r27.Dir * (along * nm) + left * (off * nm);
    var rng = new Random(3);
    var tracks = new List<IReadOnlyList<TrackPoint>>();
    List<TrackPoint> Fly(List<(V2 p, double? alt)> poly, double t0)
    {
        var res = new List<TrackPoint>(); var t = t0;
        for (var i = 0; i + 1 < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[i + 1]; var len = (b.p - a.p).Length; var brg = (b.p - a.p).Bearing;
            var n = Math.Max(1, (int)(len / (0.2 * nm)));
            for (var k = 0; k < n; k++)
            {
                var u = (double)k / n; var p = a.p + (b.p - a.p) * u;
                double? alt = a.alt is null || b.alt is null ? (u < 0.5 ? a.alt : b.alt) : a.alt + (b.alt - a.alt) * u;
                var (la, lo) = map.ToLatLon(p);
                res.Add(new TrackPoint(t, la, lo, alt, brg)); t += 3.6;
            }
        }
        return res;
    }
    for (var i = 0; i < 12; i++)
    {
        // Left downwind 4 nm out, base at 14 nm, a 30° intercept joining at about 10 nm, landing.
        var join = 9.4 + rng.NextDouble() * 1.2; var dw = 3.8 + rng.NextDouble() * 0.4; var bas = join + 4;
        var e = (bas - join) * Math.Tan(30 * Math.PI / 180);
        tracks.Add(Fly(new() { (At(3, dw), 4000), (At(-bas, dw), 3000), (At(-bas, e), 2800), (At(-join, 0), 2600), (At(0, 0), 60), (At(0.6, 0), null), (At(1.2, 0), null) }, i * 900));
    }
    for (var i = 0; i < 10; i++)
    {
        // Off runway 27, straight out 3 nm past the end, then a right turn to the north for 25 nm (radar vectors).
        var endA = r27.LengthM / nm;
        var p3 = At(endA + 3, 0);
        tracks.Add(Fly(new() { (At(0.1, 0), null), (At(endA * 0.6, 0), null), (At(endA * 0.75, 0), 300), (p3, 2500), (p3 + V2.FromBearing(15 + rng.NextDouble() * 10, 25 * nm), 12000) }, 20000 + i * 900));
    }
    var prof = TrackLearner.Learn("TEST", map, apt.ElevationFt, apt.Ends, tracks, "test", "2026-11-10");
    Console.WriteLine("  " + prof.Describe());
    {
        // The same arrivals, but the receivers lose them below about 900 ft, 2.5 nm out (KMCI, 15 Nov: arrivals barely
        // learned where the low part of the final isn't seen).
        var cut = tracks.Take(12).Select(t => (IReadOnlyList<TrackPoint>)t.Where(q => q.AltFt is { } a0 && a0 > apt.ElevationFt + 900).ToList()).ToList();
        var pc = TrackLearner.Learn("TEST", map, apt.ElevationFt, apt.Ends, cut, "test", "2026-11-10");
        var ac = pc.Arrivals.GetValueOrDefault("27");
        Console.WriteLine($"  arrivals not seen below 900 ft: {ac?.Count ?? 0} of 12 learned");
        Check(ac is not null && ac.Count == 12, "landings seen only down to about 900 ft are learned too");
        // In the mountains (KGPI, 16 Nov): arrivals lost at 1,800 ft above the field, about 7 nm out on final, and
        // departures first seen at 2,000 ft, 2½ nm past the runway's end.
        var high = tracks.Take(12).Select(t => (IReadOnlyList<TrackPoint>)t.Where(q => q.AltFt is { } a0 && a0 > apt.ElevationFt + 1800).ToList())
            .Concat(tracks.Skip(12).Select(t => (IReadOnlyList<TrackPoint>)t.Where(q => q.AltFt is { } a0 && a0 > apt.ElevationFt + 2000).ToList())).ToList();
        var ph = TrackLearner.Learn("TEST", map, apt.ElevationFt, apt.Ends, high, "test", "2026-11-10");
        var ha = ph.Samples.Count(x => x.Kind == "arrival" && x.Runway == "27"); var hd = ph.Samples.Count(x => x.Kind == "departure" && x.Runway == "27");
        Console.WriteLine($"  seen only well above the runway: {ha} of 12 arrivals and {hd} of 10 departures learned");
        Check(ha == 12 && hd == 10, "arrivals lost and departures first seen well above the runway, lined up with it, are learned too");
    }
    {
        // Close parallels (KATL, 20 Nov: 26L/26R 1,000 ft apart, 26R's real arrivals all learned as 26L's): a parallel
        // 09L/27R 305 m north of 09/27 (listed after it), 12 arrivals to 27R, seen down to the runway, lost below 900 ft,
        // and lost at 1,800 ft; and 10 take-offs off 27R first seen at 2,000 ft.
        var dataP = TestAirport.Build(false);
        var (pla2, plo2) = map.ToLatLon(new V2(0, 305));
        dataP.Runways.Add(new AirportRunway { Lat = pla2, Lon = plo2, ElevationM = 10, Heading = 90, LengthM = 3000, WidthM = 45, PrimaryNumber = 9, PrimaryDesignator = 1, SecondaryNumber = 27, SecondaryDesignator = 2 });
        var aptP = new AirportModel(dataP);
        var r27R = aptP.Ends.First(e => e.Ident == "27R");
        Console.WriteLine($"  close parallels listed: {string.Join(", ", aptP.Ends.Select(e => e.Ident))}");
        V2 AtR(double along, double off) => r27R.Threshold + r27R.Dir * (along * nm) + left * (off * nm);
        var onR = new List<IReadOnlyList<TrackPoint>>();
        for (var i = 0; i < 12; i++)
        {
            var join = 9.4 + rng.NextDouble() * 1.2; var dw = 3.8 + rng.NextDouble() * 0.4; var bas = join + 4;
            var e = (bas - join) * Math.Tan(30 * Math.PI / 180);
            var t = Fly(new() { (AtR(3, dw), 4000), (AtR(-bas, dw), 3000), (AtR(-bas, e), 2800), (AtR(-join, 0), 2600), (AtR(0, 0), 60), (AtR(0.6, 0), null), (AtR(1.2, 0), null) }, i * 900);
            var cutAt = i % 3 == 0 ? -1 : i % 3 == 1 ? 900 : 1800;
            onR.Add(cutAt < 0 ? t : t.Where(q => q.AltFt is { } a0 && a0 > aptP.ElevationFt + cutAt).ToList());
        }
        for (var i = 0; i < 10; i++)
        {
            var endA = r27R.LengthM / nm; var p3 = AtR(endA + 3, 0);
            var t = Fly(new() { (AtR(0.1, 0), null), (AtR(endA * 0.6, 0), null), (AtR(endA * 0.75, 0), 300), (p3, 2500), (p3 + V2.FromBearing(15 + rng.NextDouble() * 10, 25 * nm), 12000) }, 20000 + i * 900);
            onR.Add(i % 2 == 0 ? t : t.Where(q => q.AltFt is { } a0 && a0 > aptP.ElevationFt + 2000).ToList());
        }
        var pp = TrackLearner.Learn("TEST", aptP.Map, aptP.ElevationFt, aptP.Ends, onR, "test", "2026-11-20");
        int A(string rw) => pp.Samples.Count(x => x.Kind == "arrival" && x.Runway == rw);
        int D(string rw) => pp.Samples.Count(x => x.Kind == "departure" && x.Runway == rw);
        Console.WriteLine($"  close parallels 305 m apart: arrivals 27R {A("27R")}, 27 {A("27")}; departures 27R {D("27R")}, 27 {D("27")}");
        Check(A("27R") == 12 && A("27") == 0 && D("27R") == 10 && D("27") == 0, "close parallels: each real track is learned on the runway it was really on");
    }
    var a27 = prof.Arrivals.GetValueOrDefault("27");
    Check(a27 is not null && a27.Count == 12 && Math.Abs(a27.JoinP50 - 10) < 1 && Math.Abs(a27.InterceptDeg - 30) < 8 && Math.Abs(a27.DownwindNm - 4) < 0.7 && Math.Abs(a27.BaseNm - 14) < 1.5,
        "arrivals: joins at ~10 nm, 30° intercept, downwind 4 nm out, base at ~14 nm");
    var d27 = prof.Departures.GetValueOrDefault("27");
    Check(d27 is not null && d27.Count == 10 && d27.Paths.Any(p => p.Bearing is 0 or 30 or 330), "departures: a typical path to the north off 27");
    var json = System.Text.Json.JsonSerializer.Serialize(prof, AdsbTrace.Json);
    var back = System.Text.Json.JsonSerializer.Deserialize<AirportProfile>(json, AdsbTrace.Json)!;
    Check(back.Arrivals["27"].Count == 12 && back.Samples.Count > 0, "the profile saves and loads");
    var trace = System.Text.Encoding.UTF8.GetBytes("{\"icao\":\"a1b2c3\",\"timestamp\":1731283200.0,\"trace\":[[0.0,28.4,-81.3,\"ground\",10.1,180.0,0,null,null,\"adsb_icao\"],[5.5,28.41,-81.3,1200,150.2,181.0,0,1500,null,\"adsb_icao\"]]}");
    var pts = AdsbTrace.Parse(trace)!;
    Check(pts.Count == 2 && pts[0].AltFt is null && pts[1].AltFt == 1200 && Math.Abs(pts[1].T - 1731283205.5) < 0.01, "a readsb trace file is read (ground and altitude points)");

    // Flying it: vectored arrivals join where real ones do; a runway-heading departure turns where real ones turn.
    var data2 = TestAirport.Build(false);
    data2.Arrivals.Clear();
    var world = new TrafficWorld(new AirportModel(data2), new TestSource(), seed: 9) { Automatic = false, Wind = new Wind(270, 10), Learned = prof };
    world.SelectRunways(true);
    var joins = new List<double>();
    for (var i = 0; i < 6; i++)
    {
        Laia.Common.Airports.ScheduleLeg L() => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = "N" + i, Flight = "9" + i, Dep = "KXXX", Arr = "TEST" };
        var f = world.SpawnArrival(0, false, L(), entry: (At(-5, 15) , 6000, 6000));
        if (f is null) continue;
        var rw = f.Rwy!;
        // Where the path settles on the centreline for good.
        double joinAt = double.NaN;
        for (var s2 = f.ThrS; s2 > 0; s2 -= 100)
        {
            var (pp, dd, _) = f.Path!.At(s2);
            if (Math.Abs(rw.Offset(pp)) > 0.1 * nm || Math.Abs(Geo2.Diff(dd, rw.Heading)) > 3) { joinAt = -rw.Along(pp) / nm; break; }
        }
        joins.Add(joinAt);
        f.Done = true;
    }
    Console.WriteLine($"  vectored arrivals join the final at {string.Join(", ", joins.Select(j => j.ToString("0.0")))} nm");
    Check(joins.Count >= 4 && joins.All(j => j is > 8.5 and < 12), "vectored arrivals join where the real ones do (about 9.4–10.6 nm, not the generic 9.5 every time)");
}

Console.WriteLine("== No ATIS: real traffic picks the runways before the wind (live first, then the learned day)");
{
    var world = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 2) { Automatic = false, Wind = new Wind(100, 5) };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    var byWind = world.ArrRwy!.Ident;
    var prof = new AirportProfile { Icao = "TEST", Day = "2026-11-11" };
    prof.Arrivals["27"] = new AirportProfile.RunwayArrivals { Count = 20, JoinP25 = 9, JoinP50 = 10, JoinP75 = 11 };
    prof.Arrivals["09"] = new AirportProfile.RunwayArrivals { Count = 4, JoinP25 = 9, JoinP50 = 10, JoinP75 = 11 };
    prof.Departures["27"] = new AirportProfile.RunwayDepartures { Count = 18 };
    world.Learned = prof;
    world.SelectRunways(true);
    var byLearned = world.ArrRwy!.Ident;
    world.LiveLanding = new() { "09" }; world.LiveDeparting = new() { "09" };
    world.SelectRunways(true);
    var byLive = world.ArrRwy!.Ident;
    Console.WriteLine($"  wind 100/5: by wind {byWind}; learned day (27 most used) {byLearned}; live traffic on 09 {byLive}");
    foreach (var x in log.Where(x => x.StartsWith("Runways in use")).TakeLast(2)) Console.WriteLine("  " + x);
    Check(byWind == "09" && byLearned == "27" && byLive == "09" && world.DepRwy!.Ident == "09", "the learned day beats a light wind; live traffic beats both");
}

Console.WriteLine("== M8 Smart traffic notices: the user's place in the sequence (generous gap) and runway notices on the ground");
{
    var nm = Geo2.MPerNm;
    var world = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 14) { Automatic = false, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    var r = world.ArrRwy!;
    Laia.Common.Airports.ScheduleLeg L(string reg, string fl) => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = reg, Flight = fl, Dep = "KXXX", Arr = "TEST" };
    var a = world.SpawnArrival(18, false, L("N71", "71"))!;
    var b = world.SpawnArrival(30, false, L("N72", "72"))!;
    // The user: straight in on the final, 24 nm out, between the two.
    var u = r.Threshold - r.Dir * (24 * nm);
    world.UserCallsign = "N123AB"; world.UserType = "C172"; world.UserDestination = "TEST";
    world.UserOnGround = false; world.UserTrack = r.Heading; world.UserGs = 160; world.UserAltFt = 6000;
    double landA = -1, landB = -1, userLand = -1;
    string? firstSeq = null, behindCs = null;
    for (var t = 0.0; t < 2400 && (landB < 0 || userLand < 0 || landA < 0); t += 0.5)
    {
        if (userLand < 0)
        {
            var left = -r.Along(u) / nm;
            var kt = left > 6 ? 160 : 110;
            u += r.Dir * (kt * Geo2.MpsPerKt * 0.5);
            world.UserGs = kt; world.UserAltFt = Math.Max(0, Math.Min(6000, left * 318));
            if (r.Along(u) >= 0) userLand = world.Time;
        }
        world.UserPos = u;
        world.Step(0.5);
        if (firstSeq is null && world.Time > 12 && world.Notices.FirstOrDefault(n => n.Id == "seq") is { } sq) { firstSeq = sq.Text; behindCs = world.Sequence?.BehindCallsign; }
        if (landA < 0 && a.OnGround) landA = world.Time;
        if (landB < 0 && b.OnGround) landB = world.Time;
    }
    Console.WriteLine($"  notice: {firstSeq}");
    foreach (var x in log.Where(x => x.Contains("(you)") || x.Contains("behind N123AB") || x.Contains("for spacing"))) Console.WriteLine("  " + x);
    var behindLand = behindCs == "DAL71" ? landA : behindCs == "DAL72" ? landB : double.NaN;
    Console.WriteLine($"  landings: DAL71 {landA:0}, you {userLand:0}, DAL72 {landB:0}; behind you: {behindCs ?? "nobody"}, {behindLand - userLand:0} s after you");
    Check(firstSeq is not null && firstSeq.Contains("for 27") && firstSeq.Contains("between"), "the user is told their number and the aircraft either side");
    Check(behindCs is null || behindLand - userLand >= 125, "LAIA's arrival planned behind the user leaves the generous gap (about 140 s, 5–6 nm)");

    // On the ground: the user holding short of the runway with an arrival on short final.
    var world2 = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 15) { Automatic = false, Wind = new Wind(270, 10) };
    world2.SelectRunways(true);
    var r2 = world2.ArrRwy!;
    var arr = world2.SpawnArrival(6, false, L("N73", "73"))!;
    world2.UserOnGround = true; world2.UserGs = 0; world2.UserTrack = r2.Heading + 90;
    world2.UserPos = r2.Threshold + r2.Dir * 150 + new V2(-r2.Dir.Y, r2.Dir.X) * 60;   // beside the runway near this end
    string? fin = null;
    for (var t = 0.0; t < 120 && fin is null; t += 0.5) { world2.Step(0.5); fin = world2.Notices.FirstOrDefault(n => n.Text.Contains("final"))?.Text; }
    Console.WriteLine($"  on the ground: {fin}");
    Check(fin is not null && fin.Contains("DAL73") && fin.Contains("don't line up"), "holding short with traffic on final: a notice, not an instruction to the AI");
}

Console.WriteLine("== The user's place holds while they slow to keep their spacing (23 Nov, KBOS: the SWA behind was put ahead and sped up)");
{
    var nm = Geo2.MPerNm;
    var world = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 16) { Automatic = false, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    var r = world.ArrRwy!;
    Laia.Common.Airports.ScheduleLeg L(string reg, string fl) => new() { Airline = "SWA", Operator = "SWA", Type = "B737", Reg = reg, Flight = fl, Dep = "KXXX", Arr = "TEST" };
    var a = world.SpawnArrival(22, false, L("N81", "81"))!;
    var b = world.SpawnArrival(48, false, L("N82", "82"))!;
    var u = r.Threshold - r.Dir * (34 * nm);
    world.UserCallsign = "N456CD"; world.UserType = "A320"; world.UserDestination = "TEST";
    world.UserOnGround = false; world.UserTrack = r.Heading; world.UserGs = 230; world.UserAltFt = 9000;
    var seqs = new List<int>(); string? advice = null; double maxUp = 0, prevIas = double.NaN; var userLand = -1.0;
    for (var t = 0.0; t < 1500 && userLand < 0; t += 0.5)
    {
        var left = -r.Along(u) / nm;
        // Slows from 230 to 190 kt after a minute (keeping spacing behind the one ahead), 150 inside 8 nm.
        var kt = left > 8 ? (t < 60 ? 230 : 190) : 150;
        u += r.Dir * (kt * Geo2.MpsPerKt * 0.5);
        world.UserGs = kt; world.UserAltFt = Math.Max(0, Math.Min(9000, left * 318));
        if (r.Along(u) >= 0) userLand = world.Time;
        world.UserPos = u;
        world.Step(0.5);
        if (world.Sequence is { } sq && t > 15) { if (seqs.Count == 0 || seqs[^1] != sq.Number) seqs.Add(sq.Number); advice ??= sq.SpeedAdvice; }
        if (!b.OnGround && !double.IsNaN(prevIas)) maxUp = Math.Max(maxUp, (b.Ias - prevIas) / 0.5);
        prevIas = b.Ias;
    }
    foreach (var x in log.Where(x => x.Contains("(you)") || x.Contains("N456CD"))) Console.WriteLine("  " + x);
    Console.WriteLine($"  your numbers: {string.Join(", ", seqs)}; advice: {advice ?? "none"}; the one behind sped up at most {maxUp:0.0} kt/s");
    Check(!log.Any(x => x.Contains("now ahead of N456CD") || x.Contains("(you): new sequence") || x.Contains("re-sequenced")), "nobody behind is put ahead of the user for slowing down, and no new sequence");
    Check(seqs.Zip(seqs.Skip(1)).All(p => p.Second < p.First), "the user's number only goes down (as the ones ahead land)");
    Check(advice is not null && (advice.Contains("kt keeps your spacing") || advice.Contains("no speed change needed")), "the notice says what speed keeps the spacing (or that none is needed)");
    Check(maxUp <= 1.55, "speeding up comes in gradually (1.5 kt a second or less)");
}

Console.WriteLine("== The user's runway: never changed below 10,000 ft, at most once; live-traffic flicker waits 10 minutes");
{
    var nm = Geo2.MPerNm;
    var w = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 21) { Automatic = false, Wind = new Wind(270, 2) };
    var log = new List<string>(); w.Log = s2 => log.Add(s2);
    w.LiveLanding = new() { "27" };
    w.SelectRunways(true);
    var r27 = w.ArrRwy!;
    w.UserCallsign = "ACA1196"; w.UserType = "A320"; w.UserDestination = "TEST";
    w.UserOnGround = false; w.UserTrack = r27.Heading; w.UserGs = 0; w.UserAltFt = 6000;
    w.UserPos = r27.Threshold - r27.Dir * (20 * nm);
    for (var t = 0.0; t < 30; t += 0.5) w.Step(0.5);
    var first = w.Sequence?.Runway;
    // Real traffic flips to the other way: below 10,000 ft the user's runway isn't taken away.
    w.LiveLanding = new() { "09" };
    for (var t = 0.0; t < 1400; t += 0.5) w.Step(0.5);
    var held = w.ArrRwy?.Ident;
    // Above 10,000 ft: the one change happens.
    w.UserAltFt = 12000;
    for (var t = 0.0; t < 300; t += 0.5) w.Step(0.5);
    var second = w.Sequence?.Runway;
    // And back again: the airport changes, the user doesn't (no more than one change).
    w.LiveLanding = new() { "27" };
    for (var t = 0.0; t < 1300; t += 0.5) w.Step(0.5);
    var third = w.Sequence?.Runway;
    foreach (var x in log.Where(x => x.Contains("Runways in use") || x.Contains("(you)") || x.Contains("waits"))) Console.WriteLine("  " + x);
    Console.WriteLine($"  user's runway: {first} → {second} → {third}; airport while the user was low: {held}; airport at the end: {w.ArrRwy?.Ident}");
    Check(first == "27" && held == "27", "below 10,000 ft the user's landing runway isn't taken away (the change waits)");
    Check(second == "09", "above 10,000 ft the user's runway follows the airport's change, once");
    Check(third == "09" && w.ArrRwy?.Ident == "27", "no second change for the user: they keep their runway");
}

Console.WriteLine("== Start-up by landing slot: arrivals taken over on top of each other close in land in order with no vectors or loops");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var map = new LocalMap(data.Lat, data.Lon);
    var origin = map.ToLatLon(new V2(250 * nm, -100 * nm));
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 5)
    {
        Automatic = false, Wind = new Wind(270, 10), WeekMinute = 2000, Locate = c => c == "KFAR" ? origin : null,
    };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    Laia.Common.Airports.ScheduleLeg Leg(int dep, string fl, string reg) => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = reg, Flight = fl, Dep = "KFAR", Arr = "TEST", DepMin = dep, ArrMin = dep + 60 };
    NetPos? np = null;
    for (var dep = 2000 - 90; dep <= 2000; dep++)
    {
        var p = world.NetworkPosition(Leg(dep, "1", "N1"));
        if (p is not null && Geo2.DistanceNm(data.Lat, data.Lon, p.Lat, p.Lon) is > 45 and < 60) { np = p; break; }
    }
    Check(np is not null, "the whole sky has an arrival about 50 nm out");
    var fl = new List<Laia.Server.TrafficCore.Flight>();
    for (var k = 0; k < 4 && np is not null; k++) if (world.SpawnFromNetwork(Leg(1, (k + 1).ToString(), "N" + (k + 1)), np) is { } g) fl.Add(g);
    foreach (var x in log.Where(x => x.Contains("taken over"))) Console.WriteLine("  " + x);
    Check(fl.Count == 4, "all four are taken over");
    var dists = fl.Select(f => (f.ThrS - f.S) / nm).OrderBy(x => x).ToList();
    var apart = dists.Zip(dists.Skip(1), (x, y) => y - x).DefaultIfEmpty(99).Min();
    Check(apart >= 6.9, $"they start at least 7 track nm apart ({apart:0.0})");
    for (var t = 0.0; t < 2700 && fl.Any(f => !f.OnGround); t += 0.5) world.Step(0.5);
    var vec = log.Count(x => x.Contains("vectors for spacing") || x.Contains("breaking off") || x.Contains("going around") || x.Contains("a loop to look into"));
    foreach (var x in log.Where(x => x.Contains("vectors for spacing") || x.Contains("breaking off") || x.Contains("loop"))) Console.WriteLine("  " + x);
    Check(fl.All(f => f.OnGround), "all four land");
    Check(vec == 0, $"no vectors, break-offs, go-arounds or loops to fix the start ({vec})");
}

Console.WriteLine("== The user abreast of a LAIA arrival: the AI one goes behind and takes the delay, and stays there");
{
    var nm = Geo2.MPerNm;
    var w = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 8) { Automatic = false, Wind = new Wind(270, 5) };
    var log = new List<string>(); w.Log = s2 => log.Add(s2);
    w.SelectRunways(true);
    var r = w.ArrRwy!;
    var left = new V2(-r.Dir.Y, r.Dir.X);
    // A LAIA arrival taken over 110 nm out on the extended centreline (side), and the user abreast of it on the other
    // side, heading in at an angle (not on the final), a couple of flying miles nearer.
    var ai = w.SpawnArrival(0, false, entry: (r.Threshold - r.Dir * (110 * nm) + left * (8 * nm), 20000, 20000))!;
    var da = (ai.ThrS - ai.S) / nm;
    w.UserCallsign = "DAL1196"; w.UserType = "A321"; w.UserDestination = "TEST";
    w.UserOnGround = false; w.UserAltFt = 20000; w.UserGs = 300;
    // Right beside it, on the same STAR, a little faster.
    w.UserStar = "ARRVL1";
    w.UserPos = ai.Pos + V2.FromBearing(ai.Heading) * (0.5 * nm) + V2.FromBearing(ai.Heading + 90) * (1.5 * nm);
    w.UserTrack = ai.Heading; w.UserGs = Math.Max(ai.Gs, 250) + 15;
    Console.WriteLine($"  {ai.Callsign} {da:0} flying nm out on {ai.Route} at {ai.Gs:0} kt; the user beside it at {w.UserGs:0}");
    for (var t = 0.0; t < 40; t += 0.5) { w.Step(0.5); w.UserPos = w.UserPos.Value + V2.FromBearing(w.UserTrack) * (w.UserGs * 0.5144 * 0.5); w.UserTrack = ai.Heading; }
    var seq1 = w.Sequence?.Number;
    // The user slows a little (keeping their spacing): a few seconds of estimate either way doesn't flip the order.
    w.UserGs -= 25;
    for (var t = 0.0; t < 120; t += 0.5) { w.Step(0.5); w.UserPos = w.UserPos.Value + V2.FromBearing(w.UserTrack) * (w.UserGs * 0.5144 * 0.5); w.UserTrack = ai.Heading; }
    var seq2 = w.Sequence?.Number;
    foreach (var x in log.Where(x => x.Contains("(you)") || x.Contains(ai.Callsign + ":"))) Console.WriteLine("  " + x);
    Console.WriteLine($"  user number {seq1} then {seq2}; {ai.Callsign} {(ai.ThrS - ai.S) / nm:0} nm out, slowed {ai.SlowDv:0} kt; advice: {w.Sequence?.SpeedAdvice}");
    Check(seq1 == 1 && seq2 == 1, "the user is number 1, the LAIA arrival abreast of them behind");
    Check(ai.BehindUser && ai.Seq == 2, "the LAIA arrival is kept behind the user");
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
        RunwayOnly = true, Level = TrafficLevel.RealWorld, Wind = new Wind(270, 10), WeekMinute = 2000, ClockFrom = () => clock,
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

Console.WriteLine("== FAA preferred routes: the STAR and SID a flight would file, by airport pair and flow");
{
    var csv = new[]
    {
        "Orig,Route String,Dest,Hours1,Hours2,Hours3,Type,Area,Altitude,Aircraft,Direction,Seq,DCNTR,ACNTR",
        "ACY,ACY LEEAH ATR TRPOD Q409 CRPLR Q85 LPERD SNFLD3 MCO,MCO,,,,H,TO ISM.MCO,,JETS: RNAV 1  - DME/DME/IRU OR GPS REQUIRED,MCO LANDING NORTH,1,ZDC,ZJX",
        "ACY,ACY LEEAH ATR TRPOD Q409 CRPLR EARZZ Q131 WAALT Y289 BAHAA HIBAC ALYNA4 MCO,MCO,,,,H,TO ISM.LEE.MCO.ORL.SFB,,JETS. TURBOPROPS:  RNAV 1-DME/DME/IRU OR GPS,WATER,2,ZDC,ZJX",
        "ACY,ACY LEEAH ATR TRPOD Q409 CRPLR Q85 LPERD GTOUT1 MCO,MCO,,,,H,,,JETS: RNAV 1  - DME/DME/IRU OR GPS REQUIRED,MCO LANDING SOUTH,3,ZDC,ZJX",
        "ATL,ATL SMLTZ3 WALET ZJAYX GRNCH5 MCO,MCO,,,,H,,,,,2,ZTL,ZJX",
        "ATL,ATL SMLTZ3 WALET MJKAL JAFAR3 ORL,ORL,,,,H,,,,,1,ZTL,ZJX",
        "TPA,TPA DADES PROPP1 MCO,MCO,,,,L,,,TURBOPROPS ONLY,,1,ZJX,ZJX",
    };
    var db = PreferredRoutes.Parse(csv);
    var stars = new HashSet<string> { "SNFLD", "GTOUT", "ALYNA", "GRNCH" };
    bool IsStar(string t) => stars.Contains(AirportRules.Base(t));
    var north = db.Arrival("KACY", "KMCO", 359, true, IsStar);
    var south = db.Arrival("KACY", "KMCO", 179, true, IsStar);
    var atl = db.Arrival("KATL", "KMCO", 359, true, IsStar);
    var dep = db.Departure("KATL", "KMCO", 90, true, t => AirportRules.Base(t) == "SMLTZ");
    Console.WriteLine($"  ACY→MCO north flow {north?.star} via {north?.transition}; south flow {south?.star}; ATL→MCO {atl?.star} via {atl?.transition}; ATL departs {dep?.sid}.{dep?.transition}");
    // An origin the database doesn't list (Seattle): the STAR the routes from airports in that direction use.
    (double, double)? Where(string id) => id switch { "ATL" => (315.0, 350.0), "ACY" => (20.0, 790.0), _ => null };
    var sea = db.ArrivalFromDirection("KMCO", 305, 359, Where, IsStar);
    Console.WriteLine($"  from Seattle (not listed): {sea?.star} via {sea?.transition}");
    Check(sea?.star == "GRNCH5", "an origin not in the database gets the STAR used by listed origins in the same direction");
    Check(db.StarFamiliarity("JAFAR3", "KMCO") == 2 && db.StarFamiliarity("GRNCH5", "KMCO") == 0 && db.StarFamiliarity("SLOAO1", "KMCO") == 1,
        "the database tells an airport's own STARs (GRNCH → MCO) from a nearby airport's (JAFAR → ORL)");
    Check(db.PropsOnly("PROPP1") && !db.PropsOnly("GRNCH5"), "a STAR only routed for turboprops is never given to a jet");
    Check(north?.star == "SNFLD3" && north?.transition == "LPERD" && south?.star == "GTOUT1" && atl?.star == "GRNCH5" && atl?.transition == "ZJAYX" && dep?.sid == "SMLTZ3" && dep?.transition == "WALET",
        "the right STAR for the flow (not the oceanic/weather ALYNA4 option), its entry fix, and the SID with its exit");
}

Console.WriteLine("== Arrival manager: two arrivals bunched far out are spaced with speed, not a hold");
{
    var world = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 11) { Automatic = false, Wind = new Wind(270, 10) };
    world.SelectRunways(true);
    Laia.Common.Airports.ScheduleLeg L(string reg, string fl) => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = reg, Flight = fl, Dep = "KXXX", Arr = "TEST" };
    // 4 nm apart (about a minute) on the same STAR: under the ~95 s needed at the runway.
    var a = world.SpawnArrival(30, false, L("N1", "11"))!;
    var b = world.SpawnArrival(34, false, L("N2", "22"))!;
    double landA = -1, landB = -1; var hA = a.LastHold; var hB = b.LastHold; var holds = 0;
    for (var t = 0.0; t < 3600 && (landA < 0 || landB < 0); t += 0.5)
    {
        world.Step(0.5);
        if (a.LastHold != hA || b.LastHold != hB) { holds++; hA = a.LastHold; hB = b.LastHold; Console.WriteLine($"  hold at t={t:0}: a {(a.ThrS - a.S) / Geo2.MPerNm:0.0} nm {a.Ias:0} kt; b {(b.ThrS - b.S) / Geo2.MPerNm:0.0} nm {b.Ias:0} kt slowed {b.Slowed} delay {b.Delay:0} / a delay {a.Delay:0}"); }
        if (landA < 0 && a.OnGround) landA = world.Time;
        if (landB < 0 && b.OnGround) landB = world.Time;
    }
    Console.WriteLine($"  landed {Math.Abs(landB - landA):0} s apart; holds {holds}; go-arounds {(a.WentAround || b.WentAround ? "yes" : "no")}");
    Check(landA > 0 && landB > 0 && Math.Abs(landB - landA) >= 60 && holds == 0 && !a.WentAround && !b.WentAround,
        "both land, properly spaced, with no hold and no go-around");
}

Console.WriteLine("== Arrival manager: too close for speed alone, 25 nm out — vectors (path stretch), not a hold");
{
    var world = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 12) { Automatic = false, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    Laia.Common.Airports.ScheduleLeg L(string reg, string fl) => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = reg, Flight = fl, Dep = "KXXX", Arr = "TEST" };
    var a = world.SpawnArrival(24, false, L("N3", "33"))!;
    var b = world.SpawnArrival(25.2, false, L("N4", "44"))!;   // 1.2 nm behind: far too close, too near for speed
    double landA = -1, landB = -1;
    for (var t = 0.0; t < 3600 && (landA < 0 || landB < 0); t += 0.5)
    {
        world.Step(0.5);
        if (landA < 0 && a.OnGround) landA = world.Time;
        if (landB < 0 && b.OnGround) landB = world.Time;
    }
    foreach (var x in log.Where(x => x.Contains("vectors for spacing") || x.Contains("holding") || x.Contains("360") || x.Contains("breaking off") || x.Contains("behind DAL"))) Console.WriteLine("  " + x);
    Console.WriteLine($"  landed {Math.Abs(landB - landA):0} s apart; {world.ArrivalManagerSummary}");
    Check(log.Any(x => x.Contains("vectors for spacing")) && !log.Any(x => x.Contains("holding for spacing")), "it's vectored off the route and back, not held");
    Check(landA > 0 && landB > 0 && Math.Abs(landB - landA) >= 70 && !a.WentAround && !b.WentAround, "both land properly spaced, no go-around");
}

Console.WriteLine("== Runway change: arrivals far out and departures not yet lined up get the new runways");
{
    var world = new TrafficWorld(new AirportModel(TestAirport.Build(false)), new TestSource(), seed: 13) { Automatic = false, Wind = new Wind(270, 10) };
    var log = new List<string>(); world.Log = s2 => log.Add(s2);
    world.SelectRunways(true);
    Laia.Common.Airports.ScheduleLeg L(string reg, string fl) => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = reg, Flight = fl, Dep = "KXXX", Arr = "TEST" };
    var far = world.SpawnArrival(45, false, L("N5", "55"))!;
    var near = world.SpawnArrival(15, false, L("N6", "66"))!;
    var before = far.Rwy!.Ident;
    for (var t = 0.0; t < 20; t += 0.5) world.Step(0.5);
    world.Wind = new Wind(90, 15);
    world.SelectRunways(true);
    for (var t = 0.0; t < 5; t += 0.5) world.Step(0.5);
    Console.WriteLine($"  {log.LastOrDefault(x => x.StartsWith("Runway change")) ?? "(no runway change logged)"}; far one {before} → {far.Rwy!.Ident}, near one {near.Rwy!.Ident}");
    Check(far.Rwy!.Ident != before && near.Rwy!.Ident == before, "the far arrival is given the new runway; the one 15 nm out finishes on the old one");
    double land = -1;
    for (var t = 0.0; t < 2400 && land < 0; t += 0.5) { world.Step(0.5); if (far.OnGround) land = world.Time; }
    Check(land > 0 && far.Rwy!.Ident != before, "and lands on the new runway");
}

Console.WriteLine("== Published altitude restrictions on the STAR are met at their fixes");
{
    var nm = Geo2.MPerNm;
    foreach (var (desc, alt, label) in new[] { (2, 6000.0, "at or above 6,000"), (3, 4000.0, "at or below 4,000"), (1, 6000.0, "at 6,000") })
    {
        var data = TestAirport.Build(false);
        var star = data.Arrivals.First(a => a.Name == "ARRVL1");
        star.Legs[1].AltDesc = desc; star.Legs[1].Alt1Ft = alt; star.Legs[1].SpeedKt = 230;
        var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 5) { Automatic = false, Wind = new Wind(270, 10) };
        world.SelectRunways(true);
        Laia.Common.Airports.ScheduleLeg L() => new() { Airline = "DAL", Operator = "DAL", Type = "A321", Reg = "N77", Flight = "77", Dep = "KXXX", Arr = "TEST" };
        var f = world.SpawnArrival(40, false, L())!;
        var fix = f.AltLimits.FirstOrDefault(c => (desc == 2 ? c.Min : c.Max) == alt);
        double crossed = double.NaN, crossedKt = double.NaN;
        for (var t = 0.0; t < 1800 && double.IsNaN(crossed); t += 0.5)
        {
            world.Step(0.5);
            if (f.S >= fix.S) { crossed = f.AltFt; crossedKt = f.Ias; }
        }
        world.Step(0.5);
        Console.WriteLine($"  {label}, max 230 kt at MIDDL: crossed at {crossed:0} ft, {crossedKt:0} kt ({f.Route}); checked: {world.RestrictionSummary}");
        Check(fix.S > 0 && (desc == 2 ? crossed >= alt - 100 : desc == 3 ? crossed <= alt + 100 : Math.Abs(crossed - alt) <= 150), $"the arrival crosses MIDDL {label}");
        Check(crossedKt <= 240, "and at or below its 230 kt speed limit");
        Check(f.ChecksMet >= 1 && f.ChecksMissed.Count == 0, "the crossing is checked and counted as met");
    }
}

Console.WriteLine("== Taken over mid-descent just before a high restriction (MCO WHOVL: 9,000–14,000 ft, max 250 kt) — still met");
{
    var nm = Geo2.MPerNm;
    var data = TestAirport.Build(false);
    var star = data.Arrivals.First(a => a.Name == "ARRVL1");
    star.Legs[1].AltDesc = 4; star.Legs[1].Alt1Ft = 14000; star.Legs[1].Alt2Ft = 9000; star.Legs[1].SpeedKt = 250;
    var world = new TrafficWorld(new AirportModel(data), new TestSource(), seed: 5) { Automatic = false, Wind = new Wind(270, 10) };
    world.SelectRunways(true);
    Laia.Common.Airports.ScheduleLeg L() => new() { Airline = "WJA", Operator = "WJA", Type = "B738", Reg = "C-FWJA", Flight = "1272", Dep = "CYYZ", Arr = "TEST" };
    // 12 nm before MIDDL (18, -12) on the way from FARRR (30, -30), at 17,500 ft, as the session start puts it.
    var midd = new V2(18 * nm, -12 * nm); var farr = new V2(30 * nm, -30 * nm);
    var f = world.SpawnArrival(0, false, L(), entry: (midd + (farr - midd).Unit * (12 * nm), 17500, 37000))!;
    var fix = f.AltLimits.FirstOrDefault(c => c.Max == 14000);
    var startAlt = f.AltFt;
    Console.WriteLine($"  limits: {string.Join("; ", f.AltLimits.Select(c => $"{c.S / nm:0.0} nm: {c.Min:0}-{c.Max:0}"))}; speed limits: {string.Join("; ", f.SpeedLimits.Select(c => $"{c.S / nm:0.0} nm {c.Kt:0}"))}; route {f.Route}");
    double crossed = double.NaN, crossedKt = double.NaN, lastNm = -1;
    for (var t = 0.0; t < 1800 && double.IsNaN(crossed); t += 0.5)
    {
        world.Step(0.5);
        if (f.S / nm - lastNm >= 1) { lastNm = Math.Floor(f.S / nm); Console.WriteLine($"    {f.S / nm:0.0} nm {f.Phase}: {f.AltFt:0} ft {f.Ias:0} kt vs {f.Vs:0}"); }
        if (fix.S > 0 && f.S >= fix.S) { crossed = f.AltFt; crossedKt = f.Ias; }
    }
    Console.WriteLine($"  starts at {startAlt:0} ft (was 17,500); crosses MIDDL at {crossed:0} ft, {crossedKt:0} kt");
    Check(crossed <= 14150 && crossed >= 8900, "it crosses between 9,000 and 14,000 ft");
    Check(crossedKt <= 255, "at or below 250 kt");
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

Console.WriteLine("== Taxi flow: the inner parallel for arrivals, the outer for departures, one-way stretches");
{
    var apt = new AirportModel(TestAirport.Build(false, twoParallels: true));
    var world = new TrafficWorld(apt, new TestSource(), seed: 4) { Level = TrafficLevel.Busy, Wind = new Wind(270, 10), WaitForTaxiFlow = true };
    world.SelectRunways(true);
    world.Step(0.25);
    var fl = world.Flow;
    Console.WriteLine("  " + (fl is null ? "(no taxi flow)" : fl.Key + ": " + fl.Summary));
    Check(fl is not null && fl.InnerEdges > 0 && fl.OuterEdges > 0, $"taxiway A is found as the inner parallel and B as the outer ({fl?.InnerEdges} / {fl?.OuterEdges})");
    Check(fl is not null && fl.OneWay > 0, $"one-way stretches found ({fl?.OneWay})");
    // An arrival from the first exit to a gate, and a departure from that gate to the runway: which parallel each uses.
    var rwy = world.ArrRwys[0];
    // The far gate (x = 600) from the first exit (x = -300): 900 m along the parallels, worth staying on the inner one.
    // (A gate 300 m from the exit legitimately uses the outer one: guidance, not a rule.)
    var gate = apt.Spots.OrderByDescending(sp => sp.P.X).First();
    var exit = TaxiFlow.Exits(apt, rwy, gate.P)[0];
    double OnName(List<int>? r, string name)
    {
        if (r is null) return -1;
        double on = 0;
        for (var i = 0; i + 1 < r.Count; i++)
            if (apt.Adj[r[i]].FirstOrDefault(e => e.B == r[i + 1]) is { } e && e.Name == name) on += e.Len;
        return on;
    }
    var inRoute = apt.Route(exit, gate.Access, null, null, (u, v, len) => fl!.Cost(u, v, len, true));
    var hold = TaxiFlow.Exits(apt, world.DepRwys[0], gate.P).Count >= 0 ? apt.RunwayLinks(world.DepRwys[0].Runway).OrderBy(l => world.DepRwys[0].Along(apt.Nodes[l.on])).First().off : -1;
    var outRoute = apt.Route(gate.Access, hold, null, null, (u, v, len) => fl!.Cost(u, v, len, false));
    Console.WriteLine($"  arrival taxi in: {OnName(inRoute, "A"):0} m on A, {OnName(inRoute, "B"):0} m on B; departure taxi out: {OnName(outRoute, "A"):0} m on A, {OnName(outRoute, "B"):0} m on B");
    Check(OnName(inRoute, "A") > OnName(inRoute, "B"), "the arrival taxis in mostly on the inner taxiway A");
    Check(OnName(outRoute, "B") > OnName(outRoute, "A"), "the departure taxis out mostly on the outer taxiway B");
}

Console.WriteLine("== Taxi flow: real taxi tracks decide a stretch over the layout");
{
    var apt = new AirportModel(TestAirport.Build(false, twoParallels: true));
    var rwy = apt.Ends.First(e => e.Ident == "27");
    // Four real departures taxiing WEST on B (the layout's rehearsal sends departures east, towards runway 27's start).
    var real = new List<(bool arrival, List<(V2 p, double trk)> pts)>();
    for (var k = 0; k < 4; k++)
        real.Add((false, Enumerable.Range(0, 25).Select(i => (new V2(1200 - i * 100 + k * 7, -200 + k), 270.0)).ToList()));
    var fl = TaxiFlow.Build(apt, new[] { rwy }, new[] { rwy }, new HashSet<int>(), (d, sp) => -1, "test", real, "test day");
    Console.WriteLine("  " + fl.Summary);
    int Near(V2 p) => Enumerable.Range(0, apt.TaxiPointCount).OrderBy(i => (apt.Nodes[i] - p).Length).First();
    var (w, e) = (Near(new V2(0, -200)), Near(new V2(300, -200)));
    Check(fl.RealStretches > 0, $"real tracks decide some stretches ({fl.RealStretches})");
    Check(fl.Against(w, e) && !fl.Against(e, w), "B flows west as the real aircraft did (eastbound is against the flow)");
}

Console.WriteLine("== Runway exits: real turn-offs (learned taxi-ins) counted per exit");
{
    var data = TestAirport.Build(false, twoParallels: true);
    var apt = new AirportModel(data);
    var rwy = apt.Ends.First(e => e.Ident == "27");
    var links = apt.RunwayLinks(rwy.Runway).Select(l => l.off).Distinct().OrderBy(o => rwy.Along(apt.Nodes[o])).ToList();
    var prof = new AirportProfile { Icao = "TEST", Version = AirportProfile.CurrentVersion };
    // 24 real arrivals turned off at the second exit, 6 at the last one: each taxi-in's first point just off the runway.
    void Add(int off, int n)
    {
        for (var k = 0; k < n; k++)
        {
            var (la, lo) = apt.Map.ToLatLon(apt.Nodes[off] + new V2(k % 3 * 10, -20));
            prof.Taxi.Add(new AirportProfile.TaxiTrack { Kind = "arrival", Runway = "27", Points = new() { new[] { la, lo, 180.0 } } });
        }
    }
    var second = links[Math.Min(1, links.Count - 1)]; var last = links[^1];
    Add(second, 24); Add(last, 6);
    var world = new TrafficWorld(apt, new TestSource(), seed: 5) { Automatic = false, Wind = new Wind(270, 5), Learned = prof };
    var rex = world.RealExits(rwy);
    Console.WriteLine($"  exits {links.Count}; counted: {(rex is null ? "none" : string.Join(", ", rex.Select(kv => kv.Key + ": " + kv.Value)))}");
    Check(rex is not null && rex.GetValueOrDefault(second) >= 20 && rex.Values.Sum() == 30, "the second exit holds the real turn-offs there, all 30 placed");
    var none = new TrafficWorld(apt, new TestSource(), seed: 5) { Automatic = false, Learned = new AirportProfile { Icao = "TEST" } };
    Check(none.RealExits(rwy) is null, "no taxi tracks: no real exit shares (the braking model alone)");
}

Console.WriteLine("== STAR chart notes: \"expect radar vectors to final approach course after FIX\" (WYNDE3)");
{
    var fixes = new HashSet<string> { "WYNDE", "PAPPI", "BACEN", "ERNNY" };
    var t = "NOTE: ORD LANDING WEST: EXPECT RADAR VECTORS TO FINAL APPROACH COURSE AFTER PAPPI. ORD LANDING EAST: EXPECT RADAR VECTORS TO FINAL APPROACH COURSE AFTER ERNNY. MDW LANDING SOUTH: EXPECT RADAR VECTORS AFTER BACEN.";
    var v = Laia.Server.StarCharts.VectorNotes(t, fixes, "CHICAGO O'HARE INTL", "KORD");
    Console.WriteLine("  " + string.Join("; ", v.Select(Laia.Server.StarCharts.Describe)));
    Check(v.Any(e => e.Fix == "PAPPI" && e.When == "dir:W") && v.Any(e => e.Fix == "ERNNY" && e.When == "dir:E"), "O'Hare: after PAPPI landing west, after ERNNY landing east");
    Check(!v.Any(e => e.Fix == "BACEN"), "Midway's note isn't O'Hare's");
    var plain = Laia.Server.StarCharts.VectorNotes("EXPECT RADAR VECTORS TO FINAL APPROACH COURSE AFTER WYNDE.", fixes, "X", "KORD");
    Check(plain.Count == 1 && plain[0].When == "" && plain[0].How == "vectors after", "with no flow: always");
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

Console.WriteLine("== LAIA Collector track store");
{
    const double aLat = 33.64, aLon = -84.43, elev = 1000;
    var lonNm = 1 / (60 * Math.Cos(aLat * Math.PI / 180));
    // An arrival from the east on a 270° final: 12 nm out at 4,000 ft above the field down to the threshold.
    var arr = new List<TrackPoint>();
    for (var i = 0; i <= 60; i++) { var d = 12 - i * 0.2; arr.Add(new TrackPoint(1000 + i * 6, aLat, aLon + d * lonNm, elev + Math.Max(0, d * 320), 270)); }
    // A departure the other way: from the field climbing out west.
    var dep = new List<TrackPoint>();
    for (var i = 0; i <= 60; i++) { var d = i * 0.2; dep.Add(new TrackPoint(5000 + i * 6, aLat, aLon - d * lonNm, elev + d * 400, 270)); }
    var far = new List<TrackPoint>();
    for (var i = 0; i < 10; i++) far.Add(new TrackPoint(i, aLat + 2, aLon, 3000, 0));
    Check(TrackStore.Near(arr, aLat, aLon, elev) is { Count: > 50 }, "an arrival near the field is kept");
    Check(TrackStore.Near(far, aLat, aLon, elev) is null, "a track 120 nm away is not");
    using var ms = new MemoryStream();
    using (var w = new System.Text.Json.Utf8JsonWriter(ms)) { w.WriteStartArray(); TrackStore.Write(w, arr); TrackStore.Write(w, dep); w.WriteEndArray(); }
    var back = TrackStore.Read(ms.ToArray());
    Check(back.Count == 2 && back[0].Count == arr.Count && Math.Abs(back[0][10].Lon - arr[10].Lon) < 1e-4 && Math.Abs((back[1][5].AltFt ?? 0) - (dep[5].AltFt ?? 0)) < 1,
        $"tracks are stored and read back the same ({back.Count} tracks)");
    var land = new Dictionary<int, int>();
    TrackStore.CountLandings(arr, aLat, aLon, elev, land);
    TrackStore.CountLandings(dep, aLat, aLon, elev, land);
    Check(land.Count == 1 && land.GetValueOrDefault(270) == 1, $"the arrival counts as one landing facing 270, the departure doesn't ({string.Join(", ", land.Select(kv => kv.Key + ": " + kv.Value))})");
    Check(TrackStore.Facing(land, 280) == 1 && TrackStore.Facing(land, 90) == 0, "days are told apart by which way their landings faced");
}

Console.WriteLine("== STAR chart \"expect\" restrictions (BITHO7, FQM3; d-TPP 2611)");
{
    using var doc = UglyToad.PdfPig.PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "charts", "BITHO7.pdf"));
    var fixes = new HashSet<string> { "OMN", "LAMMA", "JESUP", "COZMO", "BITHO" };
    string Show(List<Laia.Server.StarExpect> l) => string.Join("; ", l.OrderBy(e => e.Fix).ThenBy(e => e.When).Select(Laia.Server.StarCharts.Describe));
    var mco = Laia.Server.StarCharts.ReadExpects(doc, fixes, "ORLANDO INTL");
    Console.WriteLine("  Orlando Intl: " + Show(mco));
    Check(mco.Any(e => e.Fix == "LAMMA" && e.When == "" && e.MinFt == 12000 && e.MaxFt == 12000), "Orlando: LAMMA at 12,000");
    Check(mco.Any(e => e.Fix == "LAMMA" && e.When == "dir:S" && e.Kt == 250), "Orlando landing south: LAMMA at 250 kt");
    Check(mco.Any(e => e.Fix == "COZMO" && e.When == "rwy:18" && e.MinFt == 10000), "Orlando runway 18: COZMO at 10,000");
    Check(!mco.Any(e => e.Fix == "BITHO" && e.How != "vectors after") && !mco.Any(e => e.MinFt == 15000), "Orlando: not the 15,000 or BITHO's 8,000 (they're for other airports)");
    var mlb = Laia.Server.StarCharts.ReadExpects(doc, fixes, "MELBOURNE ORLANDO INTL");
    Console.WriteLine("  Melbourne Orlando Intl: " + Show(mlb));
    Check(mlb.Any(e => e.Fix == "LAMMA" && e.MinFt == 15000) && mlb.Any(e => e.Fix == "BITHO" && e.MinFt == 8000) && !mlb.Any(e => e.Fix == "COZMO") && !mlb.Any(e => e.MinFt == 12000),
        "Melbourne: LAMMA at 15,000 and BITHO at 8,000 only");
    // Williamsport Three into Newark (user, 15 Nov): printed sideways; "Expect 13000" under RACKI and "Expect 7000" under
    // SWEET, each with a line over and under (at exactly that altitude), and HAYED "Expect clearance to cross at lowest
    // usable flight level."
    using var fqm = UglyToad.PdfPig.PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "charts", "FQM3.pdf"));
    var ewr = Laia.Server.StarCharts.ReadExpects(fqm, new HashSet<string> { "FQM", "SLT", "MEGSS", "HAYED", "RACKI", "PENNS", "SWEET" }, "NEWARK LIBERTY INTL");
    Console.WriteLine("  Newark (FQM3): " + Show(ewr));
    Check(ewr.Any(e => e.Fix == "RACKI" && e.MinFt == 13000 && e.MaxFt == 13000), "sideways chart: RACKI at 13,000 (lines over and under)");
    Check(ewr.Any(e => e.Fix == "SWEET" && e.MinFt == 7000 && e.MaxFt == 7000), "sideways chart: SWEET at 7,000 (lines over and under)");
    Check(ewr.Any(e => e.Fix == "HAYED" && e.MinFt == 18000), "HAYED at the lowest usable flight level (FL180)");
    Check(TrafficWorld.ExpectApplies("rwy:18", "18L", 175) && !TrafficWorld.ExpectApplies("rwy:18", "36R", 355) && !TrafficWorld.ExpectApplies("rwy:1", "18L", 175),
        "a runway expect applies to 18L/18R, not 36R");
    Check(TrafficWorld.ExpectApplies("dir:S", "17R", 172) && !TrafficWorld.ExpectApplies("dir:S", "35L", 352), "a landing-south expect applies to 17R, not 35L");
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
    public static AirportData Build(bool swapAxes, bool twoParallels = false)
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
        void Path(int a, int b, int type = 1, int name = 0) => d.Paths.Add(new TaxiPath { Type = type, Start = a, End = b, WidthM = 23, NameIndex = name });
        if (twoParallels) d.TaxiNames.AddRange(new[] { "", "A", "B", "J" });   // index 0 stays unnamed for everything else
        // Runway centreline points every 300 m.
        var rw = new List<int>();
        for (var x = -1500; x <= 1500; x += 300) rw.Add(Pt(x, 0));
        for (var i = 0; i + 1 < rw.Count; i++) Path(rw[i], rw[i + 1], 2);
        // Parallel taxiway at y = -200, every 300 m.
        var tw = new Dictionary<int, int>();
        for (var x = -1500; x <= 1500; x += 300) tw[x] = Pt(x, -200);
        for (var x = -1500; x < 1500; x += 300) Path(tw[x], tw[x + 300], name: twoParallels ? 2 : 0);
        // With two parallels: an inner taxiway A at y = -140 (nearer the runway) and the outer one, B, at y = -200.
        var inner = new Dictionary<int, int>();
        if (twoParallels)
        {
            for (var x = -1500; x <= 1500; x += 300) inner[x] = Pt(x, -140);
            for (var x = -1500; x < 1500; x += 300) Path(inner[x], inner[x + 300], name: 1);
        }
        // Connectors with hold-short points.
        foreach (var x in new[] { -1500, -900, -300, 300, 900, 1500 })
        {
            var hold = Pt(x, -95, 2);
            Path(rw[(x + 1500) / 300], hold, name: twoParallels ? 3 : 0);
            if (twoParallels) { Path(hold, inner[x], name: 3); Path(inner[x], tw[x], name: 3); }
            else Path(hold, tw[x]);
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
