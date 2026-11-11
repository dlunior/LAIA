namespace Laia.Server.TrafficCore;

/// <summary>
/// US en-route navigation data from the FAA's NASR subscription (free, every 28 days): every fix and navaid with its
/// position, and every airway as its sequence of points. Used to fly a filed route string ("… PAINN J53 ODF …")
/// point by point instead of the great circle. Column names are found from each file's header row, so small changes
/// in the FAA's layout don't break it.
/// </summary>
public sealed class NavDb
{
    private readonly Dictionary<string, List<(double lat, double lon)>> _points = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<List<string>>> _airways = new(StringComparer.OrdinalIgnoreCase);
    public int Fixes { get; private set; }
    public int Navaids { get; private set; }
    public int Airways => _airways.Count;
    public List<string> Notes { get; } = new();

    public bool Has(string id) => _points.ContainsKey(id);

    /// <summary>A point by name, the one nearest a reference position when the name is used more than once.</summary>
    public (double lat, double lon)? Point(string id, double nearLat, double nearLon)
    {
        if (!_points.TryGetValue(id, out var l) || l.Count == 0) return null;
        return l.Count == 1 ? l[0] : l.OrderBy(p => Geo2.DistanceNm(nearLat, nearLon, p.lat, p.lon)).First();
    }

    private void AddPoint(string id, double lat, double lon)
    {
        if (id.Length == 0 || Math.Abs(lat) > 90 || Math.Abs(lon) > 180 || (lat == 0 && lon == 0)) return;
        if (!_points.TryGetValue(id, out var l)) _points[id] = l = new();
        if (!l.Any(p => Math.Abs(p.lat - lat) < 1e-4 && Math.Abs(p.lon - lon) < 1e-4)) l.Add((lat, lon));
    }

