namespace Laia.Server.TrafficCore;

/// <summary>A planned pushback: straight back, one turn, a short straight lined up on a taxi line.</summary>
public sealed class PushPlan
{
    public required List<V2> Points;      // corner points for PathGeom (start, turn corner, end)
    public required double Radius;        // turn radius (m)
    public required V2 End;
    public required double EndHeading;    // where the nose points at the end
    public int EndNode = -1;              // taxi node ahead of the nose, where the taxi route starts
    public string Kind = "plain";         // plain | straight | fallback
    public List<string> Problems = new(); // hard problems (empty = clean)
    public double Score;
    public double TurnStart;              // distance pushed straight back before the turn
    public string Describe() => $"{Kind}, back {TurnStart:0} m, turn r {Radius:0} m, ends facing {EndHeading:000}°" +
                                (Problems.Count > 0 ? " — " + string.Join(", ", Problems) : "");
}

/// <summary>
/// Plans pushbacks the way they're done for real (lessons from the SimRouteGen Helper pushback planner):
/// push straight back along the lead-in until the wingtips are past the service road behind the gates, turn
/// once, end lined up on a taxi line facing the shorter way to the runway, never into a dead end, never
/// through another stand, never across a road. Hard problems always rank below clean options.
/// LAIA can't see buildings (the sim's airport data has none), so a pushback must stay on the pavement the scenery
/// defines: its lead-in, taxi lines and stands (user, MCO Gate 91, 12 Nov 2026: a "clear the gates" push left the
/// lines and swung the tail into the hub building). Leaving them is a hard problem; turning before the service road
/// behind the gates is only a penalty when the turn is on the scenery's own lines.
/// </summary>
public sealed class PushbackPlanner(AirportModel apt)
{
    public PushPlan Plan(AirportModel.ParkingSpot spot, Perf perf, int targetNode)
    {
        var face = V2.FromBearing(spot.Heading);
        var back = face * -1;
        var s0 = spot.P;
        var span = perf.SpanM;
        var rmin = 0.5 * span;
        var rcomf = Math.Max(1.3 * rmin, rmin + 6);

        // The service road behind the gate, and where the wingtips are clear of it.
        var (roadDist, wingDist) = RoadBehind(s0, back, span);

        var leadIn = spot.Access >= 0 ? (apt.Nodes[spot.Access] - s0).Length : 0;
        var cands = new List<PushPlan>();
        foreach (var line in apt.TaxiLines)
        {
            var a = apt.Nodes[line.A]; var b = apt.Nodes[line.B];
            if ((a - s0).Length > 260 && (b - s0).Length > 260) continue;
            var u = (b - a).Unit;
            foreach (var dir in new[] { 1.0, -1.0 })
            {
                var h = u * dir;                 // nose direction at the end
                var m1 = h * -1;                 // moving backwards along the line
                var turn = Geo2.Diff(back.Bearing, m1.Bearing);
                if (Math.Abs(turn) > 120) continue;
                var aheadNode = dir > 0 ? line.B : line.A;
                // A line straight behind the stand would leave the nose pointing back at the gate: always end with a turn.
                if (Math.Abs(turn) < 15) continue;
                // Where the straight push meets the line: the corner of the turn.
                var n = new V2(-u.Y, u.X);
                var denom = V2.Dot(n, back);
                if (Math.Abs(denom) < 0.05) continue;
                var sc = V2.Dot(n, a - s0) / denom;   // distance back to the corner
                // A tug pushes onto the taxilane behind the stand, not a long way out onto a taxiway (MCO: 205 m pushes
                // into the taxiway jammed the traffic): at most about 100 m straight back.
                // A stand's own lead-in can be longer (MCO's international gates: 110 m) — pushing back along it is fine.
                if (sc < 5 || sc > Math.Max(Math.Max(100, perf.LengthM * 2.2), leadIn + 10)) continue;
                var corner = s0 + back * sc;
                if (!WithinLine(corner, a, b, u, 40)) continue;
                foreach (var r in new[] { rcomf, rmin * 1.15, rcomf * 1.4 })
                {
                    var t = r * Math.Tan(Math.Abs(turn) * Math.PI / 360);
                    var l1 = sc - t;
                    if (l1 < 0) continue;
                    foreach (var l2 in new[] { 15.0, 8.0, 3.0, 0.0 })
                    {
                        var end = corner + m1 * (t + l2);
                        if (!WithinLine(end, a, b, u, 3)) continue;
                        var p = new PushPlan { Points = new() { s0, corner, end }, Radius = r, End = end, EndHeading = h.Bearing, EndNode = aheadNode, TurnStart = l1 };
                        cands.Add(Score(p, spot, perf, roadDist, wingDist, rcomf, rmin, Math.Abs(turn), r * Math.Abs(turn) * Math.PI / 180 + l2));
                        break; // the longest final straight that fits is enough for this radius
                    }
                }
            }
        }

        // Check the most promising ones for dead ends, facing the right way and staying on the pavement.
        var paved = Pavement(s0);
        var shortlist = cands.OrderBy(c => c.Problems.Count > 0 ? 1 : 0).ThenBy(c => c.Score).Take(8).ToList();
        foreach (var c in shortlist) { CheckRoute(c, targetNode); CheckPavement(c, perf, paved); }
        var best = shortlist.OrderBy(c => c.Problems.Count > 0 ? 1 : 0).ThenBy(c => c.Score).FirstOrDefault();
        var fallback = Fallback(spot, perf, roadDist, wingDist, rcomf, targetNode);
        CheckPavement(fallback, perf, paved);
        if (best is null || (best.Problems.Count > 0 && fallback.Problems.Count < best.Problems.Count)) return fallback;
        return best;
    }

