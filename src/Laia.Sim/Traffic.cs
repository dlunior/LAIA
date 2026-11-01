using System.Buffers.Binary;
using Laia.Common;
using Laia.Common.SimConnect;

namespace Laia.Sim;

/// <summary>AIG aircraft models installed in the Community folder, by airline and type.</summary>
internal sealed class AigCatalog
{
    public sealed record Model(string Title, string Airline, string Type);
    public List<Model> Models { get; } = new();
    public static string Fallback => "Airbus A320 Neo Asobo";

    public static AigCatalog Scan(bool msfs2024)
    {
        var cat = new AigCatalog();
        var sims = MsfsDetector.Detect();
        var community = sims.Where(s => s.Name.Contains(msfs2024 ? "2024" : "2020")).Select(s => s.CommunityPath)
            .Concat(sims.Select(s => s.CommunityPath)).Where(p => p is not null && Directory.Exists(p)).Distinct().ToList();
        foreach (var c in community)
        {
            try
            {
                foreach (var pkg in Directory.GetDirectories(c!, "aig-*"))
                    foreach (var cfg in Directory.EnumerateFiles(pkg, "aircraft.cfg", SearchOption.AllDirectories))
                        cat.Parse(cfg);
            }
            catch (Exception ex) { Log.Write("AIG scan error: " + ex.Message); }
            if (cat.Models.Count > 0) break; // use the first Community folder that has AIG models
        }
        Log.Write($"AIG catalog: {cat.Models.Count} liveries found");
        return cat;
    }

    private void Parse(string path)
    {
        string section = "", type = "";
        string? title = null, airline = null;
        void Flush()
        {
            if (title is not null) Models.Add(new Model(title, (airline ?? "").ToUpperInvariant(), type.ToUpperInvariant()));
            title = null; airline = null;
        }
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split(';')[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                if (section.StartsWith("FLTSIM.", StringComparison.OrdinalIgnoreCase)) Flush();
                section = line.Trim('[', ']').Trim();
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var val = line[(eq + 1)..].Trim().Trim('"').Trim();
            if (section.Equals("GENERAL", StringComparison.OrdinalIgnoreCase))
            {
                // icao_type_designator ("A321") is the reliable one; atc_model ("$$:A321") is only a fallback.
                if (key == "icao_type_designator" && val.Length > 0) type = val;
                else if (key == "atc_model" && type.Length == 0) type = val.Replace("$$:", "");
            }
            else if (section.StartsWith("FLTSIM.", StringComparison.OrdinalIgnoreCase))
            {
                if (key == "title") title = val;
                else if (key == "icao_airline" && val.Length > 0) airline = val;
                else if (key == "atc_parking_codes" && airline is null) airline = val.Split(',')[0].Trim();
            }
        }
        if (section.StartsWith("FLTSIM.", StringComparison.OrdinalIgnoreCase)) Flush();
    }

    private static string Family(string t) => t switch
    {
        "B736" or "B737" or "B738" or "B739" or "B37M" or "B38M" or "B39M" => "B737",
        "A318" or "A319" or "A320" or "A321" or "A19N" or "A20N" or "A21N" => "A320",
        "BCS1" or "BCS3" or "A220" => "A220",
        "B752" or "B753" => "B757",
        _ => t,
    };

    /// <summary>Best model for an airline and type: exact match, then same family, then same airline, then same type.</summary>
    public string Pick(string airline, string type, int seed)
    {
        if (Models.Count == 0) return Fallback;
        var fam = Family(type == "A220" ? "BCS3" : type);
        var tiers = new Func<Model, bool>[]
        {
            m => m.Airline == airline && m.Type == type,
            m => m.Airline == airline && Family(m.Type) == fam,
            m => m.Airline == airline,
            m => Family(m.Type) == fam,
            _ => true,
        };
        foreach (var t in tiers)
        {
            var hits = Models.Where(t).ToList();
            if (hits.Count > 0) return hits[Math.Abs(seed) % hits.Count].Title;
        }
        return Fallback;
    }
}

/// <summary>
/// Draws the server's AI traffic in MSFS: creates a non-ATC AI aircraft for each, takes it away from
/// MSFS's own AI, and moves it smoothly 20 times a second between the server's once-a-second updates.
/// </summary>
internal sealed class TrafficRenderer : IAsyncDisposable
{
    private const uint DefPos = 10, DefGround = 11, DefGear = 12;
    private const uint EvFreezeLatLon = 100, EvFreezeAlt = 101, EvFreezeAtt = 102;
    // Light switches, in the order of the TrafficAircraft.Lights bits.
    private static readonly string[] LightEvents =
        { "NAV_LIGHTS_SET", "BEACON_LIGHTS_SET", "STROBES_SET", "LANDING_LIGHTS_SET", "TAXI_LIGHTS_SET", "LOGO_LIGHTS_SET", "WING_LIGHTS_SET" };
    private const uint EvLightBase = 110;
    private readonly SimConnectClient _sc;
    private readonly AigCatalog _catalog;
    private readonly object _gate = new();
    private readonly Dictionary<string, Obj> _objs = new();
    private readonly CancellationTokenSource _cts = new();
    private uint _nextReq = 5000;

