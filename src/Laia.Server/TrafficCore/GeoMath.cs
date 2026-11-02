namespace Laia.Server.TrafficCore;

/// <summary>A 2-D point or vector in metres (X east, Y north) on the airport's local flat map.</summary>
public readonly record struct V2(double X, double Y)
{
    public static V2 operator +(V2 a, V2 b) => new(a.X + b.X, a.Y + b.Y);
    public static V2 operator -(V2 a, V2 b) => new(a.X - b.X, a.Y - b.Y);
    public static V2 operator *(V2 a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public V2 Unit => Length < 1e-9 ? new V2(0, 1) : this * (1 / Length);
    public static double Dot(V2 a, V2 b) => a.X * b.X + a.Y * b.Y;
    public static double Cross(V2 a, V2 b) => a.X * b.Y - a.Y * b.X;
    public double Bearing => Geo2.Norm(Math.Atan2(X, Y) * 180 / Math.PI);
    public static V2 FromBearing(double deg, double len = 1) { var r = deg * Math.PI / 180; return new V2(Math.Sin(r) * len, Math.Cos(r) * len); }
    public V2 Perp => new(Y, -X); // right-hand perpendicular
}

public static class Geo2
{
    public const double MPerNm = 1852.0;
    public const double FtPerM = 3.28084;
    public const double MpsPerKt = 0.514444;

    public static double Norm(double deg) { deg %= 360; return deg < 0 ? deg + 360 : deg; }
    /// <summary>Signed difference b - a in degrees, -180..180.</summary>
    public static double Diff(double a, double b) => ((b - a + 540) % 360) - 180;

    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * Math.PI / 180; var p2 = lat2 * Math.PI / 180;
        var dp = p2 - p1; var dl = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * Math.Asin(Math.Min(1, Math.Sqrt(a))) * 3440.065;
    }

    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * Math.PI / 180; var p2 = lat2 * Math.PI / 180; var dl = (lon2 - lon1) * Math.PI / 180;
        var y = Math.Sin(dl) * Math.Cos(p2);
        var x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return Norm(Math.Atan2(y, x) * 180 / Math.PI);
    }
}

/// <summary>Flat local map around an airport reference point: accurate to a few metres within ~60 nm.</summary>
public sealed class LocalMap(double lat0, double lon0)
{
    public double Lat0 { get; } = lat0;
    public double Lon0 { get; } = lon0;
    private readonly double _mPerDegLat = 111132.92 - 559.82 * Math.Cos(2 * lat0 * Math.PI / 180);
    private readonly double _mPerDegLon = 111412.84 * Math.Cos(lat0 * Math.PI / 180) - 93.5 * Math.Cos(3 * lat0 * Math.PI / 180);

    public V2 ToXY(double lat, double lon) => new((lon - Lon0) * _mPerDegLon, (lat - Lat0) * _mPerDegLat);
    public (double lat, double lon) ToLatLon(V2 p) => (Lat0 + p.Y / _mPerDegLat, Lon0 + p.X / _mPerDegLon);
}
