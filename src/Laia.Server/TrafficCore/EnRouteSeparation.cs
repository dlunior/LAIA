namespace Laia.Server.TrafficCore;

/// <summary>
/// Separation for the traffic between airports near the user (user, 15 Nov: "would this respect RVSM separation of
/// other aircraft, not only the user?" — yes, do it). The whole-sky flights fly their schedule's straight lines on the
/// direction rule's levels (odd thousands eastbound, even westbound), which keeps head-on and most crossing traffic
/// 1,000 ft apart, but not a faster one catching up at the same level, climbs and descents through others' levels, the
/// airports' own traffic or the user. Every update (2 s), each flight is projected 5 minutes ahead (every 20 s) against
/// the obstacles (the user, the airports' aircraft) and the other whole-sky flights; where two would come within 5 nm
/// (6 nm for the user) and 1,000 ft — the en-route radar and RVSM minimums — one is given what ATC would give it:
/// <list type="bullet">
/// <item>a climbing or descending flight levels off 1,000 ft below or above the other until clear;</item>
/// <item>of two level at the same altitude (one catching the other up), one moves 2,000 ft up (down above FL390),
/// keeping the direction rule's levels;</item>
/// <item>with an obstacle (the user, an airport's aircraft) the whole-sky flight is always the one that moves.</item>
/// </list>
/// Which of two whole-sky flights moves is decided the same way every time (climbing or descending before level, then
/// by id), so they never both move. Changes are flown at 2,000 ft/min (faster when following a climb or descent), and
/// the altitude is continuous between updates (shown + rate × time).
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

    public List<Event> Update(double clock, double lat0, double lon0, IReadOnlyList<NetLeg> legs, IReadOnlyList<Obstacle> obstacles)
    {
        var events = new List<Event>();
        var nm = Geo2.MPerNm;
        var live = legs.Select(l => l.Id).ToHashSet();
        foreach (var k in _st.Keys.Where(k => !live.Contains(k)).ToList()) _st.Remove(k);
        var map = new LocalMap(lat0, lon0);
        const int N = 16; const double Step = 20;
        var n = legs.Count;
        var pos = new V2[n, N]; var alt = new double[n, N]; var eff = new double[n, N];
        var ok = new bool[n]; var level = new bool[n]; var gs = new double[n]; var baseRate = new double[n];
        for (var i = 0; i < n; i++)
        {
            var l = legs[i];
            if (NetworkModel.At(l, clock) is not { } p0 || p0.GsKt < 60) continue;
            ok[i] = true; gs[i] = p0.GsKt;
            baseRate[i] = NetworkModel.At(l, clock + 0.25) is { } q ? (q.AltFt - p0.AltFt) * 4 : 0;
            level[i] = Math.Abs(baseRate[i]) < 300;
            _st.TryGetValue(l.Id, out var st);
            for (var k = 0; k < N; k++)
            {
                var p = k == 0 ? p0 : NetworkModel.At(l, clock + k * Step / 60) ?? p0;
                pos[i, k] = map.ToXY(p.Lat, p.Lon); alt[i, k] = p.AltFt;
                var a = p.AltFt;
                if (st is not null) a = Math.Min(Math.Max(Math.Min(a, st.Cap), st.Floor), st.Cap) + st.Lvl;
                eff[i, k] = a;     // what the others see of it: with the limits it was given last time
            }
        }
        var obs = obstacles.Select(o => (o.Name, p0: map.ToXY(o.Lat, o.Lon), v: V2.FromBearing(o.Track, o.GsKt * Geo2.MpsPerKt), o.AltFt, vs: Math.Clamp(o.Vs, -4000, 4000), o.SepNm)).ToList();
        for (var i = 0; i < n; i++)
        {
            if (!ok[i]) continue;
            double cap = double.PositiveInfinity, floor = double.NegativeInfinity, lvl = 0; string? why = null;
            foreach (var o in obs)
            {
                if ((pos[i, 0] - o.p0).Length > (gs[i] * Geo2.MpsPerKt + o.v.Length) * N * Step + o.SepNm * nm) continue;
                for (var k = 0; k < N; k++)
                {
                    var t = k * Step;
                    var oAlt = o.AltFt + o.vs * Math.Min(t, 90) / 60;
                    if ((pos[i, k] - (o.p0 + o.v * t)).Length >= o.SepNm * nm || Math.Abs(alt[i, k] - oAlt) >= 1000) continue;
                    // On a whole level (FL370, not 36,748 ft), at least 1,000 ft from the other.
                    if (alt[i, k] >= oAlt) { var fl = Math.Ceiling((oAlt + 1000) / 1000) * 1000; if (fl > floor) { floor = fl; why = o.Name; } }
                    else { var fl = Math.Floor((oAlt - 1000) / 1000) * 1000; if (fl < cap) { cap = fl; why = o.Name; } }
                    break;
                }
            }
            for (var j = 0; j < n; j++)
            {
                if (j == i || !ok[j]) continue;
                if ((pos[i, 0] - pos[j, 0]).Length > (gs[i] + gs[j]) * Geo2.MpsPerKt * N * Step + 5 * nm) continue;
                var iMoves = level[i] != level[j] ? !level[i] : string.CompareOrdinal(legs[i].Id, legs[j].Id) > 0;
                if (!iMoves) continue;
                for (var k = 0; k < N; k++)
                {
                    if ((pos[i, k] - pos[j, k]).Length >= 5 * nm || Math.Abs(alt[i, k] - eff[j, k]) >= 1000) continue;
                    var who = legs[j].Callsign;
                    if (level[i] && level[j])
                    {
                        var up = alt[i, k] < 39000;
                        var want = Math.Round((eff[j, k] + (up ? 2000 : -2000) - alt[i, k]) / 1000) * 1000;
                        if (lvl == 0 || Math.Abs(want) < Math.Abs(lvl)) { lvl = want; why = who; }
                    }
                    else if (alt[i, k] >= eff[j, k]) { if (eff[j, k] + 1000 > floor) { floor = eff[j, k] + 1000; why = who; } }
                    else if (eff[j, k] - 1000 < cap) { cap = eff[j, k] - 1000; why = who; }
                    break;
                }
            }
            var leg = legs[i];
            var baseNow = alt[i, 0];
            var limited = !double.IsInfinity(cap) || !double.IsInfinity(floor) || lvl != 0;
            var held = Math.Max(Math.Min(baseNow, cap), floor);
            if (floor > cap) held = cap;
            var target = held + lvl;
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
            var rate = Math.Clamp((target - shown) * 30 + (heldAtLimit ? 0 : baseRate[i]), -maxR, maxR);
            if (limited && s.Why is null && why is not null)
            {
                Limits++;
                var what = lvl != 0 ? $"to {target:N0} ft (level change)" : held < baseNow ? $"levels at {target:N0} ft, below" : $"levels at {target:N0} ft, above";
                events.Add(new Event(leg, what, why));
            }
            s.Shown = shown; s.Rate = rate; s.At = clock; s.Cap = cap; s.Floor = floor; s.Lvl = lvl; s.Why = limited ? why : null;
            if (!limited && Math.Abs(shown - baseNow) < 20) _st.Remove(leg.Id);
        }
        return events;
    }
}
