namespace Laia.Common.SimConnect;

/// <summary>
/// One AI aircraft as the server is flying it. Sent to LAIA Sim about once a second; the sim side
/// draws it in MSFS and smooths the movement between updates.
/// </summary>
public sealed class TrafficAircraft
{
    public string Id { get; set; } = "";
    public string Callsign { get; set; } = "";
    public string Airline { get; set; } = "";   // ICAO airline code, e.g. DAL
    public string Type { get; set; } = "";      // ICAO type, e.g. B739
    /// <summary>Exact model/livery title when the flight comes from an AIG schedule (empty: pick one).</summary>
    public string Model { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double AltFt { get; set; }           // MSL; ignored while on the ground (the sim uses the terrain)
    public double AglFt { get; set; }           // height above the runway / ground
    public bool OnGround { get; set; }
    public long TimeMs { get; set; }            // server clock when this position was valid (Unix ms)
    public double Heading { get; set; }         // true
    public double Pitch { get; set; }           // degrees, nose up positive
    public double Bank { get; set; }            // degrees, right wing down positive
    public double GroundSpeed { get; set; }     // knots
    public double VerticalSpeed { get; set; }   // feet per minute
    public bool GearDown { get; set; } = true;
    public string Phase { get; set; } = "";
    /// <summary>Bits: 1 nav, 2 beacon, 4 strobes, 8 landing, 16 taxi, 32 logo, 64 wing, 128 cabin.</summary>
    public int Lights { get; set; } = 1 | 2 | 32;
}

public sealed class SimStateReply
{
    public List<TrafficAircraft> Traffic { get; set; } = new();
    public long ServerTimeMs { get; set; }
    /// <summary>"msfs": MSFS's AI flies a rolling path from LAIA; "laia": LAIA sets the position every frame.</summary>
    public string Motion { get; set; } = "laia";
    /// <summary>Airports the server would like LAIA Sim to read from MSFS (layout and procedures).</summary>
    public List<string>? NeedAirports { get; set; }
    /// <summary>Airports whose real schedule the server would like (read from the AIG flight plans).</summary>
    public List<string>? NeedSchedules { get; set; }
    /// <summary>True when the server wants the list of installed AI models again.</summary>
    public bool NeedCatalog { get; set; }
    /// <summary>True when the server wants every AIG schedule (the whole sky), not just its airports'.</summary>
    public bool NeedNetwork { get; set; }
}
