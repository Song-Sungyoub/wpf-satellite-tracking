using V = (double X, double Y, double Z);
namespace WpfSatelliteTracking.Models;

public static class EarthCoordinates
{
    public const double EquatorialRadiusKm = 6378.137;
    public const double Flattening = 1 / 298.257223563;
    private const double Rad = Math.PI / 180;
    // Vallado GMST; UTC approximates UT1. Polar motion/EOP are not supplied.
    // Result is TEME -> pseudo Earth-fixed (PEF), not a claimed full ITRF transform.
    public static double GmstDegrees(DateTimeOffset utc)
    {
        double jd = 2451545 + (utc - SatelliteCatalog.ReferenceEpoch).TotalDays;
        double t = (jd - 2451545) / 36525;
        double seconds = 67310.54841 + (876600.0 * 3600 + 8640184.812866) * t + 0.093104 * t * t - 6.2e-6 * t * t * t;
        return ((seconds / 240 % 360) + 360) % 360;
    }
    public static V ToEarthFixed(V sceneTeme, double gmstDegrees)
    {
        double a = gmstDegrees * Rad, c = Math.Cos(a), s = Math.Sin(a);
        return (c * sceneTeme.X - s * sceneTeme.Z, sceneTeme.Y, s * sceneTeme.X + c * sceneTeme.Z);
    }
    public static V ToInertial(V fixedPosition, double gmstDegrees) => ToEarthFixed(fixedPosition, -gmstDegrees);
    public static V Surface(double latitude, double longitude, double altitudeKm = 0)
    {
        double lat = latitude * Rad, lon = longitude * Rad, e2 = Flattening * (2 - Flattening);
        double n = 1 / Math.Sqrt(1 - e2 * Math.Pow(Math.Sin(lat), 2)), h = altitudeKm / EquatorialRadiusKm;
        return ((n + h) * Math.Cos(lat) * Math.Cos(lon), (n * (1 - e2) + h) * Math.Sin(lat), -(n + h) * Math.Cos(lat) * Math.Sin(lon));
    }
    public static (double Latitude, double Longitude, double AltitudeKm) Geodetic(V fixedPosition)
    {
        double x = fixedPosition.X, y = -fixedPosition.Z, z = fixedPosition.Y;
        double p = Math.Sqrt(x * x + y * y), e2 = Flattening * (2 - Flattening);
        if (p < 1e-12) return (Math.Sign(z) * 90, 0, (Math.Abs(z) - (1 - Flattening)) * EquatorialRadiusKm);
        double lat = Math.Atan2(z, p * (1 - e2)), n = 1;
        for (int i = 0; i < 10; i++) { n = 1 / Math.Sqrt(1 - e2 * Math.Pow(Math.Sin(lat), 2)); lat = Math.Atan2(z + e2 * n * Math.Sin(lat), p); }
        n = 1 / Math.Sqrt(1 - e2 * Math.Pow(Math.Sin(lat), 2));
        return (lat / Rad, Math.Atan2(y, x) / Rad, (p / Math.Cos(lat) - n) * EquatorialRadiusKm);
    }
    public static (double Azimuth, double Elevation, double RangeKm) LookAngles(GroundStation station, V sceneTeme, double gmstDegrees)
    {
        var sat = ToEarthFixed(sceneTeme, gmstDegrees); var ground = Surface(station.Latitude, station.Longitude, station.AltitudeKm);
        double x = sat.X - ground.X, y = -(sat.Z - ground.Z), z = sat.Y - ground.Y;
        double lat = station.Latitude * Rad, lon = station.Longitude * Rad;
        double east = -Math.Sin(lon) * x + Math.Cos(lon) * y;
        double north = -Math.Sin(lat) * Math.Cos(lon) * x - Math.Sin(lat) * Math.Sin(lon) * y + Math.Cos(lat) * z;
        double up = Math.Cos(lat) * Math.Cos(lon) * x + Math.Cos(lat) * Math.Sin(lon) * y + Math.Sin(lat) * z;
        double length = Math.Sqrt(east * east + north * north + up * up);
        return ((Math.Atan2(east, north) / Rad + 360) % 360, Math.Atan2(up, Math.Sqrt(east * east + north * north)) / Rad, length * EquatorialRadiusKm);
    }
}
