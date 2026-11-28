using System.Text.Json;
using Laia.Common.Airports;
using Laia.Server.TrafficCore;

namespace Laia.Server;

/// <summary>
/// Release 4b — the controller's techniques learned from the hubs (user, 28 Nov: "The goal isn't to make planes fly the
/// routes from the ads data just learn how real ATC handles sequencing and speed control"; "learn the techniques from
/// those busy airports and apply that to all airports"). From every real arrival in the training days:
/// - speeds: how fast each kind of aircraft is flown at each distance to go, unimpeded and when it has time to lose;
/// - the landing order: which arrivals real controllers let land first against when each would land on its own;
/// - the gap at the threshold behind each kind of leader when the queue is pressed.
/// Written as rows of numbers (train/atc_*.bin), learned on the graphics card (learning/train_atc.py → models/atc.json),
/// and then flown on the scoreboard against the rules: used at a hub only where it beats them there, and at every other
/// airport only when it beats them at most hubs.
/// </summary>
public sealed partial class LearningService
{
    private static AtcModel? _atc;
    private static Dictionary<string, Approval> _atcApproved = new(StringComparer.OrdinalIgnoreCase);
    private const string AtcFormat = "atc1";

    /// <summary>The learned controller for this airport, where it beat the rules on the scoreboard (its own, or most hubs' for an airport without one); null otherwise.</summary>
    public static AtcModel? AtcFor(string icao)
    {
        var m = _atc;
        if (m is null) return null;
        lock (_mgate)
        {
            if (_atcApproved.TryGetValue(icao, out var own) && own.Model == m.Id) return own.Ok ? m : null;
            return _atcApproved.TryGetValue("general", out var g) && g.Model == m.Id && g.Ok ? m : null;
        }
    }

    private void LoadAtc()
    {
        try
        {
            if (AtcModel.Load(Path.Combine(ModelDir, "atc.json")) is { } m)
            {
                _atc = m;
                var ap = Path.Combine(ModelDir, "atc-approved.json");
                if (File.Exists(ap) && JsonSerializer.Deserialize<Dictionary<string, Approval>>(File.ReadAllText(ap)) is { } d)
                    lock (_mgate) _atcApproved = new(d.Where(kv => kv.Value.Model == m.Id), StringComparer.OrdinalIgnoreCase);
                var use = _atcApproved.Where(kv => kv.Value.Ok).Select(kv => kv.Key).OrderBy(x => x).ToList();
                log.LogInformation("Learned ATC {id} loaded (trained {t}); used at: {list}", m.Id, m.Trained, use.Count > 0 ? string.Join(" ", use) : "nowhere yet — the scoreboard decides");
            }
        }
        catch (Exception ex) { log.LogInformation("Learned ATC not loaded ({msg})", ex.Message); }
    }

