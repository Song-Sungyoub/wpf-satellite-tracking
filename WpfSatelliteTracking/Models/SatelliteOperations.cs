using V = (double X, double Y, double Z);

namespace WpfSatelliteTracking.Models;

public readonly record struct StationContact(GroundStation Station, V Position, double ElevationDegrees, double RangeKm)
{
    public bool Connected => ElevationDegrees >= SatelliteOperations.MinimumElevationDegrees;
}

// Geometry only: no radio device, file I/O or network connection.
public sealed class SatelliteOperations(GroundStation[] stations)
{
    public const double MinimumElevationDegrees = 10;
    // Illustrative band masks, applied alike to every satellite (real band assignments are not in TLEs):
    // S-band TT&C from 5°, X-band data downlink from 10°.
    public const double SBandMinimumElevationDegrees = 5, XBandMinimumElevationDegrees = 10;
    public const double EarthRotationDegreesPerSecond = 360.98564736629 / 86400;
    public StationContact[] Contacts { get; } = new StationContact[stations.Length];

    public void Update(SatelliteOrbit orbit, double seconds, double earthRotationDegrees)
    {
        var satellite = orbit.Position(seconds);
        for (int i = 0; i < stations.Length; i++)
        {
            var look = EarthCoordinates.LookAngles(stations[i], satellite, earthRotationDegrees);
            Contacts[i] = new(stations[i], StationPosition(stations[i], earthRotationDegrees), look.Elevation, look.RangeKm);
        }
    }

    public double? NextContactDelay(SatelliteOrbit orbit, double seconds, double maximumSeconds)
    {
        if (!double.IsFinite(maximumSeconds) || maximumSeconds < 0 || maximumSeconds > 86400) throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
        for (double delay = 0; delay <= maximumSeconds; delay += 15)
        {
            var satellite = orbit.Position(seconds + delay);
            double? first = null;
            foreach (var station in stations)
            {
                var time = SatelliteCatalog.ReferenceEpoch.AddSeconds(seconds + delay);
                if (EarthCoordinates.LookAngles(station, satellite, EarthCoordinates.GmstDegrees(time)).Elevation >= SBandMinimumElevationDegrees)
                {
                    double low = Math.Max(0, delay - 15), high = delay;
                    while (high - low > 0.1)
                    {
                        double mid = (low + high) / 2;
                        var aim = EarthCoordinates.LookAngles(station, orbit.Position(seconds + mid), EarthCoordinates.GmstDegrees(SatelliteCatalog.ReferenceEpoch.AddSeconds(seconds + mid)));
                        if (aim.Elevation >= SBandMinimumElevationDegrees) high = mid; else low = mid;
                    }
                    first = first is null ? high : Math.Min(first.Value, high);
                }
            }
            if (first is not null) return first;
        }
        return null;
    }

    public static V StationPosition(GroundStation station, double rotationDegrees)
    {
        return EarthCoordinates.ToInertial(EarthCoordinates.Surface(station.Latitude, station.Longitude, station.AltitudeKm), rotationDegrees);
    }
}
