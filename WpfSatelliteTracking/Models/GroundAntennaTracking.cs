using V = (double X, double Y, double Z);

namespace WpfSatelliteTracking.Models;

public readonly record struct GroundAntennaAim(double AzimuthDegrees, double ElevationDegrees, bool Tracking);

// Look angles from a ground station to a satellite (inertial coordinates, Earth rotation in degrees).
public static class GroundAntennaTracking
{
    public static GroundAntennaAim Calculate(GroundStation station, V satellite, double earthRotationDegrees)
    {
        var ground = SatelliteOperations.StationPosition(station, earthRotationDegrees);
        double x = satellite.X - ground.X, y = satellite.Y - ground.Y, z = satellite.Z - ground.Z;
        double length = Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(length) || length < 1e-12) throw new ArgumentException("Invalid satellite position", nameof(satellite));
        double lat = station.Latitude * Math.PI / 180, lon = (station.Longitude + earthRotationDegrees) * Math.PI / 180;
        double east = -Math.Sin(lon) * x - Math.Cos(lon) * z;
        double north = -Math.Sin(lat) * Math.Cos(lon) * x + Math.Cos(lat) * y + Math.Sin(lat) * Math.Sin(lon) * z;
        double elevation = Math.Asin(Math.Clamp((x * ground.X + y * ground.Y + z * ground.Z) / length, -1, 1)) * 180 / Math.PI;
        return new((Math.Atan2(east, north) * 180 / Math.PI + 360) % 360, elevation,
            elevation >= SatelliteOperations.MinimumElevationDegrees);
    }
}
