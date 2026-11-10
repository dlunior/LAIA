using System.Net;
using System.Text.Json;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// The real D-ATIS of US airports from atis.info (free, no key; the old datis.clowd.io address as a fallback),
/// for the traffic airport. Kept 10 minutes; an airport that publishes none (404 or an empty answer) is only asked
/// again after 12 hours; when every address fails, it backs off 10 minutes. One request per airport at a time.
/// </summary>
public sealed class AtisReader(ILogger log)
{
    private readonly Dictionary<string, (DateTime until, AtisInfo info)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
        return h;
    }

    /// <summary>The latest ATIS (empty when none is known yet or the airport publishes none); refreshes in the background.</summary>
    public AtisInfo Get(string icao)
    {
        lock (_cache)
        {
            var have = _cache.TryGetValue(icao, out var c);
            if ((!have || DateTime.UtcNow > c.until) && icao.Length == 4 && icao[0] == 'K' && _busy.Add(icao))
                _ = Task.Run(() => FetchAsync(icao));
            return have ? c.info : new AtisInfo();
        }
    }

    /// <summary>The broadcasts in a reply: an array of {type, code, datis}, or one such object, or {data: [...]}.</summary>
    internal static List<(string type, string code, string text)> Parts(string json)
    {
        var parts = new List<(string, string, string)>();
        using var doc = JsonDocument.Parse(json);
        string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        void One(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            var text = S(e, "datis");
            if (text.Length == 0) text = S(e, "atis");
            if (text.Length > 0) parts.Add((S(e, "type").ToLowerInvariant(), S(e, "code"), text));
        }
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)) root = data;
        if (root.ValueKind == JsonValueKind.Array) foreach (var e in root.EnumerateArray()) One(e);
        else One(root);
        return parts;
    }

    private async Task FetchAsync(string icao)
    {
        var info = new AtisInfo();
        var until = DateTime.UtcNow.AddMinutes(10);
        var note = "";
        try
        {
            string? from = null;
            var none = false;
            foreach (var url in new[] { $"https://atis.info/api/{icao}", $"https://datis.clowd.io/api/{icao}" })
            {
                try
                {
                    using var resp = await Http.GetAsync(url);
                    if (resp.StatusCode == HttpStatusCode.NotFound) { none = true; from = new Uri(url).Host; break; }
                    if (!resp.IsSuccessStatusCode) { note += $"{new Uri(url).Host} {(int)resp.StatusCode}; "; continue; }
                    var parts = Parts(await resp.Content.ReadAsStringAsync());
                    from = new Uri(url).Host;
                    if (parts.Count == 0) { none = true; break; }
                    info = AtisParse.Combine(parts);
                    break;
                }
                catch (Exception ex) { note += $"{new Uri(url).Host}: {ex.Message}; "; }
            }
            if (none)
            {
                until = DateTime.UtcNow.AddHours(12);
                log.LogInformation("{icao} publishes no D-ATIS ({from}); the wind picks the runways", icao, from);
            }
            else if (from is null) log.LogInformation("{icao} D-ATIS not available ({note}); trying again in 10 minutes", icao, note.TrimEnd(' ', ';'));
            else log.LogInformation("{icao} D-ATIS ({from}): {what}", icao, from, info.Describe());
        }
        catch (Exception ex) { log.LogInformation("{icao} D-ATIS error: {msg}", icao, ex.Message); }
        lock (_cache) { _cache[icao] = (until, info); _busy.Remove(icao); }
    }
}