    private sealed class Obj
    {
        public required TrafficAircraft Target;
        public DateTime TargetTime;
        public uint CreateReq, GroundReq, ObjectId;
        public bool Ready, Removed;
        public double Lat, Lon, Alt, Hdg, Pitch;
        public double GroundAlt = double.NaN, CgFt = double.NaN;
        public DateTime LastGear, LastLights;
        public int SentLights = -1;
    }

    public TrafficRenderer(SimConnectClient sc, AigCatalog catalog) { _sc = sc; _catalog = catalog; }

    public int Count { get { lock (_gate) return _objs.Count; } }

    public async Task InitAsync()
    {
        foreach (var (n, u) in new[] { ("PLANE LATITUDE", "degrees"), ("PLANE LONGITUDE", "degrees"), ("PLANE ALTITUDE", "feet"),
                     ("PLANE PITCH DEGREES", "degrees"), ("PLANE BANK DEGREES", "degrees"), ("PLANE HEADING DEGREES TRUE", "degrees") })
            await _sc.AddToDataDefinitionAsync(DefPos, n, u, SimDataType.Float64);
        await _sc.AddToDataDefinitionAsync(DefGround, "GROUND ALTITUDE", "feet", SimDataType.Float64);
        await _sc.AddToDataDefinitionAsync(DefGround, "STATIC CG TO GROUND", "feet", SimDataType.Float64);
        await _sc.AddToDataDefinitionAsync(DefGear, "GEAR HANDLE POSITION", "bool", SimDataType.Float64);
        await _sc.MapClientEventToSimEventAsync(EvFreezeLatLon, "FREEZE_LATITUDE_LONGITUDE_SET");
        await _sc.MapClientEventToSimEventAsync(EvFreezeAlt, "FREEZE_ALTITUDE_SET");
        await _sc.MapClientEventToSimEventAsync(EvFreezeAtt, "FREEZE_ATTITUDE_SET");
        for (var i = 0; i < LightEvents.Length; i++) await _sc.MapClientEventToSimEventAsync(EvLightBase + (uint)i, LightEvents[i]);
        _sc.AssignedObjectId += OnAssigned;
        _sc.Data += OnData;
        _ = Task.Run(() => RenderLoop(_cts.Token));
    }

