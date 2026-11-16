using System.Text.Json;

namespace Laia.Server;

/// <summary>
/// Real surface wind at an airport from its METAR (aviationweather.gov, free, no key), refreshed every 10 minutes, for
/// airports the user isn't at: the sim only reports the wind where the user's aircraft is (at cruise, the wind aloft —
/// which turned CYUL's runways round while the user cruised to Winnipeg).
/// </summary>
public sealed class MetarReader(ILogger<MetarReader> log)
{
    private static readonly HttpClient Http = MakeClient();
    private readonly Dictionary<string, (DateTime at, double? dir, double kt)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _low = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ceiling below 800 ft (the lowest broken, overcast or obscured layer) or visibility below 2 statute miles, from the
    /// latest METAR (false until known): no line up and wait with landing traffic then (7110.65 3-9-4 c).
    /// </summary>
    public bool LowWeather(string icao) { lock (_cache) return _low.GetValueOrDefault(icao); }
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
        return h;
    }

    /// <summary>The METAR wind (direction true degrees, null when calm/variable; speed kt), or null until fetched.</summary>
    public (double? dir, double kt)? Wind(string icao)
    {
        lock (_cache)
        {
            var have = _cache.TryGetValue(icao, out var c);
            if ((!have || DateTime.UtcNow - c.at > TimeSpan.FromMinutes(10)) && _busy.Add(icao)) _ = Task.Run(() => FetchAsync(icao));
            return have ? (c.dir, c.kt) : null;
        }
    }

    /// <summary>Whether a METAR answer (or a failure) has come back for this airport yet.</summary>
    public bool Known(string icao) { lock (_cache) return _cache.ContainsKey(icao); }

    private async Task FetchAsync(string icao)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://aviationweather.gov/api/data/metar?ids={icao}&format=json");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var m = doc.RootElement[0];
                double? dir = m.TryGetProperty("wdir", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
                var kt = m.TryGetProperty("wspd", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
                // METAR directions are true north; the sim's ambient wind is too.
                double? vis = null;
                if (m.TryGetProperty("visib", out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number) vis = v.GetDouble();
                    else if (v.ValueKind == JsonValueKind.String)
                    {
                        // "10+", "1/2", "1 1/2", "P6SM"…
                        var t = (v.GetString() ?? "").Replace("+", "").Replace("P", "").Replace("SM", "").Trim();
                        double sum = 0; var ok = t.Length > 0;
                        foreach (var part in t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (part.Split('/') is [var a, var b] && double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out var na) && double.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out var nb) && nb > 0) sum += na / nb;
                            else if (double.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var n)) sum += n;
                            else ok = false;
                        }
                        if (ok) vis = sum;
                    }
                }
                double? ceiling = null;
                if (m.TryGetProperty("clouds", out var cl) && cl.ValueKind == JsonValueKind.Array)
                    foreach (var c in cl.EnumerateArray())
                        if (c.TryGetProperty("cover", out var cv) && cv.GetString() is "BKN" or "OVC" or "OVX" or "VV"
                            && c.TryGetProperty("base", out var bs) && bs.ValueKind == JsonValueKind.Number)
                            ceiling = Math.Min(ceiling ?? double.MaxValue, bs.GetDouble());
                if (m.TryGetProperty("vertVis", out var vv) && vv.ValueKind == JsonValueKind.Number) ceiling = Math.Min(ceiling ?? double.MaxValue, vv.GetDouble());
                lock (_cache)
                {
                    _cache[icao] = (DateTime.UtcNow, dir, kt);
                    _low[icao] = (ceiling is { } ce && ce < 800) || (vis is { } vi && vi < 2);
                }
            }
            else lock (_cache) _cache[icao] = (DateTime.UtcNow, null, 0);
        }
        catch (Exception ex) { log.LogInformation("{icao} METAR not available: {msg}", icao, ex.Message); lock (_cache) _cache[icao] = (DateTime.UtcNow - TimeSpan.FromMinutes(7), null, 0); }
        finally { lock (_busy) _busy.Remove(icao); }
    }
}
