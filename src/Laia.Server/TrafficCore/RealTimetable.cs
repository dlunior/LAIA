using Laia.Common.Airports;

namespace Laia.Server.TrafficCore;

/// <summary>
/// LAIA's real timetable (user, 24 Nov: "how do we get more accurate schedules, at minimal cost?" — "add that"): the
/// flights real aircraft flew in the last weeks, from the LAIA Collector's ADS-B days (TraceLegs), as a week of
/// schedule legs like the AIG timetable's. For each weekday the most recent occurrence of each flight (callsign) is
/// kept, its gate times worked back from the runway times (push-back 12 minutes before take-off, at the gate 6 after
/// landing). The AIG data fills in what ADS-B can't say: an end the receivers didn't see (from the AIG flight with the
/// same callsign), and the exact model, livery and brand when the registration is one AIG has. A flight is kept only if
/// an installed livery fits it — AIG first, then FSLTL (LAIA Sim's catalog); otherwise it's skipped (user: "AIG
/// first, skip the flight"). The timetable is used where it's good: at an airport where it has at least half as many
/// flights a week as the AIG timetable (and 14 or more), the AIG timetable elsewhere (user: "everywhere it's good").
/// </summary>
public static class RealTimetable
{
    /// <summary>What the AIG timetable knows about an airframe and a flight.</summary>
    public sealed record AigInfo(string Brand, string Title, string Operator);

    public sealed class Result
    {
        public List<ScheduleLeg> Legs = new();
        public Dictionary<string, int> WeeklyAt = new(StringComparer.OrdinalIgnoreCase);
        public int Seen, NoEnd, NoLivery, Cargo, Kept;
        public string Summary => $"{Kept:N0} flights a week from {Seen:N0} seen ({NoEnd:N0} with an end not seen, {NoLivery:N0} with no installed livery, {Cargo:N0} cargo left out)";
    }

    /// <summary>Minutes from Monday 00:00 UTC (the AIG week).</summary>
    public static int WeekMinute(double unix)
    {
        var t = DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime;
        var dow = ((int)t.DayOfWeek + 6) % 7;
        return dow * 1440 + t.Hour * 60 + t.Minute;
    }

    /// <param name="days">Each day's flights (any order).</param>
    /// <param name="hasLivery">An installed livery for this airline and type (exact, or the same family).</param>
    /// <param name="byReg">AIG airframes by registration.</param>
    /// <param name="routeByCallsign">AIG flights by callsign: their two ends.</param>
    /// <param name="locate">An airport's position (for a missing time from the distance).</param>
    public static Result Build(IEnumerable<List<TraceLegs.Leg>> days, Func<string, string, bool> hasLivery,
        IReadOnlyDictionary<string, AigInfo> byReg, IReadOnlyDictionary<string, (string dep, string arr)> routeByCallsign,
        Func<string, (double lat, double lon)?> locate)
    {
        var res = new Result();
        // Most recent first, so the first one kept for a weekday is the newest.
        var all = days.SelectMany(d => d).Where(l => !double.IsNaN(l.DepT) || !double.IsNaN(l.ArrT))
            .OrderByDescending(l => double.IsNaN(l.DepT) ? l.ArrT : l.DepT).ToList();
        var seen = new HashSet<(string cs, int dow)>();
        foreach (var l in all)
        {
            res.Seen++;
            var op = l.Callsign.Length >= 4 ? l.Callsign[..3] : "";
            var flight = l.Callsign.Length >= 4 ? l.Callsign[3..] : "";
            if (op.Length != 3 || flight.Length == 0) continue;
            if (TrafficWorld.IsCargo(op)) { res.Cargo++; continue; }
            string dep = l.Dep, arr = l.Arr;
            if ((dep.Length == 0 || arr.Length == 0) && routeByCallsign.TryGetValue(l.Callsign, out var rt))
            {
                if (dep.Length == 0 && rt.arr.Equals(arr, StringComparison.OrdinalIgnoreCase)) dep = rt.dep;
                if (arr.Length == 0 && rt.dep.Equals(dep, StringComparison.OrdinalIgnoreCase)) arr = rt.arr;
            }
            if (dep.Length == 0 || arr.Length == 0 || dep == arr) { res.NoEnd++; continue; }
            double depT = l.DepT, arrT = l.ArrT;
            if (double.IsNaN(depT) || double.IsNaN(arrT))
            {
                if (locate(dep) is not { } a || locate(arr) is not { } b) { res.NoEnd++; continue; }
                var block = (Geo2.DistanceNm(a.lat, a.lon, b.lat, b.lon) / 440 + 0.35) * 3600;
                if (double.IsNaN(depT)) depT = arrT - block; else arrT = depT + block;
            }
            var depMin = WeekMinute(depT - 12 * 60);
            var key = (l.Callsign, depMin / 1440);
            if (!seen.Add(key)) continue;
            var title = ""; var brand = op;
            if (l.Reg.Length > 0 && byReg.TryGetValue(l.Reg, out var ai)) { title = ai.Title; if (ai.Brand.Length > 0) brand = ai.Brand; }
            if (title.Length == 0 && !hasLivery(brand, l.Type) && !hasLivery(op, l.Type)) { res.NoLivery++; seen.Remove(key); continue; }
            var leg = new ScheduleLeg
            {
                Airline = brand, Operator = op, Title = title, Type = l.Type, Reg = l.Reg, Flight = flight,
                Dep = dep, Arr = arr, DepMin = depMin, ArrMin = WeekMinute(arrT + 6 * 60),
            };
            res.Legs.Add(leg);
            res.WeeklyAt[dep] = res.WeeklyAt.GetValueOrDefault(dep) + 1;
            res.WeeklyAt[arr] = res.WeeklyAt.GetValueOrDefault(arr) + 1;
            res.Kept++;
        }
        return res;
    }

    /// <summary>The airports where the real timetable is good enough to use: half the AIG timetable's flights a week or
    /// more there, and at least 14.</summary>
    public static HashSet<string> GoodAirports(Result r, Func<string, int> aigWeekly) =>
        r.WeeklyAt.Where(kv => kv.Value >= 14 && kv.Value >= 0.5 * aigWeekly(kv.Key)).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Families of types that share liveries and models (a 737-800 livery for a 737 MAX 8 flight).</summary>
    public static string Family(string t) => t switch
    {
        "B736" or "B737" or "B738" or "B739" or "B37M" or "B38M" or "B39M" or "B3XM" => "B737",
        "A318" or "A319" or "A320" or "A321" or "A19N" or "A20N" or "A21N" => "A320",
        "BCS1" or "BCS3" or "A220" or "A221" or "A223" => "A220",
        "B752" or "B753" => "B757",
        "B762" or "B763" or "B764" => "B767",
        "B772" or "B77L" or "B77W" or "B773" => "B777",
        "B788" or "B789" or "B78X" => "B787",
        "A332" or "A333" or "A338" or "A339" => "A330",
        "A359" or "A35K" => "A350",
        "E170" or "E75L" or "E75S" or "E175" => "E175",
        "E190" or "E195" or "E290" or "E295" => "E190",
        "CRJ7" or "CRJ9" or "CRJX" => "CRJ900",
        _ => t,
    };
}
