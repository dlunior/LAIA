using System.Text.RegularExpressions;

namespace Laia.Server.TrafficCore;

/// <summary>
/// Which gates the airlines really use at an airport, learned from real flight data (AirLabs gives the
/// terminal and gate of each flight). Kept per airport and added to every time new data comes in, so the
/// picture gets better the more you fly there. When present it overrides the gate assignments in the scenery.
/// </summary>
public sealed class RealGates
{
    public string Airport { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    /// <summary>Changes whenever new data is added (so the traffic recomputes its gate plan).</summary>
    public int Version { get; set; }
    /// <summary>Airline ICAO → gate ("terminal|gate", e.g. "C|61") → how many flights were seen there.</summary>
    public Dictionary<string, Dictionary<string, int>> ByAirline { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Today's flights: callsign (e.g. DAL2131) → gate, so a known flight goes to its exact gate.</summary>
    public Dictionary<string, string> ByFlight { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Flights already counted (callsign + date), so the same flight isn't counted twice.</summary>
    public List<string> Seen { get; set; } = new();

    public static string Key(string terminal, string gate) => $"{(terminal ?? "").Trim().ToUpperInvariant()}|{(gate ?? "").Trim().ToUpperInvariant()}";

    public static (string terminal, string gate) Split(string key)
    {
        var i = key.IndexOf('|');
        return i < 0 ? ("", key) : (key[..i], key[(i + 1)..]);
    }

    /// <summary>Records one real flight's gate. Returns false if it was already counted.</summary>
    public bool Add(string airline, string callsign, string date, string terminal, string gate)
    {
        if (string.IsNullOrWhiteSpace(gate) || airline.Length != 3) return false;
        var key = Key(terminal, gate);
        if (callsign.Length > 0) ByFlight[callsign] = key;
        var seenKey = callsign.Length > 0 ? $"{callsign}|{date}|{airline}" : $"{date}|{airline}|{key}";
        if (Seen.Contains(seenKey)) return false;
        Seen.Add(seenKey);
        if (Seen.Count > 6000) Seen.RemoveRange(0, Seen.Count - 6000);
        if (!ByAirline.TryGetValue(airline, out var gates)) ByAirline[airline] = gates = new(StringComparer.OrdinalIgnoreCase);
        gates[key] = gates.GetValueOrDefault(key) + 1;
        return true;
    }

    private static readonly Regex GateRx = new(@"^\s*([A-Za-z]*)\s*-?\s*(\d+)\s*([A-Za-z]?)\s*$", RegexOptions.Compiled);
    private static readonly Regex LabelRx = new(@"^(?:Gate|Parking|Dock)\s+([A-Z]?)(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// The scenery stands that match a real gate: same number, and the same letter when the scenery uses
    /// letters (the gate's own letter like "C61", or the terminal's). Scenery without letters matches on number.
    /// </summary>
    public static List<AirportModel.ParkingSpot> Match(AirportModel apt, string terminal, string gate)
    {
        var m = GateRx.Match(gate ?? "");
        if (!m.Success) return new();
        var num = int.Parse(m.Groups[2].Value);
        var letter = m.Groups[1].Value.ToUpperInvariant();
        var term = (terminal ?? "").Trim().ToUpperInvariant();
        if (letter.Length == 0 && term.Length == 1 && char.IsLetter(term[0])) letter = term;
        var cands = new List<(AirportModel.ParkingSpot s, string letter)>();
        foreach (var s in apt.Spots)
        {
            var lm = LabelRx.Match(s.Label);
            if (lm.Success && int.Parse(lm.Groups[2].Value) == num) cands.Add((s, lm.Groups[1].Value));
        }
        if (cands.Count == 0) return new();
        var sameLetter = cands.Where(c => letter.Length > 0 && c.letter == letter.Substring(0, 1)).Select(c => c.s).ToList();
        if (sameLetter.Count > 0) return sameLetter;
        var noLetter = cands.Where(c => c.letter.Length == 0).Select(c => c.s).ToList();
        if (noLetter.Count > 0) return noLetter;
        return letter.Length == 0 ? cands.Select(c => c.s).ToList() : new();
    }
}

/// <summary>The real-gate plan for one airport's scenery: which airlines really use each stand.</summary>
public sealed class GatePlan
{
    /// <summary>Stand → the airlines that really use it (only stands with real data).</summary>
    public Dictionary<AirportModel.ParkingSpot, List<string>> Owners { get; } = new();
    /// <summary>Airline → the stands it really uses.</summary>
    public Dictionary<string, List<AirportModel.ParkingSpot>> ByAirline { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Callsign → the stand of today's flight.</summary>
    public Dictionary<string, List<AirportModel.ParkingSpot>> ByFlight { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Gates, Matched;
    public List<string> Unmatched { get; } = new();

    public GatePlan(AirportModel apt, RealGates g)
    {
        var cache = new Dictionary<string, List<AirportModel.ParkingSpot>>();
        List<AirportModel.ParkingSpot> M(string key)
        {
            if (cache.TryGetValue(key, out var l)) return l;
            var (t, gate) = RealGates.Split(key);
            return cache[key] = RealGates.Match(apt, t, gate);
        }
        foreach (var (airline, gates) in g.ByAirline)
            foreach (var (key, count) in gates)
            {
                Gates++;
                var spots = M(key);
                if (spots.Count == 0) { if (!Unmatched.Contains(key)) Unmatched.Add(key); continue; }
                Matched++;
                // A gate seen only once for an airline that is usually elsewhere is probably a one-off: still counts.
                foreach (var s in spots)
                {
                    if (!Owners.TryGetValue(s, out var o)) Owners[s] = o = new();
                    if (!o.Contains(airline)) o.Add(airline);
                    if (!ByAirline.TryGetValue(airline, out var mine)) ByAirline[airline] = mine = new();
                    if (!mine.Contains(s)) mine.Add(s);
                }
            }
        foreach (var (cs, key) in g.ByFlight)
        {
            var spots = M(key);
            if (spots.Count > 0) ByFlight[cs] = spots;
        }
    }

    public bool Knows(string? airline) => airline is not null && ByAirline.ContainsKey(airline);

    public string Describe()
    {
        var parts = ByAirline.OrderByDescending(kv => kv.Value.Count).Take(12)
            .Select(kv => $"{kv.Key} {Ranges(kv.Value.Select(s => s.Label))}");
        var un = Unmatched.Count == 0 ? "" : $"; not in this scenery: {string.Join(", ", Unmatched.Take(10).Select(k => k.Replace("|", " ").Trim()))}";
        return $"{Matched} of {Gates} real airline gates found in the scenery ({string.Join("; ", parts)}){un}";
    }

    private static string Ranges(IEnumerable<string> labels)
    {
        var list = labels.OrderBy(l => l.Length).ThenBy(l => l).ToList();
        return list.Count <= 6 ? string.Join(",", list) : $"{string.Join(",", list.Take(5))} +{list.Count - 5} more";
    }
}
