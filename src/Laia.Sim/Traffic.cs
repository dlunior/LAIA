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
/// MSFS's own AI, and moves it on every simulator frame. Positions are replayed slightly in the past
/// and blended between the server's updates (like VATSIM clients do), so speed changes are smooth.
/// </summary>
internal sealed class TrafficRenderer : IAsyncDisposable
{
    private const uint DefPos = 10, DefGround = 11, DefGear = 12, DefWaypoints = 13;
    private const uint WpSpeedRequested = 0x04, WpComputeVerticalSpeed = 0x10, WpOnGround = 0x00100000;
    public string Motion { get; private set; } = "msfs";
    private const uint EvFreezeLatLon = 100, EvFreezeAlt = 101, EvFreezeAtt = 102, EvFrame = 120;
    // Light switches, in the order of the TrafficAircraft.Lights bits.
    private static readonly string[] LightEvents =
        { "NAV_LIGHTS_SET", "BEACON_LIGHTS_SET", "STROBES_SET", "LANDING_LIGHTS_SET", "TAXI_LIGHTS_SET", "LOGO_LIGHTS_SET", "WING_LIGHTS_SET" };
    private const uint EvLightBase = 110;
    private const double RenderDelay = 0.6; // seconds behind the server (server updates arrive 4x a second)
    private readonly SimConnectClient _sc;
    private readonly AigCatalog _catalog;
    private readonly object _gate = new();
    private readonly Dictionary<string, Obj> _objs = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _frame = new(0, 1);
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Queue<double> _offsets = new();
    private double _offset = double.NaN; // local clock minus server clock, seconds
    private uint _nextReq = 5000;
    private readonly Stats _stats = new();

    /// <summary>Motion report written to the log every 10 s while traffic is shown.</summary>
    private sealed class Stats
    {
        public int Frames, Updates, LateSamples;
        public double MaxFrameGap, OffsetAdjust, MaxSampleGap;
        public double Since;
    }

    private sealed class Obj
    {
        public required TrafficAircraft Latest;
        public readonly List<(double t, TrafficAircraft s)> Samples = new();
        public uint CreateReq, GroundReq, ObjectId;
        public bool Ready, Removed;
        public string Motion = "msfs";
        public double GroundAlt = double.NaN, CgFt = double.NaN, SmoothGround = double.NaN;
        public double LastLights = -100, LastGear = -100;
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
        await _sc.AddToDataDefinitionAsync(DefWaypoints, "AI WAYPOINT LIST", "number", SimDataType.Waypoint);
        await _sc.MapClientEventToSimEventAsync(EvFreezeLatLon, "FREEZE_LATITUDE_LONGITUDE_SET");
        await _sc.MapClientEventToSimEventAsync(EvFreezeAlt, "FREEZE_ALTITUDE_SET");
        await _sc.MapClientEventToSimEventAsync(EvFreezeAtt, "FREEZE_ATTITUDE_SET");
        for (var i = 0; i < LightEvents.Length; i++) await _sc.MapClientEventToSimEventAsync(EvLightBase + (uint)i, LightEvents[i]);
        _sc.AssignedObjectId += OnAssigned;
        _sc.Data += OnData;
        _sc.Frame += OnFrame;
        await _sc.SubscribeToSystemEventAsync(EvFrame, "Frame");
        _ = Task.Run(() => RenderLoop(_cts.Token));
    }

