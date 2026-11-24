using Laia.Common.Airports;

namespace Laia.Server.TrafficCore;

/// <summary>A notice for the user in Smart traffic mode: never an instruction, only something that matters now.</summary>
public sealed record Notice(string Id, string Text, bool Warning, bool Clear = false);

/// <summary>The user's place in the arrival sequence as the traffic manager plans it.</summary>
public sealed record UserSequence(string Runway, int Number, string? AheadCallsign, string? AheadType, string? AheadWhere,
    string? BehindCallsign, string? BehindType, bool Replanned, double EtaSec, string? SpeedAdvice = null);

/// <summary>
/// M8 — Smart traffic mode for the user (user, 12 Nov): no directives, only notices that matter. On the ground: traffic on
/// short final or rolling on the runway the user is at, traffic crossing that runway downfield, a runway or taxiway the
/// user is about to cross that's about to be busy, and traffic waiting for the user. In the air: the user's place in the
/// arrival sequence — between which two aircraft to aim — with a generous gap planned for the human (about 10–12 nm
/// between the aircraft either side, where LAIA's own traffic uses 5–7), and a new sequence when the user is going to
/// miss the planned one.
/// </summary>
public sealed partial class TrafficWorld
{
    /// <summary>The user's aircraft (set by the server each tick).</summary>
    public double UserAltFt { get; set; }
    public double UserVs { get; set; }
    public double UserGs { get; set; }
    public double UserTrack { get; set; }
    public string UserCallsign { get; set; } = "";
    public string UserType { get; set; } = "";
    /// <summary>Where the user's flight plan goes (SimBrief), if known.</summary>
    public string? UserDestination { get; set; }
    public string? UserOrigin { get; set; }
    /// <summary>The user's filed STAR (SimBrief) and the fix their route joins it at.</summary>
    public string? UserStar { get; set; }
    public string? UserStarFrom { get; set; }

    /// <summary>The user as a member of the arrival sequence (never drawn, never moved by LAIA), or null.</summary>
    public Flight? UserArr { get; private set; }
    private double _userSlot = double.NaN, _userOffSince = double.NaN, _userSlotAt, _etaScale = 1;
    private readonly List<(double t, double eta)> _etaSamples = new();
    private bool _userReplanned;
    private double _userSlotRaw, _userResAt = double.NegativeInfinity;
    private int _userRwyChanges;
    private string _seqSig = "";
    private double _seqSince, _seqNewUntil = double.MinValue;
    public UserSequence? Sequence { get; private set; }
    public List<Notice> Notices { get; private set; } = new();

    /// <summary>Seconds of spacing either side of the user at the threshold: about 5–6 nm at final approach speed, so the
    /// gap between the aircraft ahead and behind is 10–12 nm (LAIA's own arrivals are spaced 5–7).</summary>
    private const double UserSideGapSec = 140;

    private bool UserArriving()
    {
        if (UserPos is not { } u || UserOnGround) return false;
        var nm = u.Length / Geo2.MPerNm;
        // With a flight plan to here: in the sequence from 200 nm, as far out as LAIA takes its own arrivals over, so
        // the arrival manager slows the traffic behind in good time (user, 16 Nov, KGPI: "LAIA should be able to read
        // my groundspeed and slow the other plane in time without having it to do a loop or hold" — the user joined
        // the sequence at 80 nm, 45 s ahead of AAL1842's slot, which then got a loop and a hold).
        if (UserDestination is { Length: > 0 } d) return nm <= 200 && d.Equals(Apt.Icao, StringComparison.OrdinalIgnoreCase);
        if (nm > 80 || UserGs < 60) return false;   // not moving (paused or slewing): not in the sequence
        // No plan: within 40 nm, below 15,000 ft above the field and coming closer.
        var toApt = (new V2(0, 0) - u).Bearing;
        return nm < 40 && UserAltFt < Apt.ElevationFt + 15000 && Math.Abs(Geo2.Diff(UserTrack, toApt)) < 100;
    }

    /// <summary>Track miles still to fly to the threshold, roughly: straight in when on the final, otherwise to a point
    /// about 9 nm out on the final and then down it.</summary>
    private double UserTrackNm(RunwayEnd r, V2 u)
    {
        var along = r.Along(u); var off = Math.Abs(r.Offset(u));
        // On the final at any distance (21 Nov, KATL: the user established 20+ nm out on 08L's final was counted along
        // CHPPR1 and a real track — "number 13, between DAL348 and SAS929", DAL348 64 nm behind).
        if (along < 0 && along > -40 * Geo2.MPerNm && off < 1.0 * Geo2.MPerNm && Math.Abs(Geo2.Diff(UserTrack, r.Heading)) < 30)
            return -along / Geo2.MPerNm;
        var routed = UserRouteNm(r, u);
        if (!double.IsNaN(routed)) return routed;
        var join = r.Threshold - r.Dir * (11 * Geo2.MPerNm);
        return (u - join).Length / Geo2.MPerNm + 11;
    }

    private readonly Dictionary<RunwayEnd, (double at, double nm)> _userRoute = new();

    /// <summary>
    /// The user's flying miles to the runway (user, 17 Nov: "Can LAIA calculate flying miles rather than distance
    /// plane to plane?"): along the rest of their filed STAR from where they are (to about 8 nm from the airport, where
    /// controllers turn traffic off it), then the airport's own downwind and base onto a 10–12 mile final (11 nm) and
    /// down the final — not a straight line to the final. Worked out every 10 s. NaN when it can't be.
    /// </summary>
    private double UserRouteNm(RunwayEnd r, V2 u)
    {
        if (_userRoute.TryGetValue(r, out var c) && Time - c.at < 10) return c.nm;
        double nmOut;
        try { nmOut = RouteNm(r, u); } catch { nmOut = double.NaN; }
        _userRoute[r] = (Time, nmOut);
        return nmOut;
    }

