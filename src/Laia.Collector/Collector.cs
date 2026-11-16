using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Laia.Server.TrafficCore;

namespace Laia.Collector;

/// <summary>
/// Collects each new adsb.lol day (github.com/adsblol/globe_history_YYYY, daily releases of every aircraft's trace,
/// ODbL): checked every hour; the newest days (14 at first) are collected, one at a time, then days older than the
/// keeping period are removed.
/// </summary>
public sealed class Collector(TrackDb db, CollectorOptions opt, ILogger<Collector> log)
{
    public sealed record Airport(string Icao, double Lat, double Lon, double ElevFt);

    private List<Airport> _airports = new();
    public string Now { get; private set; } = "starting";
    public string? Last { get; private set; }
    public int AirportCount => _airports.Count;

    public async Task RunAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Collector");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _airports = await LoadAirports(http, ct);
                var removed = db.Prune(opt.KeepDays);
                if (removed > 0) log.LogInformation("Removed {n} days older than {keep} days", removed, opt.KeepDays);
                Now = "checking adsb.lol for new days";
                var have = db.Days().ToHashSet();
                var cutoff = DateTime.UtcNow.Date.AddDays(-opt.KeepDays).ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
                var todo = (await Releases(http, opt.FirstDays, ct)).Where(r => !have.Contains(r.Day) && string.CompareOrdinal(r.Day, cutoff) >= 0).ToList();
                if (todo.Count > 0) log.LogInformation("{n} day(s) to collect: {days}", todo.Count, string.Join(", ", todo.Select(t => t.Day)));
                for (var i = 0; i < todo.Count && !ct.IsCancellationRequested; i++)
                    await CollectDay(http, todo[i], i + 1, todo.Count, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning("Collecting failed: {msg}", ex.ToString().Split('\n').Take(6).Aggregate((a, b) => a + " | " + b.Trim()));
            }
            var next = DateTime.Now.AddHours(1);
            Now = $"waiting for the next adsb.lol day (next check {next:HH:mm})";
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { break; }
        }
    }

    // ---------------------------------------------------------------- airports

    /// <summary>
    /// The airports watched: every airport in the chosen countries with scheduled airline service (OurAirports' open
    /// list, refreshed weekly and kept in the data folder in case it can't be fetched).
    /// </summary>
    private async Task<List<Airport>> LoadAirports(HttpClient http, CancellationToken ct)
    {
        var file = Path.Combine(opt.DataDir, "airports.csv");
        if (!File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(7))
        {
            try
            {
                Now = "fetching the airport list";
                var csv = await http.GetStringAsync("https://davidmegginson.github.io/ourairports-data/airports.csv", ct);
                await File.WriteAllTextAsync(file + ".tmp", csv, ct);
                File.Move(file + ".tmp", file, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Couldn't fetch the airport list: {msg}", ex.Message); }
        }
        if (!File.Exists(file)) throw new InvalidOperationException("no airport list yet");
        var lines = await File.ReadAllLinesAsync(file, ct);
        var head = Csv(lines[0]);
        int Col(string n) => head.FindIndex(h => h == n);
        int cIdent = Col("ident"), cType = Col("type"), cLat = Col("latitude_deg"), cLon = Col("longitude_deg"), cElev = Col("elevation_ft"),
            cCountry = Col("iso_country"), cSched = Col("scheduled_service"), cIcao = Col("icao_code"), cGps = Col("gps_code");
        var res = new Dictionary<string, Airport>();
        foreach (var line in lines.Skip(1))
        {
            var f = Csv(line);
            string F(int i) => i >= 0 && i < f.Count ? f[i] : "";
            if (!opt.Countries.Contains(F(cCountry))) continue;
            if (F(cSched) != "yes" || F(cType) is not ("large_airport" or "medium_airport" or "small_airport")) continue;
            var icao = new[] { F(cIcao), F(cGps), F(cIdent) }.FirstOrDefault(x => x.Length == 4 && x.All(char.IsAsciiLetterUpper));
            if (icao is null) continue;
            if (!double.TryParse(F(cLat), CultureInfo.InvariantCulture, out var lat) || !double.TryParse(F(cLon), CultureInfo.InvariantCulture, out var lon)) continue;
            double.TryParse(F(cElev), CultureInfo.InvariantCulture, out var elev);
            res[icao] = new Airport(icao, lat, lon, elev);
        }
        if (res.Count != _airports.Count) log.LogInformation("Watching {n} airports with airline service in {c}", res.Count, string.Join(" + ", opt.Countries));
        return res.Values.OrderBy(a => a.Icao, StringComparer.Ordinal).ToList();
    }

    private static List<string> Csv(string line)
    {
        var res = new List<string>();
        var sb = new System.Text.StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') q = false;
                else sb.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }

    // ---------------------------------------------------------------- adsb.lol days

    public sealed record Release(string Day, string Tag, List<(string name, string url, long size)> Parts);

    /// <summary>The newest days adsb.lol has published (this year's repository and last year's), newest first.</summary>
    private static async Task<List<Release>> Releases(HttpClient http, int max, CancellationToken ct)
    {
        var rels = new List<Release>();
        foreach (var year in new[] { DateTime.UtcNow.Year, DateTime.UtcNow.Year - 1 })
        {
            if (rels.Count >= max) break;
            using var r = await http.GetAsync($"https://api.github.com/repos/adsblol/globe_history_{year}/releases?per_page=60", ct);
            if (!r.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (rels.Count >= max) break;
                var tag = e.GetProperty("tag_name").GetString() ?? "";
                if (!tag.Contains("-prod-")) continue;
                var day = tag.Replace("v", "").Split('-')[0];
                if (rels.Any(x => x.Day == day)) continue;
                if (e.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True) continue;
                var assets = e.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString()?.Contains(".tar") == true).ToList();
                // Still being uploaded: left for the next check (a day read half-uploaded would be kept short).
                if (assets.Any(a => a.TryGetProperty("state", out var st) && st.GetString() != "uploaded")) continue;
                var parts = assets
                    .Select(a => (name: a.GetProperty("name").GetString() ?? "", url: a.GetProperty("browser_download_url").GetString() ?? "", size: a.GetProperty("size").GetInt64()))
                    .Where(a => a.name.Contains(".tar")).OrderBy(a => a.name, StringComparer.Ordinal).ToList();
                if (parts.Count == 0) continue;
                rels.Add(new Release(day, tag, parts));
            }
        }
        return rels.OrderByDescending(r => r.Day, StringComparer.Ordinal).ToList();
    }

    /// <summary>Per airport while a day is read: its tracks, written straight into a gzip stream, and its landings.</summary>
    private sealed class Out : IDisposable
    {
        public readonly MemoryStream Ms = new();
        public readonly GZipStream Gz;
        public readonly Utf8JsonWriter W;
        public int Tracks;
        public readonly Dictionary<int, int> Landings = new();
        public Out() { Gz = new GZipStream(Ms, CompressionLevel.Optimal, leaveOpen: true); W = new Utf8JsonWriter(Gz); W.WriteStartArray(); }
        public byte[] Finish() { W.WriteEndArray(); W.Flush(); W.Dispose(); Gz.Dispose(); return Ms.ToArray(); }
        public void Dispose() { W.Dispose(); Gz.Dispose(); Ms.Dispose(); }
    }

    private async Task CollectDay(HttpClient http, Release rl, int n, int of, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var total = rl.Parts.Sum(p => p.size);
        log.LogInformation("Collecting adsb.lol {day} ({gb:0.0} GB, streamed) for {apts} airports — day {n} of {of}", rl.Day, total / 1e9, _airports.Count, n, of);
        // Airports by 1° square, each listed in every square within 45 nm of it, so each point is checked only against
        // the airports that could be near it.
        var cells = new Dictionary<(int, int), List<Airport>>();
        foreach (var a in _airports)
        {
            var dLat = 0.8; var dLon = Math.Min(10, 0.8 / Math.Max(0.1, Math.Cos(a.Lat * Math.PI / 180)));
            for (var la = (int)Math.Floor(a.Lat - dLat); la <= (int)Math.Floor(a.Lat + dLat); la++)
                for (var lo = (int)Math.Floor(a.Lon - dLon); lo <= (int)Math.Floor(a.Lon + dLon); lo++)
                {
                    if (!cells.TryGetValue((la, lo), out var l)) cells[(la, lo)] = l = new();
                    l.Add(a);
                }
        }
        var maxAlt = (_airports.Count == 0 ? 0 : _airports.Max(a => a.ElevFt)) + TrackStore.AboveFt;
        var outs = new Dictionary<string, Out>();
        long aircraft = 0, kept = 0;
        try
        {
            await using var stream = new PartsStream(http, rl.Parts.Select(p => p.url).ToList(),
                read => Now = $"collecting {rl.Day} (day {n} of {of}): {read / 1e9:0.0} of {total / 1e9:0.0} GB read, {kept:N0} tracks kept");
            using var tar = new TarReader(stream);
            TarEntry? entry;
            var near = new Dictionary<Airport, List<TrackPoint>>();
            while ((entry = await tar.GetNextEntryAsync(copyData: false, ct)) is not null)
            {
                if (entry.DataStream is null || !entry.Name.Contains("trace_full")) continue;
                aircraft++;
                using var ms = new MemoryStream();
                await entry.DataStream.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();
                if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
                {
                    using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                    using var raw = new MemoryStream();
                    gz.CopyTo(raw);
                    bytes = raw.ToArray();
                }
                List<TrackPoint>? pts;
                try { pts = AdsbTrace.Parse(bytes, passengerJetsOnly: true); } catch { continue; }
                if (pts is null || pts.Count < 5) continue;
                near.Clear();
                foreach (var p in pts)
                {
                    var alt = p.AltFt ?? 0;
                    if (alt >= maxAlt) continue;
                    if (!cells.TryGetValue(((int)Math.Floor(p.Lat), (int)Math.Floor(p.Lon)), out var cand)) continue;
                    foreach (var a in cand)
                    {
                        if (alt >= a.ElevFt + TrackStore.AboveFt || Geo2.DistanceNm(a.Lat, a.Lon, p.Lat, p.Lon) >= TrackStore.RadiusNm) continue;
                        if (!near.TryGetValue(a, out var l)) near[a] = l = new();
                        l.Add(p);
                    }
                }
                foreach (var (a, l) in near)
                {
                    if (l.Count < 5) continue;
                    if (!outs.TryGetValue(a.Icao, out var o)) outs[a.Icao] = o = new Out();
                    TrackStore.Write(o.W, l);
                    if (o.W.BytesPending > 1 << 16) o.W.Flush();
                    o.Tracks++;
                    kept++;
                    TrackStore.CountLandings(l, a.Lat, a.Lon, a.ElevFt, o.Landings);
                }
            }
            Now = $"saving {rl.Day}";
            var rows = outs.Select(kv => new TrackDb.Row(kv.Key, kv.Value.Tracks, kv.Value.Landings, kv.Value.Finish())).ToList();
            var secs = (DateTime.UtcNow - started).TotalSeconds;
            db.SaveDay(rl.Day, rows, aircraft, secs);
            var mb = rows.Sum(r => r.Data.Length) / 1e6;
            Last = $"{rl.Day}: {aircraft:N0} aircraft read, {kept:N0} tracks kept at {rows.Count} airports, {mb:N0} MB, took {secs / 60:0} min";
            log.LogInformation("Collected {last}", Last);
        }
        finally
        {
            foreach (var o in outs.Values) { try { o.Dispose(); } catch { } }
        }
    }

    /// <summary>The release's parts (.tar.aa, .tar.ab, …) read one after another as one stream, without storing them.</summary>
    private sealed class PartsStream(HttpClient http, List<string> urls, Action<long> progress) : Stream
    {
        private int _i = -1;
        private Stream? _cur;
        private HttpResponseMessage? _resp;
        private long _read;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return 0;
            while (true)
            {
                if (_cur is null)
                {
                    if (++_i >= urls.Count) return 0;
                    _resp?.Dispose();
                    using (var req = new HttpRequestMessage(HttpMethod.Get, urls[_i]))
                    {
                        req.Headers.Accept.ParseAdd("application/octet-stream");
                        _resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    }
                    _resp.EnsureSuccessStatusCode();
                    _cur = await _resp.Content.ReadAsStreamAsync(ct);
                }
                var n = await _cur.ReadAsync(buffer, ct);
                if (n > 0)
                {
                    var before = _read; _read += n;
                    if (before >> 26 != _read >> 26) progress(_read);   // every 64 MB
                    return n;
                }
                await _cur.DisposeAsync(); _cur = null;
            }
        }
        protected override void Dispose(bool disposing) { _cur?.Dispose(); _resp?.Dispose(); base.Dispose(disposing); }
    }
}
