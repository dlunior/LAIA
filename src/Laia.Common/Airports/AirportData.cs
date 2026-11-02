namespace Laia.Common.Airports;

/// <summary>
/// An airport's layout and procedures, read by LAIA Sim from the simulator's own scenery and navigation
/// data (SimConnect facility functions), then sent to LAIA Server, which uses it to move AI traffic on the
/// real taxiways and fly the real departure and arrival procedures. Distances in metres unless named.
/// </summary>
public sealed class AirportData
{
    public string Icao { get; set; } = "";
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double ElevationM { get; set; }
    public double MagVar { get; set; }
    public string Simulator { get; set; } = "";
    public DateTime ScannedUtc { get; set; }
    /// <summary>Version of the reading code; older readings are read again.</summary>
    public int Format { get; set; }
    public const int CurrentFormat = 2;
    public List<AirportRunway> Runways { get; set; } = new();
    /// <summary>Taxi points, in the simulator's index order (taxi paths refer to them by index).</summary>
    public List<TaxiPoint> Points { get; set; } = new();
    /// <summary>Parking spots, in the simulator's index order.</summary>
    public List<Parking> Parkings { get; set; } = new();
    public List<TaxiPath> Paths { get; set; } = new();
    public List<string> TaxiNames { get; set; } = new();
    public List<Procedure> Departures { get; set; } = new();
    public List<Procedure> Arrivals { get; set; } = new();
    public List<Approach> Approaches { get; set; } = new();
}

public sealed class AirportRunway
{
    public double Lat { get; set; }        // centre
    public double Lon { get; set; }
    public double ElevationM { get; set; }
    public double Heading { get; set; }    // true, of the primary end
    public double LengthM { get; set; }
    public double WidthM { get; set; }
    public int Surface { get; set; }
    public int PrimaryNumber { get; set; }
    public int PrimaryDesignator { get; set; }
    public int SecondaryNumber { get; set; }
    public int SecondaryDesignator { get; set; }

    public static string Ident(int number, int designator)
    {
        if (number is < 1 or > 36) return number switch { 37 => "N", 38 => "NE", 39 => "E", 40 => "SE", 41 => "S", 42 => "SW", 43 => "W", 44 => "NW", _ => "?" };
        return number.ToString("00") + designator switch { 1 => "L", 2 => "R", 3 => "C", 5 => "A", 6 => "B", _ => "" };
    }
}

/// <summary>Offsets in metres from the airport reference point, exactly as the simulator gives them.</summary>
public sealed class TaxiPoint
{
    public int Type { get; set; }          // 1 normal, 2 hold short, 4 ILS hold short, 5/6 hold short (not drawn)
    public double BiasX { get; set; }
    public double BiasZ { get; set; }
}

public sealed class Parking
{
    public int Type { get; set; }          // 8 gate small, 9 gate medium, 10 gate heavy, 15 gate extra, 5 cargo ramp, 1-4/14 GA ramps
    public int Name { get; set; }          // 10 GATE, 12-37 GATE_A..GATE_Z, 1-9 PARKING variants
    public int Number { get; set; }
    public double Heading { get; set; }    // true
    public double RadiusM { get; set; }
    public double BiasX { get; set; }
    public double BiasZ { get; set; }
    public List<string> Airlines { get; set; } = new();

    public string Label()
    {
        var prefix = Name switch
        {
            10 => "Gate ",
            >= 12 and <= 37 => "Gate " + (char)('A' + Name - 12),
            11 => "Dock ",
            _ => "Parking ",
        };
        return prefix + Number;
    }
}

public sealed class TaxiPath
{
    public int Type { get; set; }          // 1 taxi, 2 runway, 3 parking, 4 path, 5 closed, 6 vehicle, 7 road, 8 painted line
    public int Start { get; set; }         // taxi point index
    public int End { get; set; }           // taxi point index (parking index when Type is 3)
    public int RunwayNumber { get; set; }
    public int RunwayDesignator { get; set; }
    public int NameIndex { get; set; }
    public double WidthM { get; set; }
}

public sealed class ProcedureLeg
{
    public int Type { get; set; }          // ARINC 424 path terminator: 1 AF, 2 CA, 4 CF, 7 DF, 8 FA, 11 FM, 15 IF, 17 RF, 18 TF, 19 VA, 22 VM…
    public string Fix { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public bool FlyOver { get; set; }
    public int Turn { get; set; }          // 0 none, 1 left, 2 right
    public bool TrueCourse { get; set; }
    public double Course { get; set; }     // degrees (magnetic unless TrueCourse)
    public double DistanceM { get; set; }  // route distance in metres (0 when given in minutes)
    public int AltDesc { get; set; }       // 0 none, 1 at, 2 at or above, 3 at or below, 4 between
    public double Alt1Ft { get; set; }
    public double Alt2Ft { get; set; }
    public double SpeedKt { get; set; }
}

public sealed class Transition
{
    public string Name { get; set; } = "";           // enroute transition name, or the runway ident
    public List<ProcedureLeg> Legs { get; set; } = new();
}

/// <summary>A SID (in Departures) or a STAR (in Arrivals).</summary>
public sealed class Procedure
{
    public string Name { get; set; } = "";
    public List<Transition> RunwayTransitions { get; set; } = new();
    public List<Transition> EnrouteTransitions { get; set; } = new();
    public List<ProcedureLeg> Legs { get; set; } = new();  // the common part
}

public sealed class Approach
{
    public int Type { get; set; }          // 1 GPS, 2 VOR, 3 NDB, 4 ILS, 5 LOC, 7 LDA, 10 RNAV…
    public string Suffix { get; set; } = "";
    public string Runway { get; set; } = "";
    public List<Transition> Transitions { get; set; } = new();
    public List<ProcedureLeg> FinalLegs { get; set; } = new();
}

/// <summary>Installed AI models LAIA Sim found (airline ICAO + type designator, with how many liveries).</summary>
public sealed class TrafficCatalog
{
    public List<CatalogEntry> Entries { get; set; } = new();
}

public sealed record CatalogEntry(string Airline, string Type, int Count);
