using System.Windows.Media.Media3D;

namespace WpfSatelliteTracking.Rendering;

internal static class SatelliteCamera
{
    // Keep the entire viewing ray outside the globe and atmosphere while retaining
    // the requested camera distance. Free Earth views still allow normal occlusion.
    public static Point3D Position(Point3D target, Vector3D offset)
    {
        const double clearanceRadius = 1.06;
        double radius = ((Vector3D)target).Length, distance = offset.Length;
        if (radius <= clearanceRadius || distance < 1e-12) return target + offset;

        var outward = (Vector3D)target / radius;
        var direction = offset / distance;
        double radial = Vector3D.DotProduct(direction, outward);
        double minimumRadial = -Math.Sqrt(1 - Math.Pow(clearanceRadius / radius, 2)) + 0.01;
        if (radial >= minimumRadial) return target + offset;

        var tangent = direction - outward * radial;
        if (tangent.LengthSquared < 1e-20)
            tangent = Vector3D.CrossProduct(outward, Math.Abs(outward.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0));
        tangent.Normalize();
        direction = tangent * Math.Sqrt(1 - minimumRadial * minimumRadial) + outward * minimumRadial;
        return target + direction * distance;
    }
}
