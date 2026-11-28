namespace Laia.Server.TrafficCore;

/// <summary>
/// A route through a list of points with every corner rounded into a circular arc, so an aircraft can
/// follow it exactly and smoothly: position, direction and curvature are known at every distance s.
/// </summary>
public sealed class PathGeom
{
    private abstract record Seg(double S0, double Len)
    {
        public abstract (V2 p, double dir, double k) At(double d); // d = distance into the segment
    }

    private sealed record Line(double S0, double Len, V2 A, V2 Dir) : Seg(S0, Len)
    {
        public override (V2 p, double dir, double k) At(double d) => (A + Dir * d, Dir.Bearing, 0);
    }

    /// <summary>Arc around C with radius R starting at bearing Phi0 (from C), sweeping Sweep degrees (+ clockwise).</summary>
    private sealed record Arc(double S0, double Len, V2 C, double R, double Phi0, double Sweep) : Seg(S0, Len)
    {
        public override (V2 p, double dir, double k) At(double d)
        {
            var f = Len <= 0 ? 0 : d / Len;
            var phi = Phi0 + Sweep * f;
            var p = C + V2.FromBearing(phi, R);
            var dir = Geo2.Norm(phi + (Sweep >= 0 ? 90 : -90));
            return (p, dir, (Sweep >= 0 ? 1 : -1) / R);
        }
    }

    private readonly List<Seg> _segs = new();
    /// <summary>Distance along the path at which each input point is reached (corner points: middle of the arc).</summary>
    public double[] PointS { get; }
    public IReadOnlyList<V2> Points { get; }
    /// <summary>Distance along the path of each kept (de-duplicated) point in Points.</summary>
    public double[] KeptS { get; }
    public double Length { get; }
    /// <summary>Stretches with a speed limit from their curvature: (start, end, limit in m/s).</summary>
    public List<(double s0, double s1, double vmax)> Curves { get; } = new();

