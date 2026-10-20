namespace WpfOrbitDemo.Models;

public static class SatelliteArticulation
{
    // shortcut: one shared wing hinge axis; use measured joints/drive limits when CAD is available.
    public static double SolarArrayAngle(double sunY, double sunZ, double previousAngle, bool eclipsed)
        => eclipsed || sunY * sunY + sunZ * sunZ < 1e-16
            ? previousAngle : Math.Atan2(sunZ, sunY) * 180 / Math.PI;

    public static (double X, double Y, double Z) TypeASensorDirection(double seconds, bool capturing)
    {
        double look = 35 * Math.PI / 180;
        double azimuth = capturing ? 0 : 12 * Math.PI / 180 * Math.Sin(seconds * 2 * Math.PI / 180);
        return (Math.Sin(look) * Math.Cos(azimuth), -Math.Cos(look), Math.Sin(look) * Math.Sin(azimuth));
    }
}
