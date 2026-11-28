using Laia.Common.Airports;

namespace Laia.Server.TrafficCore;

/// <summary>One real arrival read from the LAIA Collector's tracks: where it came into the airport's 45 nm (and below
/// 18,000 ft above it), when it crossed its runway's threshold, and the way it flew between.</summary>
public sealed record RealArrival(string Callsign, string Type, string Runway, double EntryT, double EntryLat, double EntryLon,
    double EntryAltFt, double ThrT, double TrackNm, List<TrackPoint> Pts);

/// <summary>A real flight from the Collector's flight list, for the callsign and type of a track.</summary>
public sealed record RealLeg(string Callsign, string Type, string From, string To, double? TakeOff, double? Landing);

/// <summary>
/// The scoreboard (Step 6, 27 Nov: "a scoreboard first — of today's rule-based LAIA"): real arrivals read from the
/// Collector's tracks, and the same arrivals flown again by LAIA from where and when each really came in, to the runway
/// it really landed on — so LAIA's landing order, landing times, track miles and paths can be compared with what really
/// happened, per airport, and every version (and later every learned model) has a number to beat.
/// </summary>
public static class RealReplay
{
    /// <summary>
    /// The real arrivals in a day of an airport's tracks: a track that crosses a runway's threshold low (under 400 ft,
    /// within 250 m of its centreline, going its way), having come in from at least 25 nm out or 10,000 ft above the
    /// field, five minutes or more before. The first threshold it crosses counts (a go-around's second try doesn't).
    /// </summary>
    public static List<RealArrival> Arrivals(AirportModel apt, IEnumerable<IReadOnlyList<TrackPoint>> tracks, IReadOnlyList<RealLeg>? legs = null)
    {
        var res = new List<RealArrival>();
        var ends = apt.Ends.Where(e => e.LengthM >= 1500).ToList();
        var landing = legs?.Where(l => l.Landing is not null && string.Equals(l.To, apt.Icao, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var tr in tracks)
        {
            if (tr.Count < 10) continue;
            var xy = tr.Select(p => apt.Map.ToXY(p.Lat, p.Lon)).ToList();
            double bestT = double.MaxValue; RunwayEnd? bestE = null; var bestK = -1; var bestF = 0.0;
            foreach (var e in ends)
            {
                for (var k = 0; k + 1 < tr.Count; k++)
                {
                    var a1 = e.Along(xy[k]); var a2 = e.Along(xy[k + 1]);
                    if (!(a1 < 0 && a2 >= 0)) continue;
                    var fr = a1 / (a1 - a2);
                    var at = xy[k] + (xy[k + 1] - xy[k]) * fr;
                    if (Math.Abs(e.Offset(at)) > 250) continue;
                    var alt = Lerp(tr[k].AltFt, tr[k + 1].AltFt, fr);
                    if (alt is null || alt - apt.ElevationFt > 400) continue;
                    if (Math.Abs(Geo2.Diff(tr[k + 1].Track, e.Heading)) > 30) continue;
                    var t = tr[k].T + (tr[k + 1].T - tr[k].T) * fr;
                    if (t < bestT) { bestT = t; bestE = e; bestK = k; bestF = fr; }
                    break;
                }
            }
            if (bestE is null) continue;
            var entry = tr[0];
            var entryAlt = entry.AltFt ?? double.NaN;
            var fromThr = (xy[0] - bestE.Threshold).Length / Geo2.MPerNm;
            if (double.IsNaN(entryAlt) || (fromThr < 25 && entryAlt - apt.ElevationFt < 10000) || bestT - entry.T < 300) continue;
            double len = 0;
            for (var k = 0; k < bestK; k++) len += (xy[k + 1] - xy[k]).Length;
            len += (xy[bestK + 1] - xy[bestK]).Length * bestF;
            var pts = new List<TrackPoint>();
            var lastT = double.NegativeInfinity;
            for (var k = 0; k <= bestK; k++)
                if (tr[k].T - lastT >= 9.5) { pts.Add(tr[k]); lastT = tr[k].T; }
            var (cs, type) = ("", "B738");
            if (landing is { Count: > 0 })
            {
                var m = landing.Where(l => Math.Abs(l.Landing!.Value - bestT) < 900).OrderBy(l => Math.Abs(l.Landing!.Value - bestT)).FirstOrDefault();
                if (m is not null) { cs = m.Callsign; if (m.Type.Length > 0) type = m.Type; }
            }
            res.Add(new RealArrival(cs, type, bestE.Ident, entry.T, entry.Lat, entry.Lon, entryAlt, bestT, len / Geo2.MPerNm, pts));
        }
        return res.OrderBy(a => a.ThrT).ToList();
    }

    private static double? Lerp(double? a, double? b, double f) => a is { } x && b is { } y ? x + (y - x) * f : a ?? b;

    /// <summary>The busiest hour (UTC, by real landings) of a day's arrivals: its start, and how many landed in it.</summary>
    public static (double start, int count) BusiestHour(IReadOnlyList<RealArrival> arr)
    {
        if (arr.Count == 0) return (double.NaN, 0);
        return arr.GroupBy(a => Math.Floor(a.ThrT / 3600)).Select(g => (start: g.Key * 3600, count: g.Count())).OrderByDescending(x => x.count).ThenBy(x => x.start).First();
    }

    public sealed class Score
    {
        public string Airport = "", Window = "";
        public int Spawned, Scored, Landed;
        /// <summary>Pairs landing within 15 minutes of each other on the same runway, put in the real order (0–1).</summary>
        public double OrderSame = double.NaN;
        /// <summary>LAIA's threshold time against the real one: median of the size, and median of the sign (s).</summary>
        public double TimeErr = double.NaN, TimeBias = double.NaN;
        /// <summary>LAIA's track miles from where it came in against the real ones (median, nm).</summary>
        public double MilesDiff = double.NaN;
        /// <summary>How far LAIA's path was from the real one (median over arrivals of the mean distance, nm).</summary>
        public double PathNm = double.NaN;
        public Dictionary<string, double> Per10 = new();
        public List<string> Worst = new();

        public string Line() =>
            $"{Airport} {Window}: {Landed} of {Scored} real arrivals flown to the runway (of {Spawned} brought in) — landing order as real {Pct(OrderSame)}, " +
            $"landing time ±{TimeErr:0} s ({TimeBias:+0;-0} s on average), track miles {MilesDiff:+0.0;-0.0} nm against the real ones, path {PathNm:0.0} nm from the real one; " +
            $"per 10 landings — {string.Join(", ", Per10.Select(kv => $"{kv.Key} {kv.Value:0.0}"))}";
        private static string Pct(double x) => double.IsNaN(x) ? "–" : $"{100 * x:0}%";
    }

    /// <summary>
    /// The real arrivals landing in [<paramref name="winStart"/>, <paramref name="winEnd"/>) (Unix seconds) flown again by
    /// LAIA: each one brought in where and when it really came in (and those landing up to 20 minutes either side, so the
    /// hour starts and ends busy), to the runway it really landed on, at its real type; the runways the real arrivals used
    /// are the ones in use; the wind is the one given. Then LAIA's threshold times, order, track miles and paths against
    /// the real ones.
    /// </summary>
    public static Score Replay(AirportModel apt, AirportProfile? learned, Wind wind, IReadOnlyList<RealArrival> real, double winStart, double winEnd,
        IFlightSource source, Action<string>? log = null, Func<bool>? keepGoing = null)
    {
        var score = new Score { Airport = apt.Icao, Window = $"{DateTimeOffset.FromUnixTimeSeconds((long)winStart):yyyy.MM.dd HH}Z" };
        var bring = real.Where(a => a.ThrT >= winStart - 1200 && a.ThrT < winEnd + 1200 && apt.Ends.Any(e => e.Ident == a.Runway)).OrderBy(a => a.EntryT).ToList();
        if (bring.Count == 0) return score;
        var t0 = bring[0].EntryT;
        var lines = new List<string>();
        TrafficWorld? w = null;
        w = new TrafficWorld(apt, source, seed: 11)
        {
            Automatic = false, GenericTraffic = false, HoldRunways = false, Wind = wind, Learned = learned,
            Log = l => { if (lines.Count < 50000) lines.Add($"{w!.Time,6:0} {l}"); log?.Invoke(l); },
        };
        var rwys = bring.Select(a => a.Runway).Distinct().ToList();
        w.AtisText = $"REPLAY INFO A. ILS RWY {string.Join(" AND RWY ", rwys)} APCHS IN USE. DEPG RWY {rwys[0]}.";
        w.SelectRunways(true);
        var queue = new Queue<RealArrival>(bring);
        var map = new Dictionary<Flight, RealArrival>();
        var cross = new Dictionary<Flight, double>();
        var path = new Dictionary<Flight, List<V2>>();
        var lastSample = new Dictionary<Flight, double>();
        var prevAlong = new Dictionary<Flight, double>();
        var end = winEnd + 2400 - t0;
        var n = 0;
        while (w.Time < end)
        {
            while (queue.Count > 0 && queue.Peek().EntryT - t0 <= w.Time)
            {
                var a = queue.Dequeue();
                var e = apt.Ends.First(x => x.Ident == a.Runway);
                var cs = a.Callsign.Length > 0 ? a.Callsign : $"RPL{++n}";
                var op = new string(cs.TakeWhile(char.IsLetter).ToArray());
                var leg = new ScheduleLeg { Operator = op, Airline = op, Flight = cs[op.Length..], Type = a.Type, Dep = "", Arr = apt.Icao };
                try
                {
                    var f = w.SpawnArrival(0, false, leg, (e, null), entry: (apt.Map.ToXY(a.EntryLat, a.EntryLon), a.EntryAltFt, Math.Max(a.EntryAltFt, 10000)));
                    if (f is not null) { map[f] = a; path[f] = new List<V2> { f.Pos }; lastSample[f] = w.Time; }
                }
                catch (Exception ex) { lines.Add($"{w.Time,6:0} {cs}: couldn't be brought in ({ex.Message})"); }
            }
            w.Step(0.5);
            foreach (var (f, a) in map)
            {
                if (cross.ContainsKey(f) || f.Rwy is null) continue;
                if (w.Time - lastSample[f] >= 10) { path[f].Add(f.Pos); lastSample[f] = w.Time; }
                var al = f.Rwy.Along(f.Pos);
                if (prevAlong.TryGetValue(f, out var pa) && pa < 0 && al >= 0 && Math.Abs(f.Rwy.Offset(f.Pos)) < 400 && f.AltFt - f.Rwy.ElevationFt < 500)
                {
                    cross[f] = w.Time; path[f].Add(f.Pos);
                }
                prevAlong[f] = al;
            }
            if (queue.Count == 0 && map.Keys.All(f => cross.ContainsKey(f) || f.Done)) break;
            if (keepGoing is not null && ((int)(w.Time * 2) % 200 == 0) && !keepGoing()) break;
        }
        // The scores, over the arrivals that really landed in the window.
        var scored = map.Where(kv => kv.Value.ThrT >= winStart && kv.Value.ThrT < winEnd).ToList();
        score.Spawned = map.Count; score.Scored = scored.Count;
        var landed = scored.Where(kv => cross.ContainsKey(kv.Key)).ToList();
        score.Landed = landed.Count;
        if (landed.Count > 0)
        {
            var diffs = landed.Select(kv => cross[kv.Key] + t0 - kv.Value.ThrT).ToList();
            score.TimeErr = Median(diffs.Select(Math.Abs));
            score.TimeBias = Median(diffs);
            score.MilesDiff = Median(landed.Select(kv => Len(path[kv.Key]) / Geo2.MPerNm - kv.Value.TrackNm));
            score.PathNm = Median(landed.Select(kv => MeanDistance(path[kv.Key], kv.Value.Pts.Select(p => apt.Map.ToXY(p.Lat, p.Lon)).ToList()) / Geo2.MPerNm));
            int same = 0, pairs = 0;
            for (var i = 0; i < landed.Count; i++)
                for (var j = i + 1; j < landed.Count; j++)
                {
                    var (fa, ra) = landed[i]; var (fb, rb) = landed[j];
                    if (ra.Runway != rb.Runway || Math.Abs(ra.ThrT - rb.ThrT) > 900) continue;
                    pairs++;
                    if (Math.Sign(ra.ThrT - rb.ThrT) == Math.Sign(cross[fa] - cross[fb])) same++;
                }
            if (pairs > 0) score.OrderSame = (double)same / pairs;
            foreach (var kv in landed.OrderByDescending(kv => Math.Abs(cross[kv.Key] + t0 - kv.Value.ThrT)).Take(3))
                score.Worst.Add($"{kv.Key.Callsign} on {kv.Value.Runway}: {cross[kv.Key] + t0 - kv.Value.ThrT:+0;-0} s against its real landing, {Len(path[kv.Key]) / Geo2.MPerNm - kv.Value.TrackNm:+0.0;-0.0} nm");
        }
        var per = Math.Max(1, score.Landed) / 10.0;
        int C(string k) => lines.Count(l => l.Contains(k));
        score.Per10["go-arounds"] = C("going around") / per;
        score.Per10["break-offs"] = C("breaking off") / per;
        score.Per10["vectors"] = lines.Count(l => l.Contains("vectors for spacing (")) / per;
        score.Per10["routes refused"] = C("route refused") / per;
        score.Per10["separation warnings"] = C("SEPARATION") / per;
        return score;
    }

    private static double Len(List<V2> p) { double l = 0; for (var i = 1; i < p.Count; i++) l += (p[i] - p[i - 1]).Length; return l; }

    private static double Median(IEnumerable<double> xs)
    {
        var l = xs.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
        return l.Count == 0 ? double.NaN : l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
    }

    /// <summary>The mean distance (m) from the points of one path to the nearest part of another.</summary>
    public static double MeanDistance(IReadOnlyList<V2> a, IReadOnlyList<V2> b)
    {
        if (a.Count == 0 || b.Count == 0) return double.NaN;
        double sum = 0;
        foreach (var p in a)
        {
            var best = double.MaxValue;
            for (var i = 0; i + 1 < b.Count; i++) best = Math.Min(best, DistToSeg(p, b[i], b[i + 1]));
            if (b.Count == 1) best = (p - b[0]).Length;
            sum += best;
        }
        return sum / a.Count;
    }

    private static double DistToSeg(V2 p, V2 a, V2 b)
    {
        var ab = b - a; var l2 = V2.Dot(ab, ab);
        var t = l2 < 1e-6 ? 0 : Math.Clamp(V2.Dot(p - a, ab) / l2, 0, 1);
        return (p - (a + ab * t)).Length;
    }
}
