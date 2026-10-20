namespace WpfSatelliteTracking.Models;

// Demonstration constellation: type A (side-looking sensor) and type B (nadir-looking sensor) satellites on
// repeat ground tracks that all cross TrackCenter (the middle of the three ground stations).
// shortcut: circular two-body orbits with constructed elements (no TLE/SGP4, no J2 precession); not real orbits.
public static class SatelliteConstellation
{
    public const int AGroups = 3, APerGroup = 10, BGroups = 2, BPerGroup = 10;
    public const int Count = AGroups * APerGroup + BGroups * BPerGroup; // 50
    // Type A repeats its ground track every day after 15 revolutions (~561 km); type B every 2 days after 29 (~725 km).
    public const int ARevolutions = 15, ACycleDays = 1, BRevolutions = 29, BCycleDays = 2;
    public const double AInclination = 97.6, BInclination = 97.8;
    // Orbit time is measured from this fixed epoch, so the tracks stay over the stations whatever date is simulated.
    public static readonly DateTimeOffset ReferenceEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly GroundStation TrackCenter = new("기준점", 34.09, 128.12);
    public static double EarthRotationPeriodSeconds => 360 / SatelliteOperations.EarthRotationDegreesPerSecond;
    public static double CycleSeconds(SatelliteMission mission) => EarthRotationPeriodSeconds * (mission == SatelliteMission.A ? ACycleDays : BCycleDays);

    public static CircularOrbit[] Create()
    {
        var satellites = new List<CircularOrbit>(Count);
        AddType(satellites, SatelliteMission.A, AGroups, APerGroup, ARevolutions, AInclination);
        AddType(satellites, SatelliteMission.B, BGroups, BPerGroup, BRevolutions, BInclination);
        return satellites.ToArray();
    }

    // Each group shares one repeat ground track through TrackCenter: its members cross the centre at evenly spaced
    // times over the repeat cycle. Groups alternate ascending/descending passes, and the third type-A group is
    // offset by half a slot so passes interleave.
    private static void AddType(List<CircularOrbit> satellites, SatelliteMission mission, int groups, int perGroup, int revolutions, double inclination)
    {
        double cycle = CycleSeconds(mission), period = cycle / revolutions, slot = cycle / perGroup;
        double altitude = Math.Cbrt(398600.4418 * Math.Pow(period / (2 * Math.PI), 2)) - CircularOrbit.EarthRadiusKm;
        for (int group = 1; group <= groups; group++)
            for (int member = 1; member <= perGroup; member++)
            {
                double crossing = (member - 1) * slot + (group == 3 ? slot / 2 : 0);
                var (node, phase) = PassOver(TrackCenter, inclination, period, crossing, ascending: group != 2);
                satellites.Add(new CircularOrbit($"{mission}-{group:00}-{member:00}", altitude, inclination, node, phase, mission, group));
            }
    }

    // Node and initial phase (degrees) so a circular orbit is directly over the point at `seconds` after ReferenceEpoch.
    public static (double NodeDegrees, double PhaseDegrees) PassOver(GroundStation point, double inclinationDegrees, double periodSeconds, double seconds, bool ascending)
    {
        const double rad = Math.PI / 180;
        double rotation = SolarLighting.At(ReferenceEpoch.AddSeconds(seconds)).EarthRotationDegrees;
        double lat = point.Latitude * rad, inc = inclinationDegrees * rad;
        double u = Math.Asin(Math.Clamp(Math.Sin(lat) / Math.Sin(inc), -1, 1));
        if (!ascending) u = Math.PI - u;
        double inertialLongitude = (point.Longitude + rotation) * rad;
        double node = inertialLongitude - Math.Atan2(Math.Sin(u) * Math.Cos(inc), Math.Cos(u));
        double phase = u - 2 * Math.PI * (seconds / periodSeconds - Math.Floor(seconds / periodSeconds));
        return ((node / rad % 360 + 360) % 360, (phase / rad % 360 + 360) % 360);
    }
}
