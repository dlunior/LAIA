using System.Text.Json;

namespace Laia.Server.TrafficCore;

/// <summary>
/// The learned ways in (Step 6, part 2, user 28 Nov: "ok lets do release three"). A small neural network, trained on the
/// LAIA PC's graphics card from the real arrivals at routegen's hubs (learning/train_routes.py), that gives the way real
/// traffic flies from where an arrival comes within 45 nm of the airport to the runway it lands on: several candidate
/// ways (the way the traffic really splits — left or right downwind, straight in, a longer way round when busy), each
/// with how likely real controllers would use it. Everything is in the runway's own frame (miles out along the
/// approach, miles left of the centreline), so what's learned at one airport carries to another. The airport and runway
/// are given to it as learned codes; an airport it has never seen uses the "unknown" code — the general way traffic flows.
///
/// LAIA Server runs it here, in C# (a few matrix products), from the weights the training writes as JSON — nothing to
/// install for flying. Its ways in still pass the route check like any other (and a refused one falls back to the next
/// candidate, then to the standard way in).
/// </summary>
public sealed class RouteModel
{
    /// <summary>Features: where it comes in (miles out, miles left, height above the field), the way it's going there
    /// against the runway's heading, the wind along and across the runway, and how busy it is (arrivals landing within
    /// half an hour either side, to this runway and to all).</summary>
    public const int F = 9;
    /// <summary>Points along each way in (equal steps of its length, the last at the threshold).</summary>
    public const int P = 24;

    public int K { get; private set; }
    public string Id { get; private set; } = "";
    public string Trained { get; private set; } = "";
    private Dictionary<string, int> _airports = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _runways = new(StringComparer.OrdinalIgnoreCase);
    private float[][] _embA = Array.Empty<float[]>(), _embR = Array.Empty<float[]>();
    private readonly List<(float[,] w, float[] b, bool relu)> _layers = new();

    public bool Knows(string icao) => _airports.ContainsKey(icao);

    /// <summary>The features of one arrival coming in at <paramref name="at"/> for runway <paramref name="rwy"/>.</summary>
    public static float[] Features(RunwayEnd rwy, V2 at, double altFt, double trackDeg, Wind wind, int loadRunway, int loadAll)
    {
        var nm = Geo2.MPerNm;
        var x = -rwy.Along(at) / nm; var y = rwy.Offset(at) / nm;
        var d = (trackDeg - rwy.Heading) * Math.PI / 180;
        var head = wind.Headwind(rwy.Heading); var cross = V2.Cross(rwy.Dir, wind.Vector);
        return new[]
        {
            (float)(x / 30), (float)(y / 30), (float)((altFt - rwy.ElevationFt) / 10000), (float)Math.Sin(d), (float)Math.Cos(d),
            (float)(head / 30), (float)(cross / 30), (float)(loadRunway / 20.0), (float)(loadAll / 40.0),
        };
    }

    /// <summary>A position in the runway's frame (miles out along the approach, miles left), back to the airport map.</summary>
    public static V2 FromFrame(RunwayEnd rwy, double outNm, double leftNm)
    {
        var nm = Geo2.MPerNm;
        var left = new V2(-rwy.Dir.Y, rwy.Dir.X);
        return rwy.Threshold - rwy.Dir * (outNm * nm) + left * (leftNm * nm);
    }

