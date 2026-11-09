namespace Laia.Common.SimConnect;

/// <summary>The user's aircraft as LAIA Sim reads it from MSFS, sent to the server about once a second.</summary>
public sealed class AircraftState
{
    public bool Connected { get; set; }
    public string Simulator { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double AltitudeFt { get; set; }
    public double AglFt { get; set; }
    public double HeadingTrue { get; set; }
    public double HeadingMag { get; set; }
    public double Ias { get; set; }
    public double GroundSpeed { get; set; }
    public double VerticalSpeed { get; set; }
    public bool OnGround { get; set; }
    public double Com1Active { get; set; }
    public double Com1Standby { get; set; }
    public double Com2Active { get; set; }
    public double Com2Standby { get; set; }
    public bool Com1Transmit { get; set; }
    public bool Com2Transmit { get; set; }
    public string Squawk { get; set; } = "";
    public string Title { get; set; } = "";
    public string AtcId { get; set; } = "";
    public string AtcAirline { get; set; } = "";
    public string AtcFlightNumber { get; set; } = "";
    public double WindDirection { get; set; }   // true, wind from
    public double WindSpeed { get; set; }       // knots
    /// <summary>The simulator's UTC time as minutes into the week (Monday 00:00 = 0); -1 if unknown.</summary>
    public double ZuluWeekMinute { get; set; } = -1;

    public AircraftState Clone() => (AircraftState)MemberwiseClone();

    /// <summary>The radio the pilot is transmitting on (COM2 only when COM2 is selected to transmit).</summary>
    public double TransmitFrequency => Com2Transmit && !Com1Transmit ? Com2Active : Com1Active;
}