    private double RouteNm(RunwayEnd r, V2 u)
    {
        var nm = Geo2.MPerNm;
        var pts = new List<V2> { u };
        var sn = UserStar ?? "";
        var star = sn.Length == 0 ? null : Apt.Data.Arrivals.FirstOrDefault(a => a.Name.Equals(sn, StringComparison.OrdinalIgnoreCase))
            ?? Apt.Data.Arrivals.FirstOrDefault(a => AirportRules.Base(a.Name) == AirportRules.Base(sn));
        if (star is not null)
        {
            var legs = new List<ProcedureLeg>();
            if (UserStarFrom is { Length: > 0 } fr && star.EnrouteTransitions.FirstOrDefault(t => t.Name.Equals(fr, StringComparison.OrdinalIgnoreCase)) is { } tr) legs.AddRange(tr.Legs);
            legs.AddRange(star.Legs);
            if (star.RunwayTransitions.FirstOrDefault(t => t.Name == r.Ident) is { } rt) legs.AddRange(rt.Legs);
            var spA = Apt.LegsToPoints(legs, u, Math.Max(UserAltFt, 3000), out _);
            var sp = spA.Select(p => p.P).ToList();
            // Off the STAR (more than 3 nm from any of its legs): it no longer says where the user goes — direct to the
            // final instead (the caller's fallback).
            {
                var dmin = double.MaxValue;
                for (var i = 1; i < sp.Count; i++) dmin = Math.Min(dmin, DistToSegment(u, sp[i - 1], sp[i]));
                if (sp.Count >= 2 && dmin > 3 * nm) return double.NaN;
            }
            // Where real traffic leaves this STAR, and the way it flies on from there (21 Nov, KORD: the user on WYNDE3
            // counted along its legs past PAPPI that real arrivals never fly — "vectors after PAPPI", real tracks off it
            // at WYNDE — put number 2 behind AMZ3838 150 nm out while the user was 40 nm out).
            if (RealWayForUser(r, sn, spA) is { } rw)
            {
                var (cut, way) = rw;
                var tail = new List<V2>();
                var k0r = Enumerable.Range(0, Math.Min(cut + 1, sp.Count)).FirstOrDefault(Ahead, -1);
                if (k0r >= 0) { tail.AddRange(sp.Skip(k0r).Take(cut - k0r + 1)); tail.AddRange(way); }
                else
                {
                    // Already past where real traffic turns off: from the nearest point of the real way on.
                    var j = 0; var bd = double.MaxValue;
                    for (var q = 0; q < way.Count; q++) { var d = (way[q] - u).Length; if (d < bd) { bd = d; j = q; } }
                    tail.AddRange(way.Skip(j + 1));
                }
                if (tail.Count > 0)
                {
                    pts.AddRange(tail);
                    double l2 = 0;
                    for (var i = 1; i < pts.Count; i++) l2 += (pts[i] - pts[i - 1]).Length;
                    return (l2 + (pts[^1] - r.Threshold).Length) / nm;
                }
            }
            // From the first fix still ahead of the user (not behind them on the way in, nor one to turn back to).
            bool Ahead(int k)
            {
                var p = sp[k];
                if (p.Length > u.Length + 5 * nm) return false;
                var to = p - u;
                if (to.Length < 2 * nm) return k + 1 >= sp.Count;
                if (Math.Abs(Geo2.Diff(to.Bearing, (new V2(0, 0) - u).Bearing)) > 90) return false;
                if (k + 1 >= sp.Count) return true;
                var outb = sp[k + 1] - p;
                return outb.Length < 1 || Math.Abs(Geo2.Diff(to.Bearing, outb.Bearing)) <= 90;
            }
            var k0 = Enumerable.Range(0, sp.Count).FirstOrDefault(Ahead, -1);
            if (k0 >= 0)
            {
                sp = sp.Skip(k0).ToList();
                var near = sp.FindIndex(p => p.Length < 8 * nm);
                if (near >= 0) sp = sp.Take(near).ToList();
                pts.AddRange(sp);
            }
        }
        pts.AddRange(Vectors(pts[^1], r, ShapeFor(r, fixedJoin: 11)));
        double len = 0;
        for (var i = 1; i < pts.Count; i++) len += (pts[i] - pts[i - 1]).Length;
        len += (pts[^1] - r.Threshold).Length;
        return len / nm;
    }

    private readonly Dictionary<string, (double at, (V2 at, List<V2> way)? res)> _userWay = new();

    /// <summary>The real way in off the user's STAR for this runway (cached 10 minutes): the quiet fan track (as an
    /// arrival with nothing to lose gets), else from the first point back from the STAR's end that real arrivals passed.</summary>
    private (int cut, List<V2> way)? RealWayForUser(RunwayEnd r, string star, List<AirPoint> spA)
    {
        var key = r.Ident + "|" + star;
        // The turn-off kept as a point and found again in this STAR's points (their number changes with where the user is).
        (int cut, List<V2> way)? Here((V2 at, List<V2> way)? x)
        {
            if (x is not { } v) return null;
            var k = 0; var bd = double.MaxValue;
            for (var i = 0; i < spA.Count; i++) { var d = (spA[i].P - v.at).Length; if (d < bd) { bd = d; k = i; } }
            return bd < 500 ? (k, v.way) : null;
        }
        if (_userWay.TryGetValue(key, out var c) && Time - c.at < 600) return Here(c.res);
        (int cut, List<V2> way)? res = null;
        if (spA.Count >= 2 && RealFan(r, spA) is { Count: >= 3 } fan)
        {
            var t = fan[Math.Clamp(_fanQuiet, 0, fan.Count - 1)];
            res = (t.cut, t.pts);
        }
        else
            for (var k = spA.Count - 1; k >= 0 && res is null; k--)
            {
                var dir = k == spA.Count - 1 ? double.NaN : k > 0 ? (spA[k].P - spA[k - 1].P).Bearing : (spA[1].P - spA[0].P).Bearing;
                if (DirectionTrack(r, spA[k].P, dir) is { } dt) res = (k, dt);
            }
        _userWay[key] = (Time, res is { } rr ? (spA[rr.cut].P, rr.way) : null);
        return res;
    }

