using System.Text.Json;

namespace Laia.Server.TrafficCore;

/// <summary>
/// Saved real tracks (LAIA Collector, 15 Nov): one day of an airport's nearby airline tracks as compact JSON —
/// [[[t, lat, lon, alt|null, track], …], …], one inner list per aircraft — and which way that day's landings faced.
/// Shared by the collector on the NAS (which writes them) and LAIA Server (which learns from them).
/// </summary>
public static class TrackStore
{
    /// <summary>What counts as near an airport: within 45 nm and below 18,000 ft above it (the rule learning has always used).</summary>
    public const double RadiusNm = 45, AboveFt = 18000;

    /// <summary>The part of a trace near this airport and low enough to be arriving or departing; null when under 5 points.</summary>
    public static List<TrackPoint>? Near(IReadOnlyList<TrackPoint> pts, double lat, double lon, double elevFt)
    {
        var near = pts.Where(p => (p.AltFt ?? 0) < elevFt + AboveFt && Geo2.DistanceNm(lat, lon, p.Lat, p.Lon) < RadiusNm).ToList();
        return near.Count >= 5 ? near : null;
    }

    /// <summary>One aircraft's track, written as an array of points.</summary>
    public static void Write(Utf8JsonWriter w, IReadOnlyList<TrackPoint> track)
    {
        w.WriteStartArray();
        foreach (var p in track)
        {
            w.WriteStartArray();
            w.WriteNumberValue(Math.Round(p.T, 1));
            w.WriteNumberValue(Math.Round(p.Lat, 5));
            w.WriteNumberValue(Math.Round(p.Lon, 5));
            if (p.AltFt is { } a) w.WriteNumberValue(Math.Round(a)); else w.WriteNullValue();
            w.WriteNumberValue(Math.Round(p.Track));
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    /// <summary>A day's tracks, read back.</summary>
    public static List<IReadOnlyList<TrackPoint>> Read(byte[] json)
    {
        var res = new List<IReadOnlyList<TrackPoint>>();
        using var doc = JsonDocument.Parse(json);
        foreach (var tr in doc.RootElement.EnumerateArray())
        {
            var pts = new List<TrackPoint>(tr.GetArrayLength());
            foreach (var p in tr.EnumerateArray())
            {
                if (p.GetArrayLength() < 5) continue;
                double? alt = p[3].ValueKind == JsonValueKind.Number ? p[3].GetDouble() : null;
                pts.Add(new TrackPoint(p[0].GetDouble(), p[1].GetDouble(), p[2].GetDouble(), alt, p[4].GetDouble()));
            }
            if (pts.Count >= 5) res.Add(pts);
        }
        return res;
    }

    /// <summary>
    /// Adds this track's landings to the day's count by direction (true track on final, to the nearest 10°): points 1–6 nm
    /// out, 200–1,500 ft above the field, flying at the airport and coming down. Each approach is counted once — a track
    /// broken by more than 10 minutes is another flight. Departures (flying away) and overflights don't count.
    /// </summary>
    public static void CountLandings(IReadOnlyList<TrackPoint> near, double lat, double lon, double elevFt, Dictionary<int, int> count)
    {
        var final = new List<TrackPoint>();
        void Close()
        {
            if (final.Count >= 3 && (final[0].AltFt ?? 0) > (final[^1].AltFt ?? 0))
            {
                double x = 0, y = 0;
                foreach (var p in final) { x += Math.Sin(p.Track * Math.PI / 180); y += Math.Cos(p.Track * Math.PI / 180); }
                var hdg = (Math.Atan2(x, y) * 180 / Math.PI + 360) % 360;
                var bin = (int)(Math.Round(hdg / 10) * 10) % 360;
                count[bin] = count.GetValueOrDefault(bin) + 1;
            }
            final.Clear();
        }
        double lastT = double.NaN;
        foreach (var p in near)
        {
            if (!double.IsNaN(lastT) && p.T - lastT > 600) Close();
            lastT = p.T;
            if (p.AltFt is not { } alt) continue;
            var above = alt - elevFt;
            if (above < 200 || above > 1500) continue;
            var d = Geo2.DistanceNm(p.Lat, p.Lon, lat, lon);
            if (d < 1 || d > 6) continue;
            if (Math.Abs(Geo2.Diff(p.Track, Geo2.BearingDeg(p.Lat, p.Lon, lat, lon))) > 25) continue;
            final.Add(p);
        }
        Close();
    }

    /// <summary>How many of a day's landings faced within 60° of this heading (true).</summary>
    public static int Facing(IReadOnlyDictionary<int, int>? count, double heading) =>
        count is null ? 0 : count.Where(kv => Math.Abs(Geo2.Diff(kv.Key, heading)) < 60).Sum(kv => kv.Value);
}
