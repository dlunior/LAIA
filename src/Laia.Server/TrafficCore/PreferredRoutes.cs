namespace Laia.Server.TrafficCore;

/// <summary>
/// The FAA's Preferred Routes database (fly.faa.gov prefroutes_db.csv, free): the routes the FAA wants flown between
/// US airport pairs, which airlines mostly file. Each route string ends with the arrival (STAR) and starts with the
/// departure (SID), and many rows say which flow they're for ("MCO LANDING NORTH", "MCO LANDING SOUTH") or that
/// they're the oceanic/weather option ("WATER", "DEEP OCEAN"). LAIA uses it to give each flight the STAR and SID it
/// would really file, by origin, destination and the flow in use.
/// </summary>
public sealed class PreferredRoutes
{
    public sealed record Row(string Orig, string Dest, string[] Route, string Type, string Aircraft, string Direction);

    private readonly Dictionary<(string, string), List<Row>> _byPair = new();
    public int Count { get; private set; }

    /// <summary>"KMCO" → "MCO" (the FAA uses 3-letter idents for US airports); others stay as they are.</summary>
    public static string Faa(string icao) => icao.Length == 4 && icao[0] == 'K' ? icao[1..] : icao;

    public static PreferredRoutes Parse(IEnumerable<string> csvLines)
    {
        var db = new PreferredRoutes();
        string[]? head = null;
        foreach (var line in csvLines)
        {
            var c = Split(line);
            if (head is null) { head = c; continue; }
            int I(string name) => Array.FindIndex(head, h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            string F(string name) { var i = I(name); return i >= 0 && i < c.Length ? c[i].Trim() : ""; }
            var orig = F("Orig").ToUpperInvariant(); var dest = F("Dest").ToUpperInvariant();
            if (orig.Length == 0 || dest.Length == 0) continue;
            var route = F("Route String").ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var row = new Row(orig, dest, route, F("Type").ToUpperInvariant(), F("Aircraft").ToUpperInvariant(), F("Direction").ToUpperInvariant());
            if (!db._byPair.TryGetValue((orig, dest), out var l)) db._byPair[(orig, dest)] = l = new();
            l.Add(row);
            db.Count++;
        }
        return db;
    }

    private static string[] Split(string line)
    {
        var res = new List<string>(); var cur = new System.Text.StringBuilder(); var q = false;
        foreach (var ch in line)
        {
            if (ch == '"') { q = !q; continue; }
            if (ch == ',' && !q) { res.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        res.Add(cur.ToString());
        return res.ToArray();
    }

    /// <summary>Does a row's direction note fit the flow ("LANDING NORTH" with runway 36L in use)? Unmarked rows fit any flow.</summary>
    private static int FlowFit(Row r, double landingHeading)
    {
        var d = r.Direction;
        if (d.Contains("WATER") || d.Contains("OCEAN")) return 1;               // the oceanic/weather option: second choice
        foreach (var (word, hdg) in new[] { ("NORTH", 0.0), ("EAST", 90.0), ("SOUTH", 180.0), ("WEST", 270.0) })
            if (d.Contains("LANDING " + word) || d.Contains("DEPARTING " + word) || d.Contains(word + " FLOW") || d.Contains(word + " OPS"))
                return Math.Abs(Geo2.Diff(landingHeading, hdg)) <= 50 ? 0 : 3; // the other flow: not for now
        return 0;
    }

    private static int AircraftFit(Row r, bool jet)
    {
        var a = r.Aircraft;
        if (a.Length == 0) return 0;
        if (jet) return a.Contains("JET") ? 0 : a.Contains("TURBOPROP") || a.Contains("PROP") ? 2 : 0;
        return a.Contains("TURBOPROP") || a.Contains("PROP") ? 0 : a.Contains("JET") ? 2 : 0;
    }

    /// <summary>The rows for a pair, best first for this flow and aircraft (high-altitude routes for jets).</summary>
    private List<Row> Rows(string orig, string dest, double landingHeading, bool jet)
    {
        if (!_byPair.TryGetValue((Faa(orig), Faa(dest)), out var l)) return new();
        return l.Where(r => FlowFit(r, landingHeading) < 3)
            .OrderBy(r => FlowFit(r, landingHeading)).ThenBy(r => AircraftFit(r, jet))
            .ThenBy(r => r.Type == (jet ? "H" : "L") || r.Type == "TEC" ? 0 : 1).ToList();
    }

    /// <summary>
    /// For an origin that isn't in the database (most international ones): the STAR most used by the database's
    /// routes from airports in about the same direction (within 25°), for this flow — what airlines from that part of
    /// the world file. Closer directions count more.
    /// </summary>
    public (string star, string? transition)? ArrivalFromDirection(string dest, double bearing, double landingHeading,
        Func<string, (double bearing, double nm)?> where, Func<string, bool> isStar)
    {
        var tally = new Dictionary<string, (double score, string? tr, double best)>();
        foreach (var ((o, d), rows) in _byPair)
        {
            if (d != Faa(dest)) continue;
            if (where(o) is not { } w || w.nm < 50) continue;
            var diff = Math.Abs(Geo2.Diff(w.bearing, bearing));
            if (diff > 25) continue;
            foreach (var r in rows.Where(r => FlowFit(r, landingHeading) == 0))
            {
                var toks = r.Route;
                for (var i = toks.Length - 1; i >= 1; i--)
                {
                    if (!isStar(toks[i])) continue;
                    var key = toks[i];
                    var cur = tally.GetValueOrDefault(key, (0, null, 99));
                    tally[key] = (cur.score + (25 - diff), diff < cur.best ? toks[i - 1] : cur.tr, Math.Min(cur.best, diff));
                    break;
                }
            }
        }
        if (tally.Count == 0) return null;
        var top = tally.OrderByDescending(kv => kv.Value.score).First();
        return (top.Key, top.Value.tr);
    }

    /// <summary>The same for departures to a destination not in the database: the SID most used towards that direction.</summary>
    public (string sid, string? transition)? DepartureToDirection(string orig, double bearing, double landingHeading,
        Func<string, (double bearing, double nm)?> where, Func<string, bool> isSid)
    {
        var tally = new Dictionary<string, (double score, string? tr, double best)>();
        foreach (var ((o, d), rows) in _byPair)
        {
            if (o != Faa(orig)) continue;
            if (where(d) is not { } w || w.nm < 50) continue;
            var diff = Math.Abs(Geo2.Diff(w.bearing, bearing));
            if (diff > 25) continue;
            foreach (var r in rows.Where(r => FlowFit(r, landingHeading) == 0))
            {
                var toks = r.Route;
                for (var i = 0; i < toks.Length - 1; i++)
                {
                    if (!isSid(toks[i])) continue;
                    var cur = tally.GetValueOrDefault(toks[i], (0, null, 99));
                    tally[toks[i]] = (cur.score + (25 - diff), diff < cur.best ? toks[i + 1] : cur.tr, Math.Min(cur.best, diff));
                    break;
                }
            }
        }
        if (tally.Count == 0) return null;
        var top = tally.OrderByDescending(kv => kv.Value.score).First();
        return (top.Key, top.Value.tr);
    }

    /// <summary>
    /// The arrival a flight from this origin would file: the STAR (its name as in the route, e.g. SNFLD3) and the fix
    /// before it (its entry transition, e.g. LPERD). Null when the pair isn't in the database or no STAR is named.
    /// </summary>
    public (string star, string? transition, Row row)? Arrival(string orig, string dest, double landingHeading, bool jet, Func<string, bool> isStar)
    {
        foreach (var r in Rows(orig, dest, landingHeading, jet))
        {
            var toks = r.Route;
            for (var i = toks.Length - 1; i >= 1; i--)
                if (isStar(toks[i])) return (toks[i], i >= 1 ? toks[i - 1] : null, r);
        }
        return null;
    }

    /// <summary>The departure a flight to this destination would file: the SID (e.g. SMLTZ3) and the fix after it (its exit).</summary>
    public (string sid, string? transition, Row row)? Departure(string orig, string dest, double landingHeading, bool jet, Func<string, bool> isSid)
    {
        foreach (var r in Rows(orig, dest, landingHeading, jet))
        {
            var toks = r.Route;
            for (var i = 0; i < toks.Length - 1; i++)
                if (isSid(toks[i])) return (toks[i], i + 1 < toks.Length ? toks[i + 1] : null, r);
        }
        return null;
    }
}
