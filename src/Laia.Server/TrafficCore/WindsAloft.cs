namespace Laia.Server.TrafficCore;

/// <summary>
/// The wind at every altitude, so LAIA's aircraft fly in the same air as the user (user, 17 Nov: "If injected aircraft
/// doesn't have wind and the user does … IAS despite being the same number, will not match"). Two sources:
/// <list type="bullet">
/// <item>the wind MSFS gives the user's aircraft, sampled as they fly — the reference: near where (and at the levels)
/// the user has flown recently, LAIA's aircraft get the user's wind;</item>
/// <item>the winds-aloft forecast (Open-Meteo's GFS pressure-level winds on a grid over the US, 3,000–39,000 ft),
/// for the levels and places the user hasn't flown, corrected by what the user found where both are known.</item>
/// </list>
/// The surface wind (METAR) still sets the runways; below the lowest forecast level the wind blends from the surface
/// up to it. With neither source, the surface wind is used at every level (as before).
/// </summary>
public sealed class WindsAloft
{
    /// <summary>The forecast levels (feet MSL).</summary>
    public static readonly double[] Levels = { 3000, 6000, 9000, 12000, 18000, 24000, 30000, 34000, 39000 };

    /// <summary>A forecast station: where it is, its elevation, and the wind at each level (vector toward which the air moves, kt; null where not given).</summary>
    public sealed record Station(string Id, double Lat, double Lon, double ElevFt, V2?[] Winds);

    private sealed record Sample(DateTime At, double Lat, double Lon, double AltFt, V2 Delta);

    private readonly object _gate = new();
    private List<Station> _stations = new();
    private readonly List<Sample> _samples = new();
    private Sample? _last;

    public DateTime ForecastAt { get; private set; }
    public int StationCount { get { lock (_gate) return _stations.Count; } }
    public int SampleCount { get { lock (_gate) return _samples.Count; } }

    // ------------------------------------------------------------------ the forecast text

