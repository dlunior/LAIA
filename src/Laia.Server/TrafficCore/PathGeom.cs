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
    public PathGeom(IList<V2> pts, Func<int, double> radius, double lateralAccel)
    {
        var p = new List<V2>();
        var map = new List<int>(); // input index -> kept index
        foreach (var q in pts)
        {
            if (p.Count == 0 || (q - p[^1]).Length > 0.5) p.Add(q);
            map.Add(p.Count - 1);
        }
        if (p.Count == 1) p.Add(p[0] + new V2(0, 0.6));
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
            var r = Math.Max(1, radius(origIndex < 0 ? i : origIndex));
            var tanHalf = Math.Tan(th * Math.PI / 360);
            t[i] = r * tanHalf;
            rad[i] = r;
        }
        for (var i = 1; i < p.Count - 1; i++)
        {
            if (t[i] <= 0) continue;
            var lin = (p[i] - p[i - 1]).Length; var lout = (p[i + 1] - p[i]).Length;
            var maxIn = i - 1 == 0 ? lin * 0.95 : lin * 0.5;
            var maxOut = i + 1 == p.Count - 1 ? lout * 0.95 : lout * 0.5;
            var tt = Math.Min(t[i], Math.Min(maxIn, maxOut));
            if (tt < t[i]) { rad[i] = tt / Math.Tan(turn[i] * Math.PI / 360); t[i] = tt; }
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
            else keptS[i] = s;
        }
        Length = s;
        KeptS = keptS;
        PointS = map.Select(k => keptS[k]).ToArray();
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
