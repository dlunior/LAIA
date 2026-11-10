using System.Text.RegularExpressions;

namespace Laia.Server.TrafficCore;

/// <summary>What one airport's D-ATIS says is in use, combined from its broadcasts (arrival, departure or combined).</summary>
public sealed class AtisInfo
{
    public List<string> Landing { get; } = new();
    public List<string> Departing { get; } = new();
    public List<string> Closed { get; } = new();
    public List<(string kind, string suffix, string runway)> Approaches { get; } = new();
    public string? CodeArr, CodeDep;
    public string Text = "";
    public string Key => $"{string.Join(",", Landing)}|{string.Join(",", Departing)}|{string.Join(",", Approaches)}|{string.Join(",", Closed)}";

    public string Describe() =>
        $"landing {(Landing.Count == 0 ? "not named" : string.Join(", ", Landing))}, departing {(Departing.Count == 0 ? "not named" : string.Join(", ", Departing))}" +
        $"; approach {(Approaches.Count == 0 ? "not named" : string.Join(", ", Approaches.Select(a => $"{a.kind}{(a.suffix.Length > 0 ? " " + a.suffix : "")} {a.runway}")))}" +
        (Closed.Count > 0 ? $"; closed {string.Join(", ", Closed)}" : "") +
        (CodeArr is not null ? $" (info {CodeArr}{(CodeDep is not null && CodeDep != CodeArr ? "/" + CodeDep : "")})" : "");
}

/// <summary>
/// Reads US digital ATIS texts (atis.info). The rules come from the FlightSim Route Generator's lessons learned
/// (real texts that broke its parser): spelled-out runways (Denver "RUNWAY 3 4 LEFT"), stop at the NOTAMs, runway
/// words in many forms ("RWY8", "RWY, 28L", "RYS"), purpose from the words before the runway in the same sentence
/// (context runs across commas, resets at a full stop), runways with no runway word (Boston "RNAV 4L, DEP 4R",
/// Atlanta "VIS 8L, ILS 9R"), "EXPECT 1R, 36" (Charlotte), closed runways removed, a closed taxiway between
/// runways isn't a closed runway (Cleveland), out-of-service items skipped. Better to leave out a runway than list
/// a wrong one: a missing runway falls back to the wind.
/// </summary>
public static class AtisParse
{
    private const string List = @"[0-3]?\d[LRC]?(?:\s*(?:,|AND|/)\s*[0-3]?\d[LRC]?)*";
    private static readonly Regex Spelled1 = new(@"\b(RWYS?|RUNWAYS?|RYS?)\s*([0-3])\s+(\d)\s*(LEFT|RIGHT|CENTER|CENTRE)?", RegexOptions.Compiled);
    private static readonly Regex Spelled2 = new(@"\b(RWYS?|RUNWAYS?|RYS?)\s*(\d{1,2})\s+(LEFT|RIGHT|CENTER|CENTRE)\b", RegexOptions.Compiled);
    private static readonly Regex Notams = new(@"NOTICES?\s+TO\s+AIR(?:MEN|\s*MISSIONS)|\bNOTAMS?\b", RegexOptions.Compiled);
    private static readonly Regex Pass1 = new(@"([A-Z0-9 .,/\-]{0,36}?)(?<![A-Z])(?:RWYS?|RUNWAYS?|RYS?),?\s*(" + List + ")", RegexOptions.Compiled);
    private static readonly Regex Pass2 = new(@"(?<![A-Z])(ILS|RNAV|RNP|VISUAL|VIS|LOC|LDA|SDF|LDG|LNDG|LANDING|ARR|ARRIVALS?|DEP|DEPG|DEPTG|DEPARTING|DEPARTURES?)(?:\s+(?:[XYZ]|APCHS?|APPROACH(?:ES)?))?\s+(" + List + @")(?![\dA-Z/]|\.\d)", RegexOptions.Compiled);
    private static readonly Regex Pass3 = new(@"(?<![A-Z])EXPECT(?:ING)?\s+(" + List + @")(?![\dA-Z/]|\.\d)", RegexOptions.Compiled);
    private static readonly Regex Sep = new(@"\s*(?:,|AND|/)\s*", RegexOptions.Compiled);
    private static readonly Regex OneRwy = new(@"^[0-3]?\d[LRC]?$", RegexOptions.Compiled);
    private static readonly Regex Approach = new(@"(?<![A-Z])(ILS|LOC|RNAV|RNP|GPS|VOR|LDA)(?:\s+OR\s+LOC(?:/DME)?)?\s*(?:\((?:GPS|RNP)\)\s*)?(?:([XYZ])\s+)?(?:(?:RWYS?|RY|RUNWAYS?),?\s*)?([0-3]?\d[LRC]?)(?![\dA-Z/]|\.\d)", RegexOptions.Compiled);

