namespace Laia.Server.TrafficCore;

/// <summary>A notice for the user in Smart traffic mode: never an instruction, only something that matters now.</summary>
public sealed record Notice(string Id, string Text, bool Warning);

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
        if (nm > 80) return false;
        if (UserDestination is { Length: > 0 } d) return d.Equals(Apt.Icao, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Seconds to the threshold at about the speeds the user will fly: their speed now far out (at most 250 kt
    /// below 10,000 ft), 210 kt inside 20 nm, 180 inside 12, the approach speed inside 6.</summary>
    private double UserEtaSec(double nm)
    {
        var vapp = Perf.For(UserType.Length > 0 ? UserType : "A320").Vapp;
        double t = 0, d = nm;
        void Leg(double from, double to, double kt) { var len = Math.Max(0, Math.Min(d, from) - to); if (len > 0) t += len / Math.Max(90, kt) * 3600; }
        var far = Math.Clamp(UserGs > 60 ? UserGs : 250, 180, UserAltFt < 11000 ? 250 : 320);
        Leg(double.MaxValue, 20, far); Leg(20, 12, Math.Min(far, 210)); Leg(12, 6, 180); Leg(6, 0, vapp + 5);
        return t;
    }

    /// <summary>Called every step before the sequencing: keeps the user's place in the arrival sequence.</summary>
    private void UpdateUserArrival()
    {
        if (!UserArriving() || UserPos is not { } u)
        {
            UserArr = null; Sequence = null; _userSlot = double.NaN; _seqSig = ""; return;
        }
        var arrs = ArrRwys.Count > 0 ? ArrRwys : ArrRwy is null ? new List<RunwayEnd>() : new() { ArrRwy };
        if (arrs.Count == 0) return;
        var f = UserArr ??= new Flight
        {
            Id = "user", Callsign = UserCallsign.Length > 0 ? UserCallsign : "you", Airline = "", Type = UserType.Length > 0 ? UserType : "A320",
            Kind = "arrival", P = Perf.For(UserType.Length > 0 ? UserType : "A320"),
        };
        // The runway: the one it already has while it's still in use, else the one on the user's side of the field.
        if (f.Rwy is null || !arrs.Contains(f.Rwy))
            f.Rwy = arrs.OrderBy(r => UserTrackNm(r, u)).First();
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
        var sig = $"{me.Rwy!.Ident}|{me.Seq}|{ahead?.Id}|{behind?.Id}";
        var changed = _userReplanned || (_seqSig.Length > 0 && sig != _seqSig && Time - _seqSince > 10);
        if (_seqSig.Length == 0) _seqSince = Time;
        _seqSig = sig;
        _userReplanned = false;
        if (changed) _seqNewUntil = Time + 30;
        changed |= Time < _seqNewUntil;
        Sequence = new UserSequence(me.Rwy!.Ident, me.Seq, ahead?.Callsign, ahead?.Type, ahead is null ? null : Where(ahead),
            behind?.Callsign, behind?.Type, changed, me.EtaSec);
        if (changed && Time >= _seqNewUntil - 0.6) Log($"{me.Callsign} (you): new sequence — number {me.Seq} for {me.Rwy.Ident}, between {ahead?.Callsign ?? "nobody"} and {behind?.Callsign ?? "nobody"}");
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
            foreach (var o in Flights)
            {
                if (o.Done || !o.OnGround || o.Gs < 3 || o.Phase is "landing roll" or "takeoff roll") continue;
                var v = o.Pos - u;
                if (v.Length > 180 || v.Length < 30) continue;
                var bearing = Geo2.Diff(v.Bearing, UserTrack);
                var crossing = Math.Abs(Math.Sin((o.Heading - UserTrack) * Math.PI / 180)) > 0.6;
                if (Math.Abs(bearing) < 70 && crossing)
                    list.Add(new Notice("tw-" + o.Id, $"Hold short: {o.Callsign} ({o.Type}) taxiing across from the {(bearing < 0 ? "left" : "right")}", true));
            }
        }
        // Traffic waiting for the user.
        foreach (var o in Flights.Where(o => !o.Done && o.OnGround && o.BlockedByUser && o.BlockedFor > 5))
            list.Add(new Notice("wait-" + o.Id, $"{o.Callsign} ({o.Type}) is {(o.Phase == "boarding" || o.Phase == "pushback" ? "waiting to push back" : "holding")} for you", false));
        Notices = list.GroupBy(n => n.Id).Select(g => g.First()).Take(4).ToList();
    }

    private string RunwayPair(RunwayEnd r)
    {
        var other = Apt.Ends.FirstOrDefault(e => e.Runway == r.Runway && e != r);
        return other is null ? "" : $"/{other.Ident}";
    }
}
