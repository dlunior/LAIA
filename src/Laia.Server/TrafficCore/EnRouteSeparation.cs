namespace Laia.Server.TrafficCore;

/// <summary>
/// Separation for the traffic between airports — near the user (every 2 s) and everywhere else (every 10 s, region by
/// region). Each flight is projected 5 minutes ahead (every 20 s) against the others and the obstacles (the user, the
/// airports' aircraft); where two would come within 5 nm (6 nm for the user) and 1,000 ft, it's solved the way a centre
/// controller would, weighing the options case by case (user, 25 Nov: "every scenario is a snowflake"):
/// <list type="bullet">
/// <item>one catching another up at its level, going the same way: in trail by speed — the one ahead faster first (up to
/// 4 %, about Mach .02–.03, unless traffic further ahead boxes it in), then the one behind slower (up to 7 %), or both a
/// little; the user ahead is asked first in the notices (90 s to speed up) before the one behind is slowed;</item>
/// <item>when speed can't: the next level of its own direction 2,000 ft away, the side chosen once and kept, only if
/// nobody is on it within 10 nm; with no level free, a vector — 25° off its route until 6 nm aside, then back;</item>
/// <item>a climbing or descending flight levels off on a level of its own direction 1,000 ft clear, on the side it's
/// on now (a climb stops below, a descent above) and kept — never through the other's level;</item>
/// <item>with an obstacle (the user, an airport's aircraft) the whole-sky flight is always the one that changes.</item>
/// </list>
/// Speed changes are flown as minutes ahead of or behind its schedule (NetLeg.SepLagMin), vectors as an offset off its
/// route (NetLeg.Vec…), so everything that places the flight (the map, the sim, the hand-over) sees the same.
/// </summary>
public sealed class EnRouteSeparation
{
    public sealed record Obstacle(string Name, double Lat, double Lon, double Track, double GsKt, double AltFt, double Vs, double SepNm);
    public sealed record Event(NetLeg Leg, string Text, string Who);

    private sealed class State
    {
        public double Shown, Rate, At;
        public double Cap = double.PositiveInfinity, Floor = double.NegativeInfinity, Lvl;
        public string? Why;
        public int? Side; public bool? SideUp;   // kept once chosen: above (+1) or below (−1) the other; up or down for a level change
        public double AltAt(double clock) => Shown + Rate * (clock - At) * 1;   // clock in minutes, rate in ft/min
    }

    private readonly Dictionary<string, State> _st = new();
    public int Limits { get; private set; }

    /// <summary>The flight's altitude as drawn: its schedule's, unless separation changed it.</summary>
    public double Alt(NetLeg l, double clock, double baseAlt) => _st.TryGetValue(l.Id, out var s) ? s.AltAt(clock) : baseAlt;
    /// <summary>Its vertical speed as drawn (ft/min), when separation is flying it; null otherwise.</summary>
    public double? Rate(NetLeg l) => _st.TryGetValue(l.Id, out var s) ? s.Rate : null;
    /// <summary>Why it's limited (the other aircraft), or null.</summary>
    public string? Why(NetLeg l) => _st.TryGetValue(l.Id, out var s) ? s.Why : null;

    /// <summary>Takes over another instance's state for a flight (moving between the near-user set and the rest of the
    /// sky), so its altitude carries on rather than jumping back to its schedule's.</summary>
    public void Adopt(NetLeg l, EnRouteSeparation from)
    {
        if (!_spdAt.ContainsKey(l.Id) && from._spdAt.Remove(l.Id, out var at)) _spdAt[l.Id] = at;
        if (_st.ContainsKey(l.Id) || !from._st.TryGetValue(l.Id, out var s)) return;
        _st[l.Id] = new State { Shown = s.Shown, Rate = s.Rate, At = s.At, Cap = s.Cap, Floor = s.Floor, Lvl = s.Lvl, Why = s.Why, Side = s.Side, SideUp = s.SideUp };
        from._st.Remove(l.Id);
    }

