namespace Laia.Server.TrafficCore;

/// <summary>
/// How an aircraft type performs, grouped into families (exact numbers vary with weight; these are
/// typical airline values). Speeds are indicated airspeed in knots.
/// </summary>
public sealed record Perf(
    string Family,
    double Vr,            // rotation
    double V2,            // takeoff safety speed (initial climb)
    double RollAccel,     // kt per second on the takeoff roll
    double ClimbFpm,      // initial climb rate
    double Vapp,          // final approach speed
    double MinClean,      // slowest speed flown clean (before flaps)
    double SpanM,
    double LengthM,
    int GateSize,         // 0 small, 1 medium, 2 heavy
    bool Heavy,           // wake turbulence category heavy (more spacing behind it)
    double CgHeightFt)    // not used for placement; reserved
{
    public static readonly Perf Turboprop = new("turboprop", 105, 115, 3.0, 1600, 115, 170, 27, 25, 0, false, 8);
    public static readonly Perf RegionalJet = new("regional jet", 135, 145, 3.6, 2400, 130, 190, 23, 30, 0, false, 8);
    public static readonly Perf LargeRegional = new("large regional", 138, 148, 3.8, 2500, 133, 200, 29, 36, 1, false, 9);
    public static readonly Perf Narrowbody = new("narrowbody", 148, 158, 4.0, 2500, 140, 210, 36, 40, 1, false, 10);
    public static readonly Perf B757 = new("757", 145, 155, 4.3, 3000, 135, 210, 38, 47, 1, false, 11);
    public static readonly Perf Widebody = new("widebody", 155, 165, 3.6, 2200, 142, 220, 60, 60, 2, true, 14);
    public static readonly Perf Jumbo = new("heavy", 165, 175, 3.3, 2000, 152, 230, 68, 72, 2, true, 16);

    public static Perf For(string type)
    {
        var t = (type ?? "").ToUpperInvariant();
        if (t is "DH8A" or "DH8B" or "DH8C" or "DH8D" or "AT43" or "AT45" or "AT72" or "AT75" or "AT76" or "SF34" or "E120" or "B190" or "JS41" or "SW4" or "C208") return Turboprop;
        if (t is "CRJ1" or "CRJ2" or "E135" or "E145" or "E35L" or "E140") return RegionalJet;
        if (t is "CRJ7" or "CRJ9" or "CRJX" or "E170" or "E175" or "E75L" or "E75S" or "E190" or "E195" or "E290" or "E295" or "BCS1" or "BCS3" or "A220" or "A221" or "A223") return LargeRegional;
        if (t is "B752" or "B753") return B757;
        if (t.StartsWith("B74") || t.StartsWith("B77") || t is "A388" or "A346" or "A345" or "A35K" or "B78X" or "MD11" or "A124" or "A225") return Jumbo;
        if (t.StartsWith("A33") || t.StartsWith("A34") || t.StartsWith("A35") || t.StartsWith("B76") || t.StartsWith("B78") || t.StartsWith("A30") || t is "A310" or "DC10" or "L101" or "IL96") return Widebody;
        return Narrowbody; // A318-A321(neo), B737 family, MD80/90, B717, etc.
    }

    /// <summary>Runway length this family is happy with (metres), so heavies don't get sent to short runways.</summary>
    public double MinRunwayM => Family switch { "turboprop" => 1200, "regional jet" => 1500, "large regional" => 1700, "heavy" => 2800, "widebody" => 2500, _ => 1900 };
}

/// <summary>Wind and airspeed conversions. Wind direction is where it blows FROM (true degrees).</summary>
public readonly record struct Wind(double FromDeg, double Kt)
{
    /// <summary>Wind velocity vector in knots (pointing where the air moves to).</summary>
    public V2 Vector => V2.FromBearing(FromDeg + 180, Kt);

    /// <summary>True airspeed from indicated: about 2% more per 1,000 ft (standard atmosphere approximation).</summary>
    public static double Tas(double iasKt, double altFt) => iasKt * (1 + Math.Max(0, altFt) / 1000 * 0.02);

    /// <summary>
    /// For a desired ground track and a true airspeed: the heading to fly (crabbing into the wind) and the
    /// resulting ground speed.
    /// </summary>
    public (double heading, double groundSpeed) Solve(double trackDeg, double tasKt)
    {
        var w = Vector;
        var along = V2.FromBearing(trackDeg);
        var tail = V2.Dot(w, along);                 // tailwind component (+) along the track
        var cross = V2.Cross(along, w);              // wind from the left pushes right: positive = wind toward the left
        var s = Math.Clamp(-cross / Math.Max(1, tasKt), -0.7, 0.7);
        var wca = Math.Asin(s) * 180 / Math.PI;      // heading correction into the wind
        var gs = Math.Sqrt(Math.Max(0, tasKt * tasKt - cross * cross)) + tail;
        return (Geo2.Norm(trackDeg - wca), Math.Max(30, gs));
    }

    /// <summary>Headwind component (+) along a heading.</summary>
    public double Headwind(double headingDeg) => -V2.Dot(Vector, V2.FromBearing(headingDeg));
}
