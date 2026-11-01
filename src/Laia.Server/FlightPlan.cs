using System.Text.Json;
using Laia.Common.SimConnect;

namespace Laia.Server;

/// <summary>The parts of the user's SimBrief plan LAIA uses.</summary>
public sealed record FlightPlan(
    string Callsign, string Origin, string OriginName, string Destination, string DestinationName, string DestinationCity,
    string? Sid, string? Star, int CruiseFt, string Route, string Aircraft, string OriginRunway, string DestinationRunway,
    string Squawk, string? DepartureFreq, DateTime FetchedUtc);

/// <summary>Loads the latest SimBrief OFP for the username saved in settings.</summary>
public sealed class SimBriefService(SettingsStore settings, FrequencyDb freqs, ILogger<SimBriefService> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), DefaultRequestHeaders = { { "User-Agent", "LAIA-Server" } } };
    private readonly SemaphoreSlim _gate = new(1, 1);
    public FlightPlan? Plan { get; private set; }
    public string Status { get; private set; } = "No SimBrief username set";
    public DateTime LastAttemptUtc { get; private set; } = DateTime.MinValue;

    public async Task RefreshAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            LastAttemptUtc = DateTime.UtcNow;
            var user = settings.Get("simbrief_user");
            if (string.IsNullOrWhiteSpace(user)) { Plan = null; Status = "No SimBrief username set"; return; }
            var param = user.All(char.IsDigit) ? "userid" : "username";
            var json = await Http.GetStringAsync($"https://www.simbrief.com/api/xml.fetcher.php?{param}={Uri.EscapeDataString(user)}&json=1");
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string obj, string field) => r.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(field, out var v) ? v.ToString() : "";
            if (S("fetch", "status") != "Success") { Status = "SimBrief: " + S("fetch", "status"); return; }

            string? sid = null, star = null;
            if (r.TryGetProperty("navlog", out var nav) && nav.TryGetProperty("fix", out var fix))
            {
                var fixes = fix.ValueKind == JsonValueKind.Array ? fix.EnumerateArray().ToList() : new List<JsonElement> { fix };
                string F(JsonElement f, string n) => f.TryGetProperty(n, out var v) ? v.ToString() : "";
                var sidStar = fixes.Where(f => F(f, "is_sid_star") == "1").Select(f => F(f, "via_airway")).Where(v => v.Length > 0 && v != "DCT").ToList();
                sid = sidStar.FirstOrDefault();
                star = sidStar.LastOrDefault();
                if (star == sid) star = sidStar.Count > 1 ? star : null;
                // A SID only exists if the first SID/STAR fix comes before the en-route part.
                var firstEnroute = fixes.FindIndex(f => F(f, "is_sid_star") != "1" && F(f, "type") != "apt");
                var firstSid = fixes.FindIndex(f => F(f, "is_sid_star") == "1");
                if (firstSid < 0 || (firstEnroute >= 0 && firstSid > firstEnroute)) { star ??= sid; sid = null; }
            }

            var origin = S("origin", "icao_code");
            var dest = S("destination", "icao_code");
            var callsign = S("atc", "callsign");
            if (callsign.Length == 0) callsign = S("general", "icao_airline") + S("general", "flight_number");
            int.TryParse(S("general", "initial_altitude"), out var cruise);
            var depFreq = freqs.AirportPositions(origin).GetValueOrDefault("Departure")?.FreqText;
            var seed = S("params", "request_id") is { Length: > 0 } rid ? rid : callsign + origin + dest;

            Plan = new FlightPlan(callsign, origin, FrequencyDb.Pretty(S("origin", "name")), dest, FrequencyDb.Pretty(S("destination", "name")),
                freqs.City(dest) ?? FrequencyDb.Pretty(S("destination", "name")), sid, star, cruise, S("general", "route"), S("aircraft", "icaocode"),
                S("origin", "plan_rwy"), S("destination", "plan_rwy"), MakeSquawk(seed), depFreq, DateTime.UtcNow);
            Status = $"Loaded {callsign} {origin}-{dest}";
            log.LogInformation("SimBrief plan: {cs} {o}-{d} SID {sid} cruise {alt} route {route}", callsign, origin, dest, sid, cruise, Plan.Route);
        }
        catch (Exception ex)
        {
            Status = "Couldn't load SimBrief plan: " + ex.Message;
            log.LogWarning("SimBrief refresh failed: {msg}", ex.Message);
        }
        finally { _gate.Release(); }
    }

    /// <summary>A stable transponder code per flight plan (octal digits, avoiding special codes).</summary>
    private static string MakeSquawk(string seed)
    {
        var h = 17u;
        foreach (var c in seed) h = h * 31 + c;
        for (var i = 0; ; i++)
        {
            var v = (h + (uint)i * 2654435761u) % 4096;
            var code = $"{(v >> 9) & 7}{(v >> 6) & 7}{(v >> 3) & 7}{v & 7}";
            if (code[0] is '0' or '7' || code == "1200" || code.StartsWith("12")) continue;
            return code;
        }
    }
}

