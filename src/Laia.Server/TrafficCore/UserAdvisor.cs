namespace Laia.Server.TrafficCore;

/// <summary>A notice for the user in Smart traffic mode: never an instruction, only something that matters now.</summary>
public sealed record Notice(string Id, string Text, bool Warning, bool Clear = false);

/// <summary>The user's place in the arrival sequence as the traffic manager plans it.</summary>
public sealed record UserSequence(string Runway, int Number, string? AheadCallsign, string? AheadType, string? AheadWhere,
    string? BehindCallsign, string? BehindType, bool Replanned, double EtaSec);

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

    /// <summary>The user as a member of the arrival sequence (never drawn, never moved by LAIA), or null.</summary>
    public Flight? UserArr { get; private set; }
    private double _userSlot = double.NaN, _userOffSince = double.NaN;
    private bool _userReplanned;
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
        if (nm > 80) return false;
        // No plan: within 40 nm, below 15,000 ft above the field and coming closer.
        var toApt = (new V2(0, 0) - u).Bearing;
        return nm < 40 && UserAltFt < Apt.ElevationFt + 15000 && Math.Abs(Geo2.Diff(UserTrack, toApt)) < 100;
    }

    /// <summary>Track miles still to fly to the threshold, roughly: straight in when on the final, otherwise to a point
    /// about 9 nm out on the final and then down it.</summary>
    private double UserTrackNm(RunwayEnd r, V2 u)
    {
        var along = r.Along(u); var off = Math.Abs(r.Offset(u));
        if (along < 0 && along > -15 * Geo2.MPerNm && off < 1.0 * Geo2.MPerNm && Math.Abs(Geo2.Diff(UserTrack, r.Heading)) < 30)
            return -along / Geo2.MPerNm;
        var join = r.Threshold - r.Dir * (9 * Geo2.MPerNm);
        return (u - join).Length / Geo2.MPerNm + 9;
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
        Leg(20, 12, Math.Min(gs, 210)); Leg(12, 6, Math.Min(gs, 180)); Leg(6, 0, vapp + 5);
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
    private void UpdateUserArrival()
    {
        UpdateExpected();
        if (!UserArriving() || UserPos is not { } u)
        {
            UserArr = null; Sequence = null; _userSlot = double.NaN; _seqSig = ""; _userRwyChanges = 0; _userHoldLogged = false; return;
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
        f.EtaSec = UserEtaSec(nm);
        // The planned slot: kept while the user stays within about 90 s of it; a new one when they're going to miss it
        // (for 20 s running), and the aircraft around them re-sequence for the new place.
        var eta = Time + f.EtaSec;
        if (double.IsNaN(_userSlot)) { _userSlot = eta; _userReplanned = false; }
        else if (Math.Abs(eta - _userSlot) > 90)
        {
            if (double.IsNaN(_userOffSince)) _userOffSince = Time;
            else if (Time - _userOffSince > 20) { _userSlot = eta; _userReplanned = true; _userOffSince = double.NaN; }
        }
        else _userOffSince = double.NaN;
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
        var changed = _userReplanned;
        if (_seqSig.Length > 0 && Time - _seqSince > 10)
        {
            bool StillThere(string id) => id.Length > 0 && ordered.Any(o => o.Id == id);
            if (parts[0] != me.Rwy!.Ident) changed = true;
            // Someone put in ahead (the one that was ahead is still coming in, or the user was number 1), or behind.
            if (parts[1] != (ahead?.Id ?? "") && (StillThere(parts[1]) || (parts[1].Length == 0 && ahead is not null))) changed = true;
            if (parts[2] != (behind?.Id ?? "") && StillThere(parts[2])) changed = true;
        }
        if (_seqSig.Length == 0) _seqSince = Time;
        _seqSig = $"{me.Rwy!.Ident}|{ahead?.Id}|{behind?.Id}";
        _userReplanned = false;
        var logNow = changed && Time >= _seqNewUntil;
        if (changed) _seqNewUntil = Time + 30;
        changed |= Time < _seqNewUntil;
        Sequence = new UserSequence(me.Rwy!.Ident, me.Seq, ahead?.Callsign, ahead?.Type, ahead is null ? null : Where(ahead),
            behind?.Callsign, behind?.Type, changed, me.EtaSec);
        if (logNow) Log($"{me.Callsign} (you): new sequence — number {me.Seq} for {me.Rwy.Ident}, between {ahead?.Callsign ?? "nobody"} and {behind?.Callsign ?? "nobody"}");
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
                    (sq.AheadWhere is null ? "" : $" — {sq.AheadCallsign} is at your {sq.AheadWhere}"), sq.Replanned));
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
                    list.Add(new Notice("hs-" + r.Runway, $"Hold short of {r.Ident}{RunwayPair(r)}: {busy.Callsign} ({busy.Type}) {(busy.Kind == "arrival" && !busy.OnGround ? "landing" : busy.Phase == "lined up" ? "lined up to depart" : busy.Phase == "takeoff roll" ? "departing" : "on the runway")}", true));
            }
            GiveWay(u, list);
        }
        else PushNearby(u, list);
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
            if (tA <= tU || (Math.Abs(tA - tU) < 15 && sA < sU))
                list.Add(new Notice("gw-" + o.Id, $"Give way: {o.Callsign} ({o.Type}) {what} ahead from the {side}, about {sU:0} m, in {tA:0} s", true));
            else if (o.Phase != "pushback")
                list.Add(new Notice("gw-" + o.Id, $"{o.Callsign} ({o.Type}) from the {side} gives way to you ({sU:0} m ahead)", false));
        }
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
        if (warns.Count > 0) { _clearUntil = 0; _lastWarn = warns.Select(n => n.Text.Split(' ').FirstOrDefault(w => w.Length > 3 && w.Any(char.IsDigit) && w.All(char.IsLetterOrDigit)) ?? "").Where(c => c.Length > 0).Distinct().ToList(); }
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
