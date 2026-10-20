namespace WpfSatelliteTracking.Models;

// Equatorial vectors: X toward the equinox, Y north, Z toward negative right ascension.
public sealed record CelestialState((double X, double Y, double Z) SunAu,
    (double X, double Y, double Z) MoonEarthRadii,
    (double X, double Y, double Z) MercuryAu, (double X, double Y, double Z) VenusAu,
    (double X, double Y, double Z)[] HeliocentricAu);

public static class CelestialPositions
{
    public static (double X, double Y, double Z)[] OrbitPath(int planet, DateTimeOffset time)
    {
        double t = (time - new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero)).TotalDays / 36525;
        return Enumerable.Range(0, 257).Select(i => ToScene(Planet(planet, t, i * 360.0 / 256))).ToArray();
    }

    private static (double X, double Y, double Z) Planet(int planet, double t, double? anomaly = null)
    {
        (double A, double E, double I, double L, double P, double N) elements = planet switch
        {
            0 => (1.00000018 - 0.00000003 * t, 0.01673163 - 0.00003661 * t, -0.00054346 - 0.01337178 * t, 100.46691572 + 35999.37306329 * t, 102.93005885 + 0.31795260 * t, -5.11260389 - 0.24123856 * t),
            1 => (0.38709843, 0.20563661 + 0.00002123 * t, 7.00559432 - 0.00590158 * t, 252.25166724 + 149472.67486623 * t, 77.45771895 + 0.15940013 * t, 48.33961819 - 0.12214182 * t),
            2 => (0.72332102 - 0.00000026 * t, 0.00676399 - 0.00005107 * t, 3.39777545 + 0.00043494 * t, 181.97970850 + 58517.81560260 * t, 131.76755713 + 0.05679648 * t, 76.67261496 - 0.27274174 * t),
            3 => (1.52371243 + 0.00000097 * t, 0.09336511 + 0.00009149 * t, 1.85181869 - 0.00724757 * t, -4.56813164 + 19140.29934243 * t, -23.91744784 + 0.45223625 * t, 49.71320984 - 0.26852431 * t),
            4 => (5.20248019 - 0.00002864 * t, 0.04853590 + 0.00018026 * t, 1.29861416 - 0.00322699 * t, 34.33479152 + 3034.90371757 * t, 14.27495244 + 0.18199196 * t, 100.29282654 + 0.13024619 * t),
            5 => (9.54149883 - 0.00003065 * t, 0.05550825 - 0.00032044 * t, 2.49424102 + 0.00451969 * t, 50.07571329 + 1222.11494724 * t, 92.86136063 + 0.54179478 * t, 113.63998702 - 0.25015002 * t),
            6 => (19.18797948 - 0.00020455 * t, 0.04685740 - 0.00001550 * t, 0.77298127 - 0.00180155 * t, 314.20276625 + 428.49512595 * t, 172.43404441 + 0.09266985 * t, 73.96250215 + 0.05739699 * t),
            7 => (30.06952752 + 0.00006447 * t, 0.00895439 + 0.00000818 * t, 1.77005520 + 0.00022400 * t, 304.22289287 + 218.46515314 * t, 46.68158724 + 0.01009938 * t, 131.78635853 - 0.00606302 * t),
            _ => throw new ArgumentOutOfRangeException(nameof(planet))
        };
        // JPL Table 2b is required for outer-planet phase, but does not change the orbit guide's ellipse.
        (double B, double C, double S, double F) correction = planet switch
        {
            4 => (-0.00012452, 0.06064060, -0.35635438, 38.35125),
            5 => (0.00025899, -0.13434469, 0.87320147, 38.35125),
            6 => (0.00058331, -0.97731848, 0.17689245, 7.67025),
            7 => (-0.00041348, 0.68346318, -0.10162547, 7.67025),
            _ => (0, 0, 0, 0)
        };
        double phase = correction.B * t * t + correction.C * Cos(correction.F * t) + correction.S * Sin(correction.F * t);
        return Orbit(elements.A, elements.E, elements.I, anomaly is double m ? elements.P + m : elements.L + phase, elements.P, elements.N);
    }
    private const double Radians = Math.PI / 180;
    public static CelestialState At(DateTimeOffset time)
    {
        if (time.UtcDateTime.Year is < 2000 or > 2100) throw new ArgumentOutOfRangeException(nameof(time));
        double days = (time - new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero)).TotalDays;
        double t = days / 36525;
        // JPL Table 2a (3000 BC..3000 AD); Earth/Moon barycenter approximates Earth.
        var planets = Enumerable.Range(0, 8).Select(i => Planet(i, t)).ToArray();
        var earth = planets[0];
        var mercury = planets[1];
        var venus = planets[2];

        double d = days + 1.5; // Schlyter's epoch: 1999-12-31 00:00 UTC.
        double node = 125.1228 - 0.0529538083 * d, perihelion = 318.0634 + 0.1643573223 * d;
        double moonAnomaly = 115.3654 + 13.0649929509 * d;
        var moon = Orbit(60.2666, 0.0549, 5.1454, moonAnomaly + perihelion + node, perihelion + node, node);
        double sunAnomaly = 356.0470 + 0.9856002585 * d;
        double elongation = moonAnomaly + perihelion + node - (sunAnomaly + 282.9404 + 4.70935E-5 * d);
        double latitudeArgument = moonAnomaly + perihelion;
        // Largest lunar perturbations: illustrative direction, not a precision lunar ephemeris.
        double lon = Math.Atan2(moon.Y, moon.X) / Radians
            - 1.274 * Sin(moonAnomaly - 2 * elongation) + 0.658 * Sin(2 * elongation) - 0.186 * Sin(sunAnomaly);
        double lat = Math.Atan2(moon.Z, Math.Sqrt(moon.X * moon.X + moon.Y * moon.Y)) / Radians
            - 0.173 * Sin(latitudeArgument - 2 * elongation);
        double radius = Math.Sqrt(moon.X * moon.X + moon.Y * moon.Y + moon.Z * moon.Z)
            - 0.58 * Cos(moonAnomaly - 2 * elongation) - 0.46 * Cos(2 * elongation);
        // Bring the lunar equinox of date approximately back to J2000 before the common conversion.
        lon -= 3.82394E-5 * d;
        moon = (radius * Cos(lat) * Cos(lon), radius * Cos(lat) * Sin(lon), radius * Sin(lat));
        // shortcut: UTC substitutes for TDB; no light-time, aberration, detailed lunar perturbations or nutation.
        return new CelestialState(ToScene((-earth.X, -earth.Y, -earth.Z)), ToScene(moon),
            ToScene((mercury.X - earth.X, mercury.Y - earth.Y, mercury.Z - earth.Z)),
            ToScene((venus.X - earth.X, venus.Y - earth.Y, venus.Z - earth.Z)), planets.Select(ToScene).ToArray());
    }

    private static (double X, double Y, double Z) Orbit(double a, double e, double inclination, double longitude, double perihelion, double node)
    {
        double m = Math.IEEERemainder(longitude - perihelion, 360) * Radians;
        double eccentric = m;
        for (int j = 0; j < 12; j++)
        {
            double correction = (eccentric - e * Math.Sin(eccentric) - m) / (1 - e * Math.Cos(eccentric));
            eccentric -= correction;
            if (Math.Abs(correction) < 1e-12) break;
        }
        double x = a * (Math.Cos(eccentric) - e), y = a * Math.Sqrt(1 - e * e) * Math.Sin(eccentric);
        double w = perihelion - node;
        return ((Cos(w) * Cos(node) - Sin(w) * Sin(node) * Cos(inclination)) * x + (-Sin(w) * Cos(node) - Cos(w) * Sin(node) * Cos(inclination)) * y,
            (Cos(w) * Sin(node) + Sin(w) * Cos(node) * Cos(inclination)) * x + (-Sin(w) * Sin(node) + Cos(w) * Cos(node) * Cos(inclination)) * y,
            Sin(w) * Sin(inclination) * x + Cos(w) * Sin(inclination) * y);
    }
    private static (double X, double Y, double Z) ToScene((double X, double Y, double Z) v) =>
        (v.X, Sin(23.43928) * v.Y + Cos(23.43928) * v.Z, -(Cos(23.43928) * v.Y - Sin(23.43928) * v.Z));
    private static double Sin(double degrees) => Math.Sin(degrees * Radians);
    private static double Cos(double degrees) => Math.Cos(degrees * Radians);
}
