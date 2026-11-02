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
    for (var step = 0; step < 3600 / dt; step++)
    {
        world.Step(dt);
        var fl = world.Flights;
        // Runway: at most one aircraft on it (departures that have lifted off don't count).
        var onRwy = fl.Where(f => f.OnRunway >= 0 && !(f.Kind == "departure" && !f.OnGround)).GroupBy(f => f.OnRunway).Where(g => g.Count() > 1).ToList();
        if (onRwy.Count > 0)
        {
            runwayConflicts++;
            if (runwayConflicts <= 5) log.Add($"{TimeSpan.FromSeconds(world.Time):hh\\:mm\\:ss} RUNWAY CONFLICT: " + string.Join(", ", onRwy.SelectMany(g => g).Select(f => $"{f.Callsign} {f.Phase}")));
        }
        // Ground separation between moving aircraft.
        var moving = fl.Where(f => f.OnGround && f.Phase is "taxi" or "taxi in" or "pushback" or "holding short" or "lining up" or "engine start").ToList();
        for (var i = 0; i < moving.Count; i++)
            for (var j = i + 1; j < moving.Count; j++)
            {
                var d = (moving[i].Pos - moving[j].Pos).Length;
                minSep = Math.Min(minSep, d);
                if (d < 12) closeCalls++;
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
                    if (jumps <= 5) log.Add($"{TimeSpan.FromSeconds(world.Time):hh\\:mm\\:ss} JUMP {f.Callsign} {f.Phase}: moved {jump:0.0} m, expected {expected:0.0}");
                }
                if (f.OnGround && l.ground && f.Gs > 1)
                {
                    var rate = Math.Abs(Geo2.Diff(l.hdg, f.Heading)) / dt;
                    if (f.Phase != "pushback") maxGroundTurnRate = Math.Max(maxGroundTurnRate, rate);
                    if (rate > 40 && f.Phase != "pushback") { sharpTurns++; if (sharpTurns <= 5) log.Add($"{TimeSpan.FromSeconds(world.Time):hh\\:mm\\:ss} SHARP TURN {f.Callsign} {f.Phase}: {rate:0}°/s at {f.Gs:0} kt"); }
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
    Check(runwayConflicts == 0, $"never two aircraft on the runway ({runwayConflicts} moments)");
    Check(jumps == 0, $"no jumps in movement ({jumps})");
    Check(closeCalls < 20, $"aircraft keep their distance on the ground ({closeCalls} moments closer than 12 m)");
    Check(stuck == 0, $"no aircraft stuck for 15 minutes ({stuck})");
    Check(maxBankRate <= 5.5, $"bank changes gently ({maxBankRate:0.0}°/s)");
    Check(!log.Any(l => l.Contains("removed")), "no errors in the traffic logic");
    Check(seenPhases.Contains("pushback") && seenPhases.Contains("lined up") && seenPhases.Contains("flare") && seenPhases.Contains("at gate") || seenPhases.Contains("parked"),
        "all phases happen (pushback, line-up, flare, gate)");
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

Console.WriteLine(failures.Count == 0 ? "TRAFFIC CHECKS PASSED" : $"TRAFFIC CHECKS FAILED: {failures.Count}");
return failures.Count == 0 ? 0 : 1;

sealed class TestSource : IFlightSource
{
    private static readonly (string a, string t)[] Types = { ("DAL", "B739"), ("UAL", "A320"), ("AAL", "A321"), ("SKW", "E75L"), ("DAL", "A333"), ("UAL", "B772"), ("SWA", "B737") };
    public AircraftIdentity NewAircraft(string airport, int maxGateSize, Random rng)
    {
        var ok = Types.Where(x => Perf.For(x.t).GateSize <= maxGateSize).ToArray();
        var (a, t) = ok[rng.Next(ok.Length)];
        return new AircraftIdentity(a, t, a + rng.Next(100, 3000), "KXXX");
    }
    public (string callsign, string other) NewFlight(string airport, string airline, bool departure, Random rng) => (airline + rng.Next(100, 3000), "KYYY");
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
