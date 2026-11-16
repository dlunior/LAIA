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
    /// The expects on one chart, for the airport aptName (its FAA name, e.g. "MELBOURNE ORLANDO INTL"), in these layouts:
    /// <list type="bullet">
    /// <item>the "TURBOJET VERTICAL NAVIGATION PLANNING INFORMATION" boxes under a fix (BITHO7, user, 15 Nov):
    /// "Expect clearance to cross at 15000' landing Patrick SFB, Melbourne Orlando Intl, and Merritt Island." — only
    /// for the airports listed; "… landing Orlando Intl Rwy 18." for that runway; "NOTE: Orlando Intl Landing South:
    /// Expect clearance to cross at 250K." for that flow;</item>
    /// <item>the same sentence under a fix's name on the plan view: HAYED "Expect clearance to cross at lowest usable
    /// flight level." (FQM3, user, 15 Nov);</item>
    /// <item>"Expect 13000" under a fix's name (FQM3's RACKI) — underlined, "at or above"; overlined, "at or below";</item>
    /// <item>the notes: "EXPECT TO CROSS BUGGZ AT 11000".</item>
    /// </list>
    /// Charts printed sideways (FQM3) are read along their text, whichever way it runs.
    /// </summary>
    public static List<StarExpect> ReadExpects(PdfDocument doc, HashSet<string> fixes, string aptName)
    {
        var res = new List<StarExpect>();
        var text = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            List<(double ang, PdfPoint a, PdfPoint b)>? pageSegs = null;
            foreach (var (ang, letters) in Frames(page))
            {
                var words = Words(letters, ang);
                text.AppendLine(string.Join(" ", words.OrderBy(w => w.Y).ThenBy(w => w.X0).Select(w => w.Text)));
                pageSegs ??= Segments(page);
                var segs = pageSegs.Select(sg => (a: Frame(sg.a, ang), b: Frame(sg.b, ang))).ToList();
                res.AddRange(Read(words, segs, fixes, aptName));
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

    /// <summary>A word in its text's own frame: text, start and end along the line, baseline (increasing down the text), size.</summary>
    private sealed record Wd(string Text, double X0, double X1, double Y, double Size)
    {
        public string U => Text.ToUpperInvariant();
        public double Cx => (X0 + X1) / 2;
    }

    /// <summary>A page point in the frame of text running at this angle (degrees from the page's x axis).</summary>
    private static (double x, double y) Frame(PdfPoint p, double ang)
    {
        var r = ang * Math.PI / 180;
        double ux = Math.Cos(r), uy = Math.Sin(r);
        return (p.X * ux + p.Y * uy, -(p.X * -uy + p.Y * ux));
    }

    /// <summary>The page's letters by the way their text runs (0°, 90°, 180°, 270°), each group of 10 or more.</summary>
    private static List<(double ang, List<Letter> letters)> Frames(Page page)
    {
        var groups = new Dictionary<int, List<Letter>>();
        foreach (var l in page.Letters)
        {
            double dx = l.EndBaseLine.X - l.StartBaseLine.X, dy = l.EndBaseLine.Y - l.StartBaseLine.Y;
            if (Math.Abs(dx) + Math.Abs(dy) < 0.01) continue;
            var k = ((int)Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI / 90) % 4 + 4) % 4 * 90;
            (groups.TryGetValue(k, out var g) ? g : groups[k] = new()).Add(l);
        }
        return groups.Where(g => g.Value.Count >= 10).Select(g => ((double)g.Key, g.Value)).ToList();
    }

    /// <summary>
    /// Words put together from letters running one way: letters on one baseline, split at spaces and gaps. Built here
    /// rather than taken from the PDF reader's own word list because chart boxes' letters are stretched sideways and
    /// drawn out of order (BITHO7's COZMO box came out one letter per word).
    /// </summary>
    private static List<Wd> Words(List<Letter> letters, double ang)
    {
        var ls = letters.Select(l =>
        {
            var a = Frame(l.StartBaseLine, ang); var b = Frame(l.EndBaseLine, ang);
            var size = l.PointSize > 0 ? l.PointSize : Math.Max(3, Math.Min(l.GlyphRectangle.Height, l.GlyphRectangle.Width));
            return (v: l.Value, s: Math.Min(a.x, b.x), e: Math.Max(a.x, b.x), y: a.y, size);
        }).OrderBy(l => l.y).ToList();
        var rows = new List<(double y, List<(string v, double s, double e, double y, double size)> ls)>();
        foreach (var l in ls)
        {
            if (rows.Count > 0 && l.y - rows[^1].y < 1.0) rows[^1].ls.Add(l);
            else rows.Add((l.y, new() { l }));
        }
        var res = new List<Wd>();
        foreach (var (y, row) in rows)
        {
            row.Sort((a, b) => a.s.CompareTo(b.s));
            var cur = new List<(string v, double s, double e, double y, double size)>();
            void Flush()
            {
                if (cur.Count == 0) return;
                res.Add(new Wd(string.Concat(cur.Select(c => c.v)), cur[0].s, cur.Max(c => c.e), y, cur.Max(c => c.size)));
                cur.Clear();
            }
            foreach (var l in row)
            {
                if (string.IsNullOrWhiteSpace(l.v)) { Flush(); continue; }
                if (cur.Count > 0 && l.s - cur.Max(c => c.e) > 0.25 * l.size) Flush();
                cur.Add(l);
            }
            Flush();
        }
        return res;
    }

    /// <summary>
    /// The page's straight lines (and the edges of its boxes), for the lines over and under altitudes. Worked out from
    /// the page's drawing instructions here, following each "move the origin" (cm) before a line: the PDF reader's own
    /// path list leaves those out, so FQM3's underlines ("q 1 0 0 1 249.96 361.03 cm 0 0 m 0 -20.88 l S Q") came out
    /// at the page's corner.
    /// </summary>
    public static string? LastSegmentError;

    private static List<(double ang, PdfPoint a, PdfPoint b)> Segments(Page page)
    {
        var res = new List<(double, PdfPoint, PdfPoint)>();
        try
        {
            var ctm = new double[] { 1, 0, 0, 1, 0, 0 };
            var stack = new Stack<double[]>();
            PdfPoint T(double x, double y) => new(ctm[0] * x + ctm[2] * y + ctm[4], ctm[1] * x + ctm[3] * y + ctm[5]);
            static double D(object v) => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
            PdfPoint cur = default, start = default;
            foreach (var op in page.Operations)
            {
              try
              {
                dynamic d = op;
                switch (op.Operator)
                {
                    case "q": stack.Push((double[])ctm.Clone()); break;
                    case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                    case "cm":
                    {
                        var v = new List<double>();
                        foreach (var x in (System.Collections.IEnumerable)d.Value) v.Add(D(x));
                        if (v.Count < 6) break;
                        // The new matrix times the current one.
                        ctm = new[]
                        {
                            v[0] * ctm[0] + v[1] * ctm[2], v[0] * ctm[1] + v[1] * ctm[3],
                            v[2] * ctm[0] + v[3] * ctm[2], v[2] * ctm[1] + v[3] * ctm[3],
                            v[4] * ctm[0] + v[5] * ctm[2] + ctm[4], v[4] * ctm[1] + v[5] * ctm[3] + ctm[5],
                        };
                        break;
                    }
                    case "m": cur = start = T(D(d.X), D(d.Y)); break;
                    case "l": { var p = T(D(d.X), D(d.Y)); res.Add((0, cur, p)); cur = p; break; }
                    case "h": res.Add((0, cur, start)); cur = start; break;
                    case "re":
                    {
                        double x0 = D(d.LowerLeftX), y0 = D(d.LowerLeftY), w = D(d.Width), h = D(d.Height);
                        PdfPoint p1 = T(x0, y0), p2 = T(x0 + w, y0), p3 = T(x0 + w, y0 + h), p4 = T(x0, y0 + h);
                        res.Add((0, p1, p2)); res.Add((0, p2, p3)); res.Add((0, p3, p4)); res.Add((0, p4, p1));
                        cur = start = p1;
                        break;
                    }
                }
              }
              catch (Exception ex) { LastSegmentError ??= op.Operator + ": " + ex.Message; }
            }
        }
        catch (Exception ex) { LastSegmentError ??= ex.Message; }
        return res;
    }

    /// <summary>Whether a word has a line under it and/or over it (a chart's "at or above" / "at or below").</summary>
    private static (bool under, bool over) Marks(Wd w, List<((double x, double y) a, (double x, double y) b)> segs)
    {
        bool under = false, over = false;
        var len = w.X1 - w.X0;
        foreach (var (a, b) in segs)
        {
            if (Math.Abs(a.y - b.y) > 0.6) continue;
            double lo = Math.Min(a.x, b.x), hi = Math.Max(a.x, b.x);
            var cover = Math.Max(0, Math.Min(hi, w.X1) - Math.Max(lo, w.X0));
            if (cover < 0.7 * len || hi - lo > 3 * len + 4) continue;
            var dy = a.y - w.Y;
            if (dy > 0 && dy < 0.7 * w.Size) under = true;
            if (dy < -0.7 * w.Size && dy > -1.6 * w.Size) over = true;
        }
        return (under, over);
    }

    /// <summary>The expects among one frame's words: sentences ("Expect clearance to cross at …") and plan view labels ("Expect 13000").</summary>
    private static List<StarExpect> Read(List<Wd> words, List<((double x, double y) a, (double x, double y) b)> segs, HashSet<string> fixes, string aptName)
    {
        var res = new List<StarExpect>();
        string Fx(Wd w) => w.U.Trim(',', '.', ':');
        var fixWords = words.Where(w => fixes.Contains(Fx(w))).ToList();
        var turbojet = words.Where(w => w.U == "TURBOJET").ToList();
        var headers = fixWords.Where(w => turbojet.Any(tj => tj.Y - w.Y is > 0 and < 22 && Math.Abs(tj.Cx - w.Cx) < 90)).ToList();
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
            for (var lj = li + 1; lj < lines.Count && !sentence[^1].Text.EndsWith('.') && sentence.Count < 40; lj++)
            {
                if (lines[lj].y - lastY > e.Size * 2.2) break;
                var startW = lines[lj].words.FirstOrDefault(w => Math.Abs(w.X0 - left) < 6);
                if (startW is null) continue;
                sentence.AddRange(Run(lines[lj].words, startW, false));
                lastY = lines[lj].y;
            }
            var s = Regex.Replace(string.Join(" ", sentence.Select(w => w.U)), @"\s+", " ");
            var m = Regex.Match(s, @"^EXPECT (?:CLEARANCE )?TO CROSS AT (OR ABOVE |OR BELOW )?(?:FL(\d{3})|(\d{4,5})(?:['’]|FT)?|(\d{3}) ?(?:K|KT|KTS)\b|(LOWEST USABLE FLIGHT LEVEL))(?:,? (?:AND|AT) (\d{3}) ?(?:K|KT|KTS)\b)?(?<rest>.*)$");
            if (m.Success)
            {
                // The fix: the planning box header above this sentence, over the same column; else a fix's name just
                // above it (a label on the plan view).
                var hdr = headers.Where(h => h.Y < e.Y && e.Y - h.Y < 170 && Math.Abs(h.Cx - (left + 75)) < 110).OrderBy(h => e.Y - h.Y).FirstOrDefault()
                    ?? fixWords.Where(h => e.Y - h.Y > 0 && e.Y - h.Y < e.Size * 3.5 && h.Cx > left - 30 && h.Cx < left + 150).OrderBy(h => e.Y - h.Y).FirstOrDefault();
                if (hdr is null) continue;
                double mn = 0, mx = 0, kt = 0;
                var how = m.Groups[1].Value.Trim();
                // The lowest usable flight level: FL180 (higher only when the altimeter is low).
                double alt = m.Groups[2].Success ? double.Parse(m.Groups[2].Value) * 100 : m.Groups[3].Success ? double.Parse(m.Groups[3].Value) : m.Groups[5].Success ? 18000 : 0;
                if (alt is > 0 and (< 1000 or > 45000)) continue;
                if (alt > 0) { mn = how == "OR BELOW" ? 0 : alt; mx = how == "OR ABOVE" ? 0 : alt; }
                if (m.Groups[4].Success) kt = double.Parse(m.Groups[4].Value);
                if (m.Groups[6].Success) kt = double.Parse(m.Groups[6].Value);
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
                res.Add(new StarExpect(Fx(hdr), mn, mx, kt, when, headers.Contains(hdr) ? "planning box" : "plan view"));
                continue;
            }
            // "Expect 13000", "Expect FL240", "Expect 11000 / 250K" beside a fix: the words right after it on its line
            // (or, if none, starting the line below), the altitude's line under it meaning "or above", over it "or below".
            var after = lines[li].words.Where(w => w.X0 >= e.X1 - 1 && w.X0 - e.X1 < 20).Take(4).ToList();
            if (after.Count == 0 && li + 1 < lines.Count && lines[li + 1].y - lines[li].y < e.Size * 2.2)
                after = lines[li + 1].words.Where(w => Math.Abs(w.X0 - e.X0) < 20).Take(4).ToList();
            double? altFt = null; Wd? altW = null; double ktV = 0;
            foreach (var w in after)
            {
                foreach (var part in w.U.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    var tok = part.Trim(',', '.');
                    if (altFt is null && AltTok.Match(tok) is { Success: true } am)
                    {
                        var v = double.Parse(am.Groups[2].Value) * (am.Groups[1].Success ? 100 : 1);
                        if (v is >= 1000 and <= 45000) { altFt = v; altW = w; }
                    }
                    else if (KtTok.Match(tok) is { Success: true } km) ktV = double.Parse(km.Groups[1].Value);
                }
                if (altFt is null && ktV == 0) break;   // "Expect vectors …": not a restriction
            }
            if (altFt is null && ktV == 0) continue;
            var fix = fixWords.Select(o => (o, d: Math.Sqrt(Math.Pow(o.Cx - e.Cx, 2) + Math.Pow(o.Y - e.Y, 2))))
                .Where(x => x.d < 160).OrderBy(x => x.d).Select(x => Fx(x.o)).FirstOrDefault();
            if (fix is null) continue;
            double mn2 = 0, mx2 = 0;
            if (altFt is { } av)
            {
                var (under, over) = altW is null ? (false, false) : Marks(altW, segs);
                mn2 = over && !under ? 0 : av;
                mx2 = under && !over ? 0 : av;
            }
            res.Add(new StarExpect(fix, mn2, mx2, ktV, "", "plan view"));
        }
        return res;
    }

    /// <summary>For the offline checks: how a word is seen (its frame position and size) and the straight lines near it.</summary>
    public static string Diagnose(PdfDocument doc, string word)
    {
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            var segsAll = Segments(page);
            sb.Append($"paths {segsAll.Count} lines{(LastSegmentError is null ? "" : " (" + LastSegmentError + ")")}; ");
            foreach (var (ang, letters) in Frames(page))
            {
                var segs = segsAll.Select(sg => (a: Frame(sg.a, ang), b: Frame(sg.b, ang))).ToList();
                foreach (var w in Words(letters, ang).Where(w => w.U == word))
                {
                    var l0 = letters.First(l => l.Value == word[..1]);
                    sb.Append($"[{ang}°] {w.Text} x {w.X0:0.0}–{w.X1:0.0} y {w.Y:0.0} size {w.Size:0.0} (pt {l0.PointSize:0.0} font {l0.FontSize:0.0} rect {l0.GlyphRectangle.Width:0.0}x{l0.GlyphRectangle.Height:0.0}); near: ");
                    foreach (var (a, b) in segs.Where(sg => Math.Abs(sg.a.y - w.Y) < 5 && Math.Max(sg.a.x, sg.b.x) > w.X0 - 5 && Math.Min(sg.a.x, sg.b.x) < w.X1 + 5).Take(8))
                        sb.Append($"({a.x:0.0},{a.y:0.0})-({b.x:0.0},{b.y:0.0}) ");
                    sb.Append("; ");
                }
            }
        }
        return sb.ToString();
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
