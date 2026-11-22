using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Keeps the winds-aloft field (TrafficCore.WindsAloft) up to date: the FD/FB forecast from aviationweather.gov (free,
/// no key; about 175 US stations, 3,000–39,000 ft) once an hour. The user's own sim wind is added by the traffic
/// engine as they fly.
/// </summary>
public sealed class WindsAloftReader(WorldAirports airports, NasrDb nasr, ILogger<WindsAloftReader> log) : BackgroundService
{
    // region=us: the service lists "all" in its help but no longer accepts it (400 Bad Request from 18 Nov 2026 — every
    // aircraft away from the user then flew the surface wind at every height); "us" is the whole contiguous US set.
    private const string Url = "https://aviationweather.gov/api/data/windtemp?region=us&level=low&fcst=06";
    private DateTime _lastOk = DateTime.UtcNow, _warned = DateTime.MinValue;
    public WindsAloft Field { get; } = new();
    public string Status { get; private set; } = "no forecast yet";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LAIA-Server");
        await Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            var ok = false;
            try
            {
                var text = await http.GetStringAsync(Url, ct);
                var parsed = WindsAloft.ParseFd(text);
                var stations = new List<WindsAloft.Station>();
                var unplaced = new List<string>();
                foreach (var (id, winds) in parsed)
                {
                    var apt = airports.Find("K" + id) ?? airports.Find("P" + id) ?? airports.Find(id);
                    (double lat, double lon)? at = apt is { } a ? (a.lat, a.lon) : nasr.Nav?.Point(id, 39, -98);
                    if (at is not { } p) { unplaced.Add(id); continue; }
                    stations.Add(new WindsAloft.Station(id, p.lat, p.lon, apt?.elevFt ?? 0, winds));
                }
                if (stations.Count > 0)
                {
                    Field.SetForecast(stations, DateTime.UtcNow);
                    ok = true;
                    Status = $"{stations.Count} stations (read {DateTime.UtcNow:HH:mm}Z)";
                    log.LogInformation("Winds aloft forecast: {n} stations{un}", stations.Count,
                        unplaced.Count > 0 ? $" ({unplaced.Count} not placed: {string.Join(", ", unplaced.Take(8))})" : "");
                }
                else log.LogInformation("Winds aloft forecast: nothing readable ({len} characters)", text.Length);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogInformation("Winds aloft forecast not read: {msg}", ex.Message); }
            if (ok) _lastOk = DateTime.UtcNow;
            else if (DateTime.UtcNow - _lastOk > TimeSpan.FromHours(1))
            {
                // Not silently: an hour without it is a warning, and the control page says so.
                Status = $"forecast unavailable since {_lastOk:HH:mm}Z — traffic away from you flies the surface wind";
                if (DateTime.UtcNow - _warned > TimeSpan.FromHours(3))
                {
                    _warned = DateTime.UtcNow;
                    log.LogWarning("Winds aloft forecast unavailable for {h:0} h: traffic away from you flies the airports' surface wind at every height", (DateTime.UtcNow - _lastOk).TotalHours);
                }
            }
            try { await Task.Delay(ok ? TimeSpan.FromMinutes(60) : TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
