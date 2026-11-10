using System.Text.Json;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// The real D-ATIS of US airports (from atis.info, the public feed of the FAA's digital ATIS that replaced
/// datis.clowd.io; the old address is kept as a fallback while it still answers), read every
/// 10 minutes for the traffic airport, so the arrivals fly the approach really in use (LGA's RNAV X or Y 31).
/// </summary>
public sealed class AtisReader(ILogger log)
{
    private readonly Dictionary<string, (DateTime at, List<(string, string, string)> apps, string text)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
        return h;
    }

    /// <summary>The approaches in use from the latest ATIS (empty until read, or when there's no D-ATIS).</summary>
    public List<(string kind, string suffix, string runway)> Approaches(string icao)
    {
        lock (_cache)
        {
            var have = _cache.TryGetValue(icao, out var c);
            if ((!have || DateTime.UtcNow - c.at > TimeSpan.FromMinutes(10)) && icao.Length == 4 && icao[0] == 'K' && _busy.Add(icao))
                _ = Task.Run(() => FetchAsync(icao));
            return have ? c.apps : new();
        }
    }

    public string? Text(string icao) { lock (_cache) return _cache.TryGetValue(icao, out var c) ? c.text : null; }

    /// <summary>The ATIS texts in a reply: an array of {type, datis}, or one such object, or {data: [...]}.</summary>
    internal static List<(string type, string text)> Parts(string json)
    {
        var parts = new List<(string, string)>();
        using var doc = JsonDocument.Parse(json);
        void One(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            foreach (var name in new[] { "datis", "atis", "text" })
                if (e.TryGetProperty(name, out var d) && d.ValueKind == JsonValueKind.String)
                {
                    parts.Add((e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "", d.GetString() ?? ""));
                    return;
                }
        }
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)) root = data;
        if (root.ValueKind == JsonValueKind.Array) foreach (var e in root.EnumerateArray()) One(e);
        else One(root);
        return parts;
    }

    private async Task FetchAsync(string icao)
    {
        var apps = new List<(string, string, string)>();
        var text = "";
        try
        {
            var parts = new List<(string type, string text)>();
            string? from = null;
            Exception? lastErr = null;
            foreach (var url in new[] { $"https://atis.info/api/{icao}", $"https://datis.clowd.io/api/{icao}" })
            {
                try
                {
                    parts = Parts(await Http.GetStringAsync(url));
                    if (parts.Count > 0) { from = new Uri(url).Host; break; }
                }
                catch (Exception ex) { lastErr = ex; }
            }
            if (from is null) throw lastErr ?? new Exception("no ATIS text for this airport");
            // Arrival ATIS first (it names the approach), then the departure one (its departure runways).
            text = string.Join(" / ", parts.OrderBy(p => p.type == "dep" ? 1 : 0).Select(p => p.text));
            apps = TrafficWorld.ParseAtisApproaches(text);
            var (ldg, dep) = TrafficWorld.ParseAtisRunways(text);
            log.LogInformation("{icao} D-ATIS ({from}): landing {ldg}, departing {dep}; approach {apps}", icao, from,
                ldg.Count == 0 ? "not named" : string.Join(", ", ldg), dep.Count == 0 ? "not named" : string.Join(", ", dep),
                apps.Count == 0 ? "not named" : string.Join(", ", apps.Select(a => $"{a.Item1}{(a.Item2.Length > 0 ? " " + a.Item2 : "")} {a.Item3}")));
        }
        catch (Exception ex) { log.LogInformation("{icao} D-ATIS not available: {msg}", icao, ex.Message); }
        lock (_cache) { _cache[icao] = (DateTime.UtcNow, apps, text); _busy.Remove(icao); }
    }
}
