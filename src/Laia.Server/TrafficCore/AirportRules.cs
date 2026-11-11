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
    public sealed record ProcRunways(bool Departure, string[] Procs, string[] Runways, string Source)
    {
        /// <summary>The hours it applies (minutes of the local day, from–to; null = always), e.g. "0700-2300 local".</summary>
        public (int from, int to)? Hours { get; init; }
        public bool ActiveAt(int? localMin) => Hours is not { } h || localMin is not { } m ||
            (h.from <= h.to ? m >= h.from && m < h.to : m >= h.from || m < h.to);
        public string HoursText => Hours is { } h ? $" ({h.from / 60:00}{h.from % 60:00}–{h.to / 60:00}{h.to % 60:00} local)" : "";
    }

    public List<ProcRunways> Rules { get; } = new();
    public string Charts = "";

    /// <summary>The rule for this SID (or STAR) in force at this local time (minutes of the day; null = ignore hours).</summary>
    public ProcRunways? ForSid(string sid, int? localMin = null) => For(true, sid, localMin);
    public ProcRunways? ForStar(string star, int? localMin = null) => For(false, star, localMin);

    private ProcRunways? For(bool dep, string proc, int? localMin)
    {
        var name = Base(proc);
        return Rules.FirstOrDefault(r => r.Departure == dep && r.Procs.Contains(name) && r.ActiveAt(localMin));
    }

    public static string Base(string proc) => proc.Trim().ToUpperInvariant().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    public string Describe() => Rules.Count == 0 ? $"no runway rules in {Charts}" :
        $"{Rules.Count} runway rule{(Rules.Count == 1 ? "" : "s")} from {Charts}: " +
        string.Join("; ", Rules.Select(r => $"{string.Join("/", r.Procs)} → {string.Join(" ", r.Runways)}{r.HoursText}"));

    private static readonly Regex RunwayList = new(@"\bRUNWAYS?\s+((?:\d{1,2}[LRC]?(?:/[LRC])*(?:\s*(?:,|AND|&)?\s*)?)+)", RegexOptions.Compiled);
    private static readonly Regex OneRunway = new(@"(\d{1,2})([LRC](?:/[LRC])*)?", RegexOptions.Compiled);
    private static readonly Regex Hours = new(@"\b([0-2]\d[0-5]\d)\s*(?:-|–|—|TO)\s*([0-2]\d[0-5]\d)\s*(?:LOCAL|LCL|L)\b", RegexOptions.Compiled);
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
        (int, int)? hours = null;
        var lines = (text ?? "").ToUpperInvariant().Split('\n').Select(raw => Regex.Replace(raw, @"\s+", " ").Trim()).Where(l => l.Length > 0).ToList();
        // Every runway-group heading on the page ("Departing Runways 35L/R 17L/R"): a PDF's text often doesn't come
        // out in reading order, so a rule whose runway belongs to exactly one group is widened to it even when the
        // heading isn't the line just above.
        var groups = lines.Select(l => Group.Match(l)).Where(m => m.Success).Select(m => Runways(m.Groups[2].Value)).Where(g => g.Count > 1).ToList();
        foreach (var line in lines)
        {
            // Hours the following rules apply: "0700-2300 local" (or "0700-2300L/LCL").
            var hm = Hours.Match(line);
            if (hm.Success)
            {
                int M(string t) => int.Parse(t[..2]) * 60 + int.Parse(t[2..]);
                var (a, b) = (M(hm.Groups[1].Value), M(hm.Groups[2].Value));
                if (a < 1440 && b <= 1440 && a != b) hours = (a, b);
            }
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
            else { var only = groups.Where(gr => rwys.All(gr.Contains)).ToList(); if (only.Count == 1) rwys = only[0]; }
            var src = line.Length > 90 ? line[..90] + "…" : line;
            if (depProcs.Length > 0) rules.Rules.Add(new ProcRunways(true, depProcs, rwys.ToArray(), $"{charts}: {src}") { Hours = hours });
            if (arrProcs.Length > 0) rules.Rules.Add(new ProcRunways(false, arrProcs, rwys.ToArray(), $"{charts}: {src}") { Hours = hours });
        }
        // Hours found only after the rules (PDF text out of order): with a single window on the page, it's theirs.
        var windows = lines.Select(l => Hours.Match(l)).Where(m => m.Success).Select(m => m.Value).Distinct().ToList();
        if (windows.Count == 1 && rules.Rules.Any(r => r.Hours is null))
        {
            var hm = Hours.Match(windows[0]);
            int M(string t) => int.Parse(t[..2]) * 60 + int.Parse(t[2..]);
            var (a, b) = (M(hm.Groups[1].Value), M(hm.Groups[2].Value));
            if (a < 1440 && b <= 1440 && a != b)
                for (var i = 0; i < rules.Rules.Count; i++) if (rules.Rules[i].Hours is null) rules.Rules[i] = rules.Rules[i] with { Hours = (a, b) };
        }
        return rules;
    }
}
