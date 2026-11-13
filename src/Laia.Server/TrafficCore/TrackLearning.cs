using System.Text.Json;
using System.Text.Json.Serialization;

namespace Laia.Server.TrafficCore;

/// <summary>One aircraft's recorded positions (from adsb.lol history): seconds, position, altitude (null on the ground), track.</summary>
public sealed record TrackPoint(double T, double Lat, double Lon, double? AltFt, double Track);

/// <summary>
/// What an airport's real traffic does, learned from real tracks (M6): per landing runway, where vectored arrivals
/// join the final, at what angle, how far out the downwind sits and where it turns base, and the altitudes on the way
/// in; per departure runway, the typical paths flown off it in each direction (the radar-vector departures); and a
/// sample of real tracks to show on the map next to LAIA's.
/// </summary>
public sealed class AirportProfile
{
    public string Icao { get; set; } = "";
    public string Source { get; set; } = "";
    public string Day { get; set; } = "";
    public DateTime LearnedUtc { get; set; }
    public Dictionary<string, RunwayArrivals> Arrivals { get; set; } = new();
    public Dictionary<string, RunwayDepartures> Departures { get; set; } = new();
    public List<SampleTrack> Samples { get; set; } = new();
    /// <summary>Real taxi tracks (ground positions off the runways), each with the runway it landed on or left from.</summary>
    public List<TaxiTrack> Taxi { get; set; } = new();
    /// <summary>What the profile holds: 2 = with taxi tracks; 3 = airline passenger jets only; 4 = three days. Older ones are learned again.</summary>
    public int Version { get; set; }
    public const int CurrentVersion = 4;

    public sealed class TaxiTrack
    {
        public string Kind { get; set; } = "";       // arrival (taxi in) or departure (taxi out)
        public string Runway { get; set; } = "";
        public List<double[]> Points { get; set; } = new();   // lat, lon, track (°)
    }

    public sealed class RunwayArrivals
    {
        public int Count { get; set; }
        public int Vectored { get; set; }
        /// <summary>Distance from the threshold where they're established on the final (nm): 25th, 50th, 75th percentile.</summary>
        public double JoinP25 { get; set; }
        public double JoinP50 { get; set; }
        public double JoinP75 { get; set; }
        public double InterceptDeg { get; set; }
        public double DownwindNm { get; set; }
        public double BaseNm { get; set; }
        public int LeftDownwinds { get; set; }
        public int RightDownwinds { get; set; }
        /// <summary>Median altitude (ft) at 10, 20 and 30 nm from the airport.</summary>
        public double[] AltAt { get; set; } = new double[3];
    }

    public sealed class RunwayDepartures
    {
        public int Count { get; set; }
        /// <summary>Typical paths off this runway, by the direction they leave in (30° bins of the bearing at ~20 nm):
        /// the median position at each nm of path, as lat/lon pairs.</summary>
        public List<DeparturePath> Paths { get; set; } = new();
        public double FirstTurnNm { get; set; }
    }

    public sealed class DeparturePath
    {
        public int Bearing { get; set; }
        public int Count { get; set; }
        public List<double[]> Points { get; set; } = new();
    }

    public sealed class SampleTrack
    {
        public string Kind { get; set; } = "";
        public string Runway { get; set; } = "";
        public List<double[]> Points { get; set; } = new();   // lat, lon, alt
    }

    public string Describe()
    {
        var parts = new List<string>();
        foreach (var (r, a) in Arrivals.OrderByDescending(x => x.Value.Count).Take(4))
            parts.Add($"{r}: {a.Count} arrivals, final joined at {a.JoinP50:0.0} nm ({a.JoinP25:0.0}–{a.JoinP75:0.0}), {a.InterceptDeg:0}° intercept" +
                      (a.Vectored > 0 ? $", downwind {a.DownwindNm:0.0} nm out, base at {a.BaseNm:0} nm" : ""));
        foreach (var (r, d) in Departures.OrderByDescending(x => x.Value.Count).Take(3))
            parts.Add($"{r}: {d.Count} departures in {d.Paths.Count} directions");
        if (Taxi.Count > 0) parts.Add($"{Taxi.Count} taxi tracks ({Taxi.Count(t => t.Kind == "arrival")} in, {Taxi.Count(t => t.Kind == "departure")} out, {Taxi.Sum(t => t.Points.Count)} ground positions)");
        return $"{Source} {Day}: " + (parts.Count == 0 ? "nothing usable" : string.Join("; ", parts));
    }
}

