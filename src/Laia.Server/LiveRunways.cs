using System.Text.Json;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// The runways an airport is really using right now, seen from live traffic (adsb.lol's free live API, every 2
/// minutes): aircraft established on a runway's final, low and descending, are landing on it; aircraft climbing away
/// off its far end, aligned with it, are departing from it. For airports with no ATIS this ranks above the wind
/// (user rule, 12 Nov — CYWG: the panel showed real traffic on 18 while LAIA used 36).
/// </summary>
public sealed class LiveRunways(ILogger<LiveRunways> log)
{
    private static readonly HttpClient Http = MakeClient();
    private readonly Dictionary<string, List<(DateTime at, string hex, bool landing, string rwy)>> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    public int Calls { get; private set; }
    private readonly Dictionary<string, int> _polls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many looks at this airport's live traffic have come back (answered or failed).</summary>
    public int Polls(string icao) { lock (_seen) return _polls.GetValueOrDefault(icao); }

    private static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
        return h;
    }

    /// <summary>The landing and departure runways seen in the last 30 minutes (at least two aircraft each, the most used
    /// first; others used at least a quarter as much), or empty lists. Starts a refresh when due.</summary>
    public (List<string> landing, List<string> departing) Get(AirportModel apt)
    {
        lock (_seen)
        {
            if ((!_asked.TryGetValue(apt.Icao, out var at) || DateTime.UtcNow - at > TimeSpan.FromMinutes(2)) && _busy.Add(apt.Icao))
            {
                _asked[apt.Icao] = DateTime.UtcNow;
                _ = Task.Run(() => PollAsync(apt));
            }
            var obs = _seen.GetValueOrDefault(apt.Icao) ?? new();
            obs.RemoveAll(o => DateTime.UtcNow - o.at > TimeSpan.FromMinutes(30));
            List<string> Pick(bool landing)
            {
                var counts = obs.Where(o => o.landing == landing).GroupBy(o => o.rwy).Select(g => (rwy: g.Key, n: g.Select(o => o.hex).Distinct().Count(), last: g.Max(o => o.at)))
                    .Where(x => x.n >= 2).OrderByDescending(x => x.last > DateTime.UtcNow.AddMinutes(-15)).ThenByDescending(x => x.n).ToList();
                if (counts.Count == 0) return new();
                var top = counts[0];
                var topEnd = apt.Ends.FirstOrDefault(e => e.Ident == top.rwy);
                // Others the same way round (parallels), used at least a quarter as much.
                return counts.Where(x => x.rwy == top.rwy || (x.n * 4 >= top.n && topEnd is not null && apt.Ends.FirstOrDefault(e => e.Ident == x.rwy) is { } e2
                        && Math.Abs(Geo2.Diff(e2.Heading, topEnd.Heading)) < 30)).Select(x => x.rwy).ToList();
            }
            return (Pick(true), Pick(false));
        }
    }

    private async Task PollAsync(AirportModel apt)
    {
        try
        {
            Calls++;
            var json = await Http.GetStringAsync($"https://api.adsb.lol/v2/point/{apt.Data.Lat:0.0000}/{apt.Data.Lon:0.0000}/15");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ac", out var acs) || acs.ValueKind != JsonValueKind.Array) return;
            const double nm = 1852;
            var found = new List<(DateTime, string, bool, string)>();
            foreach (var a in acs.EnumerateArray())
            {
                if (!a.TryGetProperty("lat", out var la) || !a.TryGetProperty("lon", out var lo) || la.ValueKind != JsonValueKind.Number) continue;
                if (!a.TryGetProperty("alt_baro", out var al) || al.ValueKind != JsonValueKind.Number) continue;   // "ground" or missing
                if (!a.TryGetProperty("track", out var tr) || tr.ValueKind != JsonValueKind.Number) continue;
                var alt = al.GetDouble(); var trk = tr.GetDouble();
                var rate = a.TryGetProperty("baro_rate", out var br) && br.ValueKind == JsonValueKind.Number ? br.GetDouble()
                         : a.TryGetProperty("geom_rate", out var gr) && gr.ValueKind == JsonValueKind.Number ? gr.GetDouble() : 0;
                var hex = a.TryGetProperty("hex", out var hx) ? hx.GetString() ?? "" : "";
                var p = apt.Map.ToXY(la.GetDouble(), lo.GetDouble());
                foreach (var e in apt.Ends)
                {
                    var along = e.Along(p); var off = Math.Abs(e.Offset(p)); var dTrk = Math.Abs(Geo2.Diff(trk, e.Heading));
                    // Landing: on the final within 8 nm, low, not climbing.
                    if (along > -8 * nm && along < 300 && off < 0.6 * nm && dTrk < 15 && alt < apt.ElevationFt + 3000 && rate < 300)
                    { found.Add((DateTime.UtcNow, hex, true, e.Ident)); break; }
                    // Departing: past the middle of the runway, climbing away aligned with it, within 6 nm of the end.
                    if (along > e.LengthM * 0.5 && along < e.LengthM + 6 * nm && off < 0.8 * nm && dTrk < 20 && alt < apt.ElevationFt + 5000 && rate > 300)
                    { found.Add((DateTime.UtcNow, hex, false, e.Ident)); break; }
                }
            }
            lock (_seen)
            {
                if (!_seen.TryGetValue(apt.Icao, out var l)) _seen[apt.Icao] = l = new();
                foreach (var f in found) if (!l.Any(o => o.hex == f.Item2 && o.landing == f.Item3 && DateTime.UtcNow - o.at < TimeSpan.FromMinutes(10))) l.Add(f);
            }
        }
        catch (Exception ex) { log.LogInformation("{icao} live traffic (adsb.lol) not available: {msg}", apt.Icao, ex.Message); }
        finally { lock (_seen) { _busy.Remove(apt.Icao); _polls[apt.Icao] = _polls.GetValueOrDefault(apt.Icao) + 1; } }
    }
}
