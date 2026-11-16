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
/// </summary>
public sealed class AdsbHistory(ILogger<AdsbHistory> log)
{
    public static JsonSerializerOptions Json => AdsbTrace.Json;
    private readonly Dictionary<string, AirportProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Task? _running;
    public string Status { get; private set; } = "nothing learned yet";
    private static string Dir => Path.Combine(Paths.DataDir, "data", "learned");

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
            if (need.Count == 0) return;
            // Not tried again within 6 hours — with the same rules: after an update that changes how airports are learned,
            // the airport and its neighbours are learned again together, at once (KMCI, 15 Nov: the pair had been tried
            // an hour before under the old rules, so they were learned one at a time, the main airport second, and its
            // traffic started at the 15-minute limit without the real tracks).
            var key = string.Join(",", need.Select(a => a.Icao)) + "|v" + AirportProfile.CurrentVersion;
            if (_lastTry.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(6)) return;
            _lastTry[key] = DateTime.UtcNow;
            _learning = need.Select(a => a.Icao).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _running = Task.Run(() => LearnAsync(need, CancellationToken.None));
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

    private async Task LearnAsync(List<AirportModel> apts, CancellationToken ct)
    {
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
                        log.LogInformation("{icao}: {miss}; learning another day for it", t.a.Icao, miss);
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

    /// <summary>
    /// A flow the real tracks hardly cover: the runways facing the other way from the most used ones had fewer than 20
    /// arrivals while the most used way had 20 or more. Null when both ways are covered (or neither is busy enough to tell).
    /// </summary>
    private static string? MissingFlow(AirportProfile prof, IReadOnlyList<Laia.Server.TrafficCore.RunwayEnd> ends)
    {
        if (prof.Arrivals.Count == 0) return null;
        var top = prof.Arrivals.OrderByDescending(kv => kv.Value.Count).First();
        var topEnd = ends.FirstOrDefault(e => e.Ident == top.Key);
        if (topEnd is null) return null;
        int Count(Func<Laia.Server.TrafficCore.RunwayEnd, bool> way) => prof.Arrivals.Where(kv => ends.FirstOrDefault(e => e.Ident == kv.Key) is { } e && way(e)).Sum(kv => kv.Value.Count);
        var same = Count(e => Math.Abs(Geo2.Diff(e.Heading, topEnd.Heading)) < 60);
        var other = Count(e => Math.Abs(Geo2.Diff(e.Heading, topEnd.Heading + 180)) < 60);
        if (same < 20 || other >= 20) return null;
        var otherIdents = string.Join("/", ends.Where(e => Math.Abs(Geo2.Diff(e.Heading, topEnd.Heading + 180)) < 60).Select(e => e.Ident).Distinct());
        return $"the other flow ({otherIdents}) has only {other} real arrivals against {same}";
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
