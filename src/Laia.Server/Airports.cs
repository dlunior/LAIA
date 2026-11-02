using System.Text.Json;
using Laia.Common.Airports;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Airport layouts and procedures read from MSFS by LAIA Sim. Kept on disk (ProgramData\LAIA\airports) so an
/// airport is only read once per simulator; re-read after 28 days in case scenery or navigation data changed.
/// </summary>
public sealed class AirportStore(ILogger<AirportStore> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Dictionary<string, AirportData> _data = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AirportModel> _models = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private static string Dir => Path.Combine(Paths.DataDir, "airports");

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var file in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    var d = JsonSerializer.Deserialize<AirportData>(File.ReadAllText(file), Json);
                    if (d is not null && d.Icao.Length > 0) _data[d.Icao] = d;
                }
                catch (Exception ex) { log.LogWarning("Couldn't read {file}: {msg}", file, ex.Message); }
            }
            log.LogInformation("Airport layouts on file: {n}", _data.Count);
        }
        catch (Exception ex) { log.LogWarning("Airport folder error: {msg}", ex.Message); }
    }

    public void Add(AirportData d)
    {
        lock (_gate)
        {
            EnsureLoaded();
            d.Icao = d.Icao.Trim().ToUpperInvariant();
            _data[d.Icao] = d;
            _models.Remove(d.Icao);
            try
            {
                Directory.CreateDirectory(Dir);
                var path = Path.Combine(Dir, d.Icao + ".json");
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(d, Json));
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception ex) { log.LogWarning("Couldn't save {icao}: {msg}", d.Icao, ex.Message); }
        }
        var m = Model(d.Icao);
        log.LogInformation("Airport {icao} received from MSFS: {rw} runways, {pk} parking spots ({gates} usable), {tp} taxi points, {sid} SIDs, {star} STARs, {app} approaches; taxi map {cal}",
            d.Icao, d.Runways.Count, d.Parkings.Count, m?.Spots.Count ?? 0, d.Points.Count, d.Departures.Count, d.Arrivals.Count, d.Approaches.Count, m?.Calibration ?? "-");
    }

    /// <summary>Which of these airports still need reading from the simulator.</summary>
    public List<string> Missing(IEnumerable<string> icaos, string simulator)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var list = new List<string>();
            foreach (var i in icaos.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim().ToUpperInvariant()).Distinct())
            {
                if (!_data.TryGetValue(i, out var d) || DateTime.UtcNow - d.ScannedUtc > TimeSpan.FromDays(28) || d.Format < AirportData.CurrentFormat ||
                    (simulator.Length > 0 && d.Simulator.Length > 0 && d.Simulator != simulator))
                    list.Add(i);
            }
            return list;
        }
    }

    public AirportModel? Model(string icao)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_models.TryGetValue(icao, out var m)) return m;
            if (!_data.TryGetValue(icao, out var d)) return null;
            try { m = new AirportModel(d); }
            catch (Exception ex) { log.LogWarning("Airport {icao} layout couldn't be used: {msg}", icao, ex.Message); return null; }
            _models[icao] = m;
            return m;
        }
    }

    /// <summary>The nearest airport on file with a usable layout, within maxNm of the position.</summary>
    public (AirportModel model, double nm)? Nearest(double lat, double lon, double maxNm)
    {
        List<AirportData> all;
        lock (_gate) { EnsureLoaded(); all = _data.Values.ToList(); }
        (AirportData d, double nm)? best = null;
        foreach (var d in all)
        {
            var nm = Geo2.DistanceNm(lat, lon, d.Lat, d.Lon);
            if (nm <= maxNm && (best is null || nm < best.Value.nm)) best = (d, nm);
        }
        if (best is null) return null;
        var m = Model(best.Value.d.Icao);
        return m is null ? null : (m, best.Value.nm);
    }

    public int Count { get { lock (_gate) { EnsureLoaded(); return _data.Count; } } }
}