/// <summary>The real-world shape of vectors to final for a runway (defaults are the generic controller numbers).</summary>
public sealed record VectorShape(double JoinNm, double BaseNm, double DownwindNm, double InterceptDeg)
{
    public static readonly VectorShape Default = new(9.5, 12.5, double.NaN, 28);
}

/// <summary>
/// Learns an <see cref="AirportProfile"/> from real tracks. Landings are found where a track comes down onto a runway
/// aligned with it; take-offs where one climbs away off a runway aligned with it. All in the runway's own frame.
/// </summary>
public static class TrackLearner
{
    private const double Nm = 1852;

    public static AirportProfile Learn(string icao, LocalMap map, double elevFt, IReadOnlyList<RunwayEnd> ends, IEnumerable<IReadOnlyList<TrackPoint>> tracks,
        string source = "", string day = "")
    {
        var arr = new Dictionary<string, List<(double join, double icpt, double dw, double bas, bool left, double[] alt)>>();
        var dep = new Dictionary<string, List<List<V2>>>();
        var firstTurn = new Dictionary<string, List<double>>();
        var samples = new List<AirportProfile.SampleTrack>();
        var sampleCount = new Dictionary<string, int>();
        var taxi = new List<AirportProfile.TaxiTrack>();
        foreach (var tr in tracks)
        {
            if (tr.Count < 5) continue;
            var xy = tr.Select(p => map.ToXY(p.Lat, p.Lon)).ToList();
            // ---- landings
            for (var i = 1; i < tr.Count; i++)
            {
                var low = tr[i].AltFt is null || tr[i].AltFt < elevFt + 300;
                if (!low) continue;
                var r = ends.FirstOrDefault(e => e.Along(xy[i]) > -1000 && e.Along(xy[i]) < e.LengthM * 0.8 && Math.Abs(e.Offset(xy[i])) < 120
                    && Math.Abs(Geo2.Diff(tr[i].Track, e.Heading)) < 25);
                if (r is null) continue;
                // It must have been flying, a minute before.
                var j0 = Back(tr, i, 60);
                if (j0 < 0 || tr[j0].AltFt is null || tr[j0].AltFt < elevFt + 800) continue;
                var a = Arrival(tr, xy, i, r, elevFt);
                if (a is not null)
                {
                    if (!arr.TryGetValue(r.Ident, out var l)) arr[r.Ident] = l = new();
                    l.Add(a.Value);
                    Sample("arrival", r.Ident, tr, Back(tr, i, 1500), i);
                }
                Taxi("arrival", r.Ident, tr, xy, i, +1);
                // Skip past this landing (taxi, park).
                while (i < tr.Count && (tr[i].AltFt is null || tr[i].AltFt < elevFt + 2000)) i++;
            }
            // ---- take-offs
            for (var i = 1; i < tr.Count; i++)
            {
                if (tr[i - 1].AltFt is not null && tr[i - 1].AltFt > elevFt + 200) continue;
                if (tr[i].AltFt is null || tr[i].AltFt > elevFt + 1200) continue;
                var r = ends.FirstOrDefault(e => e.Along(xy[i]) > e.LengthM * 0.2 && e.Along(xy[i]) < e.LengthM + 3 * Nm && Math.Abs(e.Offset(xy[i])) < 300
                    && Math.Abs(Geo2.Diff(tr[i].Track, e.Heading)) < 20);
                if (r is null) continue;
                Taxi("departure", r.Ident, tr, xy, i, -1);
                var path = new List<V2> { r.Threshold + r.Dir * r.LengthM };
                var k = i;
                double flown = 0;
                for (; k < tr.Count && tr[k].T - tr[i].T < 900; k++)
                {
                    flown += (xy[k] - path[^1]).Length;
                    path.Add(xy[k]);
                    if (xy[k].Length > 30 * Nm) break;
                }
                if (path.Count >= 6 && path[^1].Length > 12 * Nm)
                {
                    if (!dep.TryGetValue(r.Ident, out var l)) dep[r.Ident] = l = new();
                    l.Add(path);
                    // Where it first turned more than 15° off the runway heading.
                    var turn = double.NaN; double s = 0;
                    for (var q = 1; q < path.Count; q++)
                    {
                        s += (path[q] - path[q - 1]).Length;
                        if (Math.Abs(Geo2.Diff((path[q] - path[q - 1]).Bearing, r.Heading)) > 15 && (path[q] - path[q - 1]).Length > 50) { turn = s / Nm; break; }
                    }
                    if (!double.IsNaN(turn)) { if (!firstTurn.TryGetValue(r.Ident, out var ft)) firstTurn[r.Ident] = ft = new(); ft.Add(turn); }
                    Sample("departure", r.Ident, tr, i, k);
                }
                i = k;
            }
        }

        var prof = new AirportProfile { Icao = icao, Source = source, Day = day, LearnedUtc = DateTime.UtcNow, Samples = samples, Taxi = taxi, Version = AirportProfile.CurrentVersion };
        foreach (var (rw, l) in arr)
        {
            if (l.Count < 3) continue;
            var joins = l.Select(x => x.join).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
            if (joins.Count < 3) continue;
            var vec = l.Where(x => !double.IsNaN(x.dw)).ToList();
            prof.Arrivals[rw] = new AirportProfile.RunwayArrivals
            {
                Count = l.Count,
                Vectored = vec.Count,
                JoinP25 = Pct(joins, 0.25), JoinP50 = Pct(joins, 0.5), JoinP75 = Pct(joins, 0.75),
                InterceptDeg = Median(l.Select(x => x.icpt).Where(x => !double.IsNaN(x))),
                DownwindNm = Median(vec.Select(x => x.dw)),
                BaseNm = Median(vec.Select(x => x.bas).Where(x => !double.IsNaN(x))),
                LeftDownwinds = vec.Count(x => x.left), RightDownwinds = vec.Count(x => !x.left),
                AltAt = Enumerable.Range(0, 3).Select(k => Median(l.Select(x => x.alt[k]).Where(v => !double.IsNaN(v)))).ToArray(),
            };
        }
        foreach (var (rw, paths) in dep)
        {
            var d = new AirportProfile.RunwayDepartures { Count = paths.Count, FirstTurnNm = firstTurn.TryGetValue(rw, out var ft) ? Median(ft) : double.NaN };
            // Resample each path every nm of path, group by the direction it's heading at ~20 nm.
            var res = paths.Select(p => Resample(p, 25)).Where(p => p.Count >= 10).ToList();
            foreach (var g in res.GroupBy(p => ((int)Math.Round(Geo2.Norm(p[^1].Bearing) / 30) % 12) * 30))
            {
                var list = g.ToList();
                if (list.Count < 3) continue;
                var n = list.Min(p => p.Count);
                var pts = new List<double[]>();
                for (var q = 0; q < n; q++)
                {
                    var mx = Median(list.Select(p => p[q].X)); var my = Median(list.Select(p => p[q].Y));
                    var (la, lo) = map.ToLatLon(new V2(mx, my));
                    pts.Add(new[] { Math.Round(la, 5), Math.Round(lo, 5) });
                }
                d.Paths.Add(new AirportProfile.DeparturePath { Bearing = g.Key, Count = list.Count, Points = pts });
            }
            prof.Departures[rw] = d;
        }
        return prof;

        // The ground part next to a landing (after it, dir +1) or a take-off (before it, dir -1): positions on the ground
        // within 6 km of the airport and off the runways, in time order.
        void Taxi(string kind, string rw, IReadOnlyList<TrackPoint> tr, List<V2> xy, int at, int dir)
        {
            if (taxi.Count >= 8000) return;
            var pts = new List<double[]>();
            var started = false;
            V2? last = null;
            for (var q = at; q >= 0 && q < tr.Count; q += dir)
            {
                if (Math.Abs(tr[q].T - tr[at].T) > 3600) break;
                if (q != at && Math.Abs(tr[q].T - tr[q - dir].T) > 300) break;   // a long gap: parked, or out of coverage
                var ground = tr[q].AltFt is null || tr[q].AltFt < elevFt + 50;
                if (!ground) { if (started) break; continue; }                 // still flying (or flying again)
                if (xy[q].Length > 6000) break;
                if (ends.Any(e => e.Contains(xy[q], 10))) continue;            // on a runway: the roll, or crossing one
                started = true;
                if (last is { } l && (xy[q] - l).Length < 5) continue;        // standing still: no direction to learn
                last = xy[q];
                pts.Add(new[] { Math.Round(tr[q].Lat, 6), Math.Round(tr[q].Lon, 6), Math.Round(tr[q].Track) });
            }
            if (pts.Count < 3) return;
            if (dir < 0) pts.Reverse();
            taxi.Add(new AirportProfile.TaxiTrack { Kind = kind, Runway = rw, Points = pts });
        }

        void Sample(string kind, string rw, IReadOnlyList<TrackPoint> tr, int from, int to)
        {
            var key = kind + rw;
            var c = sampleCount.GetValueOrDefault(key);
            if (c >= 40 || from < 0) return;
            sampleCount[key] = c + 1;
            var pts = new List<double[]>();
            double lastT = double.MinValue;
            for (var q = Math.Max(0, from); q <= Math.Min(to, tr.Count - 1); q++)
            {
                if (tr[q].T - lastT < 15 && q != to) continue;
                lastT = tr[q].T;
                pts.Add(new[] { Math.Round(tr[q].Lat, 5), Math.Round(tr[q].Lon, 5), Math.Round(tr[q].AltFt ?? elevFt) });
            }
            samples.Add(new AirportProfile.SampleTrack { Kind = kind, Runway = rw, Points = pts });
        }
    }

