using System.Text.Json;

namespace Laia.Server.TrafficCore;

/// <summary>
/// Real flights from one aircraft's day of ADS-B (a readsb trace file): each time it took off from or landed at an
/// airport with airline service, as legs — callsign, type, registration, from, to, take-off and landing times (Unix
/// seconds). The LAIA Collector keeps them day by day, and LAIA's real timetable is built from them (user, 24 Nov:
/// "how do we get more accurate schedules, at minimal cost?" — the collector's own data, free).
/// </summary>
public static class TraceLegs
{
    public sealed record Apt(string Icao, double Lat, double Lon, double ElevFt);

    /// <summary>One flight: an end not seen (outside the airports watched, or lost by the receivers) is "".</summary>
    public sealed record Leg(string Callsign, string Type, string Reg, string Dep, string Arr, double DepT, double ArrT);

    private readonly record struct Pt(double T, double Lat, double Lon, double? Alt, string? Cs);

    /// <summary>
    /// The legs in a trace. An airborne stretch (split where it's on the ground, or seen nowhere for 90 minutes) starts
    /// with a take-off where its first point is within 8 nm of an airport and under 3,000 ft above it, and ends with a
    /// landing where its last is within 8 nm and under 2,500 ft — the receivers often lose an arrival a few hundred
    /// feet up, or see a departure only once it's climbing, so the time is worked back (or on) from there at a typical
    /// climb (2,000 ft/min) or descent (800 ft/min). <paramref name="near"/> gives the airports to look at for a point.
    /// </summary>
    public static List<Leg> Extract(byte[] json, Func<double, double, IEnumerable<Apt>> near)
    {
        var res = new List<Leg>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.TryGetProperty("t", out var tt) && tt.ValueKind == JsonValueKind.String ? (tt.GetString() ?? "").Trim().ToUpperInvariant() : "";
        var reg = root.TryGetProperty("r", out var rr) && rr.ValueKind == JsonValueKind.String ? (rr.GetString() ?? "").Trim().ToUpperInvariant() : "";
        if (!root.TryGetProperty("trace", out var trace) || trace.ValueKind != JsonValueKind.Array) return res;
        var t0 = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetDouble() : 0;
        var pts = new List<Pt>(trace.GetArrayLength());
        string? cs = null;
        foreach (var p in trace.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 4) continue;
            if (p.GetArrayLength() >= 9 && p[8].ValueKind == JsonValueKind.Object && p[8].TryGetProperty("flight", out var fl) && fl.ValueKind == JsonValueKind.String)
            {
                var c = (fl.GetString() ?? "").Trim().ToUpperInvariant();
                if (c.Length > 0) cs = c;
            }
            double? alt = p[3].ValueKind == JsonValueKind.Number ? p[3].GetDouble() : p[3].ValueKind == JsonValueKind.String ? null : double.NaN;
            if (alt is double a && double.IsNaN(a)) continue;   // no altitude at all: unusable
            pts.Add(new Pt(t0 + p[0].GetDouble(), p[1].GetDouble(), p[2].GetDouble(), alt, cs));
        }
        return FromPoints(pts.Select(p => (p.T, p.Lat, p.Lon, p.Alt, p.Cs)).ToList(), type, reg, near);
    }

    /// <summary>As <see cref="Extract"/>, from points already read (time, position, altitude — null on the ground — and
    /// the callsign then).</summary>
    public static List<Leg> FromPoints(List<(double T, double Lat, double Lon, double? Alt, string? Cs)> pts, string type, string reg,
        Func<double, double, IEnumerable<Apt>> near)
    {
        var res = new List<Leg>();
        var i = 0;
        while (i < pts.Count)
        {
            // Skip ground points; remember the last one before this airborne stretch.
            (double T, double Lat, double Lon, double? Alt, string? Cs)? groundBefore = null;
            while (i < pts.Count && pts[i].Alt is null) { groundBefore = pts[i]; i++; }
            if (i >= pts.Count) break;
            var start = i;
            while (i + 1 < pts.Count && pts[i + 1].Alt is not null && pts[i + 1].T - pts[i].T < 90 * 60) i++;
            var end = i;
            i++;
            (double T, double Lat, double Lon, double? Alt, string? Cs)? groundAfter = i < pts.Count && pts[i].Alt is null && pts[i].T - pts[end].T < 15 * 60 ? pts[i] : null;
            if (end - start < 4 || pts[end].T - pts[start].T < 10 * 60) continue;
            var callsign = pts.Skip(start).Take(end - start + 1).Select(p => p.Cs).Where(c => c is { Length: > 0 })
                .GroupBy(c => c!).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
            if (!AdsbTrace.IsAirlineCallsign(callsign)) continue;
            var first = pts[start]; var last = pts[end];
            string dep = "", arr = "";
            double depT = double.NaN, arrT = double.NaN;
            if (Nearest(first.Lat, first.Lon, first.Alt!.Value, 3000, near) is { } da)
            {
                dep = da.Icao;
                depT = groundBefore is { } gb && gb.T > first.T - 10 * 60 && Geo2.DistanceNm(gb.Lat, gb.Lon, da.Lat, da.Lon) < 4 ? gb.T
                    : first.T - Math.Max(0, first.Alt!.Value - da.ElevFt) / 2000 * 60;
            }
            if (Nearest(last.Lat, last.Lon, last.Alt!.Value, 2500, near) is { } aa)
            {
                arr = aa.Icao;
                arrT = groundAfter is { } ga && Geo2.DistanceNm(ga.Lat, ga.Lon, aa.Lat, aa.Lon) < 4 ? ga.T
                    : last.T + Math.Max(0, last.Alt!.Value - aa.ElevFt) / 800 * 60;
            }
            if (dep.Length == 0 && arr.Length == 0) continue;
            if (dep.Length > 0 && dep == arr) continue;         // a go-around or a circuit, not a flight
            res.Add(new Leg(callsign, type, reg, dep, arr, depT, arrT));
        }
        return res;
    }

    private static Apt? Nearest(double lat, double lon, double altFt, double maxAbove, Func<double, double, IEnumerable<Apt>> near)
    {
        Apt? best = null; var bestD = 8.0;
        foreach (var a in near(lat, lon))
        {
            if (altFt - a.ElevFt > maxAbove) continue;
            var d = Geo2.DistanceNm(lat, lon, a.Lat, a.Lon);
            if (d < bestD) { bestD = d; best = a; }
        }
        return best;
    }

    // ---------------------------------------------------------------- stored form: [[cs, type, reg, dep, arr, depT, arrT], …]

    public static void Write(Utf8JsonWriter w, Leg l)
    {
        w.WriteStartArray();
        w.WriteStringValue(l.Callsign); w.WriteStringValue(l.Type); w.WriteStringValue(l.Reg); w.WriteStringValue(l.Dep); w.WriteStringValue(l.Arr);
        if (double.IsNaN(l.DepT)) w.WriteNullValue(); else w.WriteNumberValue(Math.Round(l.DepT));
        if (double.IsNaN(l.ArrT)) w.WriteNullValue(); else w.WriteNumberValue(Math.Round(l.ArrT));
        w.WriteEndArray();
    }

    public static List<Leg> Read(byte[] json)
    {
        var res = new List<Leg>();
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.GetArrayLength() < 7) continue;
            string S(int k) => e[k].GetString() ?? "";
            double N(int k) => e[k].ValueKind == JsonValueKind.Number ? e[k].GetDouble() : double.NaN;
            res.Add(new Leg(S(0), S(1), S(2), S(3), S(4), N(5), N(6)));
        }
        return res;
    }
}