    /// <summary>
    /// Seconds to the threshold at about the speeds the user will fly, from their groundspeed now: that speed beyond
    /// 100 nm (cruise), at most 330 kt from 100 to 40 nm (the descent), 260 from 40 to 20 (250 below 10,000 ft), 210
    /// inside 20 nm, 180 inside 12, the approach speed inside 6. (Until 16 Nov far out was capped at 320 kt, which put
    /// a user cruising at 480 kt minutes later than they'd be.)
    /// </summary>
    private double UserEtaSec(double nm)
    {
        var vapp = Perf.For(UserType.Length > 0 ? UserType : "A320").Vapp;
        double t = 0, d = nm;
        void Leg(double from, double to, double kt) { var len = Math.Max(0, Math.Min(d, from) - to); if (len > 0) t += len / Math.Max(90, kt) * 3600; }
        var gs = Math.Clamp(UserGs > 60 ? UserGs : 300, 180, 650);
        if (UserAltFt < 11000) gs = Math.Min(gs, 260);
        Leg(double.MaxValue, 100, gs); Leg(100, 40, Math.Min(gs, 330)); Leg(40, 20, Math.Min(gs, 260));
        // The user's own approach, as they fly it (user, 17 Nov): 210 kt indicated through the base turn onto a 10–12 mile
        // final, 190 once established, 170 from 7.5 to 5 miles, the approach speed inside 5 — each turned into ground
        // speed at its height in the wind there (the final's headwind; the base is across the wind, so no wind).
        var rwy = UserArr?.Rwy ?? _expected;
        var elev = rwy?.ElevationFt ?? Apt.ElevationFt;
        double Final(double ias, double nmOut)
        {
            var alt = elev + nmOut * 318;   // a 3° path
            var tas = Wind.Tas(ias, alt);
            var g = rwy is null ? tas : tas - WindAt(alt).Headwind(rwy.Heading);
            // Never faster than they're flying now (a light aircraft at 110 kt doesn't do 190 on final), nor slower than
            // 70 % of it (its approach speed may not be the one assumed for the type).
            return UserGs > 60 ? Math.Clamp(g, UserGs * 0.7, UserGs) : g;
        }
        Leg(20, 11, Math.Min(UserGs > 60 ? UserGs : gs, Wind.Tas(210, elev + 4000)));
        Leg(11, 7.5, Final(190, 9.25)); Leg(7.5, 5, Final(170, 6.25)); Leg(5, 0, Final(vapp + 5, 2.5));
        return t;
    }

    /// <summary>
    /// The runway and approach the user can expect here, from as soon as this destination is prepared ahead (300 nm;
    /// user, 16 Nov: "to allow the user to configure arriving runway and approach sooner"): the landing runway on their
    /// side of the field, kept while it's in use, and its approach. The runway they're given at 80 nm is this one if
    /// it's still in use.
    /// </summary>
    public string? ExpectedRunway { get; private set; }
    public string? ExpectedApproach { get; private set; }
    private RunwayEnd? _expected;

    private void UpdateExpected()
    {
        if (UserPos is not { } u || UserOnGround || UserDestination is not { Length: > 0 } d || !d.Equals(Apt.Icao, StringComparison.OrdinalIgnoreCase))
        { _expected = null; ExpectedRunway = ExpectedApproach = null; return; }
        var arrs = ArrRwys.Count > 0 ? ArrRwys : ArrRwy is null ? new List<RunwayEnd>() : new() { ArrRwy };
        if (UserArr?.Rwy is { } given) _expected = given;
        else if (_expected is null || !arrs.Contains(_expected)) _expected = arrs.Count == 0 ? null : arrs.OrderBy(r => UserTrackNm(r, u)).First();
        ExpectedRunway = _expected?.Ident;
        ExpectedApproach = _expected is null ? null : ApproachFor(_expected) is { } a ? ApproachLabel(a) : "vectors to final";
    }

    /// <summary>Called every step before the sequencing: keeps the user's place in the arrival sequence.</summary>
    private double _userEtaAbs = double.NaN, _userEtaJumpAt = double.NaN, _userEtaSeen;