    public bool Has(NetLeg l) => _st.ContainsKey(l.Id);

    /// <summary>Forgets every flight not in this set (for an instance updated region by region).</summary>
    public void Prune(HashSet<string> live)
    {
        foreach (var k in _st.Keys.Where(k => !live.Contains(k)).ToList()) _st.Remove(k);
        foreach (var k in _spdAt.Keys.Where(k => !live.Contains(k)).ToList()) _spdAt.Remove(k);
    }

    /// <summary>A notice for the user (an aircraft catching them up at their level), or null.</summary>
    public (string text, bool clear)? UserNotice { get; private set; }
    private (string leg, string other, double askKt, double gsAt, double at, double clearUntil)? _ask;
    private readonly Dictionary<string, double> _spdAt = new();
    private double _clearUntil;

    /// <summary>The next level of a flight's own direction at or beyond an altitude (odd thousands east, even west).</summary>
    private static double DirLevel(NetLeg l, double alt, bool up)
    {
        var east = (int)Math.Round(NetworkModel.CruiseFt(l) / 1000) % 2 == 1;
        var k = (int)(up ? Math.Ceiling(alt / 1000 - 0.01) : Math.Floor(alt / 1000 + 0.01));
        while ((k % 2 == 1) != east) k += up ? 1 : -1;
        return k * 1000;
    }

