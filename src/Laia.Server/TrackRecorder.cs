using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// LAIA's own "ADS-B": every 5 s, every aircraft each airport LAIA runs is flying (and the user) — position, altitude,
/// speed, heading, phase, runway, place in the sequence and who it's spacing on — plus each instruction or event from
/// the log as a marker where it happened (user, 22 Nov: "a map log almost like our own version of ADS-B data so you can
/// visually see misqueues"). One file an hour (UTC), JSON lines, gzipped once the hour is over; kept 7 days. Read back
/// for the map's replay and copied by LAIA Sim to %APPDATA%\LAIA\tracks.
///
/// Lines: {"t":unix,"w":"KMSP","f":[[cs,type,kind,lat,lon,alt,gs,hdg,phase,rwy,seq,leader],…]}
///        {"t":unix,"w":"KMSP","e":[cs,lat,lon,alt,text]}
/// </summary>
public sealed class TrackRecorder
{
    public static readonly TrackRecorder Instance = new();
    private readonly object _gate = new();
    private StreamWriter? _out;
    private string _hour = "";
    private DateTime _lastFrame = DateTime.MinValue;
    private DateTime _lastClean = DateTime.MinValue;

    public static string Dir => Path.Combine(Paths.DataDir, "tracks");

    private static string HourKey(DateTime utc) => utc.ToString("yyyyMMdd-HH");

    private StreamWriter Writer(DateTime utc)
    {
        var key = HourKey(utc);
        if (_out is not null && key == _hour) return _out;
        _out?.Dispose();
        var old = _hour;
        _hour = key;
        Directory.CreateDirectory(Dir);
        _out = new StreamWriter(new FileStream(Path.Combine(Dir, $"tracks-{key}.jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = false };
        if (old.Length > 0) Task.Run(() => Compress(Path.Combine(Dir, $"tracks-{old}.jsonl")));
        if (DateTime.UtcNow - _lastClean > TimeSpan.FromHours(1)) { _lastClean = DateTime.UtcNow; Task.Run(Clean); }
        return _out;
    }

    private static void Compress(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using (var src = File.OpenRead(path))
            using (var dst = new GZipStream(File.Create(path + ".gz"), CompressionLevel.Optimal))
                src.CopyTo(dst);
            File.Delete(path);
        }
        catch { /* tried again at the next clean */ }
    }

    private void Clean()
    {
        try
        {
            foreach (var f in Directory.GetFiles(Dir, "tracks-*"))
            {
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
                else if (f.EndsWith(".jsonl") && !f.Contains(_hour) && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)) Compress(f);
            }
        }
        catch { }
    }

    private static double R(double v, int d) => Math.Round(v, d);

    /// <summary>A frame of every world, at most every 5 s.</summary>
    public void Frame(IEnumerable<TrafficWorld> worlds)
    {
        var now = DateTime.UtcNow;
        if (now - _lastFrame < TimeSpan.FromSeconds(5)) return;
        _lastFrame = now;
        var t = now.Subtract(DateTime.UnixEpoch).TotalSeconds;
        lock (_gate)
        {
            try
            {
                var w = Writer(now);
                foreach (var world in worlds)
                {
                    var rows = new List<object[]>();
                    foreach (var f in world.Flights)
                    {
                        if (f.Done || f.Kind == "parked" || f.Phase is "parked" or "boarding") continue;
                        var (lat, lon) = world.Apt.Map.ToLatLon(f.Pos);
                        rows.Add(new object[] { f.Callsign, f.Type, f.Kind, R(lat, 5), R(lon, 5), Math.Round(f.AltFt), Math.Round(f.Gs), Math.Round(f.Heading), f.Phase, f.Rwy?.Ident ?? "", f.Seq, f.LeaderCs ?? "" });
                    }
                    if (world.UserPos is { } u && !world.RunwayOnly)
                    {
                        var (lat, lon) = world.Apt.Map.ToLatLon(u);
                        rows.Add(new object[] { (world.UserCallsign.Length > 0 ? world.UserCallsign : "you") + " (you)", world.UserType, "user", R(lat, 5), R(lon, 5), Math.Round(world.UserAltFt), Math.Round(world.UserGs), Math.Round(world.UserTrack), world.UserOnGround ? "ground" : "air", world.UserArr?.Rwy?.Ident ?? "", world.UserArr?.Seq ?? 0, world.UserArr?.LeaderCs ?? "" });
                    }
                    if (rows.Count == 0) continue;
                    w.WriteLine(JsonSerializer.Serialize(new { t = Math.Round(t), w = world.Apt.Icao, f = rows }));
                }
                w.Flush();
            }
            catch { /* the recording is best-effort */ }
        }
    }

    /// <summary>A log line about one aircraft ("CALLSIGN: …"), as a marker where it is.</summary>
    public void Event(TrafficWorld world, string line)
    {
        var i = line.IndexOf(": ", StringComparison.Ordinal);
        if (i <= 1 || i > 24) return;
        var cs = line[..i];
        if (cs.EndsWith(" (you)")) cs = cs[..^6];
        var f = world.Flights.FirstOrDefault(x => x.Callsign == cs && !x.Done) ?? (world.UserArr is { } ua && ua.Callsign == cs ? ua : null);
        if (f is null) return;
        var (lat, lon) = world.Apt.Map.ToLatLon(f.Pos);
        var t = DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;
        lock (_gate)
        {
            try
            {
                var w = Writer(DateTime.UtcNow);
                w.WriteLine(JsonSerializer.Serialize(new { t = Math.Round(t, 1), w = world.Apt.Icao, e = new object[] { cs, R(lat, 5), R(lon, 5), Math.Round(f.AltFt), line[(i + 2)..] } }));
            }
            catch { }
        }
    }

    /// <summary>The files there are (name, size, last written), newest first.</summary>
    public static List<(string name, long size, DateTime modified)> Files()
    {
        if (!Directory.Exists(Dir)) return new();
        return Directory.GetFiles(Dir, "tracks-*").Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Name).Select(f => (f.Name, f.Length, f.LastWriteTimeUtc)).ToList();
    }

    /// <summary>The lines between two times (unix seconds), from the hour files covering them; frames thinned to one
    /// every <paramref name="every"/> seconds.</summary>
    public string Range(double from, double to, int every)
    {
        lock (_gate) _out?.Flush();
        var sb = new StringBuilder("[");
        var first = true;
        var start = DateTime.UnixEpoch.AddSeconds(from); var end = DateTime.UnixEpoch.AddSeconds(to);
        var lastFrame = new Dictionary<string, double>();
        for (var h = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc); h <= end; h = h.AddHours(1))
        {
            var key = HourKey(h);
            var plain = Path.Combine(Dir, $"tracks-{key}.jsonl");
            Stream? s = File.Exists(plain) ? new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                : File.Exists(plain + ".gz") ? new GZipStream(File.OpenRead(plain + ".gz"), CompressionMode.Decompress) : null;
            if (s is null) continue;
            using var sr = new StreamReader(s);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                // "t" is the first field: read it without parsing the whole line.
                var c = line.IndexOf(',');
                if (c < 6 || !double.TryParse(line.AsSpan(5, c - 5), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)) continue;
                if (t < from || t > to) continue;
                if (line.Contains("\"f\":["))
                {
                    var wk = line.Substring(c + 6, Math.Min(4, line.Length - c - 6));
                    if (lastFrame.TryGetValue(wk, out var lt) && t - lt < every - 0.5) continue;
                    lastFrame[wk] = t;
                }
                if (!first) sb.Append(',');
                sb.Append(line); first = false;
            }
        }
        return sb.Append(']').ToString();
    }
}