    private void UpdateUserArrival()
    {
        UpdateExpected();
        if (!UserArriving() || UserPos is not { } u)
        {
            UserArr = null; Sequence = null; _userSlot = double.NaN; _userEtaAbs = double.NaN; _etaScale = 1; _etaSamples.Clear(); _seqSig = ""; _userRwyChanges = 0; _userHoldLogged = false; return;
        }
        var arrs = ArrRwys.Count > 0 ? ArrRwys : ArrRwy is null ? new List<RunwayEnd>() : new() { ArrRwy };
        if (arrs.Count == 0) return;
        var f = UserArr ??= new Flight
        {
            Id = "user", Callsign = UserCallsign.Length > 0 ? UserCallsign : "you", Airline = "", Type = UserType.Length > 0 ? UserType : "A320",
            Kind = "arrival", P = Perf.For(UserType.Length > 0 ? UserType : "A320"),
        };
        // The runway: given once, on the user's side of the field. Changed at most once, and never below 10,000 ft
        // (user, 13 Nov: changed "at the last moment … no more than one runway change and never below 10000ft"):
        // otherwise the user keeps it even if the airport has stopped landing there, and LAIA's traffic works around them.
        if (f.Rwy is null) f.Rwy = _expected is { } ex && arrs.Contains(ex) ? ex : arrs.OrderBy(r => UserTrackNm(r, u)).First();
        else if (!arrs.Contains(f.Rwy) && UserAltFt >= 10000 && _userRwyChanges < 1)
        {
            var old = f.Rwy;
            f.Rwy = arrs.OrderBy(r => UserTrackNm(r, u)).First();
            _userRwyChanges++;
            _userSlot = double.NaN;
            Log($"{f.Callsign} (you): runway changed from {old.Ident} to {f.Rwy.Ident} (the only change; none below 10,000 ft)");
        }
        f.OnGround = false; f.Gear = false;
        f.Pos = u; f.AltFt = UserAltFt; f.Gs = UserGs; f.Ias = Math.Min(UserGs, 250); f.Heading = UserTrack;
        f.SetPhase("arriving", Time);
        var nm = UserTrackNm(f.Rwy, u);
        f.ThrS = nm * Geo2.MPerNm; f.S = 0;
        f.EtaAt = Time;
        var raw = UserEtaSec(nm);
        // Learn how the user actually flies (16 Nov, KAUS: the user flew in faster than the speeds assumed, caught up
        // SWA581 from behind, and was only put ahead of it 4 nm apart — a loop and a 360 for SWA581): the estimate
        // should come 1 s closer every second; if it comes closer faster (or slower), the rest of it is scaled by what
        // the last two minutes showed (between 0.75 and 1.25), beyond 10 nm out.
        _etaSamples.Add((Time, Time + raw));
        _etaSamples.RemoveAll(x => Time - x.t > 120);
        if (_etaSamples.Count >= 2 && Time - _etaSamples[0].t > 45)
        {
            var (t0, e0) = _etaSamples[0];
            var rate = ((Time + raw) - e0) / (Time - t0);   // 0 when on the estimate; −0.2 when 20 % quicker
            var k = Math.Clamp(1 / Math.Max(0.5, 1 - rate), 0.6, 1.4);
            _etaScale += (k - _etaScale) * Math.Min(1, 0.05);
        }
        f.EtaSec = nm > 10 ? UserEtaSec(10) + (raw - UserEtaSec(10)) * _etaScale : raw;
        // A jump of more than 30 s in the user's landing time counts only once it has held for 10 s (22 Nov, KMSP: on
        // final, one reading put the user minutes later — behind SWA4367 — and its downwind was stretched 12 nm for it).
        var etaAbs = Time + f.EtaSec;
        if (double.IsNaN(_userEtaAbs) || Math.Abs(etaAbs - _userEtaAbs) <= 30 + (Time - _userEtaSeen)) { _userEtaAbs = etaAbs; _userEtaJumpAt = double.NaN; }
        else if (double.IsNaN(_userEtaJumpAt)) _userEtaJumpAt = Time;
        else if (Time - _userEtaJumpAt >= 10) { _userEtaAbs = etaAbs; _userEtaJumpAt = double.NaN; }
        _userEtaSeen = Time;
        f.EtaSec = _userEtaAbs - Time;
        // The planned slot follows the user smoothly (closing any difference over 15 s with a time constant of about
        // 45 s), so the aircraft around them adjust with speed while there's time — instead of the slot holding still
        // until the user was 90 s off it and then jumping (the old way).
        var eta = Time + f.EtaSec;
        var rawAbs = Time + raw;
        if (double.IsNaN(_userSlot)) { _userSlot = eta; _userSlotRaw = rawAbs; _userReplanned = false; _userSlotAt = Time; _userOffSince = double.NaN; }
        else if (nm > 150)
        {
            var diff = eta - _userSlot;
            var step = Math.Min(1, (Time - _userSlotAt) / 45);
            if (Math.Abs(diff) > 15) _userSlot += diff * step;
            _userSlotRaw = rawAbs;
            _userReplanned = false; _userOffSince = double.NaN;
        }
        else
        {
            // Within 150 nm the user's place holds while they fly it (user, 23 Nov, KBOS: "I did try to keep the
            // sequence, slowing myself to keep adequate spacing behind the JBU. But when I slowed down it changed my
            // sequence to the SWA behind me and the SWA sped up super fast"): slowing or speeding up to keep spacing
            // is the user flying their slot, so nobody is re-sequenced for it. Only when the user is clearly off it —
            // more than 2 minutes (3 inside 30 nm: a 360, a wide turn) for over a minute — does the place move, once,
            // with a notice.
            // Off by both the learned estimate and the plain one (the learned one scales a single slow-down up to 40 %
            // more as if it were how they always fly: 3.4 min "late" for a 1-minute slow-down in the test).
            var dScaled = eta - _userSlot; var dRaw = rawAbs - _userSlotRaw;
            var diff = Math.Sign(dScaled) == Math.Sign(dRaw) ? Math.Sign(dScaled) * Math.Min(Math.Abs(dScaled), Math.Abs(dRaw)) : 0;
            var limit = nm > 30 ? 120 : 180;
            if (Math.Abs(diff) <= limit) _userOffSince = double.NaN;
            else if (double.IsNaN(_userOffSince)) _userOffSince = Time;
            else if (Time - _userOffSince >= 60)
            {
                if (Time - _userResAt > 300) Log($"{f.Callsign} (you): {Math.Abs(diff) / 60:0.0} min {(diff > 0 ? "later" : "earlier")} than your place in the sequence for over a minute — re-sequenced");
                _userSlot = eta; _userSlotRaw = rawAbs; _userReplanned = true; _userOffSince = double.NaN; _userResAt = Time;
            }
        }
        _userSlotAt = Time;
        f.EtaNominal = _userSlot;
        f.Frozen = true; f.FrozenSlot = _userSlot;
    }