    /// <summary>A real way in as the training target: <see cref="P"/> points at equal steps of its length — miles out,
    /// miles left (both /30) and height above the field (/10,000 ft) — then its length (/100 nm).</summary>
    public static float[]? Target(RunwayEnd rwy, IReadOnlyList<(V2 p, double altFt)> pts)
    {
        if (pts.Count < 2) return null;
        var nm = Geo2.MPerNm;
        var cum = new double[pts.Count];
        for (var i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + (pts[i].p - pts[i - 1].p).Length;
        var total = cum[^1];
        if (total < 5 * nm) return null;
        var res = new float[P * 3 + 1];
        var j = 0;
        for (var k = 1; k <= P; k++)
        {
            var s = total * k / P;
            while (j + 1 < pts.Count - 1 && cum[j + 1] < s) j++;
            var seg = cum[j + 1] - cum[j];
            var f = seg < 1e-6 ? 0 : Math.Clamp((s - cum[j]) / seg, 0, 1);
            var q = pts[j].p + (pts[j + 1].p - pts[j].p) * f;
            var a = pts[j].altFt + (pts[j + 1].altFt - pts[j].altFt) * f;
            res[(k - 1) * 3] = (float)(-rwy.Along(q) / nm / 30);
            res[(k - 1) * 3 + 1] = (float)(rwy.Offset(q) / nm / 30);
            res[(k - 1) * 3 + 2] = (float)((a - rwy.ElevationFt) / 10000);
        }
        res[P * 3] = (float)(total / nm / 100);
        return res;
    }

    public sealed record Way(double Prob, double LengthNm, List<V2> Points, List<double> AltFt);

    /// <summary>The candidate ways in, most likely first.</summary>
    public List<Way> Predict(string icao, RunwayEnd rwy, float[] features)
    {
        var a = _airports.GetValueOrDefault(icao, 0);
        var r = _runways.GetValueOrDefault($"{icao}/{rwy.Ident}", 0);
        if (a >= _embA.Length) a = 0;
        if (r >= _embR.Length) r = 0;
        var x = _embA[a].Concat(_embR[r]).Concat(features).ToArray();
        foreach (var (w, b, relu) in _layers)
        {
            var o = new float[b.Length];
            for (var i = 0; i < o.Length; i++)
            {
                double s = b[i];
                for (var k = 0; k < x.Length; k++) s += w[i, k] * x[k];
                o[i] = relu && s < 0 ? 0 : (float)s;
            }
            x = o;
        }
        // Output: K ways (P points × 3), then K scores, then K lengths.
        var scores = new double[K];
        for (var k = 0; k < K; k++) scores[k] = x[K * P * 3 + k];
        var max = scores.Max();
        var ex = scores.Select(s => Math.Exp(s - max)).ToArray();
        var sum = ex.Sum();
        var res = new List<Way>();
        for (var k = 0; k < K; k++)
        {
            var pts = new List<V2>(); var alts = new List<double>();
            for (var i = 0; i < P; i++)
            {
                var o = (k * P + i) * 3;
                pts.Add(FromFrame(rwy, x[o] * 30, x[o + 1] * 30));
                alts.Add(rwy.ElevationFt + x[o + 2] * 10000);
            }
            res.Add(new Way(ex[k] / sum, x[K * P * 3 + K + k] * 100, pts, alts));
        }
        return res.OrderByDescending(w => w.Prob).ToList();
    }

    /// <summary>Reads the weights the training writes (learning/train_routes.py): null if missing or not readable.</summary>
    public static RouteModel? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.GetProperty("f").GetInt32() != F || root.GetProperty("p").GetInt32() != P) return null;
            var m = new RouteModel { K = root.GetProperty("k").GetInt32(), Id = root.GetProperty("id").GetString() ?? "", Trained = root.TryGetProperty("trained", out var tr) ? tr.GetString() ?? "" : "" };
            foreach (var p in root.GetProperty("airports").EnumerateObject()) m._airports[p.Name] = p.Value.GetInt32();
            foreach (var p in root.GetProperty("runways").EnumerateObject()) m._runways[p.Name] = p.Value.GetInt32();
            m._embA = Rows(root.GetProperty("emb_airport"));
            m._embR = Rows(root.GetProperty("emb_runway"));
            foreach (var l in root.GetProperty("layers").EnumerateArray())
            {
                var rows = Rows(l.GetProperty("w"));
                var w = new float[rows.Length, rows.Length == 0 ? 0 : rows[0].Length];
                for (var i = 0; i < rows.Length; i++) for (var k = 0; k < rows[i].Length; k++) w[i, k] = rows[i][k];
                m._layers.Add((w, l.GetProperty("b").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray(), l.GetProperty("relu").GetBoolean()));
            }
            var outN = m._layers.Count == 0 ? 0 : m._layers[^1].b.Length;
            if (outN != m.K * P * 3 + 2 * m.K) return null;
            return m;
        }
        catch { return null; }
    }

    private static float[][] Rows(JsonElement e) => e.EnumerateArray().Select(r => r.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray()).ToArray();
}