    private static bool WithinLine(V2 p, V2 a, V2 b, V2 u, double tol)
    {
        var len = (b - a).Length;
        var t = V2.Dot(p - a, u);
        return t >= -tol && t <= len + tol && Math.Abs(V2.Cross(u, p - a)) < 1.5;
    }

    /// <summary>Distance straight back to the first service road, and to where both wingtips are 2 m past it.</summary>
    private (double road, double wing) RoadBehind(V2 s0, V2 back, double span)
    {
        double best = double.MaxValue, bestWing = double.MaxValue;
        foreach (var (ra, rb) in apt.Roads)
        {
            if (!RaySegment(s0, back, ra, rb, out var d) || d < 3 || d > 150) continue;
            if (d >= best) continue;
            // The road may cross at an angle: the trailing wingtip clears it later.
            var rd = (rb - ra).Unit;
            var cos = Math.Abs(V2.Dot(rd, back));
            var sin = Math.Sqrt(Math.Max(0.05, 1 - cos * cos));
            best = d;
            bestWing = d + 2 + span / 2 * cos / sin;
        }
        if (best == double.MaxValue) return (double.NaN, Math.Max(25, span * 0.8)); // no road found: just clear the gate
        return (best, Math.Min(bestWing, best + span));
    }

    private static bool RaySegment(V2 o, V2 d, V2 a, V2 b, out double dist)
    {
        dist = 0;
        var e = b - a;
        var den = V2.Cross(d, e);
        if (Math.Abs(den) < 1e-9) return false;
        var t = V2.Cross(a - o, e) / den;
        var s = V2.Cross(a - o, d) / den;
        if (t < 0 || s < 0 || s > 1) return false;
        dist = t;
        return true;
    }

    private PushPlan Score(PushPlan p, AirportModel.ParkingSpot spot, Perf perf, double roadDist, double wingDist, double rcomf, double rmin, double turnDeg, double afterTurn)
    {
        var score = 0.5 * (p.TurnStart + afterTurn) + 0.3 * turnDeg;
        if (p.Kind != "straight")
        {
            // Turning before the service road: real pushes avoid it, but on the scenery's own lead-in and taxilane
            // (a stand entered at an angle, the road crossing its lead-in) it's what the lines are for: a penalty only.
            if (!double.IsNaN(roadDist) && p.TurnStart < roadDist - 5) score += 200;
            if (p.TurnStart < wingDist) score += 10 + 0.5 * (wingDist - p.TurnStart);
            if (p.Radius < rcomf) score += 3 * (rcomf - p.Radius);
        }
        // Other stands are obstacles (parked aircraft).
        var path = new PathGeom(p.Points, _ => p.Radius, 1);
        for (double s = 0; s <= path.Length; s += 4)
        {
            var q = path.At(s).p;
            foreach (var o in apt.Spots)
            {
                if (o == spot || spot.Overlaps.Contains(o) || (o.P - spot.P).Length < 3) continue;
                if ((o.P - q).Length < Math.Max(8, o.RadiusM * 0.75)) { p.Problems.Add("passes through " + o.Label); s = double.MaxValue - 1; break; }
            }
        }
        // Don't end with the fuselage across a service road.
        var h = V2.FromBearing(p.EndHeading);
        var nose = p.End + h * (perf.LengthM / 2); var tail = p.End - h * (perf.LengthM / 2);
        foreach (var (ra, rb) in apt.Roads)
        {
            var rd = (rb - ra).Unit;
            if (Math.Abs(V2.Dot(rd, h)) > 0.94) continue; // a road alongside isn't a crossing
            if (SegmentsCross(nose, tail, ra, rb)) { p.Problems.Add("ends across a road"); break; }
        }
        p.Score = score;
        return p;
    }

