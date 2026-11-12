namespace Laia.Server.TrafficCore;

/// <summary>
/// The airport's taxi flow for the runways in use, worked out from the scenery's ground layout before (and while)
/// traffic runs — every airport, no local data (user, MCO, 12 Nov 2026):
/// <list type="bullet">
/// <item>Parallel taxiways: where a runway in use has two or more taxiways alongside it on the same side, the one
/// nearest the runway leans to arrivals (taxi in) and the one farther out to departures (taxi out). Guidance, not a rule:
/// the other kind pays half as much again for those metres.</item>
/// <item>Directions: every stand's taxi out to its departure runway and taxi in from its arrival runway's exits are
/// rehearsed with the normal routing (fewest taxiways, the shortest a controller would give). Each stretch of a named
/// taxiway (between junctions where it turns or meets a runway) whose rehearsed traffic goes at least 70% one way gets
/// that as its direction; going against it costs two and a half times the distance, so a parallel that flows the
/// other way is used when there is one, and a short stretch or a taxiway with no alternative is still used both ways
/// (the nose-to-nose waiting handles meetings there).</item>
/// </list>
/// Worked out again whenever the runways in use change.
/// </summary>
public sealed class TaxiFlow
{
    private readonly HashSet<(int, int)> _inner = new(), _outer = new();
    private readonly HashSet<(int, int)> _against = new();
    public string Key { get; init; } = "";
    public string Summary { get; private set; } = "";
    public int OneWay { get; private set; }
    public int TwoWay { get; private set; }
    public int InnerEdges => _inner.Count / 2;
    public int OuterEdges => _outer.Count / 2;
    /// <summary>The one-way stretches for the map: points in the flow's direction, with the taxiway name.</summary>
    public List<(string name, List<V2> pts, bool departures)> Stretches { get; } = new();

    /// <summary>Extra cost of taxiing u → v (length len) for an arrival taxiing in or a departure taxiing out.</summary>
    public double Cost(int u, int v, double len, bool inbound)
    {
        double c = 0;
        if (inbound ? _outer.Contains((u, v)) : _inner.Contains((u, v))) c += len * 0.5;
        if (_against.Contains((u, v))) c += len * 1.5;
        return c;
    }

    public bool Against(int u, int v) => _against.Contains((u, v));

    /// <summary>Only the parallel-taxiway preference (quick, for while the full flow is being worked out).</summary>
    public static TaxiFlow Sides(AirportModel apt, IReadOnlyList<RunwayEnd> rwys, string key)
    {
        var f = new TaxiFlow { Key = key };
        f.FindParallels(apt, rwys);
        f.Summary = $"parallel taxiways: {f.InnerEdges} inner (arrivals) and {f.OuterEdges} outer (departures) segments";
        return f;
    }

    private void FindParallels(AirportModel apt, IReadOnlyList<RunwayEnd> rwys)
    {
        var ends = rwys.GroupBy(r => r.Runway).Select(g => g.First()).ToList();
        var cands = new List<(GraphEdge e, int rwy, int side, double off, double along, string name)>();
        var cos12 = Math.Cos(12 * Math.PI / 180);
        foreach (var e in apt.TaxiLines)
        {
            if (!AirportModel.IsTaxiway(e.Name)) continue;
            var a = apt.Nodes[e.A]; var b = apt.Nodes[e.B];
            if ((b - a).Length < 1) continue;
            var dir = (b - a).Unit; var mid = (a + b) * 0.5;
            foreach (var r in ends)
            {
                if (Math.Abs(V2.Dot(dir, r.Dir)) < cos12) continue;
                var off = r.Offset(mid); var along = r.Along(mid);
                if (Math.Abs(off) < 60 || Math.Abs(off) > 450 || along < -300 || along > r.LengthM + 300) continue;
                cands.Add((e, r.Runway, Math.Sign(off), Math.Abs(off), along, e.Name.ToUpperInvariant()));
            }
        }
        foreach (var c in cands)
        {
            var farther = cands.Any(d => d.rwy == c.rwy && d.side == c.side && d.name != c.name && d.off > c.off + 30 && Math.Abs(d.along - c.along) < 150);
            var nearer = cands.Any(d => d.rwy == c.rwy && d.side == c.side && d.name != c.name && d.off < c.off - 30 && Math.Abs(d.along - c.along) < 150);
            if (farther && !nearer) { _inner.Add((c.e.A, c.e.B)); _inner.Add((c.e.B, c.e.A)); }
            else if (nearer && !farther) { _outer.Add((c.e.A, c.e.B)); _outer.Add((c.e.B, c.e.A)); }
        }
    }

