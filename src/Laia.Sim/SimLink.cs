using System.Diagnostics;
using System.Net.Http.Json;
using Laia.Common;
using Laia.Common.SimConnect;

namespace Laia.Sim;

/// <summary>
/// Connects to MSFS on this PC (no MSFS settings changed) and passes the user's aircraft state to
/// LAIA Server about once a second.
/// </summary>
internal sealed class SimLink : IDisposable
{
    private const uint DefNumbers = 1, DefStrings = 2, ReqNumbers = 1, ReqStrings = 2;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly object _gate = new();
    private readonly AircraftState _state = new();
    private SimConnectClient? _sc;
    private TrafficRenderer? _traffic;
    private AigCatalog? _catalog;
    private readonly AirportUploader _airports = new();
    private readonly ScheduleUploader _schedules = new();
    public int TrafficCount => _traffic?.Count ?? 0;
    private DateTime _lastData = DateTime.MinValue;

    public Func<ServerInfo?>? Server { get; set; }
    public event Action<string>? StatusChanged;
    public string Status { get; private set; } = "Waiting for MSFS";
    public bool Connected => _sc is not null && DateTime.UtcNow - _lastData < TimeSpan.FromSeconds(10);

    public void Start()
    {
        _ = Task.Run(() => ConnectLoop(_cts.Token));
        _ = Task.Run(() => SendLoop(_cts.Token));
    }