    /// <summary>After the sequencing: the user's number and who's either side, for the notice.</summary>
    private void DescribeUserSequence(List<Flight> ordered)
    {
        if (UserArr is not { } me || UserPos is not { } u) return;
        var i = ordered.IndexOf(me);
        if (i < 0) return;
        var ahead = i > 0 ? ordered[i - 1] : null;
        var behind = i + 1 < ordered.Count ? ordered[i + 1] : null;
        string? Where(Flight o)
        {
            var v = o.Pos - u;
            var clock = (int)Math.Round(Geo2.Norm(v.Bearing - UserTrack) / 30) % 12;
            return $"{(clock == 0 ? 12 : clock)} o'clock, {v.Length / Geo2.MPerNm:0} nm, {Math.Round(o.AltFt / 100) * 100:N0} ft";
        }
        // A new sequence when the plan was re-made for the user, or their number or neighbours changed (once settled:
        // the first 10 s after they join are the sequence taking shape).
        // Moving up because the aircraft ahead landed isn't a new sequence (user, 13 Nov: "re-sequenced me several
        // times" — it was mostly 5, 4, 3, 2, 1 as the ones ahead landed): only a different runway, or someone put in
        // ahead of or behind the user while the one before is still coming in.
        var parts = _seqSig.Split('|');
        // Only a change of runway or neighbours (21 Nov: "number 1 for 28C, between nobody and nobody" ten times in 15
        // minutes as the user's estimate drifted from the slot with no one around).
        var changed = false;
        var rwyChanged = false;
        if (_seqSig.Length > 0 && Time - _seqSince > 10)
        {
            bool StillThere(string id) => id.Length > 0 && ordered.Any(o => o.Id == id);
            if (parts[0] != me.Rwy!.Ident) { changed = true; rwyChanged = true; }
            // Someone put in ahead (the one that was ahead is still coming in, or the user was number 1), or behind.
            if (parts[1] != (ahead?.Id ?? "") && (StillThere(parts[1]) || (parts[1].Length == 0 && ahead is not null))) changed = true;
            if (parts[2] != (behind?.Id ?? "") && StillThere(parts[2])) changed = true;
        }
        if (_seqSig.Length == 0) _seqSince = Time;
        _seqSig = $"{me.Rwy!.Ident}|{ahead?.Id}|{behind?.Id}";
        // New neighbours are announced once they've held for 45 s (21 Nov, KATL: "new sequence" 15 times in 34 minutes as
        // the user's estimate swung and the neighbours either side changed every minute or two); a new runway at once.
        if (changed && !rwyChanged) { _pendSig = _seqSig; _pendSince = Time; changed = false; }
        else if (_pendSig.Length > 0)
        {
            if (_pendSig != _seqSig) _pendSig = "";
            else if (Time - _pendSince >= 45) { changed = true; _pendSig = ""; }
        }
        _userReplanned = false;
        var logNow = changed && Time >= _seqNewUntil;
        if (changed) _seqNewUntil = Time + 30;
        changed |= Time < _seqNewUntil;
        // Something to fly to (user, 23 Nov): the speed that keeps the gap behind the one ahead — its speed now, less
        // (or more) by what the gap is short (or long) of what's needed over the time left; beyond 8 nm, in 10 kt steps.
        string? advice = null;
        var toGoNm = me.ThrS / Geo2.MPerNm;
        if (ahead is not null && !double.IsNaN(ahead.EtaSec) && !double.IsNaN(me.EtaSec) && toGoNm > 8 && me.EtaSec > 120)
        {
            var need = ThresholdGapNeeded(ahead, me, forDepartures: false);
            var err = need - (me.EtaSec - ahead.EtaSec);          // > 0: too close
            var kt = ahead.Ias * (1 - Math.Clamp(err / me.EtaSec, -0.2, 0.2));
            kt = Math.Clamp(kt, me.P.Vapp + 10, UserAltFt < 10500 ? 250 : 320);
            kt = Math.Round(Math.Clamp(kt, ahead.Ias - 40, ahead.Ias + 40) / 10) * 10;
            advice = $"about {kt:0} kt keeps your spacing behind {ahead.Callsign} ({(ahead.Pos - u).Length / Geo2.MPerNm:0} nm ahead)";
        }
        Sequence = new UserSequence(me.Rwy!.Ident, me.Seq, ahead?.Callsign, ahead?.Type, ahead is null ? null : Where(ahead),
            behind?.Callsign, behind?.Type, changed, me.EtaSec, advice);
        if (logNow) Log($"{me.Callsign} (you): new sequence — number {me.Seq} for {me.Rwy.Ident}, between {ahead?.Callsign ?? "nobody"} and {behind?.Callsign ?? "nobody"}");
    }

    /// <summary>
    /// The user's place in the departure queue (user, 20 Nov, KLAS: "I don't see where I am in the queue for takeoff"):
    /// the runway of LAIA's departures queuing near the user (else the nearest departure runway in use within 3 km), and
    /// how many are ahead — lined up, holding short, or taxiing and nearer its start than the user.
    /// </summary>
    private string _pendSig = ""; private double _pendSince;
    private bool _userWasAirborne, _userLandedHere;

