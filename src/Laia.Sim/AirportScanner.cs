using System.Net.Http.Json;
using Laia.Common;
using Laia.Common.Airports;
using Laia.Common.SimConnect;

namespace Laia.Sim;

/// <summary>
/// Reads an airport's layout (runways, taxiways, parking) and its departure, arrival and approach
/// procedures from MSFS itself, so LAIA's traffic uses exactly the scenery and navigation data the user
/// has installed. Uses its own SimConnect connection with the newer MSFS protocol (the facility functions
/// need it); the main connection stays on the older protocol that everything else is proven on.
/// </summary>
internal sealed class AirportScanner : IAsyncDisposable
{
    private const uint DefLayout = 1, DefProcedures = 2;
    private SimConnectClient? _sc;
    private Task? _run;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<uint, Pending> _pending = new();
    private readonly HashSet<uint> _procDefSends = new();
    private readonly Dictionary<uint, string> _sentFields = new();
    private bool _procedureFieldsRejected;
    private uint _nextReq = 100;

    private sealed class Pending
    {
        public required uint Def;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Dictionary<uint, Node> Nodes = new();
        public Node? Root;
    }

    private sealed class Node
    {
        public uint Type;
        public uint Index;
        public object? Value;
        public readonly List<Node> Children = new();
    }

    private static readonly string[] LegFields =
    {
        "TYPE", "FIX_ICAO", "FIX_LATITUDE", "FIX_LONGITUDE", "FLY_OVER", "TURN_DIRECTION", "TRUE_DEGREE",
        "COURSE", "ROUTE_DISTANCE", "DISTANCE_MINUTE", "APPROACH_ALT_DESC", "ALTITUDE1", "ALTITUDE2", "SPEED_LIMIT",
    };

    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        var sc = await SimConnectClient.ConnectAsync("LAIA Sim airport data", null, ct, SimConnectClient.ProtocolMsfs);
        if (sc is null) { Log.Write("Airport data: couldn't open the MSFS-protocol SimConnect connection"); return false; }
        sc.FacilityData += OnData;
        sc.FacilityDataEnd += OnEnd;
        sc.SimException += (code, sendId) =>
        {
            string? field;
            lock (_gate) { _sentFields.TryGetValue(sendId, out field); if (_procDefSends.Contains(sendId)) _procedureFieldsRejected = true; }
            Log.Write($"Airport data: SimConnect exception {code} on packet {sendId}{(field is null ? "" : " (" + field + ")")}");
        };
        _sc = sc;
        _run = Task.Run(async () => { try { await sc.RunAsync(_cts.Token); } catch { } });

        async Task Add(uint def, string f)
        {
            var id = await sc.AddToFacilityDefinitionAsync(def, f);
            lock (_gate) { _sentFields[id] = $"definition {def}: {f}"; if (def == DefProcedures) _procDefSends.Add(id); }
        }

        // --- Layout: airport, runways, taxi points, parking, taxi paths, taxiway names
        foreach (var f in new[] { "OPEN AIRPORT", "LATITUDE", "LONGITUDE", "ALTITUDE", "MAGVAR", "NAME", "ICAO",
                     "N_RUNWAYS", "N_TAXI_POINTS", "N_TAXI_PARKINGS", "N_TAXI_PATHS", "N_TAXI_NAMES",
                     "OPEN RUNWAY", "LATITUDE", "LONGITUDE", "ALTITUDE", "HEADING", "LENGTH", "WIDTH", "SURFACE",
                     "PRIMARY_NUMBER", "PRIMARY_DESIGNATOR", "SECONDARY_NUMBER", "SECONDARY_DESIGNATOR", "CLOSE RUNWAY",
                     "OPEN TAXI_POINT", "TYPE", "BIAS_X", "BIAS_Z", "CLOSE TAXI_POINT",
                     "OPEN TAXI_PARKING", "TYPE", "NAME", "NUMBER", "HEADING", "RADIUS", "BIAS_X", "BIAS_Z", "CLOSE TAXI_PARKING",
                     "OPEN TAXI_PATH", "TYPE", "WIDTH", "RUNWAY_NUMBER", "RUNWAY_DESIGNATOR", "START", "END", "NAME_INDEX", "CLOSE TAXI_PATH",
                     "OPEN TAXI_NAME", "NAME", "CLOSE TAXI_NAME",
                     "CLOSE AIRPORT" })
            await Add(DefLayout, f);