    /// <summary>The index about this many seconds before i (-1 if the track starts later).</summary>
    private static int Back(IReadOnlyList<TrackPoint> tr, int i, double sec)
    {
        for (var j = i; j >= 0; j--) if (tr[i].T - tr[j].T >= sec) return j;
        return -1;
    }

    private static (double join, double icpt, double dw, double bas, bool left, double[] alt)? Arrival(IReadOnlyList<TrackPoint> tr, List<V2> xy, int land, RunwayEnd r, double elevFt)
    {
        var start = Back(tr, land, 1800);
        if (start < 0) start = 0;
        // Established on the final: going back from the landing, the first point that is no longer within 0.35 nm of
        // the extended centreline and within 8° of the runway heading.
        var k = land;
        while (k > start && Math.Abs(r.Offset(xy[k - 1])) < 0.35 * Nm && r.Along(xy[k - 1]) < 0 + 200 && Math.Abs(Geo2.Diff(tr[k - 1].Track, r.Heading)) < 8) k--;
        var join = -r.Along(xy[k]) / Nm;
        if (join < 1 || join > 40) return null;
        // The intercept: the track 1–2.5 nm (along the path) before joining.
        var icpt = double.NaN;
        {
            double s = 0;
            for (var q = k - 1; q > start; q--)
            {
                s += (xy[q + 1] - xy[q]).Length;
                if (s > 1 * Nm) { var dd = Math.Abs(Geo2.Diff(tr[q].Track, r.Heading)); if (dd < 90) icpt = dd; }
                if (s > 2.5 * Nm) break;
            }
        }
        // A downwind: flying the other way (within 30° of the reciprocal), off to one side, within 25 nm.
        var dws = new List<(double off, double along)>();
        for (var q = start; q < k; q++)
        {
            if (Math.Abs(Geo2.Diff(tr[q].Track, r.Heading + 180)) > 30) continue;
            var off = r.Offset(xy[q]); var al = r.Along(xy[q]);
            if (Math.Abs(off) < 1.5 * Nm || Math.Abs(off) > 15 * Nm || al < -25 * Nm || al > 15 * Nm) continue;
            dws.Add((off, al));
        }
        double dw = double.NaN, bas = double.NaN; var left = false;
        if (dws.Count >= 3)
        {
            dw = Median(dws.Select(x => Math.Abs(x.off))) / Nm;
            bas = -dws.Min(x => x.along) / Nm;   // the furthest out it flew on the downwind: where it turned base
            left = Median(dws.Select(x => x.off)) > 0;
        }
        // Altitudes at about 10, 20 and 30 nm from the airport.
        var alt = new double[3];
        for (var m = 0; m < 3; m++)
        {
            var want = (m + 1) * 10 * Nm;
            var best = double.NaN; var bd = double.MaxValue;
            for (var q = start; q < land; q++)
            {
                if (tr[q].AltFt is not { } a) continue;
                var dd = Math.Abs(xy[q].Length - want);
                if (dd < bd && dd < 1.5 * Nm) { bd = dd; best = a; }
            }
            alt[m] = best;
        }
        return (join, icpt, dw, bas, left, alt);
    }

