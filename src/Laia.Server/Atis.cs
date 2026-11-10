using System.Text.Json;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// The real D-ATIS of US airports (from datis.clowd.io, the public feed of the FAA's digital ATIS), read every
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

    private async Task FetchAsync(string icao)
    {
        var apps = new List<(string, string, string)>();
        var text = "";
        try
        {
            var json = await Http.GetStringAsync($"https://datis.clowd.io/api/{icao}");
            using var doc = JsonDocument.Parse(json);
            var parts = new List<(string type, string text)>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.TryGetProperty("datis", out var d) && d.ValueKind == JsonValueKind.String)
                        parts.Add((e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "", d.GetString() ?? ""));
            // The arrival ATIS (or the combined one) names the approach.
            var arr = parts.Where(p => p.type is "arr" or "combined").Select(p => p.text).ToList();
            if (arr.Count == 0) arr = parts.Select(p => p.text).ToList();
            text = string.Join(" / ", arr);
            apps = TrafficWorld.ParseAtisApproaches(text);
            log.LogInformation("{icao} D-ATIS: {apps}", icao, apps.Count == 0 ? "no instrument approach named" : string.Join(", ", apps.Select(a => $"{a.Item1}{(a.Item2.Length > 0 ? " " + a.Item2 : "")} {a.Item3}")));
        }
        catch (Exception ex) { log.LogInformation("{icao} D-ATIS not available: {msg}", icao, ex.Message); }
        lock (_cache) { _cache[icao] = (DateTime.UtcNow, apps, text); _busy.Remove(icao); }
    }
}
