using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Laia.Common;
using Laia.Server;

Directory.CreateDirectory(Paths.LogsDir);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService(o => o.ServiceName = "LAIA");
builder.WebHost.UseUrls($"http://0.0.0.0:{LaiaPorts.Http}");
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(Paths.LogsDir, "server.log")));
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

builder.Services.AddSingleton<ServerState>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<FrequencyDb>();
builder.Services.AddHostedService<FrequencyUpdater>();
builder.Services.AddSingleton<SimBriefService>();
builder.Services.AddSingleton<RunwayDb>();
builder.Services.AddHostedService<RunwayUpdater>();
builder.Services.AddSingleton<AirportStore>();
builder.Services.AddSingleton<Updater>();
builder.Services.AddSingleton<ScheduleStore>();
builder.Services.AddSingleton<FlightSource>();
builder.Services.AddSingleton<TrafficEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrafficEngine>());
builder.Services.AddSingleton<SpeechWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SpeechWorker>());
builder.Services.AddHostedService<DiscoveryResponder>();
builder.Services.AddHttpClient("speech", c =>
{
    c.BaseAddress = new Uri($"http://127.0.0.1:{LaiaPorts.SpeechWorker}/");
    c.Timeout = TimeSpan.FromSeconds(120);
});

var app = builder.Build();
var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

static byte[] Resource(string name)
{
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
}
var indexHtml = Resource("index.html");
var logoPng = Resource("logo.png");

app.MapGet("/", () => Results.Bytes(indexHtml, "text/html; charset=utf-8"));
app.MapGet("/logo.png", () => Results.Bytes(logoPng, "image/png"));

// Updates: the sim PC can see whether a newer LAIA exists and update this PC without anyone walking over.
app.MapGet("/api/update", async (Updater u, ServerState s) =>
{
    var latest = await u.LatestAsync();
    return Results.Json(new
    {
        server = s.Version,
        latest = latest?.Version,
        serverUpdate = latest is not null && Laia.Common.Updates.IsNewer(latest.Version, s.Version),
        updating = u.Updating,
        status = u.Status,
    }, jsonOpts);
});
app.MapPost("/api/update/server", async (Updater u) =>
{
    var (ok, message) = await u.UpdateAsync();
    return Results.Json(new { ok, message }, jsonOpts);
});
// Logs, so LAIA Sim can keep a copy on the sim PC.
app.MapGet("/api/logs/{name}", (string name) =>
{
    var file = name switch { "server" => "server.log", "setup" => "setup.log", _ => null };
    if (file is null) return Results.NotFound();
    var path = Path.Combine(Paths.LogsDir, file);
    if (!File.Exists(path)) return Results.Text("", "text/plain");
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    const long max = 3_000_000;
    if (fs.Length > max) fs.Seek(-max, SeekOrigin.End);
    using var sr = new StreamReader(fs);
    return Results.Text(sr.ReadToEnd(), "text/plain; charset=utf-8");
});
app.MapGet("/api/status", (ServerState s, FrequencyDb f, SimBriefService sb) => Results.Json(s.Snapshot(FlightInfo(s, f, sb)), jsonOpts));

// LAIA Sim posts the user's aircraft about once a second.
app.MapPost("/api/sim/state", async (HttpContext ctx, ServerState s, SimBriefService sb, TrafficEngine traffic, FlightSource src) =>
{
    var st = await JsonSerializer.DeserializeAsync<Laia.Common.SimConnect.AircraftState>(ctx.Request.Body, jsonOpts);
    if (st is null) return Results.BadRequest();
    s.TouchSim(ctx.Connection.RemoteIpAddress?.ToString(), null);
    var justConnected = s.SetSim(st);
    if (st.Connected && (justConnected || DateTime.UtcNow - sb.LastAttemptUtc > TimeSpan.FromMinutes(15)))
        _ = Task.Run(sb.RefreshAsync);
    // The reply carries the AI traffic for LAIA Sim to draw.
    var reply = new Laia.Common.SimConnect.SimStateReply { Traffic = st.Connected ? traffic.Snapshot() : new(), ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Motion = traffic.Motion };
    if (st.Connected)
    {
        try { var need = traffic.NeededAirports(st); if (need.Count > 0) reply.NeedAirports = need; } catch { }
        try { var sch = traffic.NeededSchedules(st); if (sch.Count > 0) reply.NeedSchedules = sch; } catch { }
        reply.NeedCatalog = !src.HasCatalog;
    }
    return Results.Json(reply, jsonOpts);
});