    /// <summary>Upper case, digit-by-digit runways collapsed, and cut before the NOTAMs.</summary>
    public static string Clean(string? text)
    {
        var t = " " + (text ?? "").ToUpperInvariant();
        t = Spelled1.Replace(t, m => $"{m.Groups[1].Value} {m.Groups[2].Value}{m.Groups[3].Value}{(m.Groups[4].Success && m.Groups[4].Length > 0 ? m.Groups[4].Value[..1] : "")}");
        t = Spelled2.Replace(t, m => $"{m.Groups[1].Value} {m.Groups[2].Value}{m.Groups[3].Value[..1]}");
        var cut = Notams.Match(t);
        return cut.Success ? t[..cut.Index] : t;
    }

    /// <summary>"4L" → "04L", the form the airport data uses.</summary>
    public static string Norm(string r) => r.Length > 0 && (r.Length == 1 || !char.IsDigit(r[1])) ? "0" + r : r;

    private static IEnumerable<string> Split(string s) => Sep.Split(s).Where(x => OneRwy.IsMatch(x)).Select(Norm);

    private static string Sentence(string trail)
    {
        var dot = trail.IndexOf('.');
        return dot > -1 ? trail[..dot] : trail;
    }

    /// <summary>The departure, arrival and closed runways one ATIS text names.</summary>
    public static (List<string> dep, List<string> arr, List<string> closed) Runways(string? text)
    {
        var dep = new List<string>(); var arr = new List<string>(); var closed = new List<string>();
        void Add(List<string> l, string r) { if (!l.Contains(r)) l.Add(r); }
        var t = Clean(text);
        string? last = null;
        foreach (Match m in Pass1.Matches(t))
        {
            var ctx = m.Groups[1].Value;
            if (ctx.Contains('.')) { last = null; ctx = ctx[(ctx.LastIndexOf('.') + 1)..]; }
            var rwys = Split(m.Groups[2].Value).ToList();
            var end = m.Index + m.Length;
            var trail = Sentence(t[end..Math.Min(t.Length, end + 20)]);
            var clause = ctx[(ctx.LastIndexOf(';') + 1)..];
            var closing = Regex.IsMatch(clause, "CLSD|CLOSED");
            if (closing && Regex.IsMatch(clause, @"\b(?:TWYS?|TAXIWAYS?|PADS?|APRONS?|RAMPS?|BTN|BETWEEN)\b")) { last = null; continue; }
            if (closing || Regex.IsMatch(trail, @"^\s*(?:IS\s+)?(?:CLSD|CLOSED)")) { foreach (var r in rwys) Add(closed, r); last = null; continue; }
            if (Regex.IsMatch(trail, @"\bOTS\b|OUT OF SERVICE") || Regex.IsMatch(ctx, @"\bOTS\b|OUT OF SERVICE")) { last = null; continue; }
            var isArr = Regex.IsMatch(ctx, @"LNDG|LDG|LANDING|ARRIV|\bARR\b|APCH|APPROACH|EXPECT|VISUAL|ILS|RNAV|LOC");
            var isDep = Regex.IsMatch(ctx, "DEPG|DEPART|DEPTG| DEPS? ");
            string cls;
            if (isArr && !isDep) cls = "arr";
            else if (isDep && !isArr) cls = "dep";
            else if (isArr && isDep) cls = "both";
            else if (last is not null) cls = last;
            else if (Regex.IsMatch(trail, @"^\s*(?:APCH|APPROACH)(?:ES)?\s+IN\s+USE")) cls = "arr";
            else if (Regex.IsMatch(trail, @"^\s*(?:IN USE|ACTIVE)") || ctx.Contains("IN USE")) cls = "both";
            else continue;
            foreach (var r in rwys) { if (cls != "dep") Add(arr, r); if (cls != "arr") Add(dep, r); }
            last = cls;
        }
        foreach (Match m in Pass2.Matches(t))
        {
            var end = m.Index + m.Length;
            var trail = Sentence(t[end..Math.Min(t.Length, end + 24)]);
            if (Regex.IsMatch(trail, @"CLSD|CLOSED|\bOTS\b|OUT OF SERVICE")) continue;
            var isDep = m.Groups[1].Value.StartsWith("DEP");
            foreach (var r in Split(m.Groups[2].Value)) Add(isDep ? dep : arr, r);
        }
        foreach (Match m in Pass3.Matches(t))
        {
            var head = t[..m.Index];
            var sent = head[(head.LastIndexOf('.') + 1)..];
            var end = m.Index + m.Length;
            var trail = Sentence(t[end..Math.Min(t.Length, end + 24)]);
            if (Regex.IsMatch(trail, @"CLSD|CLOSED|\bOTS\b|OUT OF SERVICE")) continue;
            var d = Regex.IsMatch(sent, @"\bDEPS?\b|DEPG|DEPTG|DEPART");
            var a = Regex.IsMatch(sent, @"\bARR|APCH|APPROACH|LDG|LNDG|LANDING|\bILS\b|RNAV|VISUAL|\bVIS\b");
            if (!d && !a) continue;
            foreach (var r in Split(m.Groups[1].Value)) { if (d) Add(dep, r); if (a) Add(arr, r); }
        }
        dep.RemoveAll(closed.Contains);
        arr.RemoveAll(closed.Contains);
        return (dep, arr, closed);
    }

