using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Laia.Server;

/// <summary>
/// User settings that the server needs (SimBrief, traffic data keys). They are entered in the
/// LAIA Sim settings window and stored here, on the ATC PC. API keys are encrypted with Windows
/// DPAPI (machine scope) so they're unreadable if the file is copied elsewhere.
/// </summary>
public sealed class SettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LAIA settings v1");
    private static readonly string[] Secrets = { "airlabs_key", "aeroapi_key" };
    private static readonly string[] Plain = { "simbrief_user", "schedule_source", "traffic_level" };
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(Paths.DataDir, "settings.json");
    private JsonObject _data;

    public SettingsStore() => _data = Load();

    private JsonObject Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonNode.Parse(File.ReadAllText(_path))?.AsObject() ?? new JsonObject();
        }
        catch { }
        return new JsonObject();
    }

    private void Save()
    {
        Directory.CreateDirectory(Paths.DataDir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, _data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, true);
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            var v = _data[key]?.GetValue<string>();
            if (v is null) return null;
            if (Secrets.Contains(key)) return Unprotect(v);
            return v;
        }
    }

    public string ScheduleSource => Get("schedule_source") ?? "aig";

    /// <summary>What the sim app may see: plain values, and only whether each key is set plus its last 4 characters.</summary>
    public object Public()
    {
        object Key(string k)
        {
            var v = Get(k);
            return string.IsNullOrEmpty(v) ? new { set = false, hint = "" } : new { set = true, hint = "…" + v[^Math.Min(4, v.Length)..] };
        }
        return new
        {
            simbrief_user = Get("simbrief_user") ?? "",
            schedule_source = ScheduleSource,
            airlabs = Key("airlabs_key"),
            aeroapi = Key("aeroapi_key"),
            navigraph = new { available = false, signed_in = false, note = "Navigraph sign-in arrives once Navigraph approves LAIA." },
        };
    }

    /// <summary>
    /// Applies a partial update. A missing field is left alone; an empty string clears it.
    /// </summary>
    public void Update(JsonElement patch)
    {
        lock (_gate)
        {
            foreach (var k in Plain.Concat(Secrets))
            {
                if (!patch.TryGetProperty(k, out var el) || el.ValueKind == JsonValueKind.Null) continue;
                var v = (el.GetString() ?? "").Trim();
                if (k == "schedule_source" && v is not ("aig" or "airlabs")) continue;
                if (k == "traffic_level" && v is not ("off" or "light" or "normal" or "busy")) continue;
                if (v.Length == 0) _data.Remove(k);
                else _data[k] = Secrets.Contains(k) ? Protect(v) : v;
            }
            Save();
        }
    }

    private static string Protect(string v) =>
        "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(v), Entropy, DataProtectionScope.LocalMachine));

    private static string? Unprotect(string v)
    {
        if (!v.StartsWith("dpapi:")) return v;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v[6..]), Entropy, DataProtectionScope.LocalMachine)); }
        catch { return null; }
    }
}

/// <summary>"Test" buttons in the settings window: one real request per click, so the user sees it works.</summary>
public static class ServiceTester
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15), DefaultRequestHeaders = { { "User-Agent", "LAIA-Server" } } };

    public sealed record Result(bool ok, string message);

    public static async Task<Result> TestAsync(string service, string? value)
    {
        try
        {
            return service switch
            {
                "simbrief" => await SimBrief(value),
                "airlabs" => await AirLabs(value),
                "aeroapi" => await AeroApi(value),
                _ => new Result(false, "Unknown service"),
            };
        }
        catch (TaskCanceledException) { return new Result(false, "No answer from the service (timed out). Check the ATC PC's internet connection."); }
        catch (HttpRequestException ex) { return new Result(false, "Couldn't reach the service: " + ex.Message); }
    }

    private static async Task<Result> SimBrief(string? user)
    {
        if (string.IsNullOrWhiteSpace(user)) return new Result(false, "Enter your SimBrief username or Pilot ID first.");
        user = user.Trim();
        var param = user.All(char.IsDigit) ? "userid" : "username";
        using var resp = await Http.GetAsync($"https://www.simbrief.com/api/xml.fetcher.php?{param}={Uri.EscapeDataString(user)}&json=1");
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("fetch", out var f) && f.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status != "Success") return new Result(false, string.IsNullOrEmpty(status) ? "SimBrief didn't recognise that name." : $"SimBrief says: {status}");
            string Str(string a, string b) => root.TryGetProperty(a, out var x) && x.TryGetProperty(b, out var y) ? y.ToString() : "";
            var flight = Str("general", "icao_airline") + Str("general", "flight_number");
            return new Result(true, $"Found your latest flight plan: {flight} {Str("origin", "icao_code")} to {Str("destination", "icao_code")}.");
        }
        catch (JsonException) { return new Result(false, "SimBrief didn't recognise that name."); }
    }

    private static async Task<Result> AirLabs(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return new Result(false, "Enter your AirLabs API key first.");
        using var resp = await Http.GetAsync($"https://airlabs.co/api/v9/airports?iata_code=LGA&api_key={Uri.EscapeDataString(key.Trim())}");
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return new Result(false, "AirLabs says: " + (e.TryGetProperty("message", out var m) ? m.GetString() : "key not accepted"));
            if (doc.RootElement.TryGetProperty("response", out _))
                return new Result(true, "AirLabs key works. (This test used 1 AirLabs call.)");
        }
        catch (JsonException) { }
        return new Result(false, $"Unexpected answer from AirLabs (HTTP {(int)resp.StatusCode}).");
    }

    private static async Task<Result> AeroApi(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return new Result(false, "Enter your AeroAPI key first.");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://aeroapi.flightaware.com/aeroapi/account/usage");
        req.Headers.Add("x-apikey", key.Trim());
        using var resp = await Http.SendAsync(req);
        return (int)resp.StatusCode switch
        {
            200 => new Result(true, "AeroAPI key works. (Checked your account usage page.)"),
            401 or 403 => new Result(false, "FlightAware didn't accept that key."),
            _ => new Result(false, $"Unexpected answer from FlightAware (HTTP {(int)resp.StatusCode})."),
        };
    }
}