        // --- Procedures: SIDs, STARs and approaches with their legs
        async Task Legs(string list)
        {
            await Add(DefProcedures, "OPEN " + list);
            foreach (var f in LegFields) await Add(DefProcedures, f);
            await Add(DefProcedures, "CLOSE " + list);
        }
        async Task SidStar(string kind)
        {
            foreach (var f in new[] { "OPEN " + kind, "NAME", "N_RUNWAY_TRANSITIONS", "N_ENROUTE_TRANSITIONS", "N_APPROACH_LEGS",
                         "OPEN RUNWAY_TRANSITION", "RUNWAY_NUMBER", "RUNWAY_DESIGNATOR", "N_APPROACH_LEGS" })
                await Add(DefProcedures, f);
            await Legs("APPROACH_LEG");
            await Add(DefProcedures, "CLOSE RUNWAY_TRANSITION");
            foreach (var f in new[] { "OPEN ENROUTE_TRANSITION", "NAME", "N_APPROACH_LEGS" }) await Add(DefProcedures, f);
            await Legs("APPROACH_LEG");
            await Add(DefProcedures, "CLOSE ENROUTE_TRANSITION");
            await Legs("APPROACH_LEG");
            await Add(DefProcedures, "CLOSE " + kind);
        }
        foreach (var f in new[] { "OPEN AIRPORT", "N_DEPARTURES", "N_ARRIVALS", "N_APPROACHES" }) await Add(DefProcedures, f);
        await SidStar("DEPARTURE");
        await SidStar("ARRIVAL");
        foreach (var f in new[] { "OPEN APPROACH", "TYPE", "SUFFIX", "RUNWAY_NUMBER", "RUNWAY_DESIGNATOR", "N_TRANSITIONS", "N_FINAL_APPROACH_LEGS",
                     "OPEN APPROACH_TRANSITION", "NAME", "N_APPROACH_LEGS" })
            await Add(DefProcedures, f);
        await Legs("APPROACH_LEG");
        await Add(DefProcedures, "CLOSE APPROACH_TRANSITION");
        await Legs("FINAL_APPROACH_LEG");
        await Add(DefProcedures, "CLOSE APPROACH");
        await Add(DefProcedures, "CLOSE AIRPORT");
        await Task.Delay(500, ct); // let any definition errors arrive
        return true;
    }

    /// <summary>Reads one airport. Returns null if the simulator doesn't know it.</summary>
    public async Task<AirportData?> ScanAsync(string icao, string simulator, CancellationToken ct)
    {
        if (_sc is null) return null;
        var started = DateTime.UtcNow;
        var layout = await RequestAsync(DefLayout, icao, TimeSpan.FromSeconds(60), ct);
        if (layout?.Root?.Value is not AirportData apt) { Log.Write($"Airport data: MSFS has no data for {icao}"); return null; }
        apt.Simulator = simulator;
        apt.ScannedUtc = DateTime.UtcNow;
        foreach (var c in layout.Root.Children)
        {
            switch (c.Value)
            {
                case AirportRunway r: apt.Runways.Add(r); break;
                case TaxiName n: apt.TaxiNames.Add(n.Name); break;
            }
        }
        apt.Points = layout.Root.Children.Where(c => c.Value is TaxiPoint).OrderBy(c => c.Index).Select(c => (TaxiPoint)c.Value!).ToList();
        apt.Parkings = layout.Root.Children.Where(c => c.Value is Parking).OrderBy(c => c.Index).Select(c => (Parking)c.Value!).ToList();
        apt.Paths = layout.Root.Children.Where(c => c.Value is TaxiPath).OrderBy(c => c.Index).Select(c => (TaxiPath)c.Value!).ToList();

        bool rejected;
        lock (_gate) rejected = _procedureFieldsRejected;
        if (!rejected)
        {
            var procs = await RequestAsync(DefProcedures, icao, TimeSpan.FromSeconds(60), ct);
            if (procs?.Root is { } root)
            {
                foreach (var c in root.Children)
                {
                    if (c.Type == 10 || c.Type == 11) // DEPARTURE / ARRIVAL
                    {
                        var p = new Procedure { Name = (c.Value as string) ?? "" };
                        foreach (var k in c.Children)
                        {
                            if (k.Type == 12 && k.Value is Transition rt) { rt.Legs = LegsOf(k); p.RunwayTransitions.Add(rt); }
                            else if (k.Type == 13 && k.Value is Transition et) { et.Legs = LegsOf(k); p.EnrouteTransitions.Add(et); }
                            else if (k.Value is ProcedureLeg leg) p.Legs.Add(leg);
                        }
                        (c.Type == 10 ? apt.Departures : apt.Arrivals).Add(p);
                    }
                    else if (c.Type == 5 && c.Value is Approach a)
                    {
                        foreach (var k in c.Children)
                        {
                            if (k.Type == 6 && k.Value is Transition t) { t.Legs = LegsOf(k); a.Transitions.Add(t); }
                            else if (k.Type == 8 && k.Value is ProcedureLeg leg) a.FinalLegs.Add(leg);
                        }
                        apt.Approaches.Add(a);
                    }
                }
            }
        }
        else Log.Write("Airport data: this simulator doesn't accept the procedure fields; traffic will use simple routes");
        Log.Write($"Airport data: {icao} read in {(DateTime.UtcNow - started).TotalSeconds:0.0} s — {apt.Runways.Count} runways, {apt.Parkings.Count} parking spots, " +
                  $"{apt.Points.Count} taxi points, {apt.Paths.Count} taxi paths, {apt.Departures.Count} SIDs, {apt.Arrivals.Count} STARs, {apt.Approaches.Count} approaches");
        return apt;
    }

    private static List<ProcedureLeg> LegsOf(Node n) => n.Children.OrderBy(c => c.Index).Select(c => c.Value).OfType<ProcedureLeg>().ToList();

    private async Task<Pending?> RequestAsync(uint def, string icao, TimeSpan timeout, CancellationToken ct)
    {
        var req = Interlocked.Increment(ref _nextReq);
        var p = new Pending { Def = def };
        lock (_gate) _pending[req] = p;
        try
        {
            await _sc!.RequestFacilityDataAsync(def, req, icao);
            await p.Done.Task.WaitAsync(timeout, ct);
            return p;
        }
        catch (TimeoutException) { Log.Write($"Airport data: no answer for {icao} (definition {def})"); return p.Root is null ? null : p; }
        finally { lock (_gate) _pending.Remove(req); }
    }

    private sealed record TaxiName(string Name);

    private void OnEnd(uint req)
    {
        Pending? p;
        lock (_gate) _pending.TryGetValue(req, out p);
        p?.Done.TrySetResult();
    }

    private void OnData(FacilityRecord r)
    {
        Pending? p;
        lock (_gate) _pending.TryGetValue(r.RequestId, out p);
        if (p is null) return;
        try
        {
            var node = new Node { Type = r.Type, Index = r.ItemIndex, Value = Parse(p.Def, r) };
            lock (_gate)
            {
                p.Nodes[r.UniqueId] = node;
                if (r.Type == 0 && p.Root is null) p.Root = node;
                else if (p.Nodes.TryGetValue(r.ParentId, out var parent)) parent.Children.Add(node);
            }
        }
        catch (Exception ex) { Log.Write("Airport data: couldn't read an item: " + ex.Message); }
    }

    private static object? Parse(uint def, FacilityRecord r)
    {
        switch (r.Type)
        {
            case 0 when def == DefLayout: // AIRPORT
            {
                var a = new AirportData { Lat = r.F64(), Lon = r.F64(), ElevationM = r.F64(), MagVar = r.F32() };
                a.Name = r.Str(32);
                a.Icao = r.Str(8);
                return a;
            }
            case 0: return null; // procedure definition's airport record: only counts
            case 1: // RUNWAY
                return new AirportRunway
                {
                    Lat = r.F64(), Lon = r.F64(), ElevationM = r.F64(), Heading = r.F32(), LengthM = r.F32(), WidthM = r.F32(), Surface = r.I32(),
                    PrimaryNumber = r.I32(), PrimaryDesignator = r.I32(), SecondaryNumber = r.I32(), SecondaryDesignator = r.I32(),
                };
            case 14: return new TaxiPoint { Type = r.I32(), BiasX = r.F32(), BiasZ = r.F32() };
            case 15:
                return new Parking { Type = r.I32(), Name = r.I32(), Number = r.I32(), Heading = r.F32(), RadiusM = r.F32(), BiasX = r.F32(), BiasZ = r.F32() };
            case 16:
                return new TaxiPath
                {
                    Type = r.I32(), WidthM = r.F32(), RunwayNumber = r.I32(), RunwayDesignator = r.I32(), Start = r.I32(), End = r.I32(), NameIndex = r.I32(),
                };
            case 17: return new TaxiName(r.Str(32));
            case 10 or 11: return r.Str(8); // DEPARTURE / ARRIVAL name (counts follow, not needed)
            case 12: // RUNWAY_TRANSITION
                return new Transition { Name = AirportRunway.Ident(r.I32(), r.I32()) };
            case 13 or 6: // ENROUTE_TRANSITION / APPROACH_TRANSITION
                return new Transition { Name = r.Str(8) };
            case 5: // APPROACH
            {
                var type = r.I32();
                var suffix = r.I32();
                var rwy = AirportRunway.Ident(r.I32(), r.I32());
                return new Approach { Type = type, Suffix = suffix is > 32 and < 127 ? ((char)suffix).ToString().Trim() : "", Runway = rwy };
            }
            case 7 or 8: // APPROACH_LEG / FINAL_APPROACH_LEG
            {
                var leg = new ProcedureLeg { Type = r.I32(), Fix = r.Str(8), Lat = r.F64(), Lon = r.F64(), FlyOver = r.I32() == 1, Turn = r.I32(), TrueCourse = r.I32() == 1 };
                leg.Course = r.F32();
                var dist = r.F32();
                var minutes = r.I32() == 1;
                leg.DistanceM = minutes ? 0 : dist;
                leg.AltDesc = r.I32();
                leg.Alt1Ft = r.F32() * 3.28084;
                leg.Alt2Ft = r.F32() * 3.28084;
                leg.SpeedKt = r.F32();
                return leg;
            }
            default: return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_sc is not null) await _sc.DisposeAsync();
        if (_run is not null) try { await _run; } catch { }
    }
}