    private static List<V2> Resample(List<V2> p, int nmMax)
    {
        var res = new List<V2> { p[0] };
        double carried = 0;
        for (var i = 1; i < p.Count && res.Count <= nmMax; i++)
        {
            var a = p[i - 1]; var b = p[i]; var len = (b - a).Length;
            if (len < 1e-6) continue;
            var pos = Nm - carried;
            while (pos <= len && res.Count <= nmMax) { res.Add(a + (b - a) * (pos / len)); pos += Nm; }
            carried = len - (pos - Nm);
        }
        return res;
    }

    private static double Median(IEnumerable<double> xs) { var l = xs.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList(); return l.Count == 0 ? double.NaN : Pct(l, 0.5); }

    private static double Pct(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var x = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(x); var hi = Math.Min(sorted.Count - 1, lo + 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (x - lo);
    }
}

/// <summary>adsb.lol / readsb trace files, and how profiles are saved.</summary>
public static class AdsbTrace
{
    /// <summary>A jet airliner type code (A220–A380, 717–787, MD-80/90/11, the E-jets and CRJs).</summary>
    public static bool IsAirlinerJet(string? type)
    {
        var t = (type ?? "").Trim().ToUpperInvariant();
        if (t.Length < 3) return false;
        return t.StartsWith("A3") || t.StartsWith("A2") || t.StartsWith("A19") || t.StartsWith("A20") || t.StartsWith("A21")
            || t.StartsWith("B37") || t.StartsWith("B38") || t.StartsWith("B39") || t.StartsWith("B3X") || t.StartsWith("B73") || t.StartsWith("B71")
            || t.StartsWith("B74") || t.StartsWith("B75") || t.StartsWith("B76") || t.StartsWith("B77") || t.StartsWith("B78")
            || t.StartsWith("MD8") || t.StartsWith("MD9") || t == "MD11" || t.StartsWith("BCS") || t.StartsWith("CRJ")
            || t.StartsWith("E17") || t.StartsWith("E19") || t.StartsWith("E29") || t.StartsWith("E75") || t is "E135" or "E145" or "E140" or "E35L";
    }