    /// <summary>
    /// Reads the FD/FB winds-aloft text: a "FT  3000    6000 …" header, then one line per station
    /// ("BHM 3010 2711+10 2717+05 … 780950"). Each level's field ends under the end of its heading; blank where the
    /// level is below the station. DDff: direction in tens of degrees, speed in knots; DD + 50 means speed + 100;
    /// 9900 is light and variable.
    /// </summary>
    public static List<(string id, V2?[] winds)> ParseFd(string text)
    {
        var res = new List<(string, V2?[])>();
        List<(double level, int end)>? cols = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("FT ") || line.TrimStart().StartsWith("FT\t"))
            {
                cols = new();
                var i = 0;
                while (i < line.Length)
                {
                    while (i < line.Length && line[i] == ' ') i++;
                    var s = i;
                    while (i < line.Length && line[i] != ' ') i++;
                    if (i > s && double.TryParse(line[s..i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var lv)) cols.Add((lv, i));
                }
                continue;
            }
            if (cols is null || cols.Count == 0 || line.Length < 4) continue;
            var id = line.Length >= 3 ? line[..3].Trim() : "";
            if (id.Length < 3 || !id.All(char.IsLetterOrDigit) || line.Length > 3 && line[3] != ' ') continue;
            var winds = new V2?[Levels.Length];
            var any = false;
            var prevEnd = 3;
            foreach (var (level, end) in cols)
            {
                var field = line.Length > prevEnd ? line[prevEnd..Math.Min(end, line.Length)].Trim() : "";
                prevEnd = end;
                var k = Array.IndexOf(Levels, level);
                if (k < 0 || field.Length < 4) continue;
                if (DecodeField(field) is { } w) { winds[k] = w; any = true; }
            }
            if (any) res.Add((id, winds));
        }
        return res;
    }

    /// <summary>The pressure levels asked of Open-Meteo and their standard-atmosphere heights (ft).</summary>
    public static readonly (int hPa, double ft)[] PressureLevels =
        { (925, 2500), (850, 4780), (700, 9880), (600, 13800), (500, 18290), (400, 23570), (300, 30060), (250, 33990), (200, 38660) };

    /// <summary>The "hourly=" list for an Open-Meteo request: wind speed and direction at each pressure level.</summary>
    public static string OpenMeteoHourly() =>
        string.Join(",", PressureLevels.SelectMany(p => new[] { $"wind_speed_{p.hPa}hPa", $"wind_direction_{p.hPa}hPa" }));

    /// <summary>
    /// Reads an Open-Meteo forecast (one location: an object; several: an array of them) as stations: for each, the hour
    /// nearest now, the pressure-level winds turned into the forecast levels (between two pressure levels by their
    /// standard heights), and none within 1,500 ft of the ground (as the FD leaves them out).
    /// </summary>
    public static List<Station> ParseOpenMeteo(string json, DateTime nowUtc)
    {
        var res = new List<Station>();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList() : new() { doc.RootElement };
        foreach (var it in items)
        {
            if (!it.TryGetProperty("latitude", out var la) || !it.TryGetProperty("longitude", out var lo) || !it.TryGetProperty("hourly", out var h)) continue;
            var elevFt = it.TryGetProperty("elevation", out var el) && el.ValueKind == System.Text.Json.JsonValueKind.Number ? el.GetDouble() * 3.28084 : 0;
            if (!h.TryGetProperty("time", out var times)) continue;
            var best = -1; var bestD = double.MaxValue; var idx = 0;
            foreach (var t in times.EnumerateArray())
            {
                if (DateTime.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at))
                {
                    var d = Math.Abs((at - nowUtc).TotalMinutes);
                    if (d < bestD) { bestD = d; best = idx; }
                }
                idx++;
            }
            if (best < 0) continue;
            double? Num(string name) => h.TryGetProperty(name, out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array
                && arr.GetArrayLength() > best && arr[best].ValueKind == System.Text.Json.JsonValueKind.Number ? arr[best].GetDouble() : null;
            var pl = new List<(double ft, V2 w)>();
            foreach (var (hPa, ft) in PressureLevels)
                if (Num($"wind_speed_{hPa}hPa") is { } kt && Num($"wind_direction_{hPa}hPa") is { } dir) pl.Add((ft, FromWind(dir, kt)));
            if (pl.Count < 2) continue;
            var winds = new V2?[Levels.Length];
            for (var k = 0; k < Levels.Length; k++)
            {
                var z = Levels[k];
                if (z < elevFt + 1500) continue;
                if (z <= pl[0].ft) { winds[k] = pl[0].w; continue; }
                if (z >= pl[^1].ft) { winds[k] = pl[^1].w; continue; }
                for (var j = 1; j < pl.Count; j++)
                    if (z <= pl[j].ft)
                    {
                        var f = (z - pl[j - 1].ft) / (pl[j].ft - pl[j - 1].ft);
                        winds[k] = pl[j - 1].w + (pl[j].w - pl[j - 1].w) * f;
                        break;
                    }
            }
            var lat = la.GetDouble(); var lon = lo.GetDouble();
            res.Add(new Station($"{lat:0.0},{lon:0.0}", lat, lon, elevFt, winds));
        }
        return res;
    }

    /// <summary>One DDff[±TT] field as a wind vector (kt), or null if unreadable.</summary>
    public static V2? DecodeField(string f)
    {
        if (f.Length < 4 || !int.TryParse(f[..2], out var dd) || !int.TryParse(f[2..4], out var ff)) return null;
        if (dd == 99 && ff == 0) return new V2(0, 0);   // light and variable
        if (dd >= 51 && dd <= 86) { dd -= 50; ff += 100; }
        if (dd < 1 || dd > 36) return null;
        return FromWind(dd * 10, ff);
    }

    /// <summary>The vector of a wind blowing FROM a direction (true degrees) at a speed (kt): it points where the air moves to.</summary>
    public static V2 FromWind(double fromDeg, double kt) => V2.FromBearing(fromDeg + 180, kt);

    /// <summary>A wind vector back as direction-from and speed.</summary>
    public static Wind ToWind(V2 v) => v.Length < 0.5 ? new Wind(0, 0) : new Wind(Geo2.Norm(v.Bearing + 180), v.Length);

    public void SetForecast(IEnumerable<Station> stations, DateTime at)
    {
        lock (_gate) { _stations = stations.ToList(); ForecastAt = at; }
    }

    // ------------------------------------------------------------------ what the user flies in

    /// <summary>
    /// The wind MSFS gives the user's aircraft (from direction, true; kt) where they are. Kept for three hours: one
    /// every 20 s, or sooner after a 300 ft level change. What's kept is how it differs from the forecast there and
    /// then, so it corrects the forecast nearby.
    /// </summary>
    public void AddUserSample(DateTime at, double lat, double lon, double altFt, double fromDeg, double kt, Wind surface, double surfaceElevFt)
    {
        if (double.IsNaN(kt) || kt < 0 || kt > 300) return;
        lock (_gate)
        {
            if (_last is { } l && (at - l.At).TotalSeconds < 20 && Math.Abs(altFt - l.AltFt) < 300) return;
            var baseV = Base(lat, lon, altFt, surface, surfaceElevFt);
            var s = new Sample(at, lat, lon, altFt, FromWind(fromDeg, kt) - baseV);
            _samples.Add(s); _last = s;
            _samples.RemoveAll(x => (at - x.At).TotalHours > 3);
        }
    }

    // ------------------------------------------------------------------ the wind anywhere

    /// <summary>The wind at a place and altitude (feet MSL) now.</summary>
    public Wind At(DateTime now, double lat, double lon, double altFt, Wind surface, double surfaceElevFt)
    {
        lock (_gate) return ToWind(Vector(now, lat, lon, altFt, surface, surfaceElevFt));
    }

    /// <summary>The wind every 500 ft from 0 to 45,000 ft MSL over one place (for an airport's traffic).</summary>
    public Wind[] Profile(DateTime now, double lat, double lon, Wind surface, double surfaceElevFt)
    {
        lock (_gate)
        {
            var res = new Wind[ProfileSteps];
            for (var i = 0; i < res.Length; i++) res[i] = ToWind(Vector(now, lat, lon, i * ProfileStepFt, surface, surfaceElevFt));
            return res;
        }
    }
    public const double ProfileStepFt = 500;
    public const int ProfileSteps = 91;

    /// <summary>The wind from a profile at an altitude (interpolated between its 500 ft steps).</summary>
    public static Wind FromProfile(Wind[] profile, double altFt)
    {
        var x = Math.Clamp(altFt / ProfileStepFt, 0, profile.Length - 1);
        var i = (int)Math.Floor(x);
        if (i >= profile.Length - 1) return profile[^1];
        var f = x - i;
        var a = profile[i].Vector; var b = profile[i + 1].Vector;
        return ToWind(a + (b - a) * f);
    }

    private V2 Vector(DateTime now, double lat, double lon, double altFt, Wind surface, double surfaceElevFt)
    {
        var v = Base(lat, lon, altFt, surface, surfaceElevFt);
        // What the user found nearby (the closer, the nearer the level and the more recent, the more it counts). A
        // sample right here and now gives the user's wind almost exactly (97%); 100 nm away about 80% of it, 200 nm 30%.
        double sw = 0; var sd = new V2(0, 0);
        foreach (var s in _samples)
        {
            var age = (now - s.At).TotalMinutes;
            if (age < -1) continue;
            var dz = (altFt - s.AltFt) / 1500;
            if (Math.Abs(dz) > 3) continue;
            var d = Geo2.DistanceNm(lat, lon, s.Lat, s.Lon);
            if (d > 400) continue;
            var w = Math.Exp(-d / 46) * Math.Exp(-dz * dz) * Math.Exp(-Math.Max(0, age) / 60);
            sw += w; sd += s.Delta * w;
        }
        return sw > 1e-6 ? v + sd * (1 / (sw + 0.03)) : v;
    }

    /// <summary>The forecast at a place and altitude, blended down to the surface wind below its lowest level; the surface wind everywhere with no forecast near.</summary>
    private V2 Base(double lat, double lon, double altFt, Wind surface, double surfaceElevFt)
    {
        var sfc = surface.Vector;
        // The nearest stations (up to 4, within 300 nm), by inverse distance squared.
        var near = new List<(Station s, double d)>();
        foreach (var s in _stations)
        {
            var d = Geo2.DistanceNm(lat, lon, s.Lat, s.Lon);
            if (d > 300) continue;
            near.Add((s, d));
        }
        if (near.Count == 0) return sfc;
        near.Sort((a, b) => a.d.CompareTo(b.d));
        if (near.Count > 4) near.RemoveRange(4, near.Count - 4);
        var lv = new V2?[Levels.Length];
        for (var k = 0; k < Levels.Length; k++)
        {
            double tw = 0; var acc = new V2(0, 0);
            foreach (var (s, d) in near)
            {
                if (s.Winds[k] is not { } w) continue;
                var wt = 1 / Math.Max(1, d * d);
                tw += wt; acc += w * wt;
            }
            if (tw > 0) lv[k] = acc * (1 / tw);
        }
        // The lowest and highest levels known here.
        var lo = Array.FindIndex(lv, x => x is not null);
        if (lo < 0) return sfc;
        var hi = Array.FindLastIndex(lv, x => x is not null);
        if (altFt >= Levels[hi]) return lv[hi]!.Value;
        if (altFt <= Levels[lo])
        {
            var bottom = Math.Min(surfaceElevFt, Levels[lo] - 1);
            if (altFt <= bottom) return sfc;
            var f = (altFt - bottom) / (Levels[lo] - bottom);
            return sfc + (lv[lo]!.Value - sfc) * f;
        }
        // Between the known levels either side.
        var below = lo; var above = hi;
        for (var k = lo; k <= hi; k++)
        {
            if (lv[k] is null) continue;
            if (Levels[k] <= altFt) below = k;
            if (Levels[k] >= altFt) { above = k; break; }
        }
        if (above == below) return lv[below]!.Value;
        var a = lv[below]!.Value; var b = lv[above]!.Value;
        return a + (b - a) * ((altFt - Levels[below]) / (Levels[above] - Levels[below]));
    }

    /// <summary>A line for the log: what's known.</summary>
    public string Describe(DateTime now)
    {
        lock (_gate)
            return $"{_stations.Count} forecast stations{(_stations.Count > 0 ? $" (from {ForecastAt:HH:mm}Z)" : "")}, {_samples.Count} readings of the user's wind in the last 3 hours";
    }
}
