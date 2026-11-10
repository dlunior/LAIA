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

    /// <summary>The flight's position at this minute of the week, or null if it isn't flying (or taxiing) now.</summary>
    public static NetPos? At(NetLeg l, double weekMin, bool includeGround = false)
    {
        if (!l.Located || l.DistNm < 15) return null;
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
                atDep ? "taxiing out" : "taxiing in", 0, atDep ? 0 : l.DistNm, atDep ? block - taxiIn - e : 0);
        }
        var tau = e - taxiOut;
        var cruise = CruiseFt(l);
        var jet = Perf.For(l.Type).Family != "turboprop";
        var c = Math.Min((cruise - l.DepElev) / (jet ? 2000 : 1300), 0.45 * t);
        var d = Math.Min((cruise - l.ArrElev) / (jet ? 2200 : 1400), 0.45 * t);
        // Speed ramps from 40% to 100% in the climb (and back down in the descent): 70% on average.
        var v = l.DistNm / (0.7 * c + 0.7 * d + (t - c - d)); // nm per minute in the cruise
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
        s = Math.Clamp(s, 0, l.DistNm);
        var (lat, lon) = Intermediate(l.DepLat, l.DepLon, l.ArrLat, l.ArrLon, s / l.DistNm);
        var hdg = l.DistNm - s < 0.5 ? Geo2.BearingDeg(l.DepLat, l.DepLon, l.ArrLat, l.ArrLon) : Geo2.BearingDeg(lat, lon, l.ArrLat, l.ArrLon);
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
        var n = Math.Clamp((int)(l.DistNm * Math.Abs(f1 - f0) / 25), 2, maxPoints);
        var pts = new List<double[]>();
        for (var i = 0; i <= n; i++)
        {
            var (la, lo) = Intermediate(l.DepLat, l.DepLon, l.ArrLat, l.ArrLon, f0 + (f1 - f0) * i / n);
            pts.Add(new[] { Math.Round(la, 4), Math.Round(lo, 4) });
        }
        return pts;
    }

    // ------------------------------------------------------------------ the file LAIA Sim sends (tab separated, gzipped)

    /// <summary>Reads the schedule file: a header line, then one flight per line.</summary>
    public static (List<NetLeg> legs, DateTime readUtc) Parse(Stream gz)
    {
        using var z = new GZipStream(gz, CompressionMode.Decompress);
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
