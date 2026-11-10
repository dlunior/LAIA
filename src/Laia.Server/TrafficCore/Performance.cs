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

/// <summary>
/// FAA Consolidated Wake Turbulence (CWT) categories A–I (JO 7110.126A, used by 7110.65 5-5-4 and 3-9-6):
/// A = A380, B = upper heavy (747, 777, A330/A340/A350, 787), C = lower heavy (767, MD-11, A300/A310),
/// D = non-pairwise heavy, E = 757, F = upper large (A320 family, 737, A220, MD-80/90, 717), G = lower large
/// (regional jets, Dash 8-400, ATR 72), H = upper small (15,400–41,000 lb), I = lower small.
/// </summary>
public static class Cwt
{
    public static char For(string type, Perf p)
    {
        var t = (type ?? "").ToUpperInvariant();
        if (t is "A388" or "A225") return 'A';
        if (t.StartsWith("B74") || t.StartsWith("B77") || t.StartsWith("A33") || t.StartsWith("A34") || t.StartsWith("A35") || t.StartsWith("B78") || t is "A124" or "C5M" or "IL96" or "A3ST") return 'B';
        if (t.StartsWith("B76") || t is "MD11" or "DC10" or "A306" or "A30B" or "A310" or "IL76" or "K35R") return 'C';
        if (t is "B752" or "B753") return 'E';
        if (t.StartsWith("A31") || t.StartsWith("A32") || t is "A19N" or "A20N" or "A21N" || t.StartsWith("B73") || t is "B37M" or "B38M" or "B39M" or "B3XM" or "B712"
            || t.StartsWith("MD8") || t == "MD90" || t is "BCS1" or "BCS3" or "C130" or "B721" or "B722") return 'F';
        if (t is "SF34" or "AT43" or "AT45" or "AT46" or "B190" or "JS41" or "E120" or "DH8A" or "DH8B" or "CL30" or "CL35" or "C25B" or "C25C" or "C560" or "C56X" or "LJ45" or "LJ60") return 'H';
        if (t is "C208" or "PC12" or "BE20" or "BE9L" or "C172" or "C182" or "PA28" or "SR22" or "BE58" or "C510" or "E50P") return 'I';
        return p.Family switch
        {
            "heavy" or "widebody" => 'B',
            "757" => 'E',
            "narrowbody" => 'F',
            _ => 'G',
        };
    }

    // Distance behind (nm), leader row, follower column A..I; 0 = the 3 nm radar minimum (7110.65 5-5-4, TBL 5-5-1).
    private static readonly double[,] InTrail =
    {
        /* A */ { 0, 4.5, 6, 6, 7, 7, 7, 7, 8 },
        /* B */ { 0, 3, 4, 4, 5, 5, 5, 5, 5 },
        /* C */ { 0, 0, 0, 0, 3.5, 3.5, 3.5, 3.5, 5 },
        /* D */ { 0, 3, 4, 4, 5, 5, 5, 5, 5 },
        /* E */ { 0, 0, 0, 0, 0, 0, 0, 0, 4 },
        /* F */ { 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        /* G */ { 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        /* H */ { 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        /* I */ { 0, 0, 0, 0, 0, 0, 0, 0, 0 },
    };

    /// <summary>Minimum distance on final behind the leader (nm): the wake minimum, or 3 nm radar separation.</summary>
    public static double MilesBehind(char leader, char follower)
    {
        var v = InTrail[Math.Clamp(leader - 'A', 0, 8), Math.Clamp(follower - 'A', 0, 8)];
        return Math.Max(3, v);
    }

    /// <summary>Wake time between departures from the same runway (seconds after the leader lifts off), 7110.65 3-9-6.</summary>
    public static double DepartureSeconds(char leader, char follower)
    {
        if (leader == 'A' && follower != 'A') return 180;
        if (leader is 'B' or 'D' && follower >= 'B') return 120;
        if (leader == 'C' && follower >= 'E') return 120;
        if (leader == 'E' && follower == 'I') return 120;
        return 0;
    }
}

/// <summary>Where the speed rules of 14 CFR 91.117 apply differently.</summary>
public static class Airspace
{
    // US Class B primary airports (91.117(c): the 200-knot limit near the airport doesn't apply in Class B).
    private static readonly HashSet<string> ClassB = new(StringComparer.OrdinalIgnoreCase)
    {
        "KATL","KBOS","KBWI","KCLT","KCVG","KDCA","KIAD","KADW","KDEN","KDFW","KDTW","KEWR","KJFK","KLGA","KHOU","KIAH","KLAS","KLAX",
        "KMCI","KMCO","KMEM","KMIA","KMSP","KMSY","KORD","KPHL","KPHX","KPIT","KSAN","KSEA","KSFO","KSLC","KSTL","KTPA","PHNL","KNKX","KCLE","KNFW","KDAL","KMDW","KFLL",
    };

    public static bool IsUs(string icao) => icao.Length == 4 && (icao[0] == 'K' || icao.StartsWith("PA") || icao.StartsWith("PH") || icao.StartsWith("PG"));

    /// <summary>91.117(b): at or below 2,500 ft above the surface within 4 nm of a Class C or D airport, 200 knots at most.</summary>
    public static bool Has200KnotArea(string icao) => IsUs(icao) && !ClassB.Contains(icao);
}