    public List<Event> Update(double clock, double lat0, double lon0, IReadOnlyList<NetLeg> legs, IReadOnlyList<Obstacle> obstacles, bool prune = true)
    {
        var events = new List<Event>();
        var nm = Geo2.MPerNm;
        var live = legs.Select(l => l.Id).ToHashSet();
        if (prune) foreach (var k in _st.Keys.Where(k => !live.Contains(k)).ToList()) _st.Remove(k);
        // Speed changes given last time: minutes gained or lost against the schedule since then.
        foreach (var l in legs)
        {
            if (!_spdAt.ContainsKey(l.Id) && !_st.ContainsKey(l.Id)) l.SpdKt = 0;   // new to this set: no speed change carried in
            var last = _spdAt.TryGetValue(l.Id, out var la) ? la : clock;
            _spdAt[l.Id] = clock;
            var dt = clock - last;
            if (l.SpdKt != 0 && dt > 0 && dt < 5 && NetworkModel.At(l, clock) is { GsKt: > 60 } q)
                l.SepLagMin += -l.SpdKt / q.GsKt * dt;
        }
        if (prune) foreach (var k in _spdAt.Keys.Where(k => !live.Contains(k)).ToList()) _spdAt.Remove(k);
        var map = new LocalMap(lat0, lon0);
        const int N = 16; const double Step = 20;
        var n = legs.Count;
        var pos = new V2[n, N]; var alt = new double[n, N]; var eff = new double[n, N];
        var ok = new bool[n]; var level = new bool[n]; var gs = new double[n]; var baseRate = new double[n]; var hdg = new double[n];
        for (var i = 0; i < n; i++)
        {
            var l = legs[i];
            if (NetworkModel.At(l, clock) is not { } p0 || p0.GsKt < 60) continue;
            ok[i] = true; gs[i] = p0.GsKt; hdg[i] = p0.Heading;
            baseRate[i] = NetworkModel.At(l, clock + 0.25) is { } q ? (q.AltFt - p0.AltFt) * 4 : 0;
            level[i] = Math.Abs(baseRate[i]) < 300;
            _st.TryGetValue(l.Id, out var st);
            var along = V2.FromBearing(p0.Heading, 1);
            for (var k = 0; k < N; k++)
            {
                var p = k == 0 ? p0 : NetworkModel.At(l, clock + k * Step / 60) ?? p0;
                // With the speed change it's flying now (not yet in its schedule's position ahead).
                pos[i, k] = map.ToXY(p.Lat, p.Lon) + along * (l.SpdKt * Geo2.MpsPerKt * k * Step); alt[i, k] = p.AltFt;
                var a = p.AltFt;
                if (st is not null) a = Math.Min(Math.Max(Math.Min(a, st.Cap), st.Floor), st.Cap) + st.Lvl;
                eff[i, k] = a;     // what the others see of it: with the limits it was given last time
            }
        }
        var obs = obstacles.Select(o => (o.Name, p0: map.ToXY(o.Lat, o.Lon), v: V2.FromBearing(o.Track, o.GsKt * Geo2.MpsPerKt), o.AltFt, vs: Math.Clamp(o.Vs, -4000, 4000), o.SepNm, o.Track, o.GsKt)).ToList();
        var newSpd = new double[n];
        var spdWhy = new string?[n];
        var resolved = new HashSet<(int, int)>();
        var horizonH = N * Step / 3600.0;

        // Another flight at about its level ahead of it within 20 nm and no faster than it could go: it can't speed up
        // (user, 25 Nov: "traffic even farther ahead when the preceding aircraft can't speed up").
        bool BoxedIn(int L)
        {
            var dir = V2.FromBearing(hdg[L], 1);
            for (var m = 0; m < n; m++)
            {
                if (m == L || !ok[m] || Math.Abs(Geo2.Diff(hdg[m], hdg[L])) > 60 || Math.Abs(eff[m, 0] - eff[L, 0]) >= 1000) continue;
                var rel = pos[m, 0] - pos[L, 0];
                var ahead = V2.Dot(rel, dir) / nm;
                if (ahead > 0 && ahead < 20 && Math.Abs(V2.Cross(dir, rel)) / nm < 5 && gs[m] + legs[m].SpdKt < gs[L] + 0.04 * gs[L]) return true;
            }
            foreach (var o in obs)
            {
                var rel = o.p0 - pos[L, 0];
                var ahead = V2.Dot(rel, dir) / nm;
                if (ahead > 0 && ahead < 20 && Math.Abs(V2.Cross(dir, rel)) / nm < 5 && Math.Abs(o.AltFt - eff[L, 0]) < 1000 && o.GsKt < gs[L] * 1.04) return true;
            }
            return false;
        }
        // The speeds that keep a follower in trail: how much the closing speed must come down so it stays 5 nm or more
        // behind over the next five minutes — the one ahead faster first (up to about Mach .02–.03, 4 %), then the one
        // behind slower (up to 7 %), or both a little. False when speed can't do it (already too close, closing too fast).
        bool SpeedFix(double closureKt, double dNm, double maxUp, double maxDown, out double up, out double down)
        {
            up = down = 0;
            if (dNm < 5.5) return false;
            var need = closureKt - (dNm - 5.5) / horizonH;
            if (need <= 0) return false;
            up = Math.Min(need, maxUp);
            down = need - up;
            if (down > maxDown) return false;
            up = Math.Ceiling(up / 5) * 5; down = Math.Ceiling(down / 5) * 5;
            return true;
        }

        // 1. Two whole-sky flights level at the same level going the same way, one catching the other up.
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                if (!ok[i] || !ok[j] || !level[i] || !level[j] || Math.Abs(Geo2.Diff(hdg[i], hdg[j])) > 60) continue;
                if ((pos[i, 0] - pos[j, 0]).Length > (gs[i] + gs[j]) * Geo2.MpsPerKt * N * Step + 5 * nm) continue;
                var conflict = false;
                for (var k = 0; k < N && !conflict; k++)
                    conflict = (pos[i, k] - pos[j, k]).Length < 5 * nm && Math.Abs(eff[i, k] - eff[j, k]) < 1000;
                if (!conflict) continue;
                var dir = V2.FromBearing(hdg[i], 1);
                var iBehind = V2.Dot(pos[j, 0] - pos[i, 0], dir) > 0;
                int F = iBehind ? i : j, L = iBehind ? j : i;
                var d0 = (pos[L, 0] - pos[F, 0]).Length / nm;
                var closure = gs[F] + legs[F].SpdKt - (gs[L] + legs[L].SpdKt);
                var maxUp = newSpd[L] < 0 || BoxedIn(L) ? 0 : Math.Max(0, 0.04 * gs[L] - Math.Max(0, newSpd[L]));
                var maxDown = newSpd[F] > 0 ? 0 : Math.Max(0, 0.07 * gs[F] + Math.Min(0, newSpd[F]));
                if (SpeedFix(closure + legs[F].SpdKt * 0 - 0, d0, maxUp, maxDown, out var up, out var down))
                {
                    // Relative to what each flies by its schedule (the change given last time is folded in).
                    var upKt = Math.Max(newSpd[L], legs[L].SpdKt + up);
                    var downKt = Math.Min(newSpd[F], legs[F].SpdKt - down);
                    if (upKt != 0) { newSpd[L] = upKt; spdWhy[L] ??= $"{legs[F].Callsign} catching up behind, {d0:0} nm"; }
                    if (downKt != 0) { newSpd[F] = downKt; spdWhy[F] ??= $"in trail behind {legs[L].Callsign}, {d0:0} nm"; }
                    resolved.Add((Math.Min(i, j), Math.Max(i, j)));
                }
            }
        for (var i = 0; i < n; i++)
        {
            if (!ok[i]) continue;
            var l0 = legs[i];
            double cap = double.PositiveInfinity, floor = double.NegativeInfinity, lvl = 0; string? why = null;
            _st.TryGetValue(l0.Id, out var st0);
            var vectorFor = (string?)null; var vectorSign = 0;
            foreach (var o in obs)
            {
                if ((pos[i, 0] - o.p0).Length > (gs[i] * Geo2.MpsPerKt + o.v.Length) * N * Step + o.SepNm * nm) continue;
                for (var k = 0; k < N; k++)
                {
                    var t = k * Step;
                    var oAlt = o.AltFt + o.vs * Math.Min(t, 90) / 60;
                    // 1,000 ft apart is separation (RVSM, each on its side's levels); the other's altitude as read is
                    // allowed 200 ft either way of its level (user, 16 Nov: "1000ft of separation is okay in RVSM airspace
                    // as long as planes are following east-west altitude rules" — SWA3049 at FL370 was moved up to FL380
                    // for the user at FL360, whose altitude read a few feet above 36,000).
                    if ((pos[i, k] - (o.p0 + o.v * t)).Length >= o.SepNm * nm || Math.Abs(alt[i, k] - oAlt) >= 800) continue;
                    var isUser = o.Name.EndsWith("(you)");
                    var sameWay = Math.Abs(Geo2.Diff(hdg[i], o.Track)) <= 60 && o.GsKt > 60;
                    if (level[i] && Math.Abs(o.AltFt - alt[i, 0]) < 500 && sameWay)
                    {
                        // At its level going the same way: in trail by speed if it can be (user, 25 Nov: speed up the one
                        // ahead first — "that's usually the first thing they'll do"; the one behind slower; then a level,
                        // then a vector). The other aircraft (the user, an airport's) is never the one changed.
                        var dir = V2.FromBearing(hdg[i], 1);
                        var oAhead = V2.Dot(o.p0 - pos[i, 0], dir) > 0;
                        var d0 = (o.p0 - pos[i, 0]).Length / nm;
                        var closure = oAhead ? gs[i] + l0.SpdKt - o.GsKt : o.GsKt - (gs[i] + l0.SpdKt);
                        // The user ahead, this one catching them up: ask the user to speed up first (user, 25 Nov: "use our
                        // notification area; if LAIA doesn't see the speed increase, go down the line on other methods"),
                        // and wait 90 s — unless it would be within 5 nm in under 3 minutes.
                        if (isUser && oAhead && t > 180)
                        {
                            var askKt = Math.Max(10, Math.Ceiling((closure - (d0 - 5.5) / horizonH) / 5) * 5);
                            if (_ask is not { } a0 || a0.leg != l0.Id) _ask = (l0.Id, o.Name, askKt, o.GsKt, clock, 0);
                            var waited = (clock - _ask!.Value.at) * 60;
                            if (o.GsKt >= _ask.Value.gsAt + _ask.Value.askKt / 2) break;   // the user is speeding up
                            if (waited < 90) break;
                        }
                        // Then in trail by speed (user, 25 Nov: the one ahead faster first — "that's usually the first thing
                        // they'll do" — then the one behind slower). The other aircraft (the user, an airport's) is never the
                        // one changed.
                        var maxUp = oAhead || newSpd[i] < 0 || BoxedIn(i) ? 0 : 0.04 * gs[i];
                        var maxDown = oAhead && newSpd[i] <= 0 ? 0.07 * gs[i] : 0;
                        if (SpeedFix(closure, d0, maxUp, maxDown, out var up, out var down))
                        {
                            if (up > 0) { newSpd[i] = Math.Max(newSpd[i], l0.SpdKt + up); spdWhy[i] ??= $"{o.Name} catching up behind, {d0:0} nm"; }
                            if (down > 0) { newSpd[i] = Math.Min(newSpd[i], l0.SpdKt - down); spdWhy[i] ??= $"in trail behind {o.Name}, {d0:0} nm"; }
                            break;
                        }
                        // Speed can't: a level of its own direction 2,000 ft away, chosen once and kept; or a vector.
                        var upSide = st0?.SideUp ?? (alt[i, 0] < 39000);
                        var target = DirLevel(l0, alt[i, 0] + (upSide ? 1500 : -1500), upSide);
                        if (LevelFree(i, target)) { var w = target - alt[i, 0]; if (lvl == 0 || Math.Abs(w) < Math.Abs(lvl)) { lvl = w; why = o.Name; } }
                        else
                        {
                            var other = DirLevel(l0, alt[i, 0] + (upSide ? -1500 : 1500), !upSide);
                            if (LevelFree(i, other)) { var w = other - alt[i, 0]; if (lvl == 0 || Math.Abs(w) < Math.Abs(lvl)) { lvl = w; why = o.Name; } }
                            else { vectorFor = o.Name; vectorSign = V2.Cross(dir, o.p0 - pos[i, 0]) > 0 ? -1 : 1; }
                        }
                        break;
                    }
                    // Climbing or descending into it, or crossing at its level: kept 1,000 ft clear on the side it's on now —
                    // a climb stops below, a descent above — on a level of its own direction, and the side kept (user,
                    // 24 Nov: DAL8948 was told 37,000 above the user, then 35,000 below, as the user's altitude read a few feet
                    // either way of 36,000 — straight through the user's level twice).
                    var aboveNow = st0?.Side is { } sd ? sd > 0
                        : baseRate[i] > 300 ? alt[i, 0] > o.AltFt + 300
                        : baseRate[i] < -300 ? alt[i, 0] > o.AltFt - 300
                        : alt[i, 0] >= o.AltFt;
                    if (aboveNow) { var fl = DirLevel(l0, Math.Max(oAlt, o.AltFt) + 1000, true); if (fl > floor) { floor = fl; why = o.Name; } }
                    else { var fl = DirLevel(l0, Math.Min(oAlt, o.AltFt) - 1000, false); if (fl < cap) { cap = fl; why = o.Name; } }
                    break;
                }
            }
            for (var j = 0; j < n; j++)
            {
                if (j == i || !ok[j]) continue;
                if (resolved.Contains((Math.Min(i, j), Math.Max(i, j)))) continue;
                if ((pos[i, 0] - pos[j, 0]).Length > (gs[i] + gs[j]) * Geo2.MpsPerKt * N * Step + 5 * nm) continue;
                var iMoves = level[i] != level[j] ? !level[i] : string.CompareOrdinal(legs[i].Id, legs[j].Id) > 0;
                if (!iMoves) continue;
                for (var k = 0; k < N; k++)
                {
                    if ((pos[i, k] - pos[j, k]).Length >= 5 * nm || Math.Abs(alt[i, k] - eff[j, k]) >= 1000) continue;
                    var who = legs[j].Callsign;
                    if (level[i] && level[j])
                    {
                        var upSide = st0?.SideUp ?? (alt[i, 0] < 39000);
                        var target = DirLevel(l0, eff[j, k] + (upSide ? 1500 : -1500), upSide);
                        if (!LevelFree(i, target)) target = DirLevel(l0, eff[j, k] + (upSide ? -1500 : 1500), !upSide);
                        if (!LevelFree(i, target))
                        {
                            var dir = V2.FromBearing(hdg[i], 1);
                            vectorFor = who; vectorSign = V2.Cross(dir, pos[j, 0] - pos[i, 0]) > 0 ? -1 : 1;
                        }
                        else
                        {
                            var want = target - alt[i, k];
                            if (lvl == 0 || Math.Abs(want) < Math.Abs(lvl)) { lvl = want; why = who; }
                        }
                    }
                    else
                    {
                        var aboveNow = st0?.Side is { } sd ? sd > 0
                            : baseRate[i] > 300 ? alt[i, 0] > eff[j, 0] + 300
                            : baseRate[i] < -300 ? alt[i, 0] > eff[j, 0] - 300
                            : alt[i, 0] >= eff[j, 0];
                        if (aboveNow) { var fl = DirLevel(l0, eff[j, k] + 1000, true); if (fl > floor) { floor = fl; why = who; } }
                        else { var fl = DirLevel(l0, eff[j, k] - 1000, false); if (fl < cap) { cap = fl; why = who; } }
                    }
                    break;
                }
            }
            // A vector (user, 25 Nov: "vectors for spacing, usually about 30 degrees then back on course"; sized to need):
            // 20° when there's time, 30° when it's close; out for as long as it takes to open 6 nm, then back.
            if (vectorFor is not null && (double.IsNaN(l0.VecStart) || ((clock - l0.VecStart) % 10080 + 10080) % 10080 > 2 * l0.VecOutMin))
            {
                var ang = 25.0;
                var outMin = Math.Clamp(6 / (gs[i] / 60 * Math.Sin(ang * Math.PI / 180)), 2, 8);
                l0.VecStart = clock; l0.VecOutMin = outMin; l0.VecAngle = vectorSign * ang;
                Limits++;
                events.Add(new Event(l0, $"turns {ang:0}° {(vectorSign > 0 ? "right" : "left")} for {outMin:0.#} min, then back on its route (no level free)", vectorFor));
            }
            var leg = legs[i];
            var baseNow = alt[i, 0];
            var limited = !double.IsInfinity(cap) || !double.IsInfinity(floor) || lvl != 0;
            var held = Math.Max(Math.Min(baseNow, cap), floor);
            if (floor > cap) held = cap;
            var target2 = held + lvl;
            var heldAtLimit = Math.Abs(held - baseNow) > 1;
            if (!_st.TryGetValue(leg.Id, out var s))
            {
                if (!limited) continue;
                s = new State { Shown = baseNow, Rate = baseRate[i], At = clock };
                _st[leg.Id] = s;
            }
            var shown = s.AltAt(clock);
            var maxR = Math.Max(2000, Math.Abs(baseRate[i]) + 500);
            // Toward the target within the next 2 s (×30 per minute), following its own climb or descent when not held.
            var rate = Math.Clamp((target2 - shown) * 30 + (heldAtLimit ? 0 : baseRate[i]), -maxR, maxR);
            if (limited && s.Why is null && why is not null)
            {
                Limits++;
                var what = lvl != 0 ? $"to {target2:N0} ft (level change)" : held < baseNow ? $"levels at {target2:N0} ft, below" : $"levels at {target2:N0} ft, above";
                events.Add(new Event(leg, what, why));
            }
            if (limited) { s.Side ??= floor > double.NegativeInfinity ? 1 : cap < double.PositiveInfinity ? -1 : null; if (lvl != 0) s.SideUp ??= lvl > 0; }
            s.Shown = shown; s.Rate = rate; s.At = clock; s.Cap = cap; s.Floor = floor; s.Lvl = lvl; s.Why = limited ? why : null;
            if (!limited) { s.Side = null; s.SideUp = null; }
            if (!limited && Math.Abs(shown - baseNow) < 20) _st.Remove(leg.Id);
        }
        // The speeds for the next interval; logged when one starts or changes by 10 kt or more.
        for (var i = 0; i < n; i++)
        {
            if (!ok[i]) continue;
            var l = legs[i];
            if (Math.Abs(newSpd[i] - l.SpdKt) >= 10 || (newSpd[i] != 0 && l.SpdKt == 0))
            {
                if (newSpd[i] != 0) { Limits++; events.Add(new Event(l, $"{(newSpd[i] > 0 ? "speeds up" : "slows")} {Math.Abs(newSpd[i]):0} kt (about Mach {(newSpd[i] > 0 ? "+" : "−")}{Math.Abs(newSpd[i]) / 575:.00}) — {spdWhy[i]}", spdWhy[i] ?? "")); }
                else events.Add(new Event(l, "back to its normal speed", ""));
            }
            l.SpdKt = newSpd[i];
        }
        // The notice for the user, while asked and still needed.
        if (_ask is { } ak)
        {
            var li = legs.Select((x, k) => (x, k)).FirstOrDefault(x => x.x.Id == ak.leg);
            var u = obs.FirstOrDefault(o => o.Name == ak.other);
            var still = li.x is not null && u.Name is not null && ok[li.k] && (u.p0 - pos[li.k, 0]).Length < 30 * nm && Math.Abs(u.AltFt - Alt(li.x, clock, alt[li.k, 0])) < 800;
            if (!still) { UserNotice = li.x is not null ? ($"{li.x.Callsign} stays clear behind you now", true) : null; _ask = null; _clearUntil = clock + 0.2; }
            else
            {
                var behindNm = (u.p0 - pos[li.k, 0]).Length / nm;
                var mach = ak.askKt / 575;
                if (u.GsKt >= ak.gsAt + ak.askKt / 2) UserNotice = ($"{li.x.Callsign} stays in trail behind you", true);
                else UserNotice = ($"{li.x.Callsign} ({li.x.Type}) is catching you up from behind at your level, {behindNm:0} nm back — about {ak.askKt:0} kt faster (Mach +{mach:.00}) keeps it in trail{((clock - ak.at) * 60 >= 90 ? "; it's being slowed or moved instead" : "; otherwise it'll be slowed or moved")}", false);
            }
        }
        else if (UserNotice is { clear: true } && clock > _clearUntil) UserNotice = null;
        return events;

        bool LevelFree(int i, double levelFt)
        {
            if (levelFt > 43000 || levelFt < 10000) return false;
            for (var m = 0; m < n; m++)
            {
                if (m == i || !ok[m]) continue;
                for (var k = 0; k < N; k += 3)
                    if ((pos[i, k] - pos[m, k]).Length < 10 * nm && Math.Abs(eff[m, k] - levelFt) < 1000) return false;
            }
            foreach (var o in obs)
                if ((pos[i, 0] - o.p0).Length < 20 * nm && Math.Abs(o.AltFt - levelFt) < 1000) return false;
            return true;
        }
    }
}