app.MapGet("/api/traffic", (TrafficEngine t, RunwayDb r) => Results.Json(new { runways = r.Status, motion = t.Motion, status = t.Status(), traffic = t.Snapshot() }, jsonOpts));
app.MapPost("/api/traffic/level/{level}", (string level, TrafficEngine t) =>
{
    t.SetLevel(level);
    var msg = level switch { "off" => "AI traffic off.", "light" => "Light traffic.", "busy" => "Busy traffic.", _ => "Normal traffic." };
    return Results.Json(new { ok = true, message = msg }, jsonOpts);
});
// LAIA Sim sends an airport's layout and procedures, read from MSFS.
app.MapPost("/api/airport", async (HttpContext ctx, AirportStore store) =>
{
    var d = await JsonSerializer.DeserializeAsync<Laia.Common.Airports.AirportData>(ctx.Request.Body, jsonOpts);
    if (d is null || string.IsNullOrWhiteSpace(d.Icao)) return Results.BadRequest();
    store.Add(d);
    return Results.Json(new { ok = true }, jsonOpts);
});
app.MapPost("/api/schedule", async (HttpContext ctx, ScheduleStore store) =>
{
    var d = await JsonSerializer.DeserializeAsync<Laia.Common.Airports.ScheduleData>(ctx.Request.Body, jsonOpts);
    if (d is null || string.IsNullOrWhiteSpace(d.Airport)) return Results.BadRequest();
    store.Add(d);
    return Results.Json(new { ok = true }, jsonOpts);
});
app.MapPost("/api/traffic/catalog", async (HttpContext ctx, FlightSource src) =>
{
    var c = await JsonSerializer.DeserializeAsync<Laia.Common.Airports.TrafficCatalog>(ctx.Request.Body, jsonOpts);
    if (c is not null) src.SetCatalog(c);
    return Results.Json(new { ok = true }, jsonOpts);
});
app.MapPost("/api/traffic/motion/{mode}", (string mode, TrafficEngine t) =>
{
    t.SetMotion(mode);
    return Results.Json(new { ok = true, message = t.Motion == "msfs" ? "Motion: MSFS flies the aircraft along LAIA's path. Send a new test flight." : "Motion: LAIA moves the aircraft every frame. Send a new test flight." }, jsonOpts);
});
app.MapPost("/api/traffic/test/{kind}", (string kind, TrafficEngine t) =>
{
    if (kind is not ("departure" or "arrival")) return Results.BadRequest();
    var (ok, message) = t.StartTest(kind);
    return Results.Json(new { ok, message }, jsonOpts);
});
app.MapPost("/api/traffic/clear", (TrafficEngine t) => { t.Clear(); return Results.Json(new { ok = true, message = "Traffic removed." }, jsonOpts); });

app.MapPost("/api/simbrief/reload", async (SimBriefService sb) => { await sb.RefreshAsync(); return Results.Json(new { status = sb.Status }, jsonOpts); });

// Diagnostics: the published frequencies LAIA knows for an airport (e.g. /api/airport/KLGA/frequencies).
app.MapGet("/api/airport/{id}/frequencies", (string id, FrequencyDb f) =>
    Results.Json(new { status = f.Status, frequencies = f.AirportFrequencies(id).Select(x => new { x.Name, x.FreqText, kind = x.Kind.ToString(), x.Primary }) }, jsonOpts));
app.MapGet("/api/transcript", (ServerState s) => Results.Json(s.Recent(), jsonOpts));

// The sim PC sends a push-to-talk recording here and gets the controller's spoken reply back.
app.MapPost("/api/radio", async (HttpContext ctx, IHttpClientFactory f, ServerState s, FrequencyDb freqs, SimBriefService sb, ILogger<Program> log) =>
{
    s.TouchSim(ctx.Connection.RemoteIpAddress?.ToString(), ctx.Request.Headers["X-LAIA-Client"].ToString());
    using var body = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
    if (s.SpeechStatus != "ready")
        return Results.Json(new { error = "LAIA is still warming up its speech engine. Try again in a minute." }, jsonOpts, statusCode: 503);

    var sw = Stopwatch.StartNew();
    using var content = new ByteArrayContent(body.ToArray());
    content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
    var (radioCtx, radioSummary) = RadioContext.Build(s, freqs, sb);
    s.LastRadioContext = radioSummary;
    using var req = new HttpRequestMessage(HttpMethod.Post, "radio") { Content = content };
    req.Headers.Add("X-LAIA-Context", Uri.EscapeDataString(JsonSerializer.Serialize(radioCtx)));
    HttpResponseMessage resp;
    try { resp = await f.CreateClient("speech").SendAsync(req, ctx.RequestAborted); }
    catch (Exception ex)
    {
        log.LogWarning("Speech engine call failed: {msg}", ex.Message);
        return Results.Json(new { error = "The speech engine didn't answer. It may be restarting." }, jsonOpts, statusCode: 502);
    }
    using (resp)
    {
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            // Nothing real was said (stray key-up), or nobody answers on that frequency.
            var heard0 = Header(resp, "X-LAIA-Heard");
            var note = Header(resp, "X-LAIA-Note");
            if (heard0.Length > 0)
            {
                s.AddExchange(heard0, $"(no reply: {note})", sw.Elapsed.TotalMilliseconds);
                ctx.Response.Headers["X-LAIA-Heard"] = Uri.EscapeDataString(heard0);
                ctx.Response.Headers["X-LAIA-Note"] = Uri.EscapeDataString(note);
                log.LogInformation("Radio: heard \"{heard}\" with no reply ({note})", heard0, note);
            }
            return Results.NoContent();
        }
        if (!resp.IsSuccessStatusCode)
            return Results.Json(new { error = await resp.Content.ReadAsStringAsync() }, jsonOpts, statusCode: 502);
        var heard = Header(resp, "X-LAIA-Heard");
        var reply = Header(resp, "X-LAIA-Reply");
        var wav = await resp.Content.ReadAsByteArrayAsync();
        s.AddExchange(heard, reply, sw.Elapsed.TotalMilliseconds);
        log.LogInformation("Radio [{ctx}]: heard \"{heard}\" -> \"{reply}\" ({ms:0} ms)", radioSummary, heard, reply, sw.Elapsed.TotalMilliseconds);
        ctx.Response.Headers["X-LAIA-Heard"] = Uri.EscapeDataString(heard);
        ctx.Response.Headers["X-LAIA-Reply"] = Uri.EscapeDataString(reply);
        return Results.Bytes(wav, "audio/wav");
    }
});