    private void DepartureQueue(V2 u, List<Notice> list)
    {
        if (UserArr is { Done: false }) return;
        // Only where the user departs from (21 Nov, KATL: "Number 2 for departure, runway 08R" while taxiing in after
        // landing): the plan's origin, or with no plan an airport the user hasn't just landed at.
        if (!UserOnGround) _userWasAirborne = true;
        else if (_userWasAirborne && u.Length < 6000) _userLandedHere = true;
        if (_userLandedHere) return;
        if (UserOrigin is { Length: > 0 } org && !org.Equals(Apt.Icao, StringComparison.OrdinalIgnoreCase)) return;
        var deps = Flights.Where(o => o.Kind == "departure" && !o.Done && o.OnGround && o.Rwy is not null
            && o.Phase is "taxi" or "holding short" or "lining up" or "lined up").ToList();
        if (deps.Count == 0) return;
        RunwayEnd? rwy = deps.Where(o => (o.Pos - u).Length < 250).OrderBy(o => (o.Pos - u).Length).FirstOrDefault()?.Rwy;
        rwy ??= deps.Select(o => o.Rwy!).Distinct().Where(r => (r.Threshold - u).Length < 3000).OrderBy(r => (r.Threshold - u).Length).FirstOrDefault();
        if (rwy is null) return;
        // On the runway already (lined up): nothing to number.
        if (Math.Abs(rwy.Offset(u)) < rwy.WidthM / 2 + 10 && rwy.Along(u) > -10 && rwy.Along(u) < rwy.LengthM) return;
        var mine = (rwy.Threshold - u).Length;
        var ahead = deps.Where(o => o.Rwy!.Runway == rwy.Runway &&
                (o.Phase is "holding short" or "lining up" or "lined up" || (o.Pos - rwy.Threshold).Length < mine - 20))
            .OrderBy(o => o.Phase is "lined up" or "lining up" ? 0 : o.Phase == "holding short" ? 1 : 2).ThenBy(o => (o.Pos - rwy.Threshold).Length).ToList();
        var last = ahead.LastOrDefault();
        list.Add(new Notice("depq", $"Number {ahead.Count + 1} for departure, runway {rwy.Ident}" +
            (last is null ? " — nobody ahead" : $", behind {last.Callsign} ({last.Type})"), false));
    }

    /// <summary>The notices that matter now (rebuilt every step, shown while true).</summary>
    private void UpdateNotices()
    {
        var list = new List<Notice>();
        if (UserPos is not { } u) { Notices = list; return; }
        if (!UserOnGround)
        {
            if (Sequence is { } sq)
            {
                var between = sq.AheadCallsign is null && sq.BehindCallsign is null ? "no traffic either side"
                    : $"between {sq.AheadCallsign ?? "—"}{(sq.AheadType is null ? "" : " (" + sq.AheadType + ")")} and {sq.BehindCallsign ?? "—"}{(sq.BehindType is null ? "" : " (" + sq.BehindType + ")")}";
                list.Add(new Notice("seq", $"{(sq.Replanned ? "New sequence: " : "")}Number {sq.Number} for {sq.Runway}, {between}" +
                    (sq.AheadWhere is null ? "" : $" — {sq.AheadCallsign} is at your {sq.AheadWhere}") +
                    (sq.SpeedAdvice is null ? "" : $". {char.ToUpper(sq.SpeedAdvice[0])}{sq.SpeedAdvice[1..]}"), sq.Replanned));
            }
            Notices = list; return;
        }
        var nm = Geo2.MPerNm;
        // The runway the user is on or holding short of (within 90 m of its centreline, beside it or on it).
        foreach (var r in Apt.Ends)
        {
            var along = r.Along(u); var off = Math.Abs(r.Offset(u));
            if (along < -150 || along > r.LengthM + 150 || off > 90) continue;
            var onIt = off < 30 && Math.Abs(Geo2.Diff(UserTrack, r.Heading)) < 30;
            // Only the end the user would depart from (facing the runway's direction, or holding short near this end).
            if (onIt is false && along > r.LengthM / 2) continue;
            foreach (var o in Flights)
            {
                if (o.Done || o.Rwy is null) continue;
                var same = o.Rwy.Runway == r.Runway;
                if (o.Kind == "arrival" && !o.OnGround && same && o.Phase is "final" or "approach" or "flare" && (o.ThrS - o.S) / nm < 4)
                    list.Add(new Notice("final-" + o.Id, $"Traffic on {(o.ThrS - o.S) / nm:0.0} nm final for {o.Rwy.Ident}: {o.Callsign} ({o.Type}) — don't line up or take off", true));
                else if (o.OnGround && same && o.Phase is "landing roll" or "takeoff roll" or "lined up")
                    list.Add(new Notice("rwy-" + o.Id, $"Traffic {(o.Phase == "lined up" ? "lined up" : "rolling")} on {o.Rwy.Ident}: {o.Callsign} ({o.Type})", true));
            }
            // Ground traffic crossing this runway further down (on the strip, not aligned with it, beyond the user).
            foreach (var o in Flights)
            {
                if (o.Done || !o.OnGround || o.Phase is "landing roll" or "takeoff roll" or "lined up") continue;
                var oa = r.Along(o.Pos); var oo = Math.Abs(r.Offset(o.Pos));
                if (oo < 35 && oa > along + 150 && oa < r.LengthM && Math.Abs(Math.Sin((o.Heading - r.Heading) * Math.PI / 180)) > 0.5)
                    list.Add(new Notice("cross-" + o.Id, $"Traffic crossing {r.Ident} downfield: {o.Callsign} ({o.Type})", true));
            }
        }
        // Taxiing towards a runway about to be busy, or a taxiway with crossing traffic.
        if (UserGs > 3)
        {
            var fwd = V2.FromBearing(UserTrack);
            foreach (var r in Apt.Ends.GroupBy(e => e.Runway).Select(g => g.First()))
            {
                var off = r.Offset(u); var along = r.Along(u);
                if (Math.Abs(off) < 40 || Math.Abs(off) > 200 || along < -100 || along > r.LengthM + 100) continue;
                // Heading towards the centreline.
                if (Math.Sign(V2.Dot(fwd, new V2(-r.Dir.Y, r.Dir.X))) != -Math.Sign(off)) continue;
                var busy = Flights.FirstOrDefault(o => !o.Done && o.Rwy?.Runway == r.Runway &&
                    ((o.Kind == "arrival" && !o.OnGround && (o.ThrS - o.S) / nm < 3) || (o.OnGround && o.Phase is "takeoff roll" or "landing roll" or "lined up")));
                if (busy is not null)
                    list.Add(new Notice("hs-" + r.Runway, $"Hold short of {r.Ident}{RunwayPair(r)}{(TaxiwayAt(u, UserTrack) is { } onTw ? $" on {onTw}" : "")}: {busy.Callsign} ({busy.Type}) {(busy.Kind == "arrival" && !busy.OnGround ? "landing" : busy.Phase == "lined up" ? "lined up to depart" : busy.Phase == "takeoff roll" ? "departing" : "on the runway")}", true));
            }
            GiveWay(u, list);
        }
        else PushNearby(u, list);
        DepartureQueue(u, list);
        // Traffic waiting for the user.
        foreach (var o in Flights.Where(o => !o.Done && o.OnGround && o.BlockedByUser && o.BlockedFor > 5))
            list.Add(new Notice("wait-" + o.Id, $"{o.Callsign} ({o.Type}) is {(o.Phase == "boarding" || o.Phase == "pushback" ? "waiting to push back" : "holding")} for you", false));
        AllClear(u, list);
        Notices = list.GroupBy(n => n.Id).Select(g => g.First()).OrderBy(n => n.Clear).Take(4).ToList();
    }

