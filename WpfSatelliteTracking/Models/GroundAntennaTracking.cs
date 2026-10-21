using V = (double X, double Y, double Z);
namespace WpfSatelliteTracking.Models;
public readonly record struct GroundAntennaAim(double AzimuthDegrees, double ElevationDegrees, bool Tracking);
public static class GroundAntennaTracking
{
    public static GroundAntennaAim Calculate(GroundStation station, V satellite, double earthRotationDegrees)
    {
        var look = EarthCoordinates.LookAngles(station, satellite, earthRotationDegrees);
        return new(look.Azimuth, look.Elevation, look.Elevation >= SatelliteOperations.SBandMinimumElevationDegrees);
    }
}
