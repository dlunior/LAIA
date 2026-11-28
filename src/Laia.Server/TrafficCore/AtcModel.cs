using System.Text.Json;

namespace Laia.Server.TrafficCore;

/// <summary>
/// The controller's techniques learned from the busy hubs (Step 6, Release 4b; user, 28 Nov: "just learn how real ATC
/// handles sequencing and speed control"; "learn the techniques from those busy airports and apply that to all
/// airports"). Never a route — the decisions a controller makes about the traffic LAIA routes itself:
/// - the speeds controllers have arrivals fly at each distance to go when nothing is in the way (by heavy, narrowbody
///   and regional jet);
/// - how they take time out with speed: how much slower than that, where, for how much time an arrival has to lose and
///   how busy it is (a small network);
/// - the landing order: which arrivals they let go first against when each would land on its own — straight-in ones,
///   ones already low, heavies… (a small network trained to rank real landing orders);
/// - the gap they leave at the threshold behind each kind of leader when the queue is pressed.
/// Learned per airport where there's data, and across all the hubs for every other airport (the airport's code is left
/// out one time in ten in training, so the networks know the general way too). The safety rules — wake and radar
/// separation, runway occupancy, published speed limits, 250 kt below 10,000 ft, the slowest speeds a jet flies —
/// always stand: these only choose within them.
/// </summary>
public sealed class AtcModel
{
    public string Id { get; private set; } = "";
    public string Trained { get; private set; } = "";
    private Dictionary<string, int> _airports = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Speed (IAS, kt) when nothing is in the way, at 1-nm steps of distance to go from <see cref="NomFrom"/>: [class][step], per airport and "general".</summary>
    private Dictionary<string, double[][]> _nominal = new(StringComparer.OrdinalIgnoreCase);
    public const int NomFrom = 3, NomTo = 45;
    private Mlp? _speed, _order;
    /// <summary>Gap (s) at the threshold behind a leader of each class for a follower of each class: "L-F" → s, per airport and "general".</summary>
    private Dictionary<string, Dictionary<string, double>> _gaps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>0 regional jet or turboprop, 1 narrowbody or 757, 2 widebody or heavy.</summary>
    public static int Class(Perf p) => p == Perf.Jumbo || p == Perf.Widebody ? 2 : p == Perf.Narrowbody || p == Perf.B757 ? 1 : 0;

    /// <summary>The speed controllers have this class fly here at this distance to go when nothing's in the way (IAS, kt); NaN beyond what's learned.</summary>
    public double NominalIas(string icao, int cls, double dtgNm)
    {
        if (dtgNm < NomFrom || dtgNm > NomTo) return double.NaN;
        var v = Nominal(_nominal.GetValueOrDefault(icao), cls, dtgNm);
        return double.IsNaN(v) && !icao.Equals("general", StringComparison.OrdinalIgnoreCase) ? Nominal(_nominal.GetValueOrDefault("general"), cls, dtgNm) : v;
    }

    private static double Nominal(double[][]? t, int cls, double dtgNm)
    {
        if (t is null || cls >= t.Length || t[cls].Length == 0) return double.NaN;
        var row = t[cls];
        var x = dtgNm - NomFrom; var i = (int)Math.Floor(x); var f = x - i;
        if (i >= row.Length - 1) return row[^1];
        var a = row[i]; var b = row[i + 1];
        return double.IsNaN(a) ? b : double.IsNaN(b) ? a : a + (b - a) * f;
    }

    /// <summary>How much of that speed controllers have it fly when it has time to lose (a ratio, 1 = none slower).</summary>
    public double SpeedRatio(string icao, int cls, double dtgNm, double altAglFt, double loseSec, int load)
    {
        if (_speed is null) return 1;
        var x = new[] { cls == 0 ? 1f : 0f, cls == 1 ? 1f : 0f, cls == 2 ? 1f : 0f, (float)(dtgNm / 45), (float)(altAglFt / 20000), (float)(loseSec / 600), (float)(load / 20.0) };
        return Math.Clamp(_speed.Forward(_airports.GetValueOrDefault(icao, 0), x)[0], 0.6, 1.15);
    }

