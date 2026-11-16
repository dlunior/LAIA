using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Laia.Server;

/// <summary>
/// A STAR chart's "expect" restriction (user, 13 Nov: "if a chart says Expect, treat that as an actual restriction"):
/// cross Fix between MinFt and MaxFt (0 = no limit that way) and/or at Kt. When: "" for every arrival, "dir:S" when
/// landing south (north, east, west), "rwy:18" when landing runway 18 (18L/18C/18R too).
/// </summary>
public sealed record StarExpect(string Fix, double MinFt, double MaxFt, double Kt, string When, string How);

/// <summary>Reads the "expect" restrictions printed on FAA STAR charts (d-TPP PDFs).</summary>
public static class StarCharts
{
    private static readonly Regex AltTok = new(@"^(FL)?(\d{3,5})(['’]|FT)?$");
    private static readonly Regex KtTok = new(@"^(\d{3})(K|KT|KTS|KIAS)$");

    /// <summary>
    /// The expects on one chart, for the airport aptName (its FAA name, e.g. "MELBOURNE ORLANDO INTL"), in three layouts:
    /// <list type="bullet">
    /// <item>the "TURBOJET VERTICAL NAVIGATION PLANNING INFORMATION" boxes under a fix (BITHO7, user, 15 Nov):
    /// "Expect clearance to cross at 15000' landing Patrick SFB, Melbourne Orlando Intl, and Merritt Island." — only
    /// for the airports listed; "… landing Orlando Intl Rwy 18." for that runway; "NOTE: Orlando Intl Landing South:
    /// Expect clearance to cross at 250K." for that flow;</item>
    /// <item>the notes: "EXPECT TO CROSS BUGGZ AT 11000";</item>
    /// <item>"Expect 11000 / 250K" printed beside a fix on the plan view.</item>
    /// </list>
    /// </summary>
    public static List<StarExpect> ReadExpects(PdfDocument doc, HashSet<string> fixes, string aptName)
    {
        var res = new List<StarExpect>();
        var text = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            var words = page.GetWords().ToList();
            text.AppendLine(string.Join(" ", words.Select(w => w.Text)));
            var at = new List<(double x, double y)>();
            res.AddRange(PlanningBoxes(page, fixes, aptName, at));
            for (var i = 0; i < words.Count; i++)
            {
                var w = words[i];
                if (!w.Text.ToUpperInvariant().StartsWith("EXPECT")) continue;
                if (at.Any(p => Math.Abs(p.x - w.BoundingBox.Left) < 4 && Math.Abs(p.y - (page.Height - w.BoundingBox.Bottom)) < 4)) continue;   // a planning box's, read above
                var b = w.BoundingBox;
                var h = Math.Max(4, b.Height);
                // The words right after it: on its line to the right, or the line just below, starting near it.
                var after = words.Where(o => o != w && (
                        (Math.Abs(o.BoundingBox.Bottom - b.Bottom) < h * 0.6 && o.BoundingBox.Left >= b.Left && o.BoundingBox.Left - b.Right < 90)
                        || (b.Bottom - o.BoundingBox.Top is >= -1 and < 1.8 * 14 && Math.Abs(o.BoundingBox.Left - b.Left) < 40)))
                    .OrderBy(o => -o.BoundingBox.Bottom).ThenBy(o => o.BoundingBox.Left).Take(6).Select(o => o.Text.ToUpperInvariant().Trim(',', '.', '/')).ToList();
                if (after.Count > 0 && (after[0] is "TO" or "CLEARANCE" or "RADAR" or "VECTORS" or "FILED" or "DEPARTURE")) continue;   // a note, read below
                var alts = after.Select(t => AltTok.Match(t)).Where(m => m.Success).Select(m => double.Parse(m.Groups[2].Value) * (m.Groups[1].Success ? 100 : 1))
                    .Where(a => a is >= 1000 and <= 45000).Take(2).ToList();
                var kt = after.Select(t => KtTok.Match(t)).Where(m => m.Success).Select(m => double.Parse(m.Groups[1].Value)).FirstOrDefault();
                if (alts.Count == 0 && kt == 0) continue;
                var fix = words.Where(o => fixes.Contains(o.Text.ToUpperInvariant().Trim(',', '.')))
                    .Select(o => (o, d: Math.Sqrt(Math.Pow(o.BoundingBox.Centroid.X - b.Centroid.X, 2) + Math.Pow(o.BoundingBox.Centroid.Y - b.Centroid.Y, 2))))
                    .Where(x => x.d < 160).OrderBy(x => x.d).Select(x => x.o.Text.ToUpperInvariant().Trim(',', '.')).FirstOrDefault();
                if (fix is null) continue;
                double mn = 0, mx = 0;
                if (alts.Count == 2) { mn = Math.Min(alts[0], alts[1]); mx = Math.Max(alts[0], alts[1]); }
                else if (alts.Count == 1) mn = mx = alts[0];
                res.Add(new StarExpect(fix, mn, mx, kt, "", "plan view"));
            }
        }
        // The notes: "EXPECT TO CROSS BUGGZ AT 11000", "... AT OR ABOVE FL240", "... AT 250 KT".
        var t = Regex.Replace(text.ToString().ToUpperInvariant(), @"\s+", " ");
        foreach (Match m in Regex.Matches(t,
            @"EXPECT (?:CLEARANCE )?TO CROSS ([A-Z]{3,5}) AT (OR ABOVE |OR BELOW )?(FL)?(\d{3,5})(?:['’]|FT)?(?: (?:AND|AT) (\d{3}) ?K)?"))
        {
            var fix = m.Groups[1].Value;
            if (!fixes.Contains(fix)) continue;
            var alt = double.Parse(m.Groups[4].Value) * (m.Groups[3].Success ? 100 : 1);
            if (alt is < 1000 or > 45000) continue;
            var how = m.Groups[2].Value.Trim();
            double mn = how == "OR BELOW" ? 0 : alt, mx = how == "OR ABOVE" ? 0 : alt;
            var kt = m.Groups[5].Success ? double.Parse(m.Groups[5].Value) : 0;
            res.RemoveAll(e => e.Fix == fix && e.When == "" && e.How == "plan view");   // the note's wording (with "or above/below") wins over the plan view
            res.Add(new StarExpect(fix, mn, mx, kt, "", "notes"));
        }
        return res.GroupBy(e => (e.Fix, e.When)).Select(g => g.Last()).ToList();
    }

    /// <summary>A word built from the page's letters: text, left and right, baseline (down from the top of the page), size.</summary>
    private sealed record Wd(string Text, double X0, double X1, double Y, double Size)
    {
        public string U => Text.ToUpperInvariant();
        public double Cx => (X0 + X1) / 2;
    }

    /// <summary>
    /// The page's words, put together from its letters: letters on one baseline, split at spaces and gaps. Built here
    /// rather than taken from the PDF reader's own word list because the chart boxes' letters are stretched sideways and
    /// drawn out of order (BITHO7's COZMO box came out one letter per word).
    /// </summary>
    private static List<Wd> Words(Page page)
    {
        var rows = new List<(double y, List<Letter> ls)>();
        foreach (var l in page.Letters.Where(l => l.TextDirection == TextDirection.Horizontal).OrderBy(l => page.Height - l.StartBaseLine.Y))
        {
            var y = page.Height - l.StartBaseLine.Y;
            if (rows.Count > 0 && y - rows[^1].y < 1.0) rows[^1].ls.Add(l);
            else rows.Add((y, new List<Letter> { l }));
        }
        var res = new List<Wd>();
        foreach (var (y, ls) in rows)
        {
            ls.Sort((a, b) => a.StartBaseLine.X.CompareTo(b.StartBaseLine.X));
            var cur = new List<Letter>();
            void Flush()
            {
                if (cur.Count == 0) return;
                var size = cur.Max(c => c.PointSize > 0 ? c.PointSize : Math.Max(3, c.GlyphRectangle.Height));
                res.Add(new Wd(string.Concat(cur.Select(c => c.Value)), cur[0].StartBaseLine.X, cur.Max(c => Math.Max(c.EndBaseLine.X, c.GlyphRectangle.Right)), y, size));
                cur.Clear();
            }
            foreach (var l in ls)
            {
                if (string.IsNullOrWhiteSpace(l.Value)) { Flush(); continue; }
                var size = l.PointSize > 0 ? l.PointSize : Math.Max(3, l.GlyphRectangle.Height);
                if (cur.Count > 0 && l.StartBaseLine.X - Math.Max(cur[^1].EndBaseLine.X, cur[^1].GlyphRectangle.Right) > 0.25 * size) Flush();
                cur.Add(l);
            }
            Flush();
        }
        return res;
    }

    /// <summary>
    /// The planning boxes: a fix name with "TURBOJET VERTICAL NAVIGATION PLANNING INFORMATION" under it, then one or more
    /// "Expect clearance to cross at …" sentences, each read across its lines up to the full stop. at: where each
    /// "Expect" read here is (x, baseline from the top), so the other layouts don't read it again.
    /// </summary>
    private static List<StarExpect> PlanningBoxes(Page page, HashSet<string> fixes, string aptName, List<(double x, double y)> at)
    {
        var res = new List<StarExpect>();
        var words = Words(page);
        var turbojet = words.Where(w => w.U == "TURBOJET").ToList();
        var headers = words.Where(w => fixes.Contains(w.U.Trim(',', '.', ':'))
                && turbojet.Any(tj => tj.Y - w.Y is > 0 and < 22 && Math.Abs(tj.Cx - w.Cx) < 90)).ToList();
        if (headers.Count == 0) return res;
        // The lines of text, top to bottom, each left to right.
        var lines = new List<(double y, List<Wd> words)>();
        foreach (var w in words.OrderBy(w => w.Y))
        {
            if (lines.Count > 0 && w.Y - lines[^1].y < 1.5) lines[^1].words.Add(w);
            else lines.Add((w.Y, new List<Wd> { w }));
        }
        foreach (var l in lines) l.words.Sort((a, b) => a.X0.CompareTo(b.X0));
        // The words on a line joined to this one with no wide gap (one column of a box); to the left too on the first line.
        List<Wd> Run(List<Wd> line, Wd around, bool leftToo)
        {
            var i = line.IndexOf(around);
            int a = i, z = i;
            while (leftToo && a > 0 && line[a].X0 - line[a - 1].X1 < 7) a--;
            while (z + 1 < line.Count && line[z + 1].X0 - line[z].X1 < 12) z++;
            return line.GetRange(a, z - a + 1);
        }
        foreach (var e in words.Where(w => w.U == "EXPECT"))
        {
            var li = lines.FindIndex(l => l.words.Contains(e));
            if (li < 0) continue;
            var first = Run(lines[li].words, e, true);
            var idx = first.IndexOf(e);
            // Words before it on the line belong to it only after the last full stop ("NOTE: Orlando Intl Landing South:").
            var start = 0;
            for (var k = 0; k < idx; k++) if (first[k].Text.EndsWith('.')) start = k + 1;
            var prefix = first.GetRange(start, idx - start).Select(w => w.U).ToList();
            var sentence = first.GetRange(idx, first.Count - idx).ToList();
            var left = first[0].X0;
            var lastY = lines[li].y;
            for (var lj = li + 1; lj < lines.Count && !sentence[^1].Text.EndsWith('.'); lj++)
            {
                if (lines[lj].y - lastY > e.Size * 2.2) break;
                var startW = lines[lj].words.FirstOrDefault(w => Math.Abs(w.X0 - left) < 6);
                if (startW is null) continue;
                sentence.AddRange(Run(lines[lj].words, startW, false));
                lastY = lines[lj].y;
            }
            var s = Regex.Replace(string.Join(" ", sentence.Select(w => w.U)), @"\s+", " ");
            var m = Regex.Match(s, @"^EXPECT (?:CLEARANCE )?TO CROSS AT (OR ABOVE |OR BELOW )?(?:FL(\d{3})|(\d{4,5})(?:['’]|FT)?|(\d{3}) ?(?:K|KT|KTS)\b)(?:,? (?:AND|AT) (\d{3}) ?(?:K|KT|KTS)\b)?(?<rest>.*)$");
            if (!m.Success) continue;
            // The fix: the nearest planning box header above this sentence, over the same column.
            var hdr = headers.Where(h => h.Y < e.Y && e.Y - h.Y < 170 && Math.Abs(h.Cx - (left + 75)) < 110)
                .OrderBy(h => e.Y - h.Y).FirstOrDefault();
            if (hdr is null) continue;
            at.Add((e.X0, e.Y));
            var fix = hdr.U.Trim(',', '.', ':');
            double mn = 0, mx = 0, kt = 0;
            var how = m.Groups[1].Value.Trim();
            double alt = m.Groups[2].Success ? double.Parse(m.Groups[2].Value) * 100 : m.Groups[3].Success ? double.Parse(m.Groups[3].Value) : 0;
            if (alt is > 0 and (< 1000 or > 45000)) continue;
            if (alt > 0) { mn = how == "OR BELOW" ? 0 : alt; mx = how == "OR ABOVE" ? 0 : alt; }
            if (m.Groups[4].Success) kt = double.Parse(m.Groups[4].Value);
            if (m.Groups[5].Success) kt = double.Parse(m.Groups[5].Value);
            if (alt == 0 && kt == 0) continue;
            var when = "";
            // "… landing Patrick SFB, Melbourne Orlando Intl, and Merritt Island." — for these airports only.
            var rest = m.Groups["rest"].Value.Trim().TrimEnd('.');
            var lm = Regex.Match(rest, @"^LANDING (.+)$");
            if (lm.Success)
            {
                var list = lm.Groups[1].Value;
                var rw = Regex.Match(list, @"\s+(?:RWYS?|RUNWAYS?) (\d{1,2}[LCR]?)$");
                if (rw.Success) { when = "rwy:" + rw.Groups[1].Value; list = list[..rw.Index]; }
                if (!ListHas(list, aptName)) continue;
            }
            // "NOTE: Orlando Intl Landing South: Expect …" — for that airport, in that flow.
            var pre = string.Join(" ", prefix).Replace("NOTE:", "").Trim().TrimEnd(':');
            var pm = Regex.Match(pre, @"^(.*?)\s*LANDING (NORTH|SOUTH|EAST|WEST)$");
            if (pm.Success)
            {
                if (pm.Groups[1].Value.Length > 0 && !ListHas(pm.Groups[1].Value, aptName)) continue;
                when = "dir:" + pm.Groups[2].Value[0];
            }
            res.Add(new StarExpect(fix, mn, mx, kt, when, "planning box"));
        }
        return res;
    }

    /// <summary>Whether a chart's list of airports ("Patrick SFB, Melbourne Orlando Intl, and Merritt Island") names this one.</summary>
    public static bool ListHas(string list, string aptName)
    {
        var want = Norm(aptName);
        if (want.Length == 0) return false;
        foreach (var item in Regex.Split(list.ToUpperInvariant(), @",\s*(?:AND\s+)?|\s+AND\s+"))
            if (Norm(item) == want) return true;
        return false;
    }

    /// <summary>An airport name as compared: capitals, no punctuation, the usual short forms, without "airport".</summary>
    public static string Norm(string name)
    {
        var s = Regex.Replace(name.ToUpperInvariant(), @"[^A-Z0-9 ]", " ");
        s = Regex.Replace(s, @"\bINTERNATIONAL\b", "INTL");
        s = Regex.Replace(s, @"\bREGIONAL\b", "RGNL");
        s = Regex.Replace(s, @"\bSPACE FORCE BASE\b", "SFB");
        s = Regex.Replace(s, @"\bSPACE FORCE STATION\b", "SFS");
        s = Regex.Replace(s, @"\bAIR FORCE BASE\b", "AFB");
        s = Regex.Replace(s, @"\b(AIRPORT|ARPT|FIELD|FLD)\b", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    public static string Describe(StarExpect e)
    {
        var a = e.MinFt > 0 && e.MaxFt > 0 ? (e.MinFt == e.MaxFt ? $"at {e.MinFt:N0}" : $"{e.MinFt:N0}–{e.MaxFt:N0}") : e.MinFt > 0 ? $"at or above {e.MinFt:N0}" : e.MaxFt > 0 ? $"at or below {e.MaxFt:N0}" : "";
        var w = e.When.StartsWith("dir:") ? $" (landing {e.When[4..] switch { "N" => "north", "S" => "south", "E" => "east", _ => "west" }})" : e.When.StartsWith("rwy:") ? $" (runway {e.When[4..]})" : "";
        return $"{e.Fix} {a}{(e.Kt > 0 ? $"{(a.Length > 0 ? ", " : "")}{e.Kt:0} kt" : "")}{w}".Trim();
    }
}
