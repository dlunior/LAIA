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
    /// <summary>Bits: 1 nav, 2 beacon, 4 strobes, 8 landing, 16 taxi, 32 logo, 64 wing.</summary>
    public int Lights { get; set; } = 1 | 2 | 32;
}

public sealed class SimStateReply
{
    public List<TrafficAircraft> Traffic { get; set; } = new();
    public long ServerTimeMs { get; set; }
}