    /// <summary>
    /// Seconds to add to an arrival's own landing time for the landing order: how much later (or sooner, negative) real
    /// controllers have it land than the one it's level with, from where it is (distance to go, height, speed, the
    /// side it comes from) and its class.
    /// </summary>
    public double OrderOffset(string icao, int cls, double dtgNm, double altAglFt, double gsKt, double sideSin, double sideCos, double offsetNm, int load)
    {
        if (_order is null) return 0;
        var x = new[] { cls == 0 ? 1f : 0f, cls == 1 ? 1f : 0f, cls == 2 ? 1f : 0f, (float)(dtgNm / 45), (float)(altAglFt / 20000), (float)(gsKt / 300), (float)sideSin, (float)sideCos, (float)(offsetNm / 30), (float)(load / 20.0) };
        return Math.Clamp(_order.Forward(_airports.GetValueOrDefault(icao, 0), x)[0] * 60, -240, 240);
    }

    /// <summary>The gap (s) controllers leave at the threshold behind this leader when the queue is pressed; NaN if not learned.</summary>
    public double TargetGap(string icao, int leaderCls, int cls)
    {
        var key = $"{leaderCls}-{cls}";
        if (_gaps.GetValueOrDefault(icao) is { } a && a.TryGetValue(key, out var g)) return g;
        return _gaps.GetValueOrDefault("general") is { } gen && gen.TryGetValue(key, out var g2) ? g2 : double.NaN;
    }

    public bool KnowsAirport(string icao) => _airports.ContainsKey(icao);

    public static AtcModel? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            var m = new AtcModel { Id = r.GetProperty("id").GetString() ?? "", Trained = r.TryGetProperty("trained", out var tr) ? tr.GetString() ?? "" : "" };
            foreach (var p in r.GetProperty("airports").EnumerateObject()) m._airports[p.Name] = p.Value.GetInt32();
            foreach (var p in r.GetProperty("nominal").EnumerateObject())
                m._nominal[p.Name] = p.Value.EnumerateArray().Select(row => row.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN).ToArray()).ToArray();
            foreach (var p in r.GetProperty("gaps").EnumerateObject())
                m._gaps[p.Name] = p.Value.EnumerateObject().ToDictionary(q => q.Name, q => q.Value.GetDouble());
            if (r.TryGetProperty("speed", out var sp)) m._speed = Mlp.Read(sp);
            if (r.TryGetProperty("order", out var od)) m._order = Mlp.Read(od);
            return m;
        }
        catch { return null; }
    }
}

/// <summary>A small network as the training writes it: an airport code (embedding) and the inputs, through layers.</summary>
public sealed class Mlp
{
    private float[][] _emb = Array.Empty<float[]>();
    private readonly List<(float[,] w, float[] b, bool relu)> _layers = new();

    public float[] Forward(int airport, float[] x)
    {
        if (airport >= _emb.Length) airport = 0;
        var v = (_emb.Length > 0 ? _emb[airport] : Array.Empty<float>()).Concat(x).ToArray();
        foreach (var (w, b, relu) in _layers)
        {
            var o = new float[b.Length];
            for (var i = 0; i < o.Length; i++)
            {
                double s = b[i];
                for (var k = 0; k < v.Length && k < w.GetLength(1); k++) s += w[i, k] * v[k];
                o[i] = relu && s < 0 ? 0 : (float)s;
            }
            v = o;
        }
        return v;
    }

    public static Mlp Read(JsonElement e)
    {
        var m = new Mlp { _emb = Rows(e.GetProperty("emb")) };
        foreach (var l in e.GetProperty("layers").EnumerateArray())
        {
            var rows = Rows(l.GetProperty("w"));
            var w = new float[rows.Length, rows.Length == 0 ? 0 : rows[0].Length];
            for (var i = 0; i < rows.Length; i++) for (var k = 0; k < rows[i].Length; k++) w[i, k] = rows[i][k];
            m._layers.Add((w, l.GetProperty("b").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray(), l.GetProperty("relu").GetBoolean()));
        }
        return m;
    }

    private static float[][] Rows(JsonElement e) => e.EnumerateArray().Select(r => r.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray()).ToArray();
}
