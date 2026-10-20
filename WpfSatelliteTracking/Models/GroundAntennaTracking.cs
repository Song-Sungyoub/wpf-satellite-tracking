using V = (double X, double Y, double Z);

namespace WpfSatelliteTracking.Models;

public readonly record struct GroundAntennaAim(double AzimuthDegrees, double ElevationDegrees, bool Tracking);

public sealed class GroundAntennaTracking(GroundStation station)
{
    private CircularOrbit? plannedOrbit;
    private double searchStarted, searchRotation, reconsiderAt;
    private GroundAntennaAim waitingAim = new(0, 30, false);
    public double? NextPassSeconds { get; private set; }

    public GroundAntennaAim Update(CircularOrbit orbit, double seconds, double earthRotationDegrees)
    {
        var current = Calculate(station, orbit.Position(seconds), earthRotationDegrees);
        if (current.Tracking)
        {
            plannedOrbit = null; NextPassSeconds = null; waitingAim = current with { Tracking = false }; return current;
        }
        double rotationError = (earthRotationDegrees - searchRotation -
            (seconds - searchStarted) * SatelliteOperations.EarthRotationDegreesPerSecond) % 360;
        if (rotationError > 180) rotationError -= 360;
        if (rotationError < -180) rotationError += 360;
        if (plannedOrbit != orbit || seconds < searchStarted || seconds >= reconsiderAt || Math.Abs(rotationError) > 0.001)
        {
            plannedOrbit = orbit; searchStarted = seconds; searchRotation = earthRotationDegrees; NextPassSeconds = null;
            reconsiderAt = seconds + 3600;
            // shortcut: 15-second samples over 24h; refine using a validated pass predictor for operational scheduling.
            // Samples that cannot be visible are skipped in whole 15 s steps, so the first visible sample (and the result) is unchanged:
            // the station–subsatellite angle shrinks no faster than the orbital plus Earth rotation rate.
            double elevation = SatelliteOperations.MinimumElevationDegrees * Math.PI / 180;
            double visibleAngle = Math.Acos(Math.Cos(elevation) / orbit.Radius) - elevation;
            double maxRate = (2 * Math.PI / orbit.PeriodSeconds + SatelliteOperations.EarthRotationDegreesPerSecond * Math.PI / 180) * 1.05;
            for (double delay = 15; delay <= 86400; delay += 15)
            {
                GroundAntennaAim At(double t) => Calculate(station, orbit.Position(seconds + t),
                    earthRotationDegrees + t * SatelliteOperations.EarthRotationDegreesPerSecond);
                var satellite = orbit.Position(seconds + delay);
                var ground = SatelliteOperations.StationPosition(station, earthRotationDegrees + delay * SatelliteOperations.EarthRotationDegreesPerSecond);
                double cosine = (satellite.X * ground.X + satellite.Y * ground.Y + satellite.Z * ground.Z) / orbit.Radius;
                double gap = Math.Acos(Math.Clamp(cosine, -1, 1)) - visibleAngle;
                if (gap > 1e-9) { delay += Math.Floor(gap / maxRate / 15) * 15; continue; }
                if (!At(delay).Tracking) continue;
                double low = delay - 15, high = delay;
                for (int step = 0; step < 10; step++)
                {
                    double middle = (low + high) / 2;
                    if (At(middle).Tracking) high = middle; else low = middle;
                }
                var entry = At(high);
                waitingAim = entry with { Tracking = false };
                NextPassSeconds = reconsiderAt = seconds + high;
                break;
            }
        }
        return waitingAim;
    }

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
