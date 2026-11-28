using Laia.Common.Airports;

namespace Laia.Server.TrafficCore;

/// <summary>A notice for the user in Smart traffic mode: never an instruction, only something that matters now.</summary>
public sealed record Notice(string Id, string Text, bool Warning, bool Clear = false);

/// <summary>The user's place in the arrival sequence as the traffic manager plans it.</summary>
public sealed record UserSequence(string Runway, int Number, string? AheadCallsign, string? AheadType, string? AheadWhere,
    string? BehindCallsign, string? BehindType, bool Replanned, double EtaSec, string? SpeedAdvice = null, bool InSeq = true);

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
    /// <summary>From the last sequencing pass: the earliest the traffic ahead lets the user land (NaN when nobody is
    /// ahead), and whether nobody is ahead of them on their runway.</summary>
    private double _userAheadSlot = double.NaN;
    private bool _userNoneAhead;
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
        // Over the runway, landing: there (27 Nov, KSFO: 10 s before touchdown the user's estimate went round a whole
        // new way in — "you're 8.6 min later than it would land").
        if (along >= 0 && along < r.LengthM && off < 0.5 * Geo2.MPerNm && Math.Abs(Geo2.Diff(UserTrack, r.Heading)) < 30)
            return 0;
        var routed = UserRouteNm(r, u);
        if (!double.IsNaN(routed)) return routed;
        var join = r.Threshold - r.Dir * (11 * Geo2.MPerNm);
        return (u - join).Length / Geo2.MPerNm + 11;
    }

    private readonly Dictionary<RunwayEnd, (double at, double nm)> _userRoute = new();
    /// <summary>The published speed limits on the user's way in: (flying miles to go at the fix, knots), farthest first.</summary>
    private readonly Dictionary<RunwayEnd, List<(double nm, double kt)>> _userLimits = new();
    private List<(double nm, double kt)> _routeLimits = new();
    private List<AirPoint> _lastUserStarPts = new();
    private bool _userInSeq;

    /// <summary>The speed limits along a route the user flies (points with a published speed), as miles to go.</summary>
    private static List<(double nm, double kt)> LimitsAlong(List<V2> pts, List<AirPoint> star, V2 thr)
    {
        var res = new List<(double nm, double kt)>();
        var cum = new double[pts.Count];
        for (var i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + (pts[i] - pts[i - 1]).Length;
        var total = pts.Count == 0 ? 0 : cum[^1] + (pts[^1] - thr).Length;
        foreach (var a in star)
        {
            if (a.SpeedKt < 100 || a.SpeedKt >= 400) continue;
            var k = pts.FindIndex(q => (q - a.P).Length < 5);
            if (k > 0) res.Add(((total - cum[k]) / Geo2.MPerNm, a.SpeedKt));
        }
        return res.OrderByDescending(x => x.nm).ToList();
    }

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
        _routeLimits = new();
        try { nmOut = RouteNm(r, u); } catch { nmOut = double.NaN; }
        _userRoute[r] = (Time, nmOut);
        _userLimits[r] = double.IsNaN(nmOut) ? new() : _routeLimits;
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
            _lastUserStarPts = spA;
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
                    _routeLimits = LimitsAlong(pts, spA, r.Threshold);
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
        if (star is not null) _routeLimits = LimitsAlong(pts, _lastUserStarPts, r.Threshold);
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
    private double UserEtaSec(double nm) => UserEtaSec(nm, 1.0);

    /// <summary>
    /// The same, with the user's speed beyond 20 nm scaled (a speed to suggest: 0.95 is 5 % slower), and the published
    /// speed limits on their way in flown where they are lower (user, 24 Nov: "see if the restrictions on the STAR are
    /// enough" — the STAR's "max 250" or "at 230" spaces arrivals by itself, so it's counted before any advice).
    /// </summary>
    private double UserEtaSec(double nm, double scale)
    {
        var vapp = Perf.For(UserType.Length > 0 ? UserType : "A320").Vapp;
        var gs = Math.Clamp(UserGs > 60 ? UserGs : 300, 180, 650);
        if (UserAltFt < 11000) gs = Math.Min(gs, 260);
        var rwy = UserArr?.Rwy ?? _expected;
        var elev = rwy?.ElevationFt ?? Apt.ElevationFt;
        var limits = rwy is not null && _userLimits.TryGetValue(rwy, out var ul) ? ul : null;
        // The user's own approach, as they fly it (user, 17 Nov): 210 kt indicated through the base turn onto a 10–12 mile
        // final, 190 once established, 170 from 7.5 to 5 miles, the approach speed inside 5 — each turned into ground
        // speed at its height in the wind there (the final's headwind; the base is across the wind, so no wind).
        double Final(double ias, double nmOut)
        {
            var alt = elev + nmOut * 318;   // a 3° path
            var tas = Wind.Tas(ias, alt);
            var g = rwy is null ? tas : tas - WindAt(alt).Headwind(rwy.Heading);
            // Never faster than they're flying now (a light aircraft at 110 kt doesn't do 190 on final), nor slower than
            // 70 % of it (its approach speed may not be the one assumed for the type).
            return UserGs > 60 ? Math.Clamp(g, UserGs * 0.7, UserGs) : g;
        }
        var f20 = Math.Min(UserGs > 60 ? UserGs : gs, Wind.Tas(210, elev + 4000));
        double Base(double d) => d > 100 ? gs * scale : d > 40 ? Math.Min(gs * scale, 330) : d > 20 ? Math.Min(gs * scale, 260)
            : d > 11 ? f20 : d > 7.5 ? Final(190, 9.25) : d > 5 ? Final(170, 6.25) : Final(vapp + 5, 2.5);
        // A published limit, as the flight model meets one: at it 1½ nm before the fix (slowing about 5 kt a mile), and
        // the last one passed holds until the next.
        double Limit(double d)
        {
            if (limits is null || limits.Count == 0) return double.PositiveInfinity;
            var lim = double.PositiveInfinity; double passed = double.NaN; var anyAhead = false; var passedNm = 0.0;
            foreach (var (ln, kt) in limits)
            {
                if (ln < d) { anyAhead = true; lim = Math.Min(lim, kt + Math.Max(0, d - ln - 1.5) * 5); }
                else { passed = kt; passedNm = ln; }
            }
            if (!double.IsNaN(passed))
            {
                if (anyAhead) lim = Math.Min(lim, Math.Max(passed, limits.Where(x => x.nm < d).Min(x => x.kt)));
                else if (passedNm - d < 10) lim = Math.Min(lim, passed);
            }
            return lim;
        }
        double t = 0;
        const double step = 0.5;
        for (var d = nm; d > 0; d -= step)
        {
            var v = Base(d);
            var lim = Limit(d);
            if (!double.IsInfinity(lim) && d > 5) v = Math.Min(v, Wind.Tas(lim, Math.Min(UserAltFt, elev + d * 318)));
            t += Math.Min(step, d) / Math.Max(90, v) * 3600;
        }
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
            if (UserArr is not null) foreach (var o in Flights) o.BehindUser = false;
            _userInSeq = false;
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
            // Nobody ahead and running early: the held place follows the user smoothly (nothing to keep it for, and the
            // traffic behind should plan on when the user will really land).
            if (_userNoneAhead && diff < -15)
            {
                var step = Math.Min(1, (Time - _userSlotAt) / 45);
                _userSlot += (eta - _userSlot) * step; _userSlotRaw = rawAbs; _userOffSince = double.NaN;
            }
            else if (Math.Abs(diff) <= limit) _userOffSince = double.NaN;
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
        // Something to fly to, worked out rather than guessed (user, 24 Nov: "LAIA is literally a computer... can't it
        // calculate based on the speeds and distances of the aircraft and see if speeds are even needed"): both aircraft
        // predicted to the threshold along their flying miles at the speeds each will fly — the published limits on the
        // way in, 250 kt below 10,000 ft, the approach, the winds — and the gap that gives compared with the gap needed.
        // Enough already (or more than enough: the traffic behind adjusts, not the user): no speed. Too close: the
        // smallest change beyond 20 nm that makes it, and only when it's 10 kt or more — as a Mach number at cruise (above
        // about FL280), with the speed to fly in the descent after it.
        string? advice = null;
        var toGoNm = me.ThrS / Geo2.MPerNm;
        // Before the arrival — beyond 150 flying miles, above FL240 and not yet descending — the landing order is only
        // shown, not flown (user, 25 Nov: "separation should be between me and the aircraft around me before my sequence
        // to the runway"): the aircraft around the user are kept apart en route (and the user asked in the notices when one
        // catches them up); any delay for the runway is said once as minutes to expect, absorbed later.
        // The same point LAIA's own arrivals join the landing sequence (user, 26 Nov): within 40 nm of the runway's
        // approach end and at or below 8,000 ft above the field — kept once reached.
        var dThr = me.Rwy is { } mr ? (u - mr.Threshold).Length / Geo2.MPerNm : double.PositiveInfinity;
        if (dThr <= 40 && UserAltFt <= Apt.ElevationFt + 8050) _userInSeq = true;
        var onArrival = _userInSeq;
        // Delay is what the traffic ahead imposes: the earliest it lets the user land against when they would land on
        // their own — none when nobody is ahead (user, 27 Nov, KPIT: "expect about 8 min of delay for 28L" as number 1
        // with nobody ahead: it was the held place in the sequence against a newer, earlier estimate).
        var delayMin = _userNoneAhead || double.IsNaN(_userAheadSlot) || double.IsNaN(me.EtaSec) ? 0 : Math.Max(0, (_userAheadSlot - (Time + me.EtaSec)) / 60);
        if (!onArrival)
        {
            // Spacing in the user's stream: the nearest of LAIA's arrivals ahead on the same STAR to the same runway.
            var star = UserStar ?? "";
            var ahead0 = Flights.Where(o => o.Kind == "arrival" && !o.Done && !o.OnGround && o.Path is not null && o.Rwy?.Runway == me.Rwy?.Runway
                    && star.Length > 0 && string.Equals(o.StarPick?.p.Name, star, StringComparison.OrdinalIgnoreCase) && (o.ThrS - o.S) / Geo2.MPerNm < toGoNm)
                .OrderByDescending(o => (o.ThrS - o.S)).FirstOrDefault();
            var bits = new List<string>();
            if (ahead0 is not null)
            {
                var gapNm = toGoNm - (ahead0.ThrS - ahead0.S) / Geo2.MPerNm;
                var mit = me.Rwy is { } rr && _mit.TryGetValue(rr.Runway, out var mt) && mt.nm > 0 ? mt.nm : 0;
                bits.Add($"spacing: {gapNm:0} nm behind {ahead0.Callsign} ({ahead0.Type}) on {star}" + (mit > 0 ? $" — {mit:0} nm in trail asked into {Apt.Icao}{(gapNm < mit - 1 ? $", about {Math.Ceiling((mit - gapNm) * 3600 / Math.Max(200, UserGs) / 60):0} min to lose" : "")}" : ""));
            }
            if (delayMin >= 2) bits.Add($"expect about {Math.Round(delayMin):0} min of delay for {me.Rwy?.Ident} — LAIA will take it in the descent and on the arrival");
            advice = bits.Count == 0 ? null : string.Join("; ", bits);
        }
        else if (ahead is not null && !double.IsNaN(ahead.EtaSec) && !double.IsNaN(me.EtaSec) && me.EtaSec - ahead.EtaSec < 0)
        {
            // Predicted to land before the one it's sequenced behind (it's held up): no slowing to get behind it —
            // the order is put right (UserAmongArrivals).
            advice = $"you'll reach the runway before {ahead.Callsign} — no speed change needed";
        }
        else if (ahead is not null && !double.IsNaN(ahead.EtaSec) && !double.IsNaN(me.EtaSec) && toGoNm > 8 && me.EtaSec > 120)
        {
            var need = ThresholdGapNeeded(ahead, me, forDepartures: false);
            var gapNow = me.EtaSec - ahead.EtaSec;
            var err = need - gapNow;          // > 0: too close by that many seconds
            var aheadNm = (ahead.Pos - u).Length / Geo2.MPerNm;
            if (err <= 15)
                advice = $"no speed change needed — at the published speeds you'll be about {Math.Round(gapNow / 60 * 2) / 2:0.#} min behind {ahead.Callsign} at the runway ({need / 60:0.#} needed)";
            else if (toGoNm > 25)
            {
                // The scale on the user's speed beyond 20 nm that loses err seconds (found by halving: the prediction
                // flies the limits, so slowing below a limit gains nothing there).
                var baseT = UserEtaSec(toGoNm, 1.0);
                // Never slower than a realistic speed (25 Nov: Mach .64 was asked at FL320): at most Mach .04 (about 8 %)
                // or 40 kt below what they're flying; the rest is LAIA's (the downwind, a vector).
                var tasNow0 = Math.Max(150, UserGs + WindAt(UserAltFt).Headwind(UserTrack));
                var minScale = UserAltFt >= 28000 ? 0.92 : Math.Max(0.8, 1 - 40 / Math.Max(150, tasNow0 / Wind.Tas(1, UserAltFt)));
                double lo = minScale, hi = 1.0;
                for (var k = 0; k < 14; k++) { var mid = (lo + hi) / 2; if (UserEtaSec(toGoNm, mid) - baseT >= err) lo = mid; else hi = mid; }
                var scale = lo;
                var enough = UserEtaSec(toGoNm, scale) - baseT >= err - 5;
                var tasNow = Math.Max(150, UserGs + WindAt(UserAltFt).Headwind(UserTrack));
                if (UserAltFt >= 28000)
                {
                    // Mach: the speed of sound at the level (ISA) — about 574 kt TAS at FL350.
                    var tK = Math.Max(216.65, 288.15 - 0.0019812 * UserAltFt);
                    var a = 38.967854 * Math.Sqrt(tK);
                    var mNow = tasNow / a; var mNew = Math.Max(Math.Round(mNow * scale, 2), Math.Min(Math.Round(mNow, 2), 0.74));
                    var desc = Math.Max(250, Math.Round(280 * scale / 10) * 10);
                    if (mNow - mNew >= 0.015)
                        advice = $"Mach {mNew:.00}, then {desc:0} kt in the descent — keeps your spacing behind {ahead.Callsign} ({aheadNm:0} nm, {(gapNow / 60):0.#} min ahead at the runway, {need / 60:0.#} needed)" + (enough ? "" : " — as slow as it's worth going; LAIA spaces the rest");
                    else advice = $"no speed change needed — {ahead.Callsign} will be about {gapNow / 60:0.#} min ahead at the runway ({need / 60:0.#} needed)";
                }
                else
                {
                    var iasNow = tasNow / Wind.Tas(1, UserAltFt);
                    var kt = Math.Round(Math.Max(me.P.Vapp + 20, iasNow * scale) / 10) * 10;
                    if (UserAltFt < 10500) kt = Math.Min(kt, 250);
                    if (iasNow - kt >= 10)
                        advice = $"about {kt:0} kt keeps your spacing behind {ahead.Callsign} ({aheadNm:0} nm, {gapNow / 60:0.#} min ahead at the runway, {need / 60:0.#} needed)" + (enough ? "" : " — as slow as it's worth going; LAIA spaces the rest");
                    else advice = $"no speed change needed — {ahead.Callsign} will be about {gapNow / 60:0.#} min ahead at the runway ({need / 60:0.#} needed)";
                }
            }
        }
        Sequence = new UserSequence(me.Rwy!.Ident, me.Seq, ahead?.Callsign, ahead?.Type, ahead is null || !onArrival ? null : Where(ahead),
            behind?.Callsign, behind?.Type, changed && onArrival, me.EtaSec, advice, onArrival);
        if (logNow && onArrival) Log($"{me.Callsign} (you): new sequence — number {me.Seq} for {me.Rwy.Ident}, between {ahead?.Callsign ?? "nobody"} and {behind?.Callsign ?? "nobody"}");
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
        // A runway change, for 10 minutes (plan, Phase 4: notes with lead time): which way the airport is turning and
        // where that leaves the user — after the change (their new runway and approach) or before it (keeping theirs).
        if (_changeNote is { } cn && Time - cn.at < 600 && (u.Length < 80 * Geo2.MPerNm || UserArr is not null))
        {
            var head = $"{Apt.Icao} is changing to land {cn.land} / depart {cn.dep}";
            string tail;
            if (!UserOnGround && UserArr?.Rwy is { } ur)
                tail = ArrRwys.Contains(ur)
                    ? $" — you're after the change: expect the {(ApproachFor(ur) is { } ap ? ApproachLabel(ap) : $"vectors to {ur.Ident}")}"
                    : $" — you're before it: you keep runway {ur.Ident}{(cn.lastOld is null ? "" : $"; the last one that way is {cn.lastOld}")}";
            else if (UserOnGround && DepRwys.Count > 0)
                tail = $" — expect runway {DepRwys.OrderBy(r => (r.Threshold - u).Length).First().Ident} for departure now";
            else tail = "";
            list.Add(new Notice("rwychg", head + tail, false));
        }
        if (!UserOnGround)
        {
            if (Sequence is { InSeq: false } sq0)
                list.Add(new Notice("seq", $"Landing {sq0.Runway} — the landing sequence starts within 40 nm and 8,000 ft of the field" +
                    (sq0.SpeedAdvice is null ? "" : $". {char.ToUpper(sq0.SpeedAdvice[0])}{sq0.SpeedAdvice[1..]}"), false));
            else if (Sequence is { } sq)
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
                else if (o.OnGround && same && o.Phase is "landing roll" or "takeoff roll" or "lined up" && UserGs > 5)   // stopped at it: UserRunway says what to do
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
        foreach (var r in Apt.Ends)
        {
            var along = r.Along(u); var off = Math.Abs(r.Offset(u));
            var lined = off < r.WidthM / 2 + 10 && along > -60 && along < r.LengthM / 2 && Math.Abs(Geo2.Diff(UserTrack, r.Heading)) < 30;
            var holding = !lined && off >= r.WidthM / 2 + 10 && off < 110 && along > -150 && along < r.LengthM / 3;
            if (!lined && !holding) continue;
            // Holding short: the end nearer to the user is the one they'd depart from.
            if (holding && Apt.Ends.Any(e => e.Runway == r.Runway && e != r && e.Along(u) > -150 && e.Along(u) < along)) continue;
            if (UserRunway(r, u, lined) is { } n) list.Add(n);
            return;
        }
    }

    /// <summary>
    /// The user at a runway to depart (user, 26 Nov: "it seems to think traffic has cleared when it hasn't, and never asks
    /// the user to line up and wait after a departure, only hold short"): judged by the same rules as LAIA's own
    /// departures — an aircraft on the runway, the wake and 6,000 ft behind the departure ahead (3-9-6), the next arrival
    /// (airborne and 6,000 ft down before it crosses the threshold, 3-10-3), a go-around over the runway, arrivals to a
    /// crossing runway (3-9-8) — and line up and wait behind a departure on its roll, or a landing rolling out with safety
    /// logic in fair weather (3-9-4), when the user is first in line. Advice from LAIA's traffic, not a clearance.
    /// </summary>
    private Notice? UserRunway(RunwayEnd r, V2 u, bool lined)
    {
        var type = UserType.Length > 0 ? UserType : "A320";
        var me = new Flight
        {
            Id = "user", Callsign = (UserCallsign.Length > 0 ? UserCallsign : "you") + " (you)", Airline = "", Type = type, Kind = "departure", P = Perf.For(type),
            Rwy = r, Pos = u, Phase = lined ? "lined up" : "holding short", DepCourse = r.Heading, DepPlanRwy = r,
        };
        var mineAlong = r.Along(u);
        // First in line: none of LAIA's departures for this runway lined up, lining up, or holding short before the user.
        if (!lined && Flights.FirstOrDefault(o => o.Kind == "departure" && !o.Done && o.Rwy?.Runway == r.Runway && o.Phase is "lined up" or "lining up") is { } lu)
            return new Notice("rwy-user", $"Hold short of {r.Ident}: {lu.Callsign} ({lu.Type}) {(lu.Phase == "lined up" ? "lined up" : "lining up")} ahead of you", true);
        // Whoever is on the runway, and whether the user could line up behind them.
        Flight? blocker = null, rolling = null;
        foreach (var o in Flights)
        {
            if (o.Done || o == me) continue;
            var onIt = o.OnRunway == r.Runway || o.Reserve == r.Runway;
            if (!onIt || (o.Kind == "departure" && !o.OnGround)) continue;
            var ahead = r.Along(o.Pos) > mineAlong + 300 && Math.Abs(Geo2.Diff(o.Rwy?.Heading ?? o.Heading, r.Heading)) < 90;
            var canBehind = ahead && ((o.Kind == "departure" && o.Phase == "takeoff roll") || (o.Kind == "arrival" && o.Phase == "landing roll" && SafetyLogic && !LowWeather));
            if (canBehind) rolling ??= o; else blocker ??= o;
        }
        var nm = Geo2.MPerNm;
        var eta = ArrivalEta(r.Runway);
        string etaTxt() => double.IsInfinity(eta) ? "no arrival due" : $"next arrival {eta:0} s out";
        if (blocker is not null)
            return new Notice("rwy-user", $"{(lined ? "Wait on the runway" : $"Hold short of {r.Ident}")}: {blocker.Callsign} ({blocker.Type}) {(blocker.Kind == "arrival" ? (blocker.OnGround ? "on the runway" : "landing") : "on the runway")}", true);
        if (GoAroundAhead(me) is { } ga)
            return new Notice("rwy-user", $"{(lined ? "Wait on the runway" : $"Hold short of {r.Ident}")}: {ga.Callsign} going around over the runway", true);
        var clearFor = TimeToClearForArrival(me);
        var wake = rolling is not null ? double.PositiveInfinity : WakeRemaining(me, plan: false);
        var crossingOk = CrossingRunwayClearForTakeoff(me);
        var lineUpSec = lined ? 0 : Math.Max(15, (off(u) / 4.0) + 7);
        // Take-off now: nobody on it, the wake and 6,000 ft done, the next arrival far enough, the crossing runway clear.
        if (rolling is null && wake <= 0 && eta >= clearFor + lineUpSec + 5 && crossingOk)
            return new Notice("rwy-user", lined
                ? $"Runway {r.Ident} clear — safe to take off ({etaTxt()})"
                : $"Runway {r.Ident}{RunwayPair(r)} clear — safe to line up and take off ({etaTxt()})", false, true);
        // Line up and wait: behind a departure on its roll (or a landing rolling out), or while the wake time runs —
        // with room before the next arrival for the line-up, the wait and the take-off.
        var wait = rolling is not null ? Math.Max(30, WakeAfterRoll(rolling, me)) : Math.Max(0, wake);
        if (!lined && eta >= Math.Max(lineUpSec, wait) + clearFor + 10 && CrossingRunwayClearForTakeoff(me, extraSec: 30))
        {
            var who = rolling ?? Flights.Where(o => o.Kind == "departure" && !o.Done && o.Rwy?.Runway == r.Runway && !o.OnGround).OrderByDescending(o => o.LiftoffTime).FirstOrDefault();
            return new Notice("rwy-user", $"Line up and wait on {r.Ident}{(who is null ? "" : $" behind {who.Callsign} ({who.Type}) {(who.Kind == "arrival" ? "rolling out" : who.OnGround ? "departing" : "just airborne")}")} — take-off in about {Math.Ceiling(wait / 10) * 10:0} s", false, true);
        }
        // Not yet: why.
        string why = eta < clearFor + lineUpSec + 5 ? $"arrival {eta:0} s out" : !crossingOk ? "traffic for the crossing runway" : wait > 0 ? $"wake and spacing behind the departure ahead, about {Math.Ceiling(wait / 10) * 10:0} s" : "traffic";
        return new Notice("rwy-user", $"{(lined ? "Wait on the runway" : $"Hold short of {r.Ident}")}: {why}", true);

        double off(V2 p) => Math.Abs(r.Offset(p));
    }

    /// <summary>Seconds until the user could roll behind a departure still on its roll: its lift-off, then the wake time.</summary>
    private double WakeAfterRoll(Flight l, Flight me)
    {
        if (l.Kind != "departure") return 30;
        var acc = Math.Max(0.5, l.P.RollAccel * 0.5144);
        var toLift = Math.Max(0, ((l.P.Vr + 7) * 0.5144 - l.Gs * 0.5144) / acc);
        return toLift + Cwt.DepartureSeconds(l.Wake, me.Wake);
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