// Used by the control page's "Test the controller voice" box.
app.MapPost("/api/say", async (HttpContext ctx, IHttpClientFactory f, ServerState s) =>
{
    if (s.SpeechStatus != "ready")
        return Results.Json(new { error = "Speech engine is still warming up." }, jsonOpts, statusCode: 503);
    using var reader = new StreamReader(ctx.Request.Body);
    var json = await reader.ReadToEndAsync();
    using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    using var resp = await f.CreateClient("speech").PostAsync("say", content, ctx.RequestAborted);
    var bytes = await resp.Content.ReadAsByteArrayAsync();
    return resp.IsSuccessStatusCode ? Results.Bytes(bytes, "audio/wav")
        : Results.Json(new { error = System.Text.Encoding.UTF8.GetString(bytes) }, jsonOpts, statusCode: 502);
});

// Settings entered in the LAIA Sim settings window. Keys never leave this PC in readable form.
app.MapGet("/api/settings", (SettingsStore st) => Results.Json(st.Public(), jsonOpts));
app.MapPut("/api/settings", async (HttpContext ctx, SettingsStore st, SimBriefService sb, ILogger<Program> log) =>
{
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    st.Update(doc.RootElement);
    if (doc.RootElement.TryGetProperty("simbrief_user", out _)) _ = Task.Run(sb.RefreshAsync);
    log.LogInformation("Settings updated from {ip}", ctx.Connection.RemoteIpAddress);
    return Results.Json(st.Public(), jsonOpts);
});
app.MapPost("/api/settings/test/{service}", async (string service, HttpContext ctx, SettingsStore st) =>
{
    string? value = null;
    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        if (doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0) value = v.GetString();
    }
    catch (JsonException) { }
    value ??= service switch { "simbrief" => st.Get("simbrief_user"), "airlabs" => st.Get("airlabs_key"), "aeroapi" => st.Get("aeroapi_key"), _ => null };
    return Results.Json(await ServiceTester.TestAsync(service, value), jsonOpts);
});

app.Run();

static object FlightInfo(ServerState s, FrequencyDb f, SimBriefService sb)
{
    var sim = s.Sim;
    var live = sim is { Connected: true } && DateTime.UtcNow - s.SimUpdatedUtc < TimeSpan.FromSeconds(15);
    object? where = null, radio = null;
    if (live && f.Loaded)
    {
        var near = sim!.AltitudeFt < 18000 ? f.NearestAirport(sim.Lat, sim.Lon) : null;
        if (near is { } n) where = new { airport = n.ident, nm = Math.Round(n.nm, 1) };
        var tuned = f.Lookup(sim.TransmitFrequency, sim.Lat, sim.Lon);
        radio = new { freq = sim.TransmitFrequency.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture), station = tuned?.Name };
    }
    var p = sb.Plan;
    return new
    {
        sim = live ? sim : null,
        where,
        radio,
        plan = p is null ? null : new { p.Callsign, p.Origin, p.Destination, p.Route, cruise = p.CruiseFt, p.Sid, p.Star, p.Aircraft, p.Squawk, p.DepartureFreq },
        planStatus = sb.Status,
        frequencies = f.Status,
        lastRadio = s.LastRadioContext,
    };
}

static string Header(HttpResponseMessage r, string name) =>
    r.Headers.TryGetValues(name, out var v) ? Uri.UnescapeDataString(v.FirstOrDefault() ?? "") : "";