    private void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        Log.Write("Sim link: " + s);
        StatusChanged?.Invoke(s);
    }

    private static bool SimRunning() =>
        Process.GetProcessesByName("FlightSimulator2024").Length > 0 || Process.GetProcessesByName("FlightSimulator").Length > 0;

    private async Task ConnectLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!SimRunning()) { SetStatus("Waiting for MSFS"); await Task.Delay(5000, ct); continue; }
                SetStatus("MSFS found, connecting…");
                var attempts = new List<string>();
                var sc = await SimConnectClient.ConnectAsync("LAIA Sim", attempts.Add, ct);
                if (sc is null)
                {
                    // MSFS takes a while to start SimConnect while loading; keep trying quietly.
                    Log.Write("SimConnect not ready: " + string.Join("; ", attempts));
                    await Task.Delay(8000, ct);
                    continue;
                }
                Log.Write($"SimConnect open via {sc.Endpoint} ({sc.SimulatorName})");
                await RegisterAsync(sc);
                _sc = sc;
                var is2024 = sc.SimulatorName.Contains("SunRise", StringComparison.OrdinalIgnoreCase);
                _catalog ??= await Task.Run(() => AigCatalog.Scan(is2024));
                _traffic = new TrafficRenderer(sc, _catalog);
                await _traffic.InitAsync();
                lock (_gate) { _state.Simulator = sc.SimulatorName.Contains("SunRise", StringComparison.OrdinalIgnoreCase) ? "MSFS 2024" : sc.SimulatorName.Contains("KittyHawk", StringComparison.OrdinalIgnoreCase) ? "MSFS 2020" : sc.SimulatorName; }
                SetStatus($"Connected to {_state.Simulator}");
                await sc.RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Write("Sim link error: " + ex.Message); }
            finally
            {
                if (_traffic is not null) { try { await _traffic.DisposeAsync(); } catch { } _traffic = null; }
                try { await _airports.ResetAsync(); } catch { }
                if (_sc is not null) { await _sc.DisposeAsync(); _sc = null; }
                lock (_gate) _state.Connected = false;
            }
            SetStatus("MSFS disconnected");
            try { await Task.Delay(5000, ct); } catch { break; }
        }
    }

    private static readonly (string name, string units)[] Numbers =
    {
        ("PLANE LATITUDE", "degrees"), ("PLANE LONGITUDE", "degrees"), ("PLANE ALTITUDE", "feet"), ("PLANE ALT ABOVE GROUND", "feet"),
        ("PLANE HEADING DEGREES TRUE", "degrees"), ("PLANE HEADING DEGREES MAGNETIC", "degrees"), ("AIRSPEED INDICATED", "knots"),
        ("GROUND VELOCITY", "knots"), ("VERTICAL SPEED", "feet per minute"), ("SIM ON GROUND", "bool"),
        ("COM ACTIVE FREQUENCY:1", "MHz"), ("COM STANDBY FREQUENCY:1", "MHz"), ("COM ACTIVE FREQUENCY:2", "MHz"), ("COM STANDBY FREQUENCY:2", "MHz"),
        ("COM TRANSMIT:1", "bool"), ("COM TRANSMIT:2", "bool"), ("TRANSPONDER CODE:1", "number"),
        ("AMBIENT WIND DIRECTION", "degrees"), ("AMBIENT WIND VELOCITY", "knots"),
        ("ZULU TIME", "seconds"), ("ZULU DAY OF WEEK", "number"),
    };

    private async Task RegisterAsync(SimConnectClient sc)
    {
        sc.SimException += (code, id) => Log.Write($"SimConnect exception {code} on request {id}");
        sc.Data += OnData;
        foreach (var (name, units) in Numbers) await sc.AddToDataDefinitionAsync(DefNumbers, name, units, SimDataType.Float64);
        await sc.AddToDataDefinitionAsync(DefStrings, "TITLE", null, SimDataType.String256);
        await sc.AddToDataDefinitionAsync(DefStrings, "ATC ID", null, SimDataType.String64);
        await sc.AddToDataDefinitionAsync(DefStrings, "ATC AIRLINE", null, SimDataType.String64);
        await sc.AddToDataDefinitionAsync(DefStrings, "ATC FLIGHT NUMBER", null, SimDataType.String64);
        await sc.RequestDataOnSimObjectAsync(ReqNumbers, DefNumbers, 0, SimPeriod.Second);
        await sc.RequestDataOnSimObjectAsync(ReqStrings, DefStrings, 0, SimPeriod.Second, flags: 1); // only when changed
    }

    private void OnData(SimObjectData d)
    {
        _lastData = DateTime.UtcNow;
        lock (_gate)
        {
            _state.Connected = true;
            if (d.RequestId == ReqNumbers)
            {
                _state.Lat = d.ReadFloat64();
                _state.Lon = d.ReadFloat64();
                _state.AltitudeFt = d.ReadFloat64();
                _state.AglFt = d.ReadFloat64();
                _state.HeadingTrue = d.ReadFloat64();
                _state.HeadingMag = d.ReadFloat64();
                _state.Ias = d.ReadFloat64();
                _state.GroundSpeed = d.ReadFloat64();
                _state.VerticalSpeed = d.ReadFloat64();
                _state.OnGround = d.ReadFloat64() > 0.5;
                _state.Com1Active = Math.Round(d.ReadFloat64(), 3);
                _state.Com1Standby = Math.Round(d.ReadFloat64(), 3);
                _state.Com2Active = Math.Round(d.ReadFloat64(), 3);
                _state.Com2Standby = Math.Round(d.ReadFloat64(), 3);
                _state.Com1Transmit = d.ReadFloat64() > 0.5;
                _state.Com2Transmit = d.ReadFloat64() > 0.5;
                var sq = (int)Math.Round(d.ReadFloat64());
                // MSFS 2024 reports the plain code (1200); some aircraft report BCO16 (0x1200 = 4608).
                var dec = sq.ToString("D4");
                _state.Squawk = dec.All(ch => ch <= '7') ? dec : sq.ToString("X4");
                _state.WindDirection = d.ReadFloat64();
                _state.WindSpeed = d.ReadFloat64();
                var zuluSec = d.ReadFloat64();
                var dow = (int)Math.Round(d.ReadFloat64()); // MSFS: 0 = Sunday
                _state.ZuluWeekMinute = dow is >= 0 and <= 6 ? ((dow + 6) % 7) * 1440 + zuluSec / 60 : -1;
            }
            else if (d.RequestId == ReqStrings)
            {
                _state.Title = d.ReadString(256);
                _state.AtcId = d.ReadString(64);
                _state.AtcAirline = d.ReadString(64);
                _state.AtcFlightNumber = d.ReadString(64);
            }
        }
    }

    public AircraftState Snapshot()
    {
        lock (_gate)
        {
            var copy = _state.Clone();
            copy.Connected = Connected;
            return copy;
        }
    }

    private async Task SendLoop(CancellationToken ct)
    {
        var lastSentConnected = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = Server?.Invoke();
                var snap = Snapshot();
                if (server is not null && (snap.Connected || lastSentConnected))
                {
                    using var resp = await _http.PostAsJsonAsync(server.BaseUrl + "/api/sim/state", snap, ct);
                    lastSentConnected = snap.Connected;
                    if (resp.IsSuccessStatusCode && _traffic is { } tr && snap.Connected)
                    {
                        var reply = await resp.Content.ReadFromJsonAsync<SimStateReply>(cancellationToken: ct);
                        if (reply is not null)
                        {
                            tr.Update(reply.Traffic, reply.ServerTimeMs / 1000.0, reply.Motion ?? "laia");
                            _airports.Request(reply.NeedAirports, server.BaseUrl, snap.Simulator, ct);
                            if (reply.NeedCatalog && _catalog is { } cat) _ = SendCatalogAsync(server.BaseUrl, cat, ct);
                            if (_catalog is { } cat2) _schedules.Request(reply.NeedSchedules, server.BaseUrl, cat2, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* server briefly unreachable; the tray heartbeat handles reconnects */ }
            try { await Task.Delay(250, ct); } catch { break; } // 4x a second: smooth traffic, trivial network load
        }
    }

    private async Task SendCatalogAsync(string baseUrl, AigCatalog cat, CancellationToken ct)
    {
        try
        {
            var entries = cat.Models.GroupBy(m => (m.Airline, m.Type)).Where(g => g.Key.Airline.Length is 2 or 3 && g.Key.Type.Length > 0)
                .Select(g => new Laia.Common.Airports.CatalogEntry(g.Key.Airline, g.Key.Type, g.Count())).ToList();
            using var resp = await _http.PostAsJsonAsync(baseUrl + "/api/traffic/catalog", new Laia.Common.Airports.TrafficCatalog { Entries = entries }, ct);
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
