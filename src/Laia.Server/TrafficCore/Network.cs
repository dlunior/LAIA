using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Laia.Server.TrafficCore;

/// <summary>
/// One scheduled flight anywhere in the world (from the AIG flight plans), with its airports' positions
/// filled in. Times are minutes into the week (Monday 00:00 UTC = 0), block times as AIG stores them.
/// </summary>
public sealed class NetLeg
{
    public string Airline = "", Operator = "", Type = "", Reg = "", Flight = "", Dep = "", Arr = "", Title = "";
    public int DepMin, ArrMin;
    public double DepLat, DepLon, DepElev, ArrLat, ArrLon, ArrElev, DistNm;
    public bool Located;
    /// <summary>
    /// Metering (user, 18 Nov: "maybe the issue isn't the amount of traffic but how they are spaced when injected into
    /// the planning"): minutes this flight runs late so it reaches a busy airport in an arrival slot — set straight away
    /// before it takes off (a departure clearance time), eased in at no more than 15% slower once airborne.
    /// </summary>
    public double MeterMin, MeterTarget;
    /// <summary>The average tailwind (+) along its route at its cruise level, knots (NaN until worked out).</summary>
    public double WindKt = double.NaN, WindAt = double.NegativeInfinity;
    /// <summary>
    /// Separation en route (EnRouteSeparation): minutes behind (+) or ahead of (−) its schedule from speed changes given
    /// to keep it in trail; the speed change now (kt, + faster); and a vector off its route and back — started at a
    /// clock minute, turned VecAngle degrees (+ right) for VecOutMin, then back the same way onto its route.
    /// </summary>
    public double SepLagMin, SpdKt, VecStart = double.NaN, VecOutMin, VecAngle;
    /// <summary>
    /// Its route (user, 18 Nov: "would adding the real world routes to our sim traffic help?" — "yes"): points from the
    /// departure airport to the arrival airport (both included) along its FAA preferred route and, into an airport LAIA
    /// runs, the STAR it would fly; RouteCum the distance (nm) to each point. Null: the great circle.
    /// </summary>
    public double[]? RouteLat, RouteLon, RouteCum;
    /// <summary>What its route was made from ("preferred", "STAR …").</summary>
    public string? RouteFrom;
    /// <summary>The distance it flies: along its route, else the great circle.</summary>
    public double PathNm => RouteCum is { Length: > 1 } c ? c[^1] : DistNm;
    public void SetRoute(List<(double lat, double lon)>? pts, string? from)
    {
        if (pts is null || pts.Count < 2) { RouteLat = RouteLon = RouteCum = null; RouteFrom = null; return; }
        var cum = new double[pts.Count];
        for (var i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + Geo2.DistanceNm(pts[i - 1].lat, pts[i - 1].lon, pts[i].lat, pts[i].lon);
        RouteLat = pts.Select(p => p.lat).ToArray(); RouteLon = pts.Select(p => p.lon).ToArray(); RouteCum = cum; RouteFrom = from;
    }
    /// <summary>The point s nm along its path, and the bearing it's flying there.</summary>
    public (double lat, double lon, double hdg) PointAt(double s)
    {
        if (RouteCum is not { Length: > 1 } c)
        {
            var (la, lo) = NetworkModel.Intermediate(DepLat, DepLon, ArrLat, ArrLon, Math.Clamp(s / Math.Max(1e-6, DistNm), 0, 1));
            var h = DistNm - s < 0.5 ? Geo2.BearingDeg(DepLat, DepLon, ArrLat, ArrLon) : Geo2.BearingDeg(la, lo, ArrLat, ArrLon);
            return (la, lo, h);
        }
        s = Math.Clamp(s, 0, c[^1]);
        var i = Array.BinarySearch(c, s);
        if (i < 0) i = ~i;
        i = Math.Clamp(i, 1, c.Length - 1);
        var segLen = c[i] - c[i - 1];
        var f = segLen < 1e-6 ? 1 : (s - c[i - 1]) / segLen;
        var (lat, lon) = NetworkModel.Intermediate(RouteLat![i - 1], RouteLon![i - 1], RouteLat[i], RouteLon[i], f);
        return (lat, lon, Geo2.BearingDeg(RouteLat[i - 1], RouteLon[i - 1], RouteLat[i], RouteLon[i]));
    }
    public string Callsign => Operator + Flight.TrimStart('0');
    public string Id => $"net-{Reg}-{DepMin}";
    /// <summary>Gate to gate, minutes (a leg can run over the end of the week).</summary>
    public int BlockMin => ((ArrMin - DepMin) % 10080 + 10080) % 10080;
}

/// <summary>Where a network flight is right now.</summary>
public sealed record NetPos(NetLeg Leg, double Lat, double Lon, double AltFt, double GsKt, double Heading, string Phase,
    double CruiseFt, double FlownNm, double EtaMin);

/// <summary>
/// The whole-sky layer: every scheduled flight flies logically along the great circle between its airports,
/// timed by its schedule (taxi out, a climb, the cruise, a descent, taxi in). Nothing here is drawn in the sim;
/// it's what makes the rest of the sky busy and what hands real flights over to the airports near the user.
/// </summary>
public static class NetworkModel
{
    /// <summary>The wind aloft at a place and altitude (set by the traffic engine from the winds-aloft field).</summary>
    public static Func<double, double, double, Wind>? WindAloft;

