using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Learned airport profiles from real tracks (M6): one day of adsb.lol's open history (github.com/adsblol/
/// globe_history_YYYY, a daily release of every aircraft's trace, ODbL), streamed — never stored whole — and every
/// track passing within 45 nm of the airports wanted, low enough to be arriving or departing, kept and learned from
/// (<see cref="TrackLearner"/>). Each airport is learned once, then refreshed after 60 days; saved in data/learned.
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
            var need = airports.Where(a => Profile(a.Icao) is not { } p || DateTime.UtcNow - p.LearnedUtc > TimeSpan.FromDays(60)).ToList();
            if (need.Count == 0) return;
            if (_lastTry.TryGetValue(string.Join(",", need.Select(a => a.Icao)), out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(6)) return;
            _lastTry[string.Join(",", need.Select(a => a.Icao))] = DateTime.UtcNow;
            _running = Task.Run(() => LearnAsync(need, CancellationToken.None));
        }
    }
    private readonly Dictionary<string, DateTime> _lastTry = new();

    private async Task LearnAsync(List<AirportModel> apts, CancellationToken ct)
    {
        var names = string.Join(", ", apts.Select(a => a.Icao));
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
            // The newest full day: the year's repository, its latest "prod" release.
            (string tag, List<(string name, string url, long size)> parts)? rel = null;
            foreach (var year in new[] { DateTime.UtcNow.Year, DateTime.UtcNow.Year - 1 })
            {
                using var r = await http.GetAsync($"https://api.github.com/repos/adsblol/globe_history_{year}/releases?per_page=12", ct);
                if (!r.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var tag = e.GetProperty("tag_name").GetString() ?? "";
                    if (!tag.Contains("-prod-")) continue;
                    var parts = e.GetProperty("assets").EnumerateArray()
                        .Select(a => (name: a.GetProperty("name").GetString() ?? "", url: a.GetProperty("browser_download_url").GetString() ?? "", size: a.GetProperty("size").GetInt64()))
                        .Where(a => a.name.Contains(".tar")).OrderBy(a => a.name, StringComparer.Ordinal).ToList();
                    if (parts.Count == 0) continue;
                    rel = (tag, parts); break;
                }
                if (rel is not null) break;
            }
            if (rel is not { } rl) { Status = "couldn't find an adsb.lol history day"; log.LogWarning("adsb.lol history: no release found"); return; }
            var total = rl.parts.Sum(p => p.size);
            var day = rl.tag.Replace("v", "").Split('-')[0];
            log.LogInformation("Learning {apts} from real tracks: adsb.lol {tag} ({gb:0.0} GB, streamed)", names, rl.tag, total / 1e9);
            var targets = apts.Select(a => (a, lat: a.Data.Lat, lon: a.Data.Lon, elev: a.ElevationFt)).ToList();
            var kept = targets.ToDictionary(t => t.a.Icao, _ => new List<IReadOnlyList<TrackPoint>>(), StringComparer.OrdinalIgnoreCase);
            long done = 0; var files = 0;
            await using var stream = new PartsStream(http, rl.parts.Select(p => p.url).ToList(), n => { done = n; Status = $"learning {names} from real tracks: {done / 1e9:0.0} of {total / 1e9:0.0} GB read"; });
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
                try { pts = AdsbTrace.Parse(bytes); } catch { continue; }
                if (pts is null || pts.Count < 5) continue;
                foreach (var t in targets)
                {
                    // Near this airport and low: a candidate arrival or departure (the rest of its day isn't kept).
                    var near = pts.Where(p => (p.AltFt ?? 0) < t.elev + 18000 && Geo2.DistanceNm(t.lat, t.lon, p.Lat, p.Lon) < 45).ToList();
                    if (near.Count >= 5) kept[t.a.Icao].Add(near);
                }
            }
            foreach (var t in targets)
            {
                var prof = TrackLearner.Learn(t.a.Icao, t.a.Map, t.elev, t.a.Ends, kept[t.a.Icao], "adsb.lol", day);
                Directory.CreateDirectory(Dir);
                await File.WriteAllTextAsync(Path.Combine(Dir, t.a.Icao + ".json"), JsonSerializer.Serialize(prof, Json), ct);
                lock (_gate) _profiles[t.a.Icao] = prof;
                log.LogInformation("Learned {icao} from {n} real tracks: {what}", t.a.Icao, kept[t.a.Icao].Count, prof.Describe());
            }
            Status = $"learned {names} from adsb.lol {day} ({files:N0} aircraft read)";
        }
        catch (Exception ex)
        {
            Status = "learning from real tracks failed: " + ex.Message;
            log.LogWarning("adsb.lol history: {msg}", ex.Message);
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
            while (true)
            {
                if (_cur is null)
                {
                    if (++_i >= urls.Count) return 0;
                    _resp?.Dispose();
                    _resp = await http.GetAsync(urls[_i], HttpCompletionOption.ResponseHeadersRead, ct);
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
