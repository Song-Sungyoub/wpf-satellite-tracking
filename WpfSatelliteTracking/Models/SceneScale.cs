namespace WpfSatelliteTracking.Models;

public static class SceneScale
{
    public const double ObservationSatelliteScale = 0.3; // 0.4 → 0.3 (2026-10-20, team request: smaller satellites)
    public const double AstronomicalUnitKm = 149597870.7;
    public const double LunarReferenceRadiusKm = 6378.14;
    public static double Units(double kilometers) => kilometers / SatelliteOrbit.EarthRadiusKm;
    public static double ObservationRadius(int body) => body switch
    {
        0 => 0.9, 1 => 0.273, 2 => 0.25, 3 => 0.43, 4 => 0.45,
        5 => 0.32, 6 => 1.2, 7 => 1.0, 8 => 0.7, 9 => 0.68,
        _ => throw new ArgumentOutOfRangeException(nameof(body))
    };
    public static double Radius(int body) => Units(body switch
    {
        0 => 695700, 1 => 1737.4, 2 => 2439.4, 3 => 6051.8, 4 => SatelliteOrbit.EarthRadiusKm,
        5 => 3389.5, 6 => 69911, 7 => 58232, 8 => 25362, 9 => 24622,
        _ => throw new ArgumentOutOfRangeException(nameof(body))
    });
    // Provisional deployed panel span, not measured spacecraft specifications.
    public static double SatelliteSpanMeters(SatelliteMission mission) => mission == SatelliteMission.A ? 12 : 8;
}