    /// <summary>
    /// The rows the controller's techniques are learned from (written again only when there are new days):
    /// atc_speed.bin — each real arrival every 90 s from 45 to 3 nm to go: [airport, held back, class, distance to go
    /// (nm along its real track), height above the field, ground speed, estimated indicated speed, seconds still to the
    /// threshold, arrivals to its runway within 30 minutes, seconds behind the one that landed before it];
    /// atc_order.bin — each arrival as it first came within 35 nm to go: [airport, held back, runway-day group, class,
    /// distance to go, height, ground speed, sin and cos of the side it comes from, nm off the final's line, time, its
    /// threshold time (s of the day), load];
    /// atc_gaps.bin — each two landing one after the other on a runway within 10 minutes: [airport, held back, leader's
    /// class, follower's class, gap (s), the follower's distance to go and seconds still to go at 35 nm, load].
    /// </summary>
    private async Task ExportAtc(List<string> hubs, CancellationToken ct)
    {
        var files = hubs.SelectMany(h => Directory.Exists(Path.Combine(DataDir, h)) ? Directory.GetFiles(Path.Combine(DataDir, h), "*.json.gz") : Array.Empty<string>()).ToList();
        var sig = $"{AtcFormat}:{files.Count}:{files.Sum(f => new FileInfo(f).Length)}";
        var metaPath = Path.Combine(TrainDir, "atc_meta.json");
        if (File.Exists(metaPath))
        {
            using var md = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath, ct));
            if (md.RootElement.TryGetProperty("signature", out var sg) && sg.GetString() == sig) return;
        }
        Status = "learned ATC: writing what real controllers did";
        Directory.CreateDirectory(TrainDir);
        var airportsIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Hubs.Length; i++) airportsIdx[Hubs[i]] = i + 1;
        long nSpeed = 0, nOrder = 0, nGap = 0, nArr = 0;
        var group = 0;
        var nm = Geo2.MPerNm;
        await using (var sf = File.Create(Path.Combine(TrainDir, "atc_speed.bin.tmp")))
        await using (var of = File.Create(Path.Combine(TrainDir, "atc_order.bin.tmp")))
        await using (var gf = File.Create(Path.Combine(TrainDir, "atc_gaps.bin.tmp")))
        {
            var sw = new BinaryWriter(sf); var ow = new BinaryWriter(of); var gw = new BinaryWriter(gf);
            void Row(BinaryWriter w, params double[] v) { foreach (var x in v) w.Write((float)x); }
            foreach (var icao in hubs)
            {
                if (airports.Data(icao) is not { } data || !Directory.Exists(Path.Combine(DataDir, icao))) continue;
                var apt = new AirportModel(data);
                var ai = airportsIdx.GetValueOrDefault(icao, 0);
                foreach (var f in Directory.GetFiles(Path.Combine(DataDir, icao), "*.json.gz"))
                {
                    ct.ThrowIfCancellationRequested();
                    var day = Path.GetFileName(f)[..^".json.gz".Length];
                    if (ReadDay(icao, day) is not { } dd || dd.Arrivals.Count == 0) continue;
                    var held = dd.HeldBack ? 1 : 0;
                    var day0 = Math.Floor(dd.Arrivals.Min(a => a.ThrT) / 86400) * 86400;
                    var snaps = new Dictionary<ArrRec, (double dtg, double tRem)>();
                    foreach (var byRwy in dd.Arrivals.GroupBy(a => a.Rwy))
                    {
                        var rwy = apt.Ends.FirstOrDefault(e => e.Ident == byRwy.Key);
                        if (rwy is null) continue;
                        group++;
                        var list = byRwy.OrderBy(a => a.ThrT).ToList();
                        for (var ix = 0; ix < list.Count; ix++)
                        {
                            var a = list[ix];
                            if (a.Pts.Count < 6) continue;
                            var cls = AtcModel.Class(Perf.For(a.Type));
                            var load = list.Count(o => o != a && Math.Abs(o.ThrT - a.ThrT) < 1800);
                            var gapLeader = ix > 0 ? a.ThrT - list[ix - 1].ThrT : 9999;
                            var xy = a.Pts.Select(q => apt.Map.ToXY(q[1], q[2])).ToList();
                            var alt = new double[a.Pts.Count];
                            for (var i = 0; i < alt.Length; i++) alt[i] = a.Pts[i][3] > -9000 ? a.Pts[i][3] : i > 0 ? alt[i - 1] : rwy.ElevationFt + 3000;
                            // Distance to go along its real track, from each point to the threshold.
                            var dtg = new double[xy.Count];
                            dtg[^1] = (rwy.Threshold - xy[^1]).Length / nm;
                            for (var i = xy.Count - 2; i >= 0; i--) dtg[i] = dtg[i + 1] + (xy[i + 1] - xy[i]).Length / nm;
                            var w = dd.Wind.Where(x => x[0] <= a.EntryT).OrderByDescending(x => x[0]).FirstOrDefault() ?? dd.Wind.FirstOrDefault();
                            nArr++;
                            var lastRow = double.NegativeInfinity;
                            var snapped = false;
                            for (var i = 1; i + 1 < xy.Count; i++)
                            {
                                var dt = a.Pts[i + 1][0] - a.Pts[i - 1][0];
                                if (dt < 5 || dt > 60) continue;
                                var gs = (xy[i + 1] - xy[i - 1]).Length / dt / 0.514444;
                                if (gs < 60 || gs > 600) continue;
                                var agl = alt[i] - rwy.ElevationFt;
                                var tRem = a.ThrT - a.Pts[i][0];
                                if (!snapped && dtg[i] <= 35)
                                {
                                    snapped = true;
                                    var side = Geo2.Diff(rwy.Heading + 180, (xy[i] - rwy.Threshold).Bearing) * Math.PI / 180;
                                    Row(ow, ai, held, group, cls, dtg[i], agl, gs, Math.Sin(side), Math.Cos(side), rwy.Offset(xy[i]) / nm, a.Pts[i][0] - day0, a.ThrT - day0, load);
                                    nOrder++;
                                    snaps[a] = (dtg[i], tRem);
                                }
                                if (dtg[i] > 45 || dtg[i] < 3 || a.Pts[i][0] - lastRow < 85) continue;
                                lastRow = a.Pts[i][0];
                                // Indicated speed from the ground speed: the wind along its track (the surface wind,
                                // stronger with height) and true airspeed about 2 % above indicated per 1,000 ft.
                                var hw = w is null ? 0 : w[2] * Math.Cos((w[1] - a.Pts[i][4]) * Math.PI / 180) * (1 + 0.6 * Math.Clamp(agl, 0, 10000) / 10000);
                                var ias = (gs + hw) / (1 + 0.02 * Math.Max(0, alt[i]) / 1000);
                                Row(sw, ai, held, cls, dtg[i], agl, gs, ias, tRem, load, gapLeader);
                                nSpeed++;
                            }
                        }
                        for (var ix = 1; ix < list.Count; ix++)
                        {
                            var l = list[ix - 1]; var fo = list[ix];
                            var gap = fo.ThrT - l.ThrT;
                            if (gap > 600 || gap < 20) continue;
                            var (sd, st) = snaps.TryGetValue(fo, out var sn) ? sn : (-1, -1);
                            Row(gw, ai, held, AtcModel.Class(Perf.For(l.Type)), AtcModel.Class(Perf.For(fo.Type)), gap, sd, st, list.Count(o => o != fo && Math.Abs(o.ThrT - fo.ThrT) < 1800));
                            nGap++;
                        }
                    }
                }
            }
            sw.Flush(); ow.Flush(); gw.Flush();
        }
        foreach (var n in new[] { "atc_speed", "atc_order", "atc_gaps" })
            File.Move(Path.Combine(TrainDir, n + ".bin.tmp"), Path.Combine(TrainDir, n + ".bin"), true);
        var meta = new Dictionary<string, object>
        {
            ["format"] = AtcFormat, ["signature"] = sig, ["airports"] = airportsIdx, ["arrivals"] = nArr,
            ["speed_rows"] = nSpeed, ["order_rows"] = nOrder, ["gap_rows"] = nGap, ["written"] = DateTime.UtcNow.ToString("u"),
            ["nominal_from"] = AtcModel.NomFrom, ["nominal_to"] = AtcModel.NomTo,
        };
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta), ct);
        log.LogInformation("Learned ATC: {n} real arrivals at {h} hubs written for training ({s} speed samples, {o} landing-order snapshots, {g} threshold gaps)", nArr, hubs.Count, nSpeed, nOrder, nGap);
    }

    /// <summary>Learns the controller's techniques on the graphics card when the rows are newer than the last model. The
    /// new model replaces the old; the scoreboard then decides where it's used.</summary>
    private async Task TrainAtcIfNeeded(CancellationToken ct)
    {
        var meta = Path.Combine(TrainDir, "atc_meta.json");
        var outPath = Path.Combine(ModelDir, "atc.json");
        if (!File.Exists(meta)) return;
        if (File.Exists(outPath) && File.GetLastWriteTimeUtc(outPath) > File.GetLastWriteTimeUtc(meta)) return;
        var script = Path.Combine(Paths.InstallDir, "learning", "train_atc.py");
        if (!File.Exists(Paths.Python) || !File.Exists(script)) { log.LogInformation("Learned ATC: Python or the training script isn't installed ({py})", Paths.Python); return; }
        if (!await EnsureTorch(ct)) return;
        Status = "learned ATC: training on the graphics card";
        Directory.CreateDirectory(ModelDir);
        var tmpOut = Path.Combine(ModelDir, "atc-new.json");
        log.LogInformation("Learned ATC: training started");
        var (code, last) = await RunPython($"-u \"{script}\" --data \"{TrainDir}\" --out \"{tmpOut}\" --pylib \"{PyLib}\"",
            line => { if (!line.StartsWith("epoch")) log.LogInformation("Learned ATC: {line}", line); },
            TimeSpan.FromHours(4), ct);
        if (code != 0 || AtcModel.Load(tmpOut) is not { } m) { log.LogInformation("Learned ATC: training didn't finish (code {code}: {last})", code, last); return; }
        File.Move(tmpOut, outPath, true);
        _atc = m;
        lock (_mgate) _atcApproved.Clear();
        log.LogInformation("Learned ATC {id} ready; the scoreboard decides where it's used", m.Id);
    }

    /// <summary>
    /// The learned controller is used at a hub only where, on the same held-back hours, it puts the arrivals in the real
    /// order no worse (2 points), lands them no further from their real times (5 % or 5 s), has no more go-arounds and
    /// break-offs (half a one per 10 landings) — and is better at one of: the order (2 points), the landing times (5 %),
    /// the speeds at 30/20/10 nm (3 kt). At every other airport, once it's been used at 60 % of the hubs scored (5 or more).
    /// </summary>
    private void DecideAtc(string icao, List<ScoreRec> rules, List<ScoreRec> atc, AtcModel model, string ver)
    {
        if (rules.Count == 0 || atc.Count == 0) return;
        double M(List<ScoreRec> l, Func<ScoreRec, double> f) => Med(l.Select(f));
        double Ev(List<ScoreRec> l) => M(l, r => r.Per10.GetValueOrDefault("go-arounds") + r.Per10.GetValueOrDefault("break-offs"));
        var oR = M(rules, r => r.Order); var oA = M(atc, r => r.Order);
        var tR = M(rules, r => r.TimeErr); var tA = M(atc, r => r.TimeErr);
        var sR = M(rules, r => r.SpeedErr); var sA = M(atc, r => r.SpeedErr);
        var why = new List<string>();
        if (!double.IsNaN(oR) && !double.IsNaN(oA) && oA < oR - 0.02) why.Add($"landing order {100 * oA:0}% against {100 * oR:0}%");
        if (tA > tR * 1.05 + 5) why.Add($"landing times ±{tA:0} s against ±{tR:0}");
        if (Ev(atc) > Ev(rules) + 0.5) why.Add($"go-arounds and break-offs {Ev(atc):0.0} per 10 against {Ev(rules):0.0}");
        var better = (oA >= oR + 0.02) || tA < tR * 0.95 || (!double.IsNaN(sA) && !double.IsNaN(sR) && sA < sR - 3);
        if (why.Count == 0 && !better) why.Add("no better than the rules");
        var ok = why.Count == 0;
        var text = $"[{ver}] order {100 * oA:0}% (rules {100 * oR:0}%), landing times ±{tA:0} s (±{tR:0}), speeds ±{sA:0} kt (±{sR:0}), go-arounds and break-offs {Ev(atc):0.0} per 10 ({Ev(rules):0.0})"
                   + (ok ? "" : " — " + string.Join("; ", why));
        lock (_mgate)
        {
            _atcApproved[icao] = new Approval { Ok = ok, Why = text, Model = model.Id };
            var hubs = _atcApproved.Where(kv => kv.Key != "general" && kv.Value.Model == model.Id).ToList();
            var share = hubs.Count == 0 ? 0 : (double)hubs.Count(kv => kv.Value.Ok) / hubs.Count;
            _atcApproved["general"] = new Approval { Ok = hubs.Count >= 5 && share >= 0.6, Why = $"better than the rules at {hubs.Count(kv => kv.Value.Ok)} of {hubs.Count} hubs", Model = model.Id };
        }
        log.LogInformation("Learned ATC at {icao}: {verdict} — {why}", icao, ok ? "used (better than the rules)" : "not used", text);
        try
        {
            Directory.CreateDirectory(ModelDir);
            string json;
            lock (_mgate) json = JsonSerializer.Serialize(_atcApproved, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(ModelDir, "atc-approved.json"), json);
        }
        catch { }
    }
}
