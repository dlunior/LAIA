using System.Text.RegularExpressions;

namespace Laia.Server.TrafficCore;

/// <summary>
/// Local rules an airport publishes that the procedures themselves don't show, read from its FAA "Attention All
/// Users Pages" (AAUPs) — downloaded and read automatically for every US airport, no airport-specific code. A rule
/// ties procedures to runways ("DDANY, MZULO, JEEMY, FATHE - Expect to Depart Runway 35L"); the general logic
/// (STAR/SID side, stand side, ATIS) still decides everything else, and a rule only narrows the choice among the
/// runways actually in use.
/// </summary>
public sealed class AirportRules
{
    /// <summary>Procedures (SID or STAR names without their number) and the runways they're assigned.</summary>
    public sealed record ProcRunways(bool Departure, string[] Procs, string[] Runways, string Source);

    public List<ProcRunways> Rules { get; } = new();
    public string Charts = "";

    public ProcRunways? ForSid(string sid) => For(true, sid);
    public ProcRunways? ForStar(string star) => For(false, star);

    private ProcRunways? For(bool dep, string proc)
    {
        var name = Base(proc);
        return Rules.FirstOrDefault(r => r.Departure == dep && r.Procs.Contains(name));
    }

    public static string Base(string proc) => proc.Trim().ToUpperInvariant().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    public string Describe() => Rules.Count == 0 ? $"no runway rules in {Charts}" :
        $"{Rules.Count} runway rule{(Rules.Count == 1 ? "" : "s")} from {Charts}: " +
        string.Join("; ", Rules.Select(r => $"{string.Join("/", r.Procs)} → {string.Join(" ", r.Runways)}"));

    private static readonly Regex RunwayList = new(@"\bRUNWAYS?\s+((?:\d{1,2}[LRC]?(?:/[LRC])*(?:\s*(?:,|AND|&)?\s*)?)+)", RegexOptions.Compiled);
    private static readonly Regex OneRunway = new(@"(\d{1,2})([LRC](?:/[LRC])*)?", RegexOptions.Compiled);
    private static readonly Regex Group = new(@"\b(DEPARTING|DEPARTURE|ARRIVING|ARRIVAL|LANDING)\s+RUNWAYS?\s+(.+)$", RegexOptions.Compiled);

    /// <summary>"35L/R 17L/R" → 35L, 35R, 17L, 17R ("4" → "04").</summary>
    public static List<string> Runways(string list)
    {
        var res = new List<string>();
        foreach (Match m in OneRunway.Matches(list))
        {
            var n = int.Parse(m.Groups[1].Value);
            if (n < 1 || n > 36) continue;
            var num = n.ToString("00");
            var sides = m.Groups[2].Success ? m.Groups[2].Value.Split('/', StringSplitOptions.RemoveEmptyEntries) : new[] { "" };
            foreach (var s in sides) if (!res.Contains(num + s)) res.Add(num + s);
        }
        return res;
    }

    /// <summary>
    /// Reads AAUP text. A line naming SIDs or STARs of this airport and a runway is a rule; a heading line like
    /// "Departing Runways 35L/R 17L/R" just before it widens the rule to that group of runways (the same side of the
    /// field in either flow), so it still applies when the airport turns around.
    /// </summary>
    public static AirportRules Parse(string text, IEnumerable<string> sids, IEnumerable<string> stars, string charts)
    {
        var rules = new AirportRules { Charts = charts };
        var sidNames = sids.Select(Base).Where(x => x.Length >= 3).ToHashSet();
        var starNames = stars.Select(Base).Where(x => x.Length >= 3).ToHashSet();
        List<string>? group = null;
        foreach (var raw in (text ?? "").ToUpperInvariant().Split('\n'))
        {
            var line = Regex.Replace(raw, @"\s+", " ").Trim();
            if (line.Length == 0) continue;
            var g = Group.Match(line);
            if (g.Success) { group = Runways(g.Groups[2].Value); continue; }
            var rl = RunwayList.Match(line);
            if (!rl.Success) continue;
            var words = Regex.Matches(line[..rl.Index], @"\b[A-Z]{3,6}\b").Select(m => m.Value).ToList();
            var depProcs = words.Where(sidNames.Contains).Distinct().ToArray();
            var arrProcs = words.Where(w => starNames.Contains(w) && !sidNames.Contains(w)).Distinct().ToArray();
            var rwys = Runways(rl.Groups[1].Value);
            if (rwys.Count == 0) continue;
            if (group is not null && rwys.All(group.Contains)) rwys = group;
            var src = line.Length > 90 ? line[..90] + "…" : line;
            if (depProcs.Length > 0) rules.Rules.Add(new ProcRunways(true, depProcs, rwys.ToArray(), $"{charts}: {src}"));
            if (arrProcs.Length > 0) rules.Rules.Add(new ProcRunways(false, arrProcs, rwys.ToArray(), $"{charts}: {src}"));
        }
        return rules;
    }
}