/// <summary>Scans the airports the server asks for (one at a time) and uploads them.</summary>
internal sealed class AirportUploader
{
    private readonly HashSet<string> _tried = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private AirportScanner? _scanner;
    private int _busy;
    private DateTime _nextConnectTry = DateTime.MinValue;

    public void Request(IEnumerable<string>? icaos, string serverBase, string simulator, CancellationToken ct)
    {
        if (icaos is null) return;
        var todo = icaos.Where(i => !string.IsNullOrWhiteSpace(i) && !_tried.Contains(i)).ToList();
        if (todo.Count == 0 || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (_scanner is null)
                {
                    if (DateTime.UtcNow < _nextConnectTry) return;
                    var s = new AirportScanner();
                    if (!await s.ConnectAsync(ct)) { _nextConnectTry = DateTime.UtcNow.AddMinutes(2); await s.DisposeAsync(); return; }
                    _scanner = s;
                }
                foreach (var icao in todo)
                {
                    _tried.Add(icao);
                    var apt = await _scanner.ScanAsync(icao, simulator, ct);
                    if (apt is null) continue;
                    using var resp = await _http.PostAsJsonAsync(serverBase + "/api/airport", apt, ct);
                    if (!resp.IsSuccessStatusCode) Log.Write($"Airport data: server didn't accept {icao} (HTTP {(int)resp.StatusCode})");
                }
            }
            catch (Exception ex) { Log.Write("Airport data error: " + ex.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }, ct);
    }

    public async Task ResetAsync()
    {
        _tried.Clear();
        if (_scanner is not null) { await _scanner.DisposeAsync(); _scanner = null; }
    }
}