    /// <param name="pts">Points to pass (duplicates are dropped).</param>
    /// <param name="radius">Turn radius wanted at corner i (metres).</param>
    /// <param name="lateralAccel">For curve speed limits (m/s²).</param>
    /// <param name="ease">
    /// Ease into and out of each turn (for taxiing): the turn tightens gradually from straight to its full radius and
    /// unwinds the same way, like a pilot feeding in and taking off nosewheel steering — the transition curve roads and
    /// railways use, instead of going from straight to full turn in an instant.
    /// </param>
    /// <param name="startDir">
    /// The way the aircraft is already going at the first point (an air route given in flight): the path starts with
    /// the turn it can fly from there — an arc of <paramref name="startRadius"/> until it points at the first point it
    /// can reach — instead of swinging round on the spot (user, 28 Nov: "Even one loop or unrealistic turn is a failure").
    /// Points it can't reach inside that turn are passed by.
    /// </param>
    /// <param name="flyBy">
    /// An air route: two turns the same way too close together for the turn the aircraft flies there (the second
    /// would have to start before the first has finished) become one turn where the line in and the line out meet —
    /// as an airliner's navigation flies them — instead of being squeezed into turns tighter than it can fly.
    /// </param>
    public PathGeom(IList<V2> pts, Func<int, double> radius, double lateralAccel, bool ease = false, double? startDir = null, double startRadius = 0, bool flyBy = false)
    {
        var p = new List<V2>();
        var map = new List<int>(); // input index -> kept index
        foreach (var q in pts)
        {
            if (p.Count == 0 || (q - p[^1]).Length > 0.5) p.Add(q);
            map.Add(p.Count - 1);
        }
        if (p.Count == 1) p.Add(p[0] + new V2(0, 0.6));
        var rOverride = new Dictionary<int, double>();
        var dropped0 = false;
        if (!ease && startDir is { } sd && startRadius > 1 && p.Count >= 2)
        {
            var r0 = startRadius; var p0 = p[0];
            V2 Centre(double dir, bool right) => p0 + V2.FromBearing(dir + (right ? 90 : -90), r0);
            bool Unreachable(V2 t)
            {
                var right = Geo2.Diff(sd, (t - p0).Bearing) >= 0;
                return (t - Centre(sd, right)).Length < 1.02 * r0;
            }
            var k = 1;
            while (k < p.Count - 1 && Unreachable(p[k])) k++;
            var turn0 = Geo2.Diff(sd, (p[k] - p0).Bearing);
            if (Math.Abs(turn0) > 3)
            {
                var right = turn0 > 0;
                var c = Centre(sd, right);
                var arc = new List<V2>();
                var h = sd;
                for (var step = 0; step < 72; step++)
                {
                    var left = Geo2.Diff(h, (p[k] - (arc.Count > 0 ? arc[^1] : p0)).Bearing);
                    if (Math.Abs(left) <= 5 || (right ? left < 0 : left > 0)) break;
                    h += right ? 5 : -5;
                    arc.Add(c + V2.FromBearing(h + (right ? -90 : 90), r0));
                }
                if (arc.Count > 0)
                {
                    var np = new List<V2> { p0 };
                    np.AddRange(arc);
                    for (var i = k; i < p.Count; i++) np.Add(p[i]);
                    for (var j = 0; j < map.Count; j++)
                        map[j] = map[j] == 0 ? 0 : map[j] < k ? arc.Count : map[j] - k + 1 + arc.Count;
                    for (var i = 1; i <= arc.Count; i++) rOverride[i] = r0;
                    p = np;
                    dropped0 = true;
                }
            }
        }
        // Taxiing: scenery often has many short segments around junctions (10–20 m), which would squeeze every turn
        // into a tight radius. Smooth the line first, never more than a few metres off the taxi line (still on the
        // pavement), so each turn has room to be a proper curve, as a pilot uses the fillet at a junction.
        if (flyBy && !ease)
        {
            double R(int i) { var oi = map.IndexOf(i); return rOverride.TryGetValue(i, out var ro) ? ro : Math.Max(1, radius(oi < 0 ? i : oi)); }
            for (var pass = 0; pass < 40; pass++)
            {
                var merged = false;
                for (var i = 1; i + 2 < p.Count; i++)
                {
                    if (rOverride.ContainsKey(i) || rOverride.ContainsKey(i + 1)) continue;
                    var b0 = (p[i] - p[i - 1]).Bearing; var b1 = (p[i + 1] - p[i]).Bearing; var b2 = (p[i + 2] - p[i + 1]).Bearing;
                    var thI = Geo2.Diff(b0, b1); var thJ = Geo2.Diff(b1, b2);
                    if (Math.Abs(thI) < 0.5 || Math.Abs(thJ) < 0.5 || Math.Sign(thI) != Math.Sign(thJ) || Math.Abs(thI + thJ) >= 170) continue;
                    // A shallow join (an intercept onto a final, 35° or less) stays shallow: the leg before it is kept.
                    if (Math.Abs(thJ) <= 35 && Math.Abs(thI + thJ) > 35) continue;
                    var seg = (p[i + 1] - p[i]).Length;
                    var need = R(i) * Math.Tan(Math.Abs(thI) * Math.PI / 360) + R(i + 1) * Math.Tan(Math.Abs(thJ) * Math.PI / 360);
                    if (need <= seg) continue;
                    var rv = p[i] - p[i - 1]; var qv = p[i + 2] - p[i + 1];
                    var den = V2.Cross(rv, qv);
                    if (Math.Abs(den) < 1e-9) continue;
                    var tm = V2.Cross(p[i + 1] - p[i - 1], qv) / den;
                    if (tm <= 0) continue;
                    p[i] = p[i - 1] + rv * tm;
                    p.RemoveAt(i + 1);
                    for (var j = 0; j < map.Count; j++) if (map[j] > i) map[j]--;
                    dropped0 = true;
                    merged = true;
                    break;
                }
                if (!merged) break;
            }
        }
        var dropped = new HashSet<int>();
        if (dropped0) dropped.Add(-1);
        if (ease && p.Count > 3)
        {
            var keep = Simplify(p, 6.0);
            if (keep.Count < p.Count)
            {
                var remap = new int[p.Count];
                var kept = new List<V2>();
                for (var i = 0; i < p.Count; i++)
                {
                    if (keep.Contains(i)) kept.Add(p[i]); else dropped.Add(i);
                    remap[i] = Math.Max(0, kept.Count - 1);
                }
                for (var j = 0; j < map.Count; j++) map[j] = remap[map[j]];
                p = kept;
            }
            // Two corners close together (a junction drawn as a short dog-leg) become one turn where the line in
            // and the line out meet, so the turn has room for its full curve.
            for (var pass = 0; pass < 4; pass++)
            {
                var merged = false;
                for (var i = 1; i + 2 < p.Count; i++)
                {
                    if ((p[i + 1] - p[i]).Length > 30) continue;
                    var a0 = p[i - 1]; var a1 = p[i]; var b0 = p[i + 1]; var b1 = p[i + 2];
                    var rv = a1 - a0; var qv = b1 - b0;
                    var den = V2.Cross(rv, qv);
                    if (Math.Abs(den) < 1e-6 * rv.Length * qv.Length) continue;
                    var tm = V2.Cross(b0 - a0, qv) / den;
                    var x = a0 + rv * tm;
                    if ((x - a1).Length > 18 || (x - b0).Length > 18 || tm < 0.5) continue;
                    p[i] = x;
                    p.RemoveAt(i + 1);
                    for (var j = 0; j < map.Count; j++) if (map[j] > i) map[j]--;
                    dropped.Add(-1);   // positions changed: re-project the original points below
                    merged = true;
                    break;
                }
                if (!merged) break;
            }
        }
        Points = p;
        var keptS = new double[p.Count];

        // Tangent distances at each corner, clamped so neighbouring arcs never overlap.
        var t = new double[p.Count];
        var rad = new double[p.Count];
        var turn = new double[p.Count];
        for (var i = 1; i < p.Count - 1; i++)
        {
            var din = (p[i] - p[i - 1]).Unit; var dout = (p[i + 1] - p[i]).Unit;
            var th = Math.Abs(Geo2.Diff(din.Bearing, dout.Bearing));
            if (th < 0.5 || th > 179.5) continue; // straight on, or a complete reversal (no arc possible)
            turn[i] = th;
            var origIndex = map.IndexOf(i);
            var r = rOverride.TryGetValue(i, out var ro) ? ro : Math.Max(1, radius(origIndex < 0 ? i : origIndex));
            var tanHalf = ease ? EasedTangent(th) : Math.Tan(th * Math.PI / 360);
            t[i] = r * tanHalf;
            rad[i] = r;
        }
        var t0 = (double[])t.Clone();
        for (var i = 1; i < p.Count - 1; i++)
        {
            if (t[i] <= 0) continue;
            var lin = (p[i] - p[i - 1]).Length; var lout = (p[i + 1] - p[i]).Length;
            var maxIn = i - 1 == 0 ? lin * 0.95 : lin * 0.5;
            var maxOut = i + 1 == p.Count - 1 ? lout * 0.95 : lout * 0.5;
            // An air route: a leg between two turns is shared as each needs it (a big turn and a small one), only
            // squeezed in proportion when the two together don't fit.
            if (flyBy)
            {
                if (i - 1 > 0) maxIn = t0[i - 1] + t0[i] <= lin ? t0[i] : lin * t0[i] / (t0[i - 1] + t0[i]);
                if (i + 1 < p.Count - 1) maxOut = t0[i + 1] + t0[i] <= lout ? t0[i] : lout * t0[i] / (t0[i + 1] + t0[i]);
            }
            var tt = Math.Min(t[i], Math.Min(maxIn, maxOut));
            if (tt < t[i]) { rad[i] = tt / (ease ? EasedTangent(turn[i]) : Math.Tan(turn[i] * Math.PI / 360)); t[i] = tt; }
        }

        double s = 0;
        var cur = p[0];
        keptS[0] = 0;
        for (var i = 1; i < p.Count; i++)
        {
            var dir = (p[i] - p[i - 1]).Unit;
            var end = i < p.Count - 1 && t[i] > 0 ? p[i] - dir * t[i] : p[i];
            var len = (end - cur).Length;
            if (len > 1e-6) { _segs.Add(new Line(s, len, cur, (end - cur).Unit)); s += len; }
            cur = end;
            if (i < p.Count - 1 && t[i] > 0)
            {
                var dout = (p[i + 1] - p[i]).Unit;
                var right = V2.Cross(dir, dout) < 0;
                var r = rad[i];
                if (ease)
                {
                    // A chain of short arcs whose curvature ramps up, holds, and ramps down (a clothoid, piece by piece).
                    var s0c = s;
                    var heading = dir;
                    foreach (var (k, len1) in EasedPieces(turn[i]))
                    {
                        var rr = r / k;
                        var plen = len1 * r;
                        var c = cur + (right ? heading.Perp : heading.Perp * -1) * rr;
                        var phi0 = (cur - c).Bearing;
                        var sweep = (right ? 1 : -1) * plen / rr * 180 / Math.PI;
                        _segs.Add(new Arc(s, plen, c, rr, phi0, sweep));
                        s += plen;
                        cur = c + V2.FromBearing(phi0 + sweep, rr);
                        heading = V2.FromBearing(heading.Bearing + sweep);
                    }
                    Curves.Add((s0c, s, Math.Sqrt(lateralAccel * r)));
                    keptS[i] = (s0c + s) / 2;
                    cur = p[i] + dout * t[i];   // the same point the pieces end at (they are built to fit exactly)
                }
                else
                {
                    var c = cur + (right ? dir.Perp : dir.Perp * -1) * r;
                    var phi0 = (cur - c).Bearing;
                    var sweep = (right ? 1 : -1) * turn[i];
                    var alen = r * turn[i] * Math.PI / 180;
                    _segs.Add(new Arc(s, alen, c, r, phi0, sweep));
                    Curves.Add((s, s + alen, Math.Sqrt(lateralAccel * r)));
                    keptS[i] = s + alen / 2;
                    s += alen;
                    cur = p[i] + dout * t[i];
                }
            }
            else keptS[i] = s;
        }
        Length = s;
        KeptS = keptS;
        PointS = map.Select(k => keptS[k]).ToArray();
        // Points smoothed away: their distance along the path is where the path passes closest to them.
        if (dropped.Count > 0)
            for (var j = 0; j < pts.Count; j++)
                if ((pts[j] - p[map[j]]).Length > 0.5) PointS[j] = ProjectNear(pts[j], PointS[j]);
    }