    /// <summary>
    /// The full flow: parallels, then the rehearsal and the directions. Reads only the airport layout (safe to run in
    /// the background). holds: each departure runway's hold-short node for a stand (the node the world would use).
    /// </summary>
    public static TaxiFlow Build(AirportModel apt, IReadOnlyList<RunwayEnd> arr, IReadOnlyList<RunwayEnd> dep,
        IReadOnlySet<int> activeRunways, Func<RunwayEnd, AirportModel.ParkingSpot, int> holdFor, string key)
    {
        var f = new TaxiFlow { Key = key };
        f.FindParallels(apt, arr.Concat(dep).ToList());

        // ---- the rehearsal
        var stands = RehearsalStands(apt);
        var depUse = new Dictionary<(int, int), double>();
        var arrUse = new Dictionary<(int, int), double>();
        void Add(Dictionary<(int, int), double> use, List<int>? r, double w)
        {
            if (r is null) return;
            for (var i = 0; i + 1 < r.Count; i++) use[(r[i], r[i + 1])] = use.GetValueOrDefault((r[i], r[i + 1])) + w;
        }
        Func<int, int, double, double> outCost = (u, v, len) => f.Cost(u, v, len, false);
        Func<int, int, double, double> inCost = (u, v, len) => f.Cost(u, v, len, true);
        var depEnds = dep.ToList(); var arrEnds = arr.ToList();
        var routes = 0;
        foreach (var s in stands)
        {
            foreach (var d in depEnds)
            {
                var hold = holdFor(d, s);
                if (hold < 0) continue;
                Add(depUse, apt.Route(s.Access, hold, activeRunways, null, outCost), 1.0 / depEnds.Count); routes++;
            }
            foreach (var a in arrEnds)
            {
                var exits = Exits(apt, a, s.P);
                foreach (var x in exits) { Add(arrUse, apt.Route(x, s.Access, activeRunways, null, inCost), 1.0 / (arrEnds.Count * exits.Count)); routes++; }
            }
        }

        // ---- stretches of each named taxiway, and their direction
        var runs = StretchesOf(apt);
        var oneWay = new List<(string name, double lenM, double brg, bool deps, double share)>();
        foreach (var (name, nodes) in runs)
        {
            double fwd = 0, bwd = 0, fDep = 0, bDep = 0, len = 0;
            for (var i = 0; i + 1 < nodes.Count; i++)
            {
                var (u, v) = (nodes[i], nodes[i + 1]);
                var l = (apt.Nodes[v] - apt.Nodes[u]).Length; len += l;
                var fd = depUse.GetValueOrDefault((u, v)); var fa = arrUse.GetValueOrDefault((u, v));
                var bd = depUse.GetValueOrDefault((v, u)); var ba = arrUse.GetValueOrDefault((v, u));
                fwd += (fd + fa) * l; bwd += (bd + ba) * l; fDep += fd * l; bDep += bd * l;
            }
            var total = fwd + bwd;
            // At least about two rehearsed aircraft over the whole stretch, and 70% one way.
            if (len < 1 || total / len < 2 || Math.Max(fwd, bwd) / total < 0.7) { f.TwoWay++; continue; }
            var forward = fwd >= bwd;
            for (var i = 0; i + 1 < nodes.Count; i++)
                f._against.Add(forward ? (nodes[i + 1], nodes[i]) : (nodes[i], nodes[i + 1]));
            var pts = nodes.Select(n => apt.Nodes[n]).ToList();
            if (!forward) pts.Reverse();
            var depShare = forward ? fDep / Math.Max(1e-9, fwd) : bDep / Math.Max(1e-9, bwd);
            f.Stretches.Add((name, pts, depShare >= 0.5));
            f.OneWay++;
            oneWay.Add((name, len, (pts[^1] - pts[0]).Bearing, depShare >= 0.5, Math.Max(depShare, 1 - depShare)));
        }

        // ---- the log line: one entry per taxiway and direction
        string Compass(double b) => new[] { "northbound", "northeastbound", "eastbound", "southeastbound", "southbound", "southwestbound", "westbound", "northwestbound" }[(int)Math.Round(Geo2.Norm(b) / 45) % 8];
        var groups = oneWay.GroupBy(o => (o.name, Compass(o.brg), o.deps))
            .Select(g => (g.Key.name, dir: g.Key.Item2, g.Key.deps, len: g.Sum(o => o.lenM)))
            .OrderByDescending(g => g.len).ToList();
        var shown = string.Join(", ", groups.Take(14).Select(g => $"{g.name} {g.dir} ({(g.deps ? "departures" : "arrivals")}, {g.len / 1000:0.0} km)"));
        f.Summary = $"{f.OneWay} one-way stretches ({shown}{(groups.Count > 14 ? $", +{groups.Count - 14} more" : "")}); {f.TwoWay} stretches both ways; " +
                    $"parallel taxiways: {f.InnerEdges} inner (arrivals) and {f.OuterEdges} outer (departures) segments; from {routes} rehearsed taxi routes at {stands.Count} stands";
        return f;
    }