    private static int Col(string[] head, params string[] names)
    {
        foreach (var n in names)
        {
            var i = Array.FindIndex(head, h => h.Trim().Trim('"').Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    private static int ColLike(string[] head, Func<string, bool> test) => Array.FindIndex(head, h => test(h.Trim().Trim('"').ToUpperInvariant()));

    /// <summary>Fixes or navaids: an id column and decimal latitude/longitude columns.</summary>
    public void AddPoints(IEnumerable<string[]> rows, bool navaids, string file)
    {
        string[]? head = null; int iId = -1, iLat = -1, iLon = -1, n = 0;
        foreach (var r in rows)
        {
            if (head is null)
            {
                head = r;
                iId = Col(head, navaids ? "NAV_ID" : "FIX_ID", "ID", "IDENT");
                if (iId < 0) iId = ColLike(head, h => h.EndsWith("_ID"));
                iLat = Col(head, "LAT_DECIMAL", "LATITUDE_DECIMAL", "LAT_DEC");
                if (iLat < 0) iLat = ColLike(head, h => h.Contains("LAT") && h.Contains("DEC"));
                iLon = Col(head, "LONG_DECIMAL", "LON_DECIMAL", "LONGITUDE_DECIMAL", "LONG_DEC");
                if (iLon < 0) iLon = ColLike(head, h => (h.Contains("LONG") || h.Contains("LON")) && h.Contains("DEC"));
                Notes.Add($"{file}: id={(iId >= 0 ? head[iId] : "?")}, lat={(iLat >= 0 ? head[iLat] : "?")}, lon={(iLon >= 0 ? head[iLon] : "?")}");
                if (iId < 0 || iLat < 0 || iLon < 0) { Notes.Add($"{file} header: {string.Join(",", head.Take(30))}"); return; }
                continue;
            }
            if (r.Length <= Math.Max(iId, Math.Max(iLat, iLon))) continue;
            if (!double.TryParse(r[iLat], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(r[iLon], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon)) continue;
            AddPoint(r[iId].Trim(), lat, lon); n++;
        }
        if (navaids) Navaids += n; else Fixes += n;
    }

    /// <summary>
    /// Airways: either one row per airway with its whole point list in one column (a space-separated "airway
    /// string"), or one row per point with a sequence number. Both are handled.
    /// </summary>
    public void AddAirways(IEnumerable<string[]> rows, string file)
    {
        string[]? head = null; int iId = -1, iStr = -1, iSeq = -1, iPt = -1;
        var seqs = new Dictionary<string, List<(double seq, string pt)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (head is null)
            {
                head = r;
                iId = Col(head, "AWY_ID", "AIRWAY_ID");
                iStr = ColLike(head, h => h.Contains("STRING"));
                iSeq = ColLike(head, h => h.Contains("SEQ"));
                iPt = Col(head, "FROM_POINT", "POINT_ID", "FIX_ID", "NAV_ID", "POINT");
                if (iPt < 0) iPt = ColLike(head, h => h.Contains("POINT") && !h.Contains("SEQ") && !h.Contains("TYPE"));
                Notes.Add($"{file}: id={(iId >= 0 ? head[iId] : "?")}, string={(iStr >= 0 ? head[iStr] : "-")}, seq={(iSeq >= 0 ? head[iSeq] : "-")}, point={(iPt >= 0 ? head[iPt] : "-")}");
                if (iId < 0 || (iStr < 0 && (iSeq < 0 || iPt < 0))) { Notes.Add($"{file} header: {string.Join(",", head.Take(30))}"); return; }
                continue;
            }
            if (r.Length <= iId) continue;
            var id = r[iId].Trim();
            if (id.Length == 0) continue;
            if (iStr >= 0 && iStr < r.Length && r[iStr].Trim().Length > 0)
            {
                var pts = r[iStr].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (pts.Count >= 2) Add(id, pts);
            }
            else if (iSeq >= 0 && iPt >= 0 && r.Length > Math.Max(iSeq, iPt) &&
                     double.TryParse(r[iSeq], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sq))
            {
                if (!seqs.TryGetValue(id, out var l)) seqs[id] = l = new();
                l.Add((sq, r[iPt].Trim()));
            }
        }
        foreach (var (id, l) in seqs)
        {
            var pts = l.OrderBy(x => x.seq).Select(x => x.pt).Where(p => p.Length > 0).ToList();
            // A gap in an airway (it can have several pieces) shows as a repeat; keep the order as given.
            if (pts.Count >= 2) Add(id, pts);
        }

        void Add(string id, List<string> pts)
        {
            if (!_airways.TryGetValue(id, out var list)) _airways[id] = list = new();
            list.Add(pts);
        }
    }

    /// <summary>The points of an airway from one fix to another (inclusive), in flying order; null if not on it.</summary>
    public List<string>? AirwayBetween(string awy, string from, string to)
    {
        if (!_airways.TryGetValue(awy, out var pieces)) return null;
        foreach (var pts in pieces)
        {
            var a = pts.FindIndex(p => p.Equals(from, StringComparison.OrdinalIgnoreCase));
            var b = pts.FindIndex(p => p.Equals(to, StringComparison.OrdinalIgnoreCase));
            if (a < 0 || b < 0) continue;
            return a <= b ? pts.GetRange(a, b - a + 1) : pts.GetRange(b, a - b + 1).AsEnumerable().Reverse().ToList();
        }
        return null;
    }

    /// <summary>
    /// A route string's points ("PAINN J53 ODF Q99 HONIE"): fixes and navaids by name (nearest the point before when a
    /// name is used more than once), airways expanded between the fixes either side. Tokens it can't place (SID and
    /// STAR names, DCT, unknown names) are skipped; the start point resolves the first name.
    /// </summary>
    public List<(string name, double lat, double lon)> Expand(IEnumerable<string> tokens, double startLat, double startLon)
    {
        var res = new List<(string name, double lat, double lon)>();
        var toks = tokens.Select(t => t.Trim().ToUpperInvariant()).Where(t => t.Length > 0 && t != "DCT").ToList();
        double lat = startLat, lon = startLon;
        void Put(string name)
        {
            if (res.Count > 0 && res[^1].name == name) return;
            if (Point(name, lat, lon) is { } p) { res.Add((name, p.lat, p.lon)); (lat, lon) = p; }
        }
        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (_airways.ContainsKey(t) && !Has(t) && res.Count > 0 && i + 1 < toks.Count)
            {
                var seg = AirwayBetween(t, res[^1].name, toks[i + 1]);
                if (seg is not null) foreach (var p in seg.Skip(1)) Put(p);
                continue;
            }
            if (Has(t)) Put(t);
        }
        return res;
    }
}
