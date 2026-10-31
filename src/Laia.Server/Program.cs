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

app.MapGet("/api/status", (ServerState s) => Results.Json(s.Snapshot(), jsonOpts));
app.MapGet("/api/transcript", (ServerState s) => Results.Json(s.Recent(), jsonOpts));

// The sim PC sends a push-to-talk recording here and gets the controller's spoken reply back.
app.MapPost("/api/radio", async (HttpContext ctx, IHttpClientFactory f, ServerState s, ILogger<Program> log) =>
{
    s.TouchSim(ctx.Connection.RemoteIpAddress?.ToString(), ctx.Request.Headers["X-LAIA-Client"].ToString());
    using var body = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
    if (s.SpeechStatus != "ready")
        return Results.Json(new { error = "LAIA is still warming up its speech engine. Try again in a minute." }, jsonOpts, statusCode: 503);

    var sw = Stopwatch.StartNew();
    using var content = new ByteArrayContent(body.ToArray());
    content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
    HttpResponseMessage resp;
    try { resp = await f.CreateClient("speech").PostAsync("radio", content, ctx.RequestAborted); }
    catch (Exception ex)
    {
        log.LogWarning("Speech engine call failed: {msg}", ex.Message);
        return Results.Json(new { error = "The speech engine didn't answer. It may be restarting." }, jsonOpts, statusCode: 502);
    }
    using (resp)
    {
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return Results.NoContent(); // nothing real was said (stray key-up)
        if (!resp.IsSuccessStatusCode)
            return Results.Json(new { error = await resp.Content.ReadAsStringAsync() }, jsonOpts, statusCode: 502);
        var heard = Header(resp, "X-LAIA-Heard");
        var reply = Header(resp, "X-LAIA-Reply");
        var wav = await resp.Content.ReadAsByteArrayAsync();
        s.AddExchange(heard, reply, sw.Elapsed.TotalMilliseconds);
        log.LogInformation("Radio: heard \"{heard}\" -> \"{reply}\" ({ms:0} ms)", heard, reply, sw.Elapsed.TotalMilliseconds);
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
app.MapPut("/api/settings", async (HttpContext ctx, SettingsStore st, ILogger<Program> log) =>
{
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    st.Update(doc.RootElement);
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

static string Header(HttpResponseMessage r, string name) =>
    r.Headers.TryGetValues(name, out var v) ? Uri.UnescapeDataString(v.FirstOrDefault() ?? "") : "";
