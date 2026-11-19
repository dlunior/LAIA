using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Learned airport profiles from real tracks (M6): the last three days of adsb.lol's open history (github.com/adsblol/
/// globe_history_YYYY, a daily release of every aircraft's trace, ODbL), streamed — never stored whole — and every
/// track passing within 45 nm of the airports wanted, low enough to be arriving or departing, kept and learned from
/// (<see cref="TrackLearner"/>). Each airport is learned once, then refreshed after 60 days; saved in data/learned.
/// Only airline passenger jets are kept (user, 13 Nov), and only airports with airline passenger service are learned.
/// The LAIA Collector (15 Nov) is asked first: a container on the user's NAS that collects every new adsb.lol day for
/// all US and Canadian airline airports and keeps 90 days, reached through the Cloudflare tunnel at
/// laia-data.sageks.dev (or the address in the "track_source" setting). From it an airport is learned in seconds,
/// with the days that show both flows; the download here is only for when it can't be reached or hasn't the airport.
/// </summary>
public sealed class AdsbHistory(ILogger<AdsbHistory> log, SettingsStore settings)
{
    public static JsonSerializerOptions Json => AdsbTrace.Json;
    private readonly Dictionary<string, AirportProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Task? _running;
    public string Status { get; private set; } = "nothing learned yet";
    private static string Dir => Path.Combine(Paths.DataDir, "data", "learned");

    /// <summary>The saved profile file for an airport (for LAIA Sim to keep a copy, so the offline tests can replay it).</summary>
    public string? ProfileFile(string icao)
    {
        if (icao.Length is < 3 or > 5 || !icao.All(char.IsLetterOrDigit)) return null;
        var file = Path.Combine(Dir, icao.ToUpperInvariant() + ".json");
        return File.Exists(file) ? file : null;
    }

    public AirportProfile? Profile(string icao)
    {
        lock (_gate)
        {
            if (_profiles.TryGetValue(icao, out var p)) return p;
            var file = Path.Combine(Dir, icao + ".json");
            if (!File.Exists(file)) return null;
            try { p = JsonSerializer.Deserialize<AirportProfile>(File.ReadAllText(file), Json); } catch { p = null; }
            if (p is not null) _profiles[icao] = p;
            return p;
        }
    }

