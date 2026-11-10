namespace Laia.Server.TrafficCore;

/// <summary>
/// Local rules an airport publishes that the procedures themselves don't show — mostly from its FAA "Attention All
/// Users Page" (AAUP). The general logic (STAR/SID side, stand side, ATIS) still decides everything else; a rule only
/// narrows the choice where the airport says how it's done. Each rule names its source so it can be checked and
/// updated when the chart changes.
/// </summary>
public static class AirportRules
{
    /// <summary>A departure-runway rule: flights on these SIDs go to these runways (whichever of them are in use).</summary>
    public sealed record SidRunways(string[] Sids, string[] Runways, string Source);

    private static readonly Dictionary<string, SidRunways[]> Departure = new(StringComparer.OrdinalIgnoreCase)
    {
        // Orlando: RNAV Departure AAUP (AL-571, 14 JUL 22), section 5 — simultaneous RNAV departures 0700–2300 local.
        ["KMCO"] = new[]
        {
            new SidRunways(new[] { "DDANY", "MZULO", "JEEMY", "FATHE" }, new[] { "35L", "35R", "17L", "17R" }, "MCO RNAV Departure AAUP: expect runway 35L"),
            new SidRunways(new[] { "RDSOX", "OSPRY", "FSHUN" }, new[] { "36L", "36R", "18L", "18R" }, "MCO RNAV Departure AAUP: expect runway 36R"),
        },
    };

    /// <summary>The airport's rule for a SID (by its name without the number, e.g. "JEEMY4" → JEEMY), or null.</summary>
    public static SidRunways? ForSid(string airport, string sid)
    {
        if (!Departure.TryGetValue(airport, out var rules)) return null;
        var name = sid.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return rules.FirstOrDefault(r => r.Sids.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }
}