    /// <summary>
    /// Give way on the taxiways (user, 16 Nov: "does LAIA notify the user if they need to give way to another aircraft
    /// on a taxiway?"). For each of LAIA's aircraft moving on the ground within 600 m: where its own route ahead crosses
    /// the user's way (straight on at their speed), and who gets there first. If it does — or they'd get there within
    /// 15 s of each other and it's already the closer — the user is told to give way ("Give way: DAL123 (A321) crossing
    /// ahead from the right, about 120 m, in 12 s"); if the user is first, that it's giving way to them. Traffic coming
    /// the other way on the user's taxiway, and an aircraft pushing back across the user's way, are called out too.
    /// </summary>
    private void GiveWay(V2 u, List<Notice> list)
    {
        var fwd = V2.FromBearing(UserTrack);
        var ums = Math.Max(UserGs, 5) * 0.5144;
        foreach (var o in Flights)
        {
            if (o.Done || !o.OnGround || o.Path is null || o.Phase is "landing roll" or "takeoff roll" or "lined up" or "lining up" or "parked" or "boarding" or "at gate") continue;
            var v = o.Pos - u;
            if (v.Length > 600 || v.Length < 20) continue;
            var side = Geo2.Diff(v.Bearing, UserTrack) < 0 ? "left" : "right";
            // Coming the other way on the user's taxiway: ahead, nearly on the user's line, heading back at them.
            var ahead = V2.Dot(v, fwd); var lateral = Math.Abs(v.X * fwd.Y - v.Y * fwd.X);
            if (ahead > 0 && ahead < 400 && lateral < 30 && Math.Abs(Geo2.Diff(o.Heading, UserTrack)) > 150 && o.Phase != "pushback")
            {
                list.Add(new Notice("headon-" + o.Id, $"Traffic ahead coming the other way: {o.Callsign} ({o.Type}), {ahead:0} m — hold position, it will turn off or wait for you", true));
                continue;
            }
            // Where its route ahead first comes within 30 m of the user's way ahead.
            var oms = Math.Max(o.Gs, o.Phase == "pushback" ? 1.5 : 6) * 0.5144;
            var dir = o.Reverse ? -1 : 1;
            double sA = double.NaN, sU = 0;
            for (var d = 0.0; d <= 400; d += 6)
            {
                var sAt = o.S + dir * d;
                if (sAt < 0 || sAt > o.Path.Length) break;
                var q = o.Path.At(sAt).p - u;
                var al = V2.Dot(q, fwd);
                if (al < 10 || al > 450) continue;
                if (Math.Abs(q.X * fwd.Y - q.Y * fwd.X) < 30) { sA = d; sU = al; break; }
            }
            if (double.IsNaN(sA)) continue;
            double tA = sA / oms, tU = sU / ums;
            if (Math.Min(tA, tU) > 45 || Math.Abs(tA - tU) > 20) continue;
            var what = o.Phase == "pushback" ? "pushing back" : "crossing";
            // Where, by name, as ground would say it (user, 21 Nov: "Hold short of B on E for AAL123 (B738)"): the
            // taxiway the user is on, and the one the other aircraft crosses on where they'd meet.
            var meet = o.Path.At(o.S + dir * sA);
            var mine = TaxiwayAt(u, UserTrack);
            var theirs = TaxiwayAt(meet.p, (o.Path.At(Math.Min(o.Path.Length, o.S + dir * sA + 10)).p - meet.p).Bearing);
            if (tA <= tU || (Math.Abs(tA - tU) < 15 && sA < sU))
                list.Add(new Notice("gw-" + o.Id, theirs is not null && theirs != mine && o.Phase != "pushback"
                    ? $"Hold short of {theirs}{(mine is not null ? $" on {mine}" : "")} for {o.Callsign} ({o.Type}) crossing from the {side}, about {sU:0} m, in {tA:0} s"
                    : $"Give way: {o.Callsign} ({o.Type}) {what} ahead from the {side}, about {sU:0} m, in {tA:0} s", true));
            else if (o.Phase != "pushback")
                list.Add(new Notice("gw-" + o.Id, $"{o.Callsign} ({o.Type}) from the {side} gives way to you ({sU:0} m ahead)", false));
        }
    }