    /// <summary>
    /// The average tailwind along a flight's route at its cruise level (five points along it), worked out once — so it
    /// cruises at its type's normal speed through the air and its ground speed follows the wind like everything else
    /// (user, 24 Nov: DAL8948 at 440 kt over the ground in still air caught up the user, at M.78 into an 80 kt headwind).
    /// </summary>
    public static double TailwindKt(NetLeg l)
    {
        // Once per flight: worked out again only when its last one is long over (the same leg next week, a newer
        // forecast), never during a flight, so nothing jumps along its route in the air.
        var now = Environment.TickCount64 / 60000.0;
        if (!double.IsNaN(l.WindKt) && now - l.WindAt <= l.BlockMin + 30) return l.WindKt;
        l.WindAt = now;
        // A new flight on this leg (next week's): no speed change, lag or vector carried over from the last one.
        l.SepLagMin = 0; l.SpdKt = 0; l.VecStart = double.NaN;
        if (WindAloft is not { } w) { l.WindKt = 0; return 0; }
        var cruise = CruiseFt(l);
        double sum = 0; var n = 0;
        try
        {
            for (var k = 1; k <= 5; k++)
            {
                var (la, lo, hdg) = l.PointAt(l.PathNm * (k - 0.5) / 5);
                sum += -w(la, lo, cruise).Headwind(hdg); n++;
            }
        }
        catch { }
        l.WindKt = n == 0 ? 0 : Math.Clamp(sum / n, -150, 200);
        return l.WindKt;
    }

    public const string Header = "LAIA-NET";

    /// <summary>The cruise level: by distance and aircraft, on the right semicircle (odd thousands eastbound, even westbound).</summary>
    public static double CruiseFt(NetLeg l)
    {
        var fam = Perf.For(l.Type).Family;
        var cap = fam switch { "turboprop" => 25000, "regional jet" => 37000, "large regional" => 37000, "widebody" or "heavy" => 39000, _ => 37000 };
        var want = fam == "turboprop" ? 6000 + l.DistNm * 80 : 8000 + l.DistNm * 100;
        var k = (int)Math.Round(Math.Clamp(want, 9000, cap) / 1000);
        var course = Geo2.BearingDeg(l.DepLat, l.DepLon, l.ArrLat, l.ArrLon);
        var east = course < 180;
        if (east && k % 2 == 0) k--;
        if (!east && k % 2 == 1) k--;
        return k * 1000;
    }