    /// <summary>Called about once a second with the server's current traffic list.</summary>
    public void Update(List<TrafficAircraft> list)
    {
        var toCreate = new List<Obj>();
        var toRemove = new List<Obj>();
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var t in list)
            {
                if (_objs.TryGetValue(t.Id, out var o)) { o.Target = t; o.TargetTime = now; continue; }
                o = new Obj { Target = t, TargetTime = now, CreateReq = _nextReq++, GroundReq = _nextReq++, Lat = t.Lat, Lon = t.Lon, Alt = t.AltFt, Hdg = t.Heading };
                _objs[t.Id] = o;
                toCreate.Add(o);
            }
            foreach (var (id, o) in _objs.ToList())
            {
                if (list.Any(t => t.Id == id)) continue;
                _objs.Remove(id);
                toRemove.Add(o);
            }
        }
        foreach (var o in toCreate) _ = CreateAsync(o);
        foreach (var o in toRemove) _ = RemoveAsync(o);
    }

    private async Task CreateAsync(Obj o)
    {
        var t = o.Target;
        var title = _catalog.Pick(t.Airline, t.Type, t.Id.GetHashCode());
        Log.Write($"Traffic: creating {t.Callsign} ({t.Type}) as \"{title}\"");
        try
        {
            await _sc.AICreateNonATCAircraftAsync(title, t.Callsign.Length > 12 ? t.Callsign[..12] : t.Callsign, t.Lat, t.Lon, t.AltFt,
                -t.Pitch, t.Bank, t.Heading, t.OnGround, (int)Math.Round(t.GroundSpeed), o.CreateReq);
        }
        catch (Exception ex) { Log.Write("Traffic create failed: " + ex.Message); }
    }

    private async void OnAssigned(uint req, uint objectId)
    {
        Obj? o;
        lock (_gate) o = _objs.Values.FirstOrDefault(x => x.CreateReq == req);
        if (o is null) { _ = _sc.AIRemoveObjectAsync(objectId, _nextReq++); return; } // removed before it appeared
        o.ObjectId = objectId;
        try
        {
            await _sc.AIReleaseControlAsync(objectId, _nextReq++);
            await _sc.TransmitClientEventAsync(objectId, EvFreezeLatLon, 1);
            await _sc.TransmitClientEventAsync(objectId, EvFreezeAlt, 1);
            await _sc.TransmitClientEventAsync(objectId, EvFreezeAtt, 1);
            await _sc.RequestDataOnSimObjectAsync(o.GroundReq, DefGround, objectId, SimPeriod.Second);
            o.Ready = true;
            Log.Write($"Traffic: {o.Target.Callsign} is object {objectId}");
        }
        catch (Exception ex) { Log.Write("Traffic setup failed: " + ex.Message); }
    }

    private void OnData(SimObjectData d)
    {
        if (d.RequestId < 5000) return;
        Obj? o;
        lock (_gate) o = _objs.Values.FirstOrDefault(x => x.GroundReq == d.RequestId);
        if (o is null) return;
        o.GroundAlt = d.ReadFloat64();
        o.CgFt = d.ReadFloat64();
    }

    private async Task RemoveAsync(Obj o)
    {
        o.Removed = true;
        if (o.ObjectId == 0) return;
        Log.Write($"Traffic: removing {o.Target.Callsign}");
        try { await _sc.AIRemoveObjectAsync(o.ObjectId, _nextReq++); } catch { }
    }

    private async Task RenderLoop(CancellationToken ct)
    {
        const double frame = 0.05;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(frame));
        var buf = new byte[48];
        var gear = new byte[8];
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                List<Obj> objs;
                lock (_gate) objs = _objs.Values.Where(o => o.Ready && !o.Removed).ToList();
                var now = DateTime.UtcNow;
                foreach (var o in objs)
                {
                    var t = o.Target;
                    // Dead-reckon the server's last position forward, then glide toward it.
                    var age = Math.Min((now - o.TargetTime).TotalSeconds, 3);
                    var (pLat, pLon) = Offset(t.Lat, t.Lon, t.Heading, t.GroundSpeed * age / 3600);
                    var pAlt = t.AltFt + t.VerticalSpeed / 60 * age;
                    var k = Math.Min(1, 6 * frame);
                    o.Lat += (pLat - o.Lat) * k;
                    o.Lon += (pLon - o.Lon) * k;
                    o.Alt += (pAlt - o.Alt) * k;
                    o.Pitch += (t.Pitch - o.Pitch) * k;
                    var dh = ((t.Heading - o.Hdg + 540) % 360) - 180;
                    o.Hdg = (o.Hdg + dh * k + 360) % 360;
                    var alt = t.OnGround && !double.IsNaN(o.GroundAlt) && !double.IsNaN(o.CgFt) ? o.GroundAlt + o.CgFt : o.Alt;
                    if (!t.OnGround && !double.IsNaN(o.GroundAlt) && !double.IsNaN(o.CgFt)) alt = Math.Max(alt, o.GroundAlt + o.CgFt);

                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(0), o.Lat);
                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(8), o.Lon);
                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(16), alt);
                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(24), -o.Pitch); // MSFS: positive pitch is nose down
                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(32), t.Bank);
                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(40), o.Hdg);
                    try
                    {
                        await _sc.SetDataOnSimObjectAsync(DefPos, o.ObjectId, buf.ToArray());
                        // Lights: send when they change, and repeat every 5 s in case the model reset them.
                        if (t.Lights != o.SentLights || (now - o.LastLights).TotalSeconds > 5)
                        {
                            o.SentLights = t.Lights;
                            o.LastLights = now;
                            for (var i = 0; i < LightEvents.Length; i++)
                                await _sc.TransmitClientEventAsync(o.ObjectId, EvLightBase + (uint)i, (uint)((t.Lights >> i) & 1));
                        }
                        if ((now - o.LastGear).TotalSeconds > 1)
                        {
                            o.LastGear = now;
                            BinaryPrimitives.WriteDoubleLittleEndian(gear, t.GearDown ? 1 : 0);
                            await _sc.SetDataOnSimObjectAsync(DefGear, o.ObjectId, gear.ToArray());
                        }
                    }
                    catch (Exception ex) { Log.Write("Traffic update failed: " + ex.Message); return; }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static (double lat, double lon) Offset(double lat, double lon, double bearing, double nm)
    {
        var b = bearing * Math.PI / 180;
        return (lat + nm * Math.Cos(b) / 60.0, lon + nm * Math.Sin(b) / (60.0 * Math.Cos(lat * Math.PI / 180)));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _sc.AssignedObjectId -= OnAssigned;
        _sc.Data -= OnData;
        List<Obj> all;
        lock (_gate) { all = _objs.Values.ToList(); _objs.Clear(); }
        foreach (var o in all) if (o.ObjectId != 0) try { await _sc.AIRemoveObjectAsync(o.ObjectId, _nextReq++); } catch { }
    }
}