    /// <summary>The named taxiway at a point (nearest named taxi line within 40 m, preferring one going this way), or null.</summary>
    private string? TaxiwayAt(V2 p, double brg)
    {
        string? best = null; var bd = 40.0;
        for (var i = 0; i < Apt.TaxiPointCount; i++)
        {
            if ((Apt.Nodes[i] - p).Length > 400) continue;
            foreach (var e in Apt.Adj[i])
            {
                if (e.Type != 1 || e.Name.Length == 0 || !AirportModel.IsTaxiway(e.Name) || e.B >= Apt.TaxiPointCount) continue;
                var a = Apt.Nodes[e.A]; var b = Apt.Nodes[e.B];
                var d = DistToSegment(p, a, b);
                var along = Math.Abs(Geo2.Diff((b - a).Bearing, brg)); along = Math.Min(along, 180 - along);
                d += along > 45 ? 15 : 0;
                if (d < bd) { bd = d; best = e.Name; }
            }
        }
        return best;
    }

    /// <summary>
    /// The all-clear, in green (user, 16 Nov: "when the user is given a notice for traffic, a continue taxi or safe to
    /// take off in green"). On the ground, once the taxiway traffic the user was told about has gone (give way, hold
    /// short for a taxiing aircraft, traffic coming the other way, a pushback): "Clear — continue taxi (DAL123 has
    /// passed)" for 12 s. Lined up on a runway, or stopped holding short near its end: when nothing is on it, nothing
    /// within 6 nm on final and nothing crossing it — "Runway 27C clear — safe to take off" / "— safe to line up".
    /// Advice from what LAIA's traffic is doing, not a clearance.
    /// </summary>
    private void AllClear(V2 u, List<Notice> list)
    {
        static bool Taxiway(Notice n) => n.Id.StartsWith("gw-") || n.Id.StartsWith("headon-") || n.Id.StartsWith("tw-") || n.Id.StartsWith("push-") || n.Id.StartsWith("hs-");
        var warns = list.Where(n => n.Warning && Taxiway(n)).ToList();
        if (warns.Count > 0) { _clearUntil = 0; _lastWarn = warns.Select(n => n.Text.Split(' ').FirstOrDefault(w => System.Text.RegularExpressions.Regex.IsMatch(w, @"^[A-Z]{3}\d+[A-Z]?$")) ?? "").Where(c => c.Length > 0).Distinct().ToList(); }
        else if (_lastWarn is not null)
        {
            _clearUntil = Time + 12;
            _clearText = "Clear — continue taxi" + (_lastWarn.Count == 1 ? $" ({_lastWarn[0]} has passed)" : "");
            _lastWarn = null;
        }
        if (Time < _clearUntil && warns.Count == 0) list.Add(new Notice("clear-taxi", _clearText, false, true));
        // The runway: lined up on it, or stopped holding short near its end.
        if (UserGs > 5) return;
        var nm = Geo2.MPerNm;
        foreach (var r in Apt.Ends)
        {
            var along = r.Along(u); var off = Math.Abs(r.Offset(u));
            var lined = off < 30 && along > -60 && along < r.LengthM / 2 && Math.Abs(Geo2.Diff(UserTrack, r.Heading)) < 30;
            var holding = !lined && off >= 30 && off < 110 && along > -150 && along < r.LengthM / 3;
            if (!lined && !holding) continue;
            if (list.Any(n => n.Warning && (n.Id.StartsWith("final-") || n.Id.StartsWith("rwy-") || n.Id.StartsWith("cross-") || n.Id == "hs-" + r.Runway))) return;
            var busy = Flights.Any(o => !o.Done && o.Rwy?.Runway == r.Runway && (
                (o.Kind == "arrival" && !o.OnGround && o.Phase is "final" or "approach" or "flare" && (o.ThrS - o.S) / nm < 6)
                || (o.OnGround && o.Phase is "landing roll" or "takeoff roll" or "lined up" or "lining up")))
                || Flights.Any(o => !o.Done && o.OnGround && o != null && Math.Abs(r.Offset(o.Pos)) < 35 && r.Along(o.Pos) > -60 && r.Along(o.Pos) < r.LengthM + 60 && o.Phase is not ("landing roll" or "takeoff roll"));
            if (busy) return;
            // Facing this end's direction when lined up; holding short, the end nearer to the user.
            if (holding && Apt.Ends.Any(e => e.Runway == r.Runway && e != r && e.Along(u) > -150 && e.Along(u) < along)) continue;
            list.Add(new Notice("clear-rwy-" + r.Runway, lined
                ? $"Runway {r.Ident} clear — nothing on final within 6 nm, safe to take off"
                : $"Runway {r.Ident}{RunwayPair(r)} clear — nothing on final within 6 nm, safe to line up", false, true));
            return;
        }
    }
    private double _clearUntil;
    private string _clearText = "";
    private List<string>? _lastWarn;

    /// <summary>Stopped on the ground: an aircraft pushing back within 80 m of the user (behind or beside them).</summary>
    private void PushNearby(V2 u, List<Notice> list)
    {
        foreach (var o in Flights)
        {
            if (o.Done || o.Phase != "pushback") continue;
            var d = (o.Pos - u).Length;
            if (d > 80) continue;
            var side = Geo2.Diff((o.Pos - u).Bearing, UserTrack);
            var where = Math.Abs(side) < 45 ? "ahead" : Math.Abs(side) > 135 ? "behind you" : side < 0 ? "on your left" : "on your right";
            list.Add(new Notice("push-" + o.Id, $"{o.Callsign} ({o.Type}) pushing back {where}, {d:0} m", true));
        }
    }

    private string RunwayPair(RunwayEnd r)
    {
        var other = Apt.Ends.FirstOrDefault(e => e.Runway == r.Runway && e != r);
        return other is null ? "" : $"/{other.Ident}";
    }
}