    /// <summary>A type's usual cruise speed over the ground in still air (knots).</summary>
    public static double CruiseGsKt(string type) => Perf.For(type).Family switch
    {
        "turboprop" => 290, "regional jet" => 420, "large regional" => 440, "757" => 460, "widebody" => 480, "heavy" => 490, _ => 450,
    };

    /// <summary>The flight's position at this minute of the week, or null if it isn't flying (or taxiing) now.</summary>
    /// <summary>When the flight lands (week minute), without metering.</summary>
    public static double LandingMin(NetLeg l) => l.DepMin + l.BlockMin - Math.Min(6, l.BlockMin * 0.1);

    public static NetPos? At(NetLeg l, double weekMin, bool includeGround = false)
    {
        if (!l.Located || l.DistNm < 15) return null;
        var clock = weekMin;
        weekMin -= l.MeterMin + l.SepLagMin;
        var block = l.BlockMin;
        if (block < 10) return null;
        var e = ((weekMin - l.DepMin) % 10080 + 10080) % 10080;
        if (e > block) return null;
        // Taxi times: about 12 out and 6 in, but never more than 30% of a short flight.
        var taxiOut = Math.Min(12, block * 0.2);
        var taxiIn = Math.Min(6, block * 0.1);
        var t = block - taxiOut - taxiIn;           // airborne minutes
        if (e < taxiOut || e > block - taxiIn)
        {
            if (!includeGround) return null;
            var atDep = e < taxiOut;
            return new NetPos(l, atDep ? l.DepLat : l.ArrLat, atDep ? l.DepLon : l.ArrLon, atDep ? l.DepElev : l.ArrElev, 0, 0,
                atDep ? "taxiing out" : "taxiing in", 0, atDep ? 0 : l.PathNm, atDep ? block - taxiIn - e : 0);
        }
        var cruise = CruiseFt(l);
        var jet = Perf.For(l.Type).Family != "turboprop";
        // Airline schedules are padded (and real flights follow airways, wait at the runway and in holds), so timing a
        // flight's whole block on the straight line could have a jet cruising at 300 kt (user, 17 Nov: "why UAL2458 is
        // flying so slow"). It flies its type's normal speed (8% slack) instead; the spare time is spent on the ground
        // before take-off, so it still lands when the schedule says.
        var nominal = Math.Max(0.6 * CruiseGsKt(l.Type), CruiseGsKt(l.Type) + TailwindKt(l)) / 60;   // nm per minute, over the ground
        for (var i = 0; i < 2; i++)
        {
            var c0 = Math.Min((cruise - l.DepElev) / (jet ? 2000 : 1300), 0.45 * t);
            var d0 = Math.Min((cruise - l.ArrElev) / (jet ? 2200 : 1400), 0.45 * t);
            var tMin = 1.08 * (l.PathNm / nominal + 0.3 * (c0 + d0));
            if (t <= tMin + 0.01) break;
            taxiOut += t - tMin; t = tMin;
        }
        if (e < taxiOut)
        {
            if (!includeGround) return null;
            return new NetPos(l, l.DepLat, l.DepLon, l.DepElev, 0, 0, "taxiing out", 0, 0, block - taxiIn - e);
        }
        var tau = e - taxiOut;
        var c = Math.Min((cruise - l.DepElev) / (jet ? 2000 : 1300), 0.45 * t);
        var d = Math.Min((cruise - l.ArrElev) / (jet ? 2200 : 1400), 0.45 * t);
        // Speed ramps from 40% to 100% in the climb (and back down in the descent): 70% on average.
        var path = l.PathNm;
        var v = path / (0.7 * c + 0.7 * d + (t - c - d)); // nm per minute in the cruise
        double s, alt, gs; string phase;
        if (tau < c)
        {
            s = v * (0.4 * tau + 0.3 * tau * tau / c);
            var x = tau / c;
            alt = l.DepElev + (cruise - l.DepElev) * (1 - Math.Pow(1 - x, 1.6));
            gs = v * (0.4 + 0.6 * x); phase = "climbing";
        }
        else if (tau <= t - d)
        {
            s = v * 0.7 * c + v * (tau - c);
            alt = cruise; gs = v; phase = "cruising";
        }
        else
        {
            var y = tau - (t - d); // minutes into the descent
            s = v * 0.7 * c + v * (t - c - d) + v * (y - 0.3 * y * y / d);
            alt = cruise - (cruise - l.ArrElev) * (y / d);
            gs = v * (1 - 0.6 * y / d); phase = "descending";
        }
        s = Math.Clamp(s, 0, path);
        // Coming down, never above an airliner's 3:1 path to the destination (about 300 ft per nm still to go, plus a
        // little to slow down): the timed descent alone slows near the end and left arrivals thousands of feet high
        // when the airport took them over (WJA1272 at 26,490 ft 52 nm out).
        // The cruise ends where that path meets the cruise level, so the top of descent is where an airliner's would be.
        var (lat, lon, hdg) = l.PointAt(s);
        // On a vector: off to the side of its route and back.
        if (!double.IsNaN(l.VecStart) && l.VecOutMin > 0)
        {
            var dt = ((clock - l.VecStart) % 10080 + 15120) % 10080 - 5040;
            if (dt >= 0 && dt <= 2 * l.VecOutMin)
            {
                var outT = dt <= l.VecOutMin ? dt : 2 * l.VecOutMin - dt;
                var side = v * Math.Sin(Math.Abs(l.VecAngle) * Math.PI / 180) * outT;   // nm off the route
                var perp = hdg + (l.VecAngle >= 0 ? 90 : -90);
                (lat, lon) = Geo2.Offset(lat, lon, perp, side);
                hdg = Geo2.Norm(hdg + (dt <= l.VecOutMin ? l.VecAngle : -l.VecAngle));
            }
        }
        // Miles to go: the filed route's, but no more than the way it will really be flown from here (user, 19 Nov,
        // AAL2867 taken over 54 nm from O'Hare at 32,450 ft: its filed route still had about 100 nm to run, while from
        // the hand-over it flies direct to its STAR). A little over the straight line, for the STAR and the vectors.
        var toGo = Math.Min(path - s, Geo2.DistanceNm(lat, lon, l.ArrLat, l.ArrLon) * 1.3 + 10);
        var cap = l.ArrElev + toGo * 300 + 1500;
        if (tau >= c) { alt = Math.Min(cruise, cap); phase = alt < cruise - 50 ? "descending" : "cruising"; }
        else alt = Math.Min(alt, cap);
        return new NetPos(l, lat, lon, Math.Round(alt / 10) * 10, gs * 60, hdg, phase, cruise, s, t - tau + taxiIn);
    }