    /// <summary>Starts learning these airports in the background if any has no profile (or an old one). One run at a time.</summary>
    public void Want(IReadOnlyList<AirportModel> airports)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) return;
            var need = airports.Where(a => Profile(a.Icao) is not { } p || p.Version < AirportProfile.CurrentVersion || DateTime.UtcNow - p.LearnedUtc > TimeSpan.FromDays(60)).ToList();
            // A week old: refreshed from the collector's newer days (only from it — never a download for this).
            var refresh = airports.Where(a => !need.Contains(a) && Profile(a.Icao) is { } p && DateTime.UtcNow - p.LearnedUtc > TimeSpan.FromDays(7)).ToList();
            if (need.Count == 0 && refresh.Count == 0) return;
            var refreshOnly = refresh.Select(a => a.Icao).ToHashSet(StringComparer.OrdinalIgnoreCase);
            need.AddRange(refresh);
            // Not tried again within 6 hours — with the same rules: after an update that changes how airports are learned,
            // the airport and its neighbours are learned again together, at once (KMCI, 15 Nov: the pair had been tried
            // an hour before under the old rules, so they were learned one at a time, the main airport second, and its
            // traffic started at the 15-minute limit without the real tracks).
            var key = string.Join(",", need.Select(a => a.Icao)) + "|v" + AirportProfile.CurrentVersion;
            if (_lastTry.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(6)) return;
            _lastTry[key] = DateTime.UtcNow;
            _learning = need.Select(a => a.Icao).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _running = Task.Run(() => LearnAsync(need, refreshOnly, CancellationToken.None));
        }
    }
    private readonly Dictionary<string, DateTime> _lastTry = new();
    private HashSet<string> _learning = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this airport is being learned right now.</summary>
    public bool Learning(string icao) { lock (_gate) return _running is { IsCompleted: false } && _learning.Contains(icao); }

    /// <summary>A current profile (learned with today's rules) for this airport.</summary>
    public bool Current(string icao) => Profile(icao) is { } p && p.Version >= AirportProfile.CurrentVersion;

    /// <summary>The last attempt to learn this airport ended without a profile (no history day found, or it failed).</summary>
    public bool Gave(string icao) { lock (_gate) return _running is not { IsCompleted: false } && _lastTry.Keys.Any(k => k.Split('|')[0].Split(',').Contains(icao, StringComparer.OrdinalIgnoreCase)) && !Current(icao); }

    private async Task LearnAsync(List<AirportModel> all, HashSet<string> refreshOnly, CancellationToken ct)
    {
        var apts = (await FromCollector(all, ct)).Where(a => !refreshOnly.Contains(a.Icao)).ToList();
        if (apts.Count == 0) return;
        var names = string.Join(", ", apts.Select(a => a.Icao));
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
            // The newest full days (user, 13 Nov: one day was thin for whichever flow was used less that day): the
            // year's repository (and last year's), its latest "prod" releases — up to three days.
            const int Days = 3, MaxDays = 7;   // three days first; up to seven when one flow wasn't seen (below)
            var rels = new List<(string tag, List<(string name, string url, long size)> parts)>();
            foreach (var year in new[] { DateTime.UtcNow.Year, DateTime.UtcNow.Year - 1 })
            {
                if (rels.Count >= MaxDays) break;
                using var r = await http.GetAsync($"https://api.github.com/repos/adsblol/globe_history_{year}/releases?per_page=20", ct);
                if (!r.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (rels.Count >= MaxDays) break;
                    var tag = e.GetProperty("tag_name").GetString() ?? "";
                    if (!tag.Contains("-prod-")) continue;
                    var dayOf = tag.Replace("v", "").Split('-')[0];
                    if (rels.Any(x => x.tag.Replace("v", "").Split('-')[0] == dayOf)) continue;
                    var parts = e.GetProperty("assets").EnumerateArray()
                        .Select(a => (name: a.GetProperty("name").GetString() ?? "", url: a.GetProperty("browser_download_url").GetString() ?? "", size: a.GetProperty("size").GetInt64()))
                        .Where(a => a.name.Contains(".tar")).OrderBy(a => a.name, StringComparer.Ordinal).ToList();
                    if (parts.Count == 0) continue;
                    rels.Add((tag, parts));
                }
            }
            if (rels.Count == 0) { Status = "couldn't find an adsb.lol history day"; log.LogWarning("adsb.lol history: no release found"); return; }
            string DayText(int count)
            {
                var dl = rels.Take(count).Select(x => x.tag.Replace("v", "").Split('-')[0]).OrderBy(x => x, StringComparer.Ordinal).ToList();
                return dl.Count == 1 ? dl[0] : $"{dl[0]}–{dl[^1].Split('.').Last()} ({dl.Count} days)";
            }
            var day = DayText(Math.Min(Days, rels.Count));
            var targets = apts.Select(a => (a, lat: a.Data.Lat, lon: a.Data.Lon, elev: a.ElevationFt)).ToList();
            var kept = targets.ToDictionary(t => t.a.Icao, _ => new List<IReadOnlyList<TrackPoint>>(), StringComparer.OrdinalIgnoreCase);
            var files = 0;
            var active = targets.ToList();
            for (var di = 0; di < rels.Count && active.Count > 0; di++)
            {
                var rl = rels[di];
                var total = rl.parts.Sum(p => p.size);
                long done = 0;
                var activeNames = string.Join(", ", active.Select(t => t.a.Icao));
                log.LogInformation("Learning {apts} from real tracks: adsb.lol {tag} ({gb:0.0} GB, streamed), day {n} of {of}", activeNames, rl.tag, total / 1e9, di + 1, di < Days ? Math.Min(Days, rels.Count) : rels.Count);
                var dn = di + 1;
                await using var stream = new PartsStream(http, rl.parts.Select(p => p.url).ToList(), n => { done = n; Status = $"learning {activeNames} from real tracks: day {dn}, {done / 1e9:0.0} of {total / 1e9:0.0} GB read"; })
                { Log = m => log.LogInformation("adsb.lol {m}", m) };
                using var tar = new TarReader(stream);
                TarEntry? entry;
                while ((entry = await tar.GetNextEntryAsync(copyData: false, ct)) is not null)
                {
                    if (entry.DataStream is null || !entry.Name.Contains("trace_full")) continue;
                    files++;
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
                    foreach (var t in active)
                    {
                        // Near this airport and low: a candidate arrival or departure (the rest of its day isn't kept).
                        var near = pts.Where(p => (p.AltFt ?? 0) < t.elev + 18000 && Geo2.DistanceNm(t.lat, t.lon, p.Lat, p.Lon) < 45).ToList();
                        if (near.Count >= 5) kept[t.a.Icao].Add(near);
                    }
                }
                if (di + 1 < Math.Min(Days, rels.Count)) continue;
                // Learned (after the first three days, and again after each extra day): saved at once, so the airport's
                // preparation goes on with it.
                var stillShort = new List<(AirportModel a, double lat, double lon, double elev)>();
                foreach (var t in active)
                {
                    var prof = TrackLearner.Learn(t.a.Icao, t.a.Map, t.elev, t.a.Ends, kept[t.a.Icao], "adsb.lol", DayText(di + 1));
                    Directory.CreateDirectory(Dir);
                    await File.WriteAllTextAsync(Path.Combine(Dir, t.a.Icao + ".json"), JsonSerializer.Serialize(prof, Json), ct);
                    lock (_gate) _profiles[t.a.Icao] = prof;
                    log.LogInformation("Learned {icao} from {n} real tracks: {what}", t.a.Icao, kept[t.a.Icao].Count, prof.Describe());
                    // The other flow not seen (KATL, 15 Nov: three days all landing west, so east flow had no real
                    // arrivals at all — no tracks on the map, no real vectors): more days, up to seven, until it is.
                    if (MissingFlow(prof, t.a.Ends) is { } miss && di + 1 < rels.Count)
                    {
                        log.LogInformation("{icao}: {miss}; learning another day for it", t.a.Icao, miss.text);
                        stillShort.Add(t);
                    }
                }
                active = stillShort;
            }
            Status = $"learned {names} from adsb.lol {day} ({files:N0} aircraft read)";
        }
        catch (Exception ex)
        {
            Status = "learning from real tracks failed: " + ex.Message;
            log.LogWarning("adsb.lol history: {msg}", ex.ToString().Split('\n').Take(6).Aggregate((a, b) => a + " | " + b.Trim()));
        }
    }

    public const string DefaultSource = "https://laia-data.sageks.dev";
    private sealed record CollectorDay(
        [property: JsonPropertyName("day")] string Day,
        [property: JsonPropertyName("tracks")] int Tracks,
        [property: JsonPropertyName("landings")] Dictionary<int, int>? Landings);

    /// <summary>
    /// Learns what it can from the LAIA Collector: each airport from its three newest days, plus — when one flow is
    /// hardly seen in them — up to four more days whose landings faced that way (picked from all 90, so no guessing
    /// which days to fetch). Returns the airports it couldn't learn (collector not reachable, or it hasn't them yet).
    /// </summary>
    private async Task<List<AirportModel>> FromCollector(List<AirportModel> apts, CancellationToken ct)
    {
        var src = (settings.Get("track_source") ?? "").Trim();
        if (src.Length == 0) src = DefaultSource;
        if (src.Equals("off", StringComparison.OrdinalIgnoreCase)) return apts;
        src = src.TrimEnd('/');
        var left = new List<AirportModel>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
        var reachable = true;
        foreach (var a in apts)
        {
            if (!reachable) { left.Add(a); continue; }
            try
            {
                Status = $"learning {a.Icao} from the LAIA Collector";
                var days = JsonSerializer.Deserialize<List<CollectorDay>>(await http.GetStringAsync($"{src}/api/days/{a.Icao}", ct)) ?? new();
                days = days.Where(d => d.Tracks > 0).OrderByDescending(d => d.Day, StringComparer.Ordinal).ToList();
                if (days.Count == 0) { log.LogInformation("LAIA Collector has no days for {icao} yet", a.Icao); left.Add(a); continue; }
                var used = new List<CollectorDay>();
                var tracks = new List<IReadOnlyList<TrackPoint>>();
                async Task Add(CollectorDay d)
                {
                    var bytes = await http.GetByteArrayAsync($"{src}/api/tracks/{a.Icao}/{d.Day}", ct);
                    if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
                    {
                        using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                        using var raw = new MemoryStream();
                        await gz.CopyToAsync(raw, ct);
                        bytes = raw.ToArray();
                    }
                    tracks.AddRange(TrackStore.Read(bytes));
                    used.Add(d);
                }
                string DaysText()
                {
                    var dl = used.Select(d => d.Day).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    return dl.Count == 1 ? dl[0] : $"{dl[0]}–{dl[^1]} ({dl.Count} days)";
                }
                foreach (var d in days.Take(3)) await Add(d);
                var prof = TrackLearner.Learn(a.Icao, a.Map, a.ElevationFt, a.Ends, tracks, "LAIA Collector", DaysText());
                if (MissingFlow(prof, a.Ends) is { } miss)
                {
                    var extra = days.Skip(3).Where(d => TrackStore.Facing(d.Landings, miss.heading) >= 10)
                        .OrderByDescending(d => TrackStore.Facing(d.Landings, miss.heading)).Take(4).ToList();
                    log.LogInformation("{icao}: {miss}; {n} stored day(s) have landings that way", a.Icao, miss.text, extra.Count);
                    if (extra.Count > 0)
                    {
                        foreach (var d in extra) await Add(d);
                        prof = TrackLearner.Learn(a.Icao, a.Map, a.ElevationFt, a.Ends, tracks, "LAIA Collector", DaysText());
                    }
                }
                // A quiet airport (KGPI, 16 Nov: 24 tracks in 3 days): more of the stored days, newest first, until
                // there are 30 real arrivals and 30 departures or every stored day is in.
                int Seen(string kind) => prof.Samples.Count(x => x.Kind == kind);
                if (Seen("arrival") < 30 || Seen("departure") < 30)
                {
                    var more = days.Where(d => !used.Contains(d)).ToList();
                    var added = 0;
                    foreach (var d in more)
                    {
                        await Add(d); added++;
                        if (added % 3 == 0 || d == more[^1])
                        {
                            prof = TrackLearner.Learn(a.Icao, a.Map, a.ElevationFt, a.Ends, tracks, "LAIA Collector", DaysText());
                            if (Seen("arrival") >= 30 && Seen("departure") >= 30) break;
                        }
                    }
                    if (added > 0) log.LogInformation("{icao}: a quiet airport — {n} more stored day(s) learned ({arr} arrivals, {dep} departures now)", a.Icao, added, Seen("arrival"), Seen("departure"));
                }
                Directory.CreateDirectory(Dir);
                await File.WriteAllTextAsync(Path.Combine(Dir, a.Icao + ".json"), JsonSerializer.Serialize(prof, Json), ct);
                lock (_gate) _profiles[a.Icao] = prof;
                log.LogInformation("Learned {icao} from {n} real tracks (LAIA Collector, {days}): {what}", a.Icao, tracks.Count, DaysText(), prof.Describe());
                Status = $"learned {a.Icao} from the LAIA Collector ({DaysText()})";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Not reachable (no tunnel yet, NAS or internet down): the rest go straight to the download.
                if (ex is HttpRequestException or TaskCanceledException or JsonException) reachable = false;
                log.LogInformation("LAIA Collector not used for {icao} ({src}): {msg}", a.Icao, src, ex.Message);
                left.Add(a);
            }
        }
        return left;
    }

    /// <summary>
    /// A flow the real tracks hardly cover: the runways facing the other way from the most used ones had fewer than 20
    /// arrivals, or under a quarter as many, while the most used way had 20 or more. Null when both ways are covered (or neither is busy enough to tell).
    /// </summary>
    private static (string text, double heading)? MissingFlow(AirportProfile prof, IReadOnlyList<Laia.Server.TrafficCore.RunwayEnd> ends)
    {
        if (prof.Arrivals.Count == 0) return null;
        var top = prof.Arrivals.OrderByDescending(kv => kv.Value.Count).First();
        var topEnd = ends.FirstOrDefault(e => e.Ident == top.Key);
        if (topEnd is null) return null;
        int Count(Func<Laia.Server.TrafficCore.RunwayEnd, bool> way) => prof.Arrivals.Where(kv => ends.FirstOrDefault(e => e.Ident == kv.Key) is { } e && way(e)).Sum(kv => kv.Value.Count);
        var same = Count(e => Math.Abs(Geo2.Diff(e.Heading, topEnd.Heading)) < 60);
        var other = Count(e => Math.Abs(Geo2.Diff(e.Heading, topEnd.Heading + 180)) < 60);
        // Thin as well as missing (KORD, 16 Nov: 12–14 Nov west flow, ~2,500 arrivals, against a few dozen landing east
        // from a short switch — enough to pass "20", not enough to show or fly the east flow that night): under a quarter
        // of the main flow's arrivals.
        if (same < 20 || other >= Math.Max(20, same / 4)) return null;
        var otherIdents = string.Join("/", ends.Where(e => Math.Abs(Geo2.Diff(e.Heading, topEnd.Heading + 180)) < 60).Select(e => e.Ident).Distinct());
        return ($"the other flow ({otherIdents}) has only {other} real arrivals against {same}", (topEnd.Heading + 180) % 360);
    }

    /// <summary>The release's parts (.tar.aa, .tar.ab, …) read one after another as one stream, without storing them.</summary>
    private sealed class PartsStream(HttpClient http, List<string> urls, Action<long> progress) : Stream
    {
        private int _i = -1;
        private Stream? _cur;
        private HttpResponseMessage? _resp;
        private long _read;
        public Action<string>? Log;
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
                    Log?.Invoke($"part {_i + 1} of {urls.Count}: HTTP {(int)_resp.StatusCode}, {_resp.Content.Headers.ContentLength?.ToString() ?? "?"} bytes from {_resp.RequestMessage?.RequestUri?.Host}");
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