    private void OnFrame() { if (_frame.CurrentCount == 0) try { _frame.Release(); } catch (SemaphoreFullException) { } }

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Called with the server's current traffic list (about 4 times a second).</summary>
    public void Update(List<TrafficAircraft> list, double serverTimeSec, string motion)
    {
        if (motion != Motion)
        {
            Log.Write($"Traffic motion mode: {motion}");
            Motion = motion;
        }
        var toCreate = new List<Obj>();
        var toRemove = new List<Obj>();
        lock (_gate)
        {
            // Keep the smallest recent clock offset: it's the one with the least network delay.
            var off = Now - serverTimeSec;
            _offsets.Enqueue(off);
            while (_offsets.Count > 40) _offsets.Dequeue();
            // Ease clock corrections in (at most 2 ms per update) so aircraft never hop when the estimate changes.
            var target = _offsets.Min();
            if (double.IsNaN(_offset) || Math.Abs(target - _offset) > 0.5) _offset = target;
            else
            {
                var step = Math.Clamp(target - _offset, -0.002, 0.002);
                if (Math.Abs(step) > 0.0005) _stats.OffsetAdjust += Math.Abs(step);
                _offset += step;
            }
            _stats.Updates++;

            foreach (var t in list)
            {
                var ts = t.TimeMs > 0 ? t.TimeMs / 1000.0 : serverTimeSec;
                if (!_objs.TryGetValue(t.Id, out var o))
                {
                    o = new Obj { Latest = t, CreateReq = _nextReq++, GroundReq = _nextReq++, Motion = Motion };
                    _objs[t.Id] = o;
                    toCreate.Add(o);
                }
                o.Latest = t;
                if (o.Samples.Count > 0 && ts > o.Samples[^1].t) _stats.MaxSampleGap = Math.Max(_stats.MaxSampleGap, ts - o.Samples[^1].t);
                if (o.Samples.Count == 0 || ts > o.Samples[^1].t) o.Samples.Add((ts, t));
                if (o.Samples.Count > 12) o.Samples.RemoveAt(0);
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
        List<Obj> flown;
        lock (_gate) flown = _objs.Values.Where(o => o.Ready && !o.Removed && o.Motion == "msfs").ToList();
        foreach (var o in flown) _ = SendPathAsync(o);
    }

    /// <summary>
    /// "MSFS flies" mode: give MSFS's AI a short path ahead of where LAIA has the aircraft (2 and 6 seconds
    /// ahead at its speed). Refreshed 4 times a second, so MSFS keeps flying smoothly toward LAIA's track.
    /// </summary>
    private static byte[] BuildPath(TrafficAircraft t)
    {
        var buf = new byte[44 * 2];
        var i = 0;
        foreach (var ahead in new[] { 2.0, 6.0 })
        {
            var nm = Math.Max(t.GroundSpeed, t.OnGround ? 0 : 120) * ahead / 3600;
            if (t.OnGround && t.GroundSpeed < 1) nm = 0.002 * (i + 1); // holding: points just ahead, speed zero
            var b = t.Heading * Math.PI / 180;
            var lat = t.Lat + nm * Math.Cos(b) / 60.0;
            var lon = t.Lon + nm * Math.Sin(b) / (60.0 * Math.Cos(t.Lat * Math.PI / 180));
            var alt = t.AltFt + t.VerticalSpeed / 60 * ahead;
            var flags = WpSpeedRequested | (t.OnGround ? WpOnGround : WpComputeVerticalSpeed);
            var span = buf.AsSpan(i * 44);
            BinaryPrimitives.WriteDoubleLittleEndian(span, lat);
            BinaryPrimitives.WriteDoubleLittleEndian(span[8..], lon);
            BinaryPrimitives.WriteDoubleLittleEndian(span[16..], alt);
            BinaryPrimitives.WriteUInt32LittleEndian(span[24..], flags);
            BinaryPrimitives.WriteDoubleLittleEndian(span[28..], t.OnGround && t.GroundSpeed < 1 ? 0 : t.GroundSpeed);
            BinaryPrimitives.WriteDoubleLittleEndian(span[36..], 0);
            i++;
        }
        return buf;
    }

    private async Task SendPathAsync(Obj o)
    {
        var buf = BuildPath(o.Latest);
        try { await _sc.SetDataArrayOnSimObjectAsync(DefWaypoints, o.ObjectId, buf, 2, 44); }
        catch (Exception ex) { Log.Write("Traffic path failed: " + ex.Message); }
    }

    private async Task CreateAsync(Obj o)
    {
        var t = o.Latest;
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
        if (o.Motion == "msfs")
        {
            // MSFS's AI keeps control and flies the path LAIA sends.
            o.Ready = true;
            Log.Write($"Traffic: {o.Latest.Callsign} is object {objectId} (MSFS flies)");
            _ = SendPathAsync(o);
            return;
        }
        try
        {
            await _sc.AIReleaseControlAsync(objectId, _nextReq++);
            await _sc.TransmitClientEventAsync(objectId, EvFreezeLatLon, 1);
            await _sc.TransmitClientEventAsync(objectId, EvFreezeAlt, 1);
            await _sc.TransmitClientEventAsync(objectId, EvFreezeAtt, 1);
            await _sc.RequestDataOnSimObjectAsync(o.GroundReq, DefGround, objectId, SimPeriod.VisualFrame, flags: 1);
            o.Ready = true;
            Log.Write($"Traffic: {o.Latest.Callsign} is object {objectId}");
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
        Log.Write($"Traffic: removing {o.Latest.Callsign}");
        try { await _sc.AIRemoveObjectAsync(o.ObjectId, _nextReq++); } catch { }
    }

    /// <summary>Where the aircraft was at server time rt: blended between the two samples around it.</summary>
    private static (double lat, double lon, double alt, double agl, double hdg, double pitch, double bank, bool ground) Sample(Obj o, double rt)
    {
        var s = o.Samples;
        if (s.Count == 1 || rt <= s[0].t)
        {
            var a = s[0].s;
            return (a.Lat, a.Lon, a.AltFt, a.AglFt, a.Heading, a.Pitch, a.Bank, a.OnGround);
        }
        for (var i = 1; i < s.Count; i++)
        {
            if (rt > s[i].t) continue;
            var (t0, a) = s[i - 1];
            var (t1, b) = s[i];
            var f = (rt - t0) / Math.Max(1e-3, t1 - t0);
            double L(double x, double y) => x + (y - x) * f;
            var dh = ((b.Heading - a.Heading + 540) % 360) - 180;
            return (L(a.Lat, b.Lat), L(a.Lon, b.Lon), L(a.AltFt, b.AltFt), L(a.AglFt, b.AglFt), (a.Heading + dh * f + 360) % 360,
                L(a.Pitch, b.Pitch), L(a.Bank, b.Bank), a.OnGround && b.OnGround);
        }
        // Past the newest sample (a late update): carry on along its track for up to a second.
        var (tn, n) = s[^1];
        var dt = Math.Min(rt - tn, 1.0);
        var bR = n.Heading * Math.PI / 180;
        var nm = n.GroundSpeed * dt / 3600;
        var lat = n.Lat + nm * Math.Cos(bR) / 60.0;
        var lon = n.Lon + nm * Math.Sin(bR) / (60.0 * Math.Cos(n.Lat * Math.PI / 180));
        var climb = n.VerticalSpeed / 60 * dt;
        return (lat, lon, n.AltFt + climb, Math.Max(0, n.AglFt + climb), n.Heading, n.Pitch, n.Bank, n.OnGround);
    }

    private async Task RenderLoop(CancellationToken ct)
    {
        var buf = new byte[48];
        var gear = new byte[8];
        var lastFrame = Now;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Move once per simulator frame (falls back to ~60 Hz if frame events stop).
                await _frame.WaitAsync(TimeSpan.FromMilliseconds(16), ct);
                var now = Now;
                _stats.MaxFrameGap = Math.Max(_stats.MaxFrameGap, now - lastFrame);
                var frameDt = Math.Clamp(now - lastFrame, 0.001, 0.2);
                lastFrame = now;
                _stats.Frames++;
                if (now - _stats.Since >= 10)
                {
                    int count;
                    lock (_gate) count = _objs.Count;
                    if (count > 0)
                        Log.Write($"Traffic motion: {count} aircraft, {_stats.Frames / (now - _stats.Since):0} frames/s (longest gap {_stats.MaxFrameGap * 1000:0} ms), " +
                                  $"{_stats.Updates / (now - _stats.Since):0.0} updates/s (longest sample gap {_stats.MaxSampleGap * 1000:0} ms), " +
                                  $"{_stats.LateSamples} frames ran past the newest sample, clock eased {_stats.OffsetAdjust * 1000:0} ms");
                    _stats.Frames = 0; _stats.Updates = 0; _stats.LateSamples = 0; _stats.MaxFrameGap = 0; _stats.MaxSampleGap = 0; _stats.OffsetAdjust = 0;
                    _stats.Since = now;
                }
                if (double.IsNaN(_offset)) continue;
                var rt = now - _offset - RenderDelay; // server time we're showing
                List<Obj> objs;
                lock (_gate) objs = _objs.Values.Where(o => o.Ready && !o.Removed && o.Samples.Count > 0).ToList();
                foreach (var o in objs)
                {
                    if (o.Motion == "msfs")
                    {
                        var tl = o.Latest;
                        if (tl.Lights != o.SentLights || now - o.LastLights > 5)
                        {
                            o.SentLights = tl.Lights;
                            o.LastLights = now;
                            try
                            {
                                for (var i = 0; i < LightEvents.Length; i++)
                                    await _sc.TransmitClientEventAsync(o.ObjectId, EvLightBase + (uint)i, (uint)((tl.Lights >> i) & 1));
                            }
                            catch { }
                        }
                        continue;
                    }
                    (double lat, double lon, double alt, double agl, double hdg, double pitch, double bank, bool ground) p;
                    lock (_gate)
                    {
                        if (rt > o.Samples[^1].t) _stats.LateSamples++;
                        p = Sample(o, rt);
                    }

                    // Near the ground, height comes from the sim's own terrain under the aircraft, so the
                    // wheels meet the runway exactly (FAA runway elevation and MSFS terrain differ by a few feet).
                    double alt = p.alt;
                    if (!double.IsNaN(o.GroundAlt) && !double.IsNaN(o.CgFt))
                    {
                        o.SmoothGround = double.IsNaN(o.SmoothGround) ? o.GroundAlt : o.SmoothGround + (o.GroundAlt - o.SmoothGround) * Math.Min(1, 8 * frameDt);
                        var terrainAlt = o.SmoothGround + o.CgFt + (p.ground ? 0 : p.agl);
                        var w = Math.Clamp((p.agl - 1500) / 1000, 0, 1); // blend to the server's altitude above 1,500-2,500 ft
                        alt = terrainAlt * (1 - w) + p.alt * w;
                    }

                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(0), p.lat);
                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(8), p.lon);
                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(16), alt);
                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(24), -p.pitch); // MSFS: positive pitch is nose down
                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(32), p.bank);
                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(40), p.hdg);
                    var t = o.Latest;
                    try
                    {
                        await _sc.SetDataOnSimObjectAsync(DefPos, o.ObjectId, buf.ToArray());
                        // Lights: send when they change, and repeat every 5 s in case the model reset them.
                        if (t.Lights != o.SentLights || now - o.LastLights > 5)
                        {
                            o.SentLights = t.Lights;
                            o.LastLights = now;
                            for (var i = 0; i < LightEvents.Length; i++)
                                await _sc.TransmitClientEventAsync(o.ObjectId, EvLightBase + (uint)i, (uint)((t.Lights >> i) & 1));
                        }
                        if (now - o.LastGear > 1)
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

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _sc.AssignedObjectId -= OnAssigned;
        _sc.Data -= OnData;
        _sc.Frame -= OnFrame;
        List<Obj> all;
        lock (_gate) { all = _objs.Values.ToList(); _objs.Clear(); }
        foreach (var o in all) if (o.ObjectId != 0) try { await _sc.AIRemoveObjectAsync(o.ObjectId, _nextReq++); } catch { }
    }
}