    /// <summary>The point a fraction f of the way along the great circle.</summary>
    public static (double lat, double lon) Intermediate(double lat1, double lon1, double lat2, double lon2, double f)
    {
        const double R = Math.PI / 180;
        double p1 = lat1 * R, l1 = lon1 * R, p2 = lat2 * R, l2 = lon2 * R;
        var dd = Geo2.DistanceNm(lat1, lon1, lat2, lon2) / 3440.065;
        if (dd < 1e-9) return (lat1, lon1);
        var a = Math.Sin((1 - f) * dd) / Math.Sin(dd);
        var b = Math.Sin(f * dd) / Math.Sin(dd);
        var x = a * Math.Cos(p1) * Math.Cos(l1) + b * Math.Cos(p2) * Math.Cos(l2);
        var y = a * Math.Cos(p1) * Math.Sin(l1) + b * Math.Cos(p2) * Math.Sin(l2);
        var z = a * Math.Sin(p1) + b * Math.Sin(p2);
        return (Math.Atan2(z, Math.Sqrt(x * x + y * y)) / R, Math.Atan2(y, x) / R);
    }

    /// <summary>Points along the great circle from a fraction f0 to f1 (for drawing a route on the map).</summary>
    public static List<double[]> Line(NetLeg l, double f0, double f1, int maxPoints = 64)
    {
        var path = l.PathNm;
        var n = Math.Clamp((int)(path * Math.Abs(f1 - f0) / 25), 2, maxPoints);
        var pts = new List<double[]>();
        double prev = double.NaN;
        void Add(double s0) { var (la, lo, _) = l.PointAt(s0); pts.Add(new[] { Math.Round(la, 4), Math.Round(lo, 4) }); prev = s0; }
        for (var i = 0; i <= n; i++)
        {
            var sNow = path * (f0 + (f1 - f0) * i / n);
            // Keep the route's corners.
            if (l.RouteCum is { } c && !double.IsNaN(prev))
                foreach (var cs in c) if ((cs > prev && cs < sNow) || (cs < prev && cs > sNow)) Add(cs);
            Add(sNow);
        }
        return pts;
    }