    /// <summary>
    /// The instrument approaches a text names ("ILS RWY 22 APCH IN USE", "EXPECT RNAV Y RWY 31", "ILS OR LOC RWY 4",
    /// Boston's "RNAV 4L"), before the NOTAMs and not out of service. Visual approaches aren't flown yet.
    /// </summary>
    public static List<(string kind, string suffix, string runway)> Approaches(string? text)
    {
        var list = new List<(string, string, string)>();
        var t = Clean(text);
        foreach (Match m in Approach.Matches(t))
        {
            var end = m.Index + m.Length;
            var trail = Sentence(t[end..Math.Min(t.Length, end + 25)]);
            if (Regex.IsMatch(trail, @"\bOTS\b|OUT OF SERVICE|\bU/S\b|UNUSABLE|UNUSBL|NOT AVBL|CLSD|CLOSED")) continue;
            var e = (m.Groups[1].Value, m.Groups[2].Value, Norm(m.Groups[3].Value));
            if (!list.Contains(e)) list.Add(e);
        }
        return list;
    }

    /// <summary>
    /// Combines an airport's broadcasts: a departure ATIS gives the departures (its arrivals if it names none), an
    /// arrival ATIS the arrivals (likewise), and a combined one that names only one direction uses it for both.
    /// </summary>
    public static AtisInfo Combine(IEnumerable<(string type, string code, string text)> entries)
    {
        var info = new AtisInfo();
        var texts = new List<string>();
        foreach (var (type, code, text) in entries.OrderBy(e => e.type == "dep" ? 1 : 0))
        {
            texts.Add(text);
            var (dep, arr, closed) = Runways(text);
            foreach (var c in closed) if (!info.Closed.Contains(c)) info.Closed.Add(c);
            List<string> d = dep, a = arr;
            if (type == "dep") { d = dep.Count > 0 ? dep : arr; a = new(); info.CodeDep ??= code; }
            else if (type == "arr") { a = arr.Count > 0 ? arr : dep; d = new(); info.CodeArr ??= code; }
            else
            {
                if (d.Count > 0 && a.Count == 0) a = d; else if (a.Count > 0 && d.Count == 0) d = a;
                info.CodeArr ??= code; info.CodeDep ??= code;
            }
            foreach (var r in d) if (!info.Departing.Contains(r)) info.Departing.Add(r);
            foreach (var r in a) if (!info.Landing.Contains(r)) info.Landing.Add(r);
            if (type != "dep")
                foreach (var x in Approaches(text)) if (!info.Approaches.Contains(x)) info.Approaches.Add(x);
        }
        info.Departing.RemoveAll(info.Closed.Contains);
        info.Landing.RemoveAll(info.Closed.Contains);
        info.Approaches.RemoveAll(x => info.Closed.Contains(x.runway));
        info.Text = string.Join(" / ", texts);
        return info;
    }

    /// <summary>One combined broadcast (tests, and airports that send a single ATIS).</summary>
    public static AtisInfo FromText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? new AtisInfo() : Combine(new[] { ("combined", "", text!) });
}