    /// <summary>Douglas–Peucker: the indices of the points to keep so the line stays within tol metres of the original.</summary>
    private static HashSet<int> Simplify(List<V2> p, double tol)
    {
        var keep = new HashSet<int> { 0, p.Count - 1 };
        var stack = new Stack<(int a, int b)>();
        stack.Push((0, p.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double best = -1; var at = -1;
            for (var i = a + 1; i < b; i++)
            {
                var ab = p[b] - p[a];
                var t = ab.Length < 1e-6 ? 0 : Math.Clamp(V2.Dot(p[i] - p[a], ab) / V2.Dot(ab, ab), 0, 1);
                var d = (p[a] + ab * t - p[i]).Length;
                if (d > best) { best = d; at = i; }
            }
            if (at >= 0 && best > tol) { keep.Add(at); stack.Push((a, at)); stack.Push((at, b)); }
        }
        return keep;
    }

    /// <summary>Distance along the path closest to q, searched around a first guess.</summary>
    private double ProjectNear(V2 q, double guess)
    {
        double best = guess, bestD = (At(guess).p - q).Length;
        for (var s = Math.Max(0, guess - 150); s <= Math.Min(Length, guess + 150); s += 2)
        {
            var d = (At(s).p - q).Length;
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    // ---------------------------------------------------------------- eased turns (for a turn of radius 1)
    private const int EasePieces = 6;

    /// <summary>The pieces of an eased turn of the given angle at radius 1: (curvature, length).</summary>
    private static List<(double k, double len)> EasedPieces(double turnDeg)
    {
        var th = turnDeg * Math.PI / 180;
        var q = Math.Min(0.8, th * 0.8);                 // length of each easing spiral (at radius 1); turns q/2 each
        var list = new List<(double, double)>();
        for (var j = 0; j < EasePieces; j++) list.Add(((j + 0.5) / EasePieces, q / EasePieces));
        var mid = th - q;
        if (mid > 1e-6) list.Add((1.0, mid));
        for (var j = EasePieces - 1; j >= 0; j--) list.Add(((j + 0.5) / EasePieces, q / EasePieces));
        // Scale the turn so the pieces add up to exactly the corner's angle.
        var total = list.Sum(x => x.Item1 * x.Item2);
        return list.Select(x => (x.Item1, x.Item2 * th / total)).ToList();
    }

    private static readonly Dictionary<int, double> TangentCache = new();

    /// <summary>
    /// How far before (and after) the corner an eased turn of radius 1 starts: integrate the pieces from the start
    /// line and solve where the end lands on the outgoing line (it's symmetric, so both distances are equal).
    /// </summary>
    private static double EasedTangent(double turnDeg)
    {
        var key = (int)Math.Round(turnDeg * 10);
        lock (TangentCache)
            if (TangentCache.TryGetValue(key, out var hit)) return hit;
        double x = 0, y = 0, a = 0;
        foreach (var (k, len) in EasedPieces(turnDeg))
        {
            var da = k * len;
            x += (Math.Sin(a + da) - Math.Sin(a)) / k;
            y += -(Math.Cos(a + da) - Math.Cos(a)) / k;
            a += da;
        }
        var th = turnDeg * Math.PI / 180;
        // (x, y) = tIn * (1, 0) + tOut * (cos th, sin th)
        var tOut = y / Math.Sin(th);
        var tIn = x - tOut * Math.Cos(th);
        var t = (tIn + tOut) / 2;
        lock (TangentCache) TangentCache[key] = t;
        return t;
    }

    public (V2 p, double dir, double k) At(double s)
    {
        if (_segs.Count == 0) return (Points[0], 0, 0);
        s = Math.Clamp(s, 0, Length);
        // Binary search for the segment containing s.
        int lo = 0, hi = _segs.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_segs[mid].S0 <= s) lo = mid; else hi = mid - 1;
        }
        var seg = _segs[lo];
        return seg.At(Math.Min(s - seg.S0, seg.Len));
    }

    /// <summary>Distance along the path closest to point q (coarse search then refine).</summary>
    public double Project(V2 q)
    {
        double best = 0, bestD = double.MaxValue;
        var step = Math.Max(5, Length / 400);
        for (double s = 0; s <= Length; s += step)
        {
            var d = (At(s).p - q).Length;
            if (d < bestD) { bestD = d; best = s; }
        }
        for (var h = step / 2; h > 0.5; h /= 2)
        {
            foreach (var c in new[] { best - h, best + h })
            {
                if (c < 0 || c > Length) continue;
                var d = (At(c).p - q).Length;
                if (d < bestD) { bestD = d; best = c; }
            }
        }
        return best;
    }

    /// <summary>The slowest speed allowed at s by curves ahead, given a braking rate (m/s²) and a lookahead.</summary>
    public double CurveLimit(double s, double brake, double vmax)
    {
        var v = vmax;
        foreach (var (s0, s1, lim) in Curves)
        {
            if (s1 < s) continue;
            if (s0 > s + 3000) break;
            var d = Math.Max(0, s0 - s);
            v = Math.Min(v, Math.Sqrt(lim * lim + 2 * brake * d));
        }
        return v;
    }
}