    /// <summary>An airline callsign: three letters, then a number (DAL1234, JBU22A), not a registration (N123AB).</summary>
    public static bool IsAirlineCallsign(string cs) =>
        cs.Length >= 4 && char.IsLetter(cs[0]) && char.IsLetter(cs[1]) && char.IsLetter(cs[2]) && char.IsDigit(cs[3]);

    public static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>
    /// A readsb trace file: {"icao":…, "timestamp": t0, "trace": [[dt, lat, lon, alt|"ground", gs, track, flags, vrate, …], …]}.
    /// </summary>
    public static List<TrackPoint>? Parse(byte[] json) => Parse(json, passengerJetsOnly: false);

    /// <summary>
    /// As above; with passengerJetsOnly, only airline passenger jets are kept (user, 13 Nov: "only include air passenger
    /// jets"): the trace's type code must be a jet airliner, and its callsign (when it has one) an airline's — three
    /// letters and a number — that isn't a cargo airline. Light aircraft, business jets, turboprops, helicopters and
    /// freighters are skipped before their points are read.
    /// </summary>
    public static List<TrackPoint>? Parse(byte[] json, bool passengerJetsOnly)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (passengerJetsOnly)
        {
            var type = root.TryGetProperty("t", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
            if (!IsAirlinerJet(type)) return null;
        }
        if (!root.TryGetProperty("trace", out var trace) || trace.ValueKind != JsonValueKind.Array) return null;
        if (passengerJetsOnly)
        {
            foreach (var p in trace.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 9 || p[8].ValueKind != JsonValueKind.Object) continue;
                if (!p[8].TryGetProperty("flight", out var fl) || fl.ValueKind != JsonValueKind.String) continue;
                var cs = (fl.GetString() ?? "").Trim().ToUpperInvariant();
                if (cs.Length == 0) continue;
                if (!IsAirlineCallsign(cs) || TrafficWorld.IsCargo(cs[..3])) return null;
                break;
            }
        }
        var t0 = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetDouble() : 0;
        var res = new List<TrackPoint>(trace.GetArrayLength());
        double lastTrack = 0;
        foreach (var p in trace.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 6) continue;
            var dt = p[0].GetDouble(); var lat = p[1].GetDouble(); var lon = p[2].GetDouble();
            double? alt = p[3].ValueKind == JsonValueKind.Number ? p[3].GetDouble() : p[3].ValueKind == JsonValueKind.String ? null : null;
            if (p[3].ValueKind == JsonValueKind.Null) continue;   // no altitude: unusable
            var trk = p[5].ValueKind == JsonValueKind.Number ? p[5].GetDouble() : lastTrack;
            lastTrack = trk;
            res.Add(new TrackPoint(t0 + dt, lat, lon, alt, trk));
        }
        return res;
    }

}