    /// <summary>The scenery's pavement near a stand: taxi lines, every stand's lead-in, and the stands themselves.</summary>
    private (List<(V2 a, V2 b)> lines, List<(V2 p, double r)> stands) Pavement(V2 s0)
    {
        var lines = new List<(V2 a, V2 b)>();
        foreach (var l in apt.TaxiLines)
        {
            var a = apt.Nodes[l.A]; var b = apt.Nodes[l.B];
            if (DistToSegment(s0, a, b) < 400) lines.Add((a, b));
        }
        var stands = new List<(V2 p, double r)>();
        foreach (var o in apt.Spots)
        {
            if ((o.P - s0).Length > 400) continue;
            stands.Add((o.P, Math.Max(10, o.RadiusM)));
            if (o.Access >= 0) lines.Add((o.P, apt.Nodes[o.Access]));
        }
        return (lines, stands);
    }

    /// <summary>
    /// Hard problem when the pushback leaves the pavement: the aircraft's middle more than 30 m from any line or stand,
    /// or its tail more than 40 m — beyond that there may be a building the sim's data doesn't show.
    /// </summary>
    private static void CheckPavement(PushPlan p, Perf perf, (List<(V2 a, V2 b)> lines, List<(V2 p, double r)> stands) paved)
    {
        double Off(V2 q)
        {
            var d = double.MaxValue;
            foreach (var (a, b) in paved.lines) d = Math.Min(d, DistToSegment(q, a, b));
            foreach (var (c, r) in paved.stands) d = Math.Min(d, Math.Max(0, (c - q).Length - r));
            return d;
        }
        var path = new PathGeom(p.Points, _ => p.Radius, 1);
        for (double s = 0; s <= path.Length; s += 4)
        {
            var (q, dir, _) = path.At(s);
            var tail = q + V2.FromBearing(dir) * (perf.LengthM / 2);   // pushing: the tail leads
            if (Off(q) > 30 || Off(tail) > 40) { p.Problems.Add("leaves the taxi lines"); return; }
        }
    }