    /// <summary>The stands rehearsed: gates and larger stands, at most 90 spread evenly.</summary>
    public static List<AirportModel.ParkingSpot> RehearsalStands(AirportModel apt)
    {
        var stands = apt.Spots.Where(s => s.Access >= 0 && (s.IsGate || s.RadiusM >= 17)).ToList();
        if (stands.Count > 90) { var k = stands.Count / 90.0; stands = Enumerable.Range(0, 90).Select(i => stands[(int)(i * k)]).ToList(); }
        return stands;
    }

    /// <summary>Exits a landing would take towards a stand: the first two past normal braking on the stand's side.</summary>
    public static List<int> Exits(AirportModel apt, RunwayEnd rwy, V2 stand)
    {
        var side = Math.Sign(rwy.Offset(stand));
        var links = apt.RunwayLinks(rwy.Runway)
            .Select(l => (l.on, l.off, along: rwy.Along(apt.Nodes[l.on]), dir: (apt.Nodes[l.off] - apt.Nodes[l.on]).Unit))
            .Where(l => V2.Dot(l.dir, rwy.Dir) > -0.2 && l.along < rwy.LengthM + 30).ToList();
        var good = links.Where(l => l.along >= 1300 && (side == 0 || Math.Sign(rwy.Offset(apt.Nodes[l.off])) == side)).OrderBy(l => l.along).ToList();
        if (good.Count == 0) good = links.Where(l => l.along >= 1000).OrderBy(l => l.along).ToList();
        if (good.Count == 0) good = links.OrderByDescending(l => l.along).ToList();
        return good.Take(2).Select(l => l.off).Distinct().ToList();
    }

    /// <summary>
    /// Each named taxiway cut into stretches: runs of its own segments, ending where it branches, turns more than 35°
    /// or meets a runway.
    /// </summary>
    public static List<(string name, List<int> nodes)> StretchesOf(AirportModel apt)
    {
        var res = new List<(string, List<int>)>();
        foreach (var grp in apt.TaxiLines.Where(e => AirportModel.IsTaxiway(e.Name)).GroupBy(e => e.Name.ToUpperInvariant()))
        {
            var adj = new Dictionary<int, List<int>>();
            var edges = new HashSet<(int, int)>();
            foreach (var e in grp)
            {
                if (e.A == e.B || !edges.Add((Math.Min(e.A, e.B), Math.Max(e.A, e.B)))) continue;
                (adj.TryGetValue(e.A, out var la) ? la : adj[e.A] = new()).Add(e.B);
                (adj.TryGetValue(e.B, out var lb) ? lb : adj[e.B] = new()).Add(e.A);
            }
            var used = new HashSet<(int, int)>();
            bool Stop(int prev, int n, int next)
            {
                if (adj[n].Count != 2 || apt.NodeRunway[n] >= 0) return true;
                var d1 = (apt.Nodes[n] - apt.Nodes[prev]); var d2 = (apt.Nodes[next] - apt.Nodes[n]);
                if (d1.Length < 0.1 || d2.Length < 0.1) return false;
                return Math.Abs(Geo2.Diff(d1.Bearing, d2.Bearing)) > 35;
            }
            List<int> Extend(int prev, int n)
            {
                var chain = new List<int>();
                while (true)
                {
                    var next = adj[n].FirstOrDefault(x => x != prev, -1);
                    if (next < 0 || Stop(prev, n, next) || used.Contains((Math.Min(n, next), Math.Max(n, next)))) break;
                    used.Add((Math.Min(n, next), Math.Max(n, next)));
                    chain.Add(next);
                    (prev, n) = (n, next);
                }
                return chain;
            }
            foreach (var (a, b) in edges)
            {
                if (used.Contains((a, b))) continue;
                used.Add((a, b));
                var fwd = Extend(a, b);
                var back = Extend(b, a);
                var nodes = new List<int>();
                for (var i = back.Count - 1; i >= 0; i--) nodes.Add(back[i]);
                nodes.Add(a); nodes.Add(b); nodes.AddRange(fwd);
                res.Add((grp.Key, nodes));
            }
        }
        return res;
    }
}
