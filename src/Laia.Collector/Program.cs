// LAIA Collector (15 Nov): runs in a container on the NAS. Each day adsb.lol publishes, it streams that day's
// whole-world history once (~3.5 GB, never stored whole), keeps every airline passenger jet's track near each US and
// Canadian airline airport, and saves them in one SQLite database (tracks.db) — kept 90 days. LAIA Server (and anyone
// LAIA is shared with, through the Cloudflare tunnel) asks it for an airport's days and learns from them in seconds,
// instead of downloading days itself.
using System.Reflection;
using System.Text.Json;
using Laia.Collector;

var dataDir = Environment.GetEnvironmentVariable("LAIA_DATA") ?? "/data";
Directory.CreateDirectory(dataDir);
int Env(string name, int def) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : def;
var options = new CollectorOptions(
    dataDir,
    KeepDays: Env("KEEP_DAYS", 90),
    FirstDays: Env("FIRST_DAYS", 14),
    Countries: (Environment.GetEnvironmentVariable("COUNTRIES") ?? "US,CA").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(c => c.ToUpperInvariant()).ToHashSet());
var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TrackDb>(_ => new TrackDb(Path.Combine(dataDir, "tracks.db")));
builder.Services.AddSingleton<Collector>();
var app = builder.Build();

var db = app.Services.GetRequiredService<TrackDb>();
var collector = app.Services.GetRequiredService<Collector>();
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => collector.RunAsync(app.Lifetime.ApplicationStopping)));
app.Logger.LogInformation("LAIA Collector {v}: data in {dir}, keeping {keep} days, {countries}", version, dataDir, options.KeepDays, string.Join("+", options.Countries));

object Status() => new
{
    version,
    now = collector.Now,
    last = collector.Last,
    daysStored = db.Days(),
    airports = collector.AirportCount,
    airportsWithTracks = db.AirportCount(),
    databaseMb = Math.Round(db.SizeBytes() / 1e6),
    keepDays = options.KeepDays,
    countries = options.Countries,
};

app.MapGet("/api/status", () => Results.Json(Status()));

// An airport's stored days, newest first: [{day, tracks, landings: {"270": 41, …}}]
app.MapGet("/api/days/{icao}", (string icao, HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "public, max-age=600";
    return Results.Json(db.AirportDays(icao.ToUpperInvariant()).Select(d => new { day = d.Day, tracks = d.Tracks, landings = d.Landings }));
});

// One day's tracks for an airport: gzipped JSON (see TrackStore), sent as it is stored.
app.MapGet("/api/tracks/{icao}/{day}", (string icao, string day, HttpContext ctx) =>
{
    var blob = db.Tracks(icao.ToUpperInvariant(), day);
    if (blob is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.Bytes(blob, "application/octet-stream");
});

// A small page to check on it from a browser.
app.MapGet("/", () =>
{
    var s = JsonSerializer.Serialize(Status(), new JsonSerializerOptions { WriteIndented = true });
    var days = db.Days();
    string H(string? x) => System.Net.WebUtility.HtmlEncode(x ?? "");
    const string Css = "body{font:15px system-ui,sans-serif;margin:24px;max-width:760px;color:#1d2330;background:#f6f7f9}"
        + "h1{font-size:20px}.card{background:#fff;border:1px solid #dde1e7;border-radius:8px;padding:14px 16px;margin:12px 0}"
        + "b{font-weight:600}pre{font-size:12px;overflow:auto;color:#555}"
        + "@media (prefers-color-scheme: dark){body{background:#15181d;color:#e6e8eb}.card{background:#1e2228;border-color:#30353d}pre{color:#9aa}}";
    var range = days.Count > 0 ? $" ({days[^1]} to {days[0]})" : "";
    var html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
        + "<meta http-equiv=\"refresh\" content=\"30\"><title>LAIA Collector</title><style>" + Css + "</style></head><body>"
        + $"<h1>LAIA Collector {H(version)}</h1>"
        + $"<div class=\"card\"><b>Now:</b> {H(collector.Now)}</div>"
        + $"<div class=\"card\"><b>Last day collected:</b> {H(collector.Last ?? "none yet")}</div>"
        + $"<div class=\"card\"><b>Days stored:</b> {days.Count}{H(range)}<br>"
        + $"<b>Airports watched:</b> {collector.AirportCount} ({H(string.Join(" + ", options.Countries))}) · <b>with tracks:</b> {db.AirportCount()}<br>"
        + $"<b>Database:</b> {db.SizeBytes() / 1e6:N0} MB · kept {options.KeepDays} days</div>"
        + $"<details><summary>Details</summary><pre>{H(s)}</pre></details></body></html>";
    return Results.Content(html, "text/html; charset=utf-8");
});

app.Run();

public sealed record CollectorOptions(string DataDir, int KeepDays, int FirstDays, HashSet<string> Countries);