    private static double DistToSegment(V2 p, V2 a, V2 b)
    {
        var ab = b - a;
        var len2 = V2.Dot(ab, ab);
        var t = len2 < 1e-9 ? 0 : Math.Clamp(V2.Dot(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    private static bool SegmentsCross(V2 p1, V2 p2, V2 q1, V2 q2)
    {
        double O(V2 a, V2 b, V2 c) => V2.Cross(b - a, c - a);
        return O(p1, p2, q1) * O(p1, p2, q2) < 0 && O(q1, q2, p1) * O(q1, q2, p2) < 0;
    }

    /// <summary>Dead end and facing: the taxi distance to the runway from the nose's way vs. the other way.</summary>
    private void CheckRoute(PushPlan p, int targetNode)
    {
        if (targetNode < 0) return;
        var h = V2.FromBearing(p.EndHeading);
        var ahead = Distance(p.End, h, targetNode);
        if (ahead is null) { p.Problems.Add("faces a dead end"); return; }
        var other = Distance(p.End, h * -1, targetNode);
        if (other is not null && ahead.Value > other.Value + 60) p.Score += 150; // facing the long way round
    }

    /// <summary>Route length to the target starting off in the given direction (never back through the end).</summary>
    private double? Distance(V2 end, V2 dir, int target)
    {
        var start = -1; var bestD = double.MaxValue;
        for (var i = 0; i < apt.TaxiPointCount; i++)
        {
            if (apt.Adj[i].Count == 0) continue;
            var v = apt.Nodes[i] - end;
            var d = v.Length;
            if (d < 15 || d > 90 || V2.Dot(v.Unit, dir) < 0.6) continue;
            if (d < bestD) { bestD = d; start = i; }
        }
        if (start < 0) return null;
        // Same rules as the taxi-out itself: nothing just behind, no doubling back from the first point.
        var behind = new HashSet<int>();
        for (var i = 0; i < apt.TaxiPointCount; i++)
        {
            var v = apt.Nodes[i] - end;
            if (v.Length < 40 && V2.Dot(v, dir) < -2 && i != start) behind.Add(i);
        }
        foreach (var e in apt.Adj[start])
            if (e.B != target && V2.Dot((apt.Nodes[e.B] - apt.Nodes[start]).Unit, dir) < -0.5) behind.Add(e.B);
        var route = apt.Route(start, target, null, behind);
        if (route is null) return null;
        double len = bestD;
        for (var i = 1; i < route.Count; i++) len += (apt.Nodes[route[i]] - apt.Nodes[route[i - 1]]).Length;
        return len;
    }

    /// <summary>"Clear the gates": straight back to the wingtip point, a 90° turn, then on until clear of the stands.</summary>
    private PushPlan Fallback(AirportModel.ParkingSpot spot, Perf perf, double roadDist, double wingDist, double rcomf, int targetNode)
    {
        var face = V2.FromBearing(spot.Heading);
        var back = face * -1;
        PushPlan? best = null;
        foreach (var side in new[] { 1.0, -1.0 })
        {
            var l1 = Math.Clamp(wingDist, 20, Math.Max(60, perf.LengthM * 1.3));
            var corner = spot.P + back * (l1 + rcomf);
            var m1 = face.Perp * side;
            var end = corner + m1 * (rcomf + 20);
            var p = new PushPlan { Points = new() { spot.P, corner, end }, Radius = rcomf, End = end, EndHeading = (m1 * -1).Bearing, Kind = "fallback", TurnStart = l1 };
            p.EndNode = apt.NearestNode(end + (m1 * -1) * 30);
            Score(p, spot, perf, roadDist, wingDist, rcomf, rcomf, 90, rcomf + 20);
            CheckRoute(p, targetNode);
            p.Score += 300;
            if (best is null || p.Problems.Count < best.Problems.Count || (p.Problems.Count == best.Problems.Count && p.Score < best.Score)) best = p;
        }
        return best!;
    }

    /// <summary>Plans a pushback at every stand (for the log): how many are clean, and the problems found.</summary>
    public string Survey(Perf perf, int targetNode, int examples = 40)
    {
        int clean = 0, straight = 0, fallback = 0, total = 0;
        var problems = new Dictionary<string, int>();
        var bad = new List<string>();
        foreach (var spot in apt.Spots.Where(s => s.IsGate || s.RadiusM >= 17))
        {
            total++;
            PushPlan p;
            try { p = Plan(spot, perf, targetNode); }
            catch (Exception ex) { bad.Add($"{spot.Label}: error {ex.Message}"); continue; }
            if (p.Kind == "fallback") fallback++;
            if (p.Problems.Count == 0) { clean++; if (p.Kind == "straight") straight++; }
            foreach (var pr in p.Problems.Select(x => x.StartsWith("passes through") ? "passes through a stand" : x))
                problems[pr] = problems.GetValueOrDefault(pr) + 1;
            if ((p.Problems.Count > 0 || p.Kind == "fallback") && bad.Count < examples) bad.Add($"{spot.Label}: {p.Describe()}");
        }
        return $"pushbacks planned at {total} stands: {clean} clean ({straight} straight back), {fallback} fallback" +
               (problems.Count > 0 ? "; problems: " + string.Join(", ", problems.Select(kv => $"{kv.Key} {kv.Value}")) : "") +
               (bad.Count > 0 ? "; e.g. " + string.Join(" | ", bad) : "");
    }
}