/// <summary>Builds what the speech engine needs to know about the pilot's radio for one transmission.</summary>
public static class RadioContext
{
    public static (Dictionary<string, object?> ctx, string summary) Build(ServerState state, FrequencyDb freqs, SimBriefService simbrief)
    {
        var ctx = new Dictionary<string, object?>();
        var plan = simbrief.Plan;
        if (plan is not null)
        {
            ctx["plan"] = new Dictionary<string, object?>
            {
                ["callsign"] = plan.Callsign, ["dest"] = plan.Destination, ["dest_city"] = plan.DestinationCity,
                ["sid"] = plan.Sid, ["cruise"] = plan.CruiseFt, ["departure_freq"] = plan.DepartureFreq, ["squawk"] = plan.Squawk,
            };
        }
        var sim = state.Sim;
        if (sim is null || !sim.Connected || DateTime.UtcNow - state.SimUpdatedUtc > TimeSpan.FromSeconds(15) || !freqs.Loaded)
            return (ctx, "no sim position, answering by what was said");

        var freq = sim.TransmitFrequency;
        var f = freqs.Lookup(freq, sim.Lat, sim.Lon);
        var freqText = freq.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        if (f is null)
        {
            ctx["no_atc"] = true;
            ctx["note"] = $"No ATC answers on {freqText} here.";
            return (ctx, $"{freqText}: nobody");
        }
        if (f.Kind is FacKind.Ctaf or FacKind.Atis)
        {
            ctx["no_atc"] = true;
            ctx["note"] = f.Kind == FacKind.Atis ? $"{freqText} is the {f.BaseName} ATIS." : $"{freqText} is the {f.BaseName} CTAF. No controller answers there.";
            return (ctx, $"{freqText}: {f.Name}");
        }
        var kindWord = f.Kind == FacKind.ApproachDeparture ? (sim.VerticalSpeed > 500 && !sim.OnGround ? "Departure" : "Approach") : f.KindWord;
        var name = $"{f.BaseName} {kindWord}";
        ctx["tuned"] = new Dictionary<string, object?> { ["name"] = name, ["kind"] = kindWord, ["freq"] = f.FreqText };

        // Positions at the same airport (or the plan's origin while on the ground) for redirects.
        var airport = f.Kind is FacKind.Clearance or FacKind.Ground or FacKind.Tower or FacKind.Ramp ? f.Airport
                    : sim.OnGround && plan is not null ? plan.Origin : null;
        if (airport is not null)
        {
            ctx["airport_freqs"] = freqs.AirportPositions(airport).ToDictionary(
                kv => kv.Key, kv => (object?)new Dictionary<string, string> { ["name"] = kv.Value.Name, ["freq"] = kv.Value.FreqText });
        }
        return (ctx, $"{freqText}: {name}");
    }
}