    // ------------------------------------------------------------------ the file LAIA Sim sends (tab separated, gzipped)

    /// <summary>Reads the schedule file: a header line, then one flight per line.</summary>
    public static (List<NetLeg> legs, DateTime readUtc) Parse(Stream gz)
    {
        using var z = new GZipStream(gz, CompressionMode.Decompress, leaveOpen: true);
        using var r = new StreamReader(z, Encoding.UTF8);
        var legs = new List<NetLeg>();
        var read = DateTime.MinValue;
        var head = r.ReadLine();
        if (head is null || !head.StartsWith(Header)) return (legs, read);
        var hp = head.Split('\t');
        if (hp.Length > 2) DateTime.TryParse(hp[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out read);
        var pool = new Dictionary<string, string>(); // the same strings repeat thousands of times
        string P(string s) => pool.TryGetValue(s, out var x) ? x : pool[s] = s;
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            var c = line.Split('\t');
            if (c.Length < 10 || !int.TryParse(c[7], out var dm) || !int.TryParse(c[8], out var am)) continue;
            legs.Add(new NetLeg
            {
                Airline = P(c[0]), Operator = P(c[1]), Type = P(c[2]), Reg = c[3], Flight = c[4], Dep = P(c[5]), Arr = P(c[6]),
                DepMin = ((dm % 10080) + 10080) % 10080, ArrMin = ((am % 10080) + 10080) % 10080, Title = P(c[9]),
            });
        }
        return (legs, read);
    }

    public static void Write(Stream gz, IEnumerable<NetLeg> legs, DateTime readUtc)
    {
        using var z = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true);
        using var w = new StreamWriter(z, new UTF8Encoding(false));
        w.Write($"{Header}\t1\t{readUtc:O}\n");
        static string C(string s) => s.Replace('\t', ' ').Replace('\n', ' ');
        foreach (var l in legs)
            w.Write($"{C(l.Airline)}\t{C(l.Operator)}\t{C(l.Type)}\t{C(l.Reg)}\t{C(l.Flight)}\t{C(l.Dep)}\t{C(l.Arr)}\t{l.DepMin}\t{l.ArrMin}\t{C(l.Title)}\n");
    }

    /// <summary>Fills in the airports' positions; legs between airports we can't place are left out of the sky.</summary>
    public static int Locate(IEnumerable<NetLeg> legs, Func<string, (double lat, double lon, double elevFt)?> airport)
    {
        var n = 0;
        foreach (var l in legs)
        {
            var a = airport(l.Dep); var b = airport(l.Arr);
            l.Located = a is not null && b is not null && l.Dep != l.Arr;
            if (!l.Located) continue;
            (l.DepLat, l.DepLon, l.DepElev) = a!.Value;
            (l.ArrLat, l.ArrLon, l.ArrElev) = b!.Value;
            l.DistNm = Geo2.DistanceNm(l.DepLat, l.DepLon, l.ArrLat, l.ArrLon);
            n++;
        }
        return n;
    }
}
