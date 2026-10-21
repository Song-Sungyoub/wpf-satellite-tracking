using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfSatelliteTracking.Models;

namespace WpfSatelliteTracking.Rendering;

// Reference-inspired radio telescope: triangulated rear dish frame, feed tripod and braced pedestal.
// Dimensions are illustrative; fixed foundation, azimuth turret and elevation head stay separate.
internal sealed class GroundAntennaGeometry
{
    private static readonly Model3DGroup Template = Build();
    private readonly AxisAngleRotation3D azimuth = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D elevation = new(new Vector3D(1, 0, 0), -60);
    private readonly RotateTransform3D yaw;
    private readonly Transform3DGroup pitch;
    public Model3DGroup Model { get; } = new();
    public Point3D FeedPoint => Model.Transform.Transform(yaw.Transform(pitch.Transform(new Point3D(0, 0.044, 0))));

    public GroundAntennaGeometry(GroundStation station)
    {
        yaw = new RotateTransform3D(azimuth);
        pitch = new Transform3DGroup();
        pitch.Children.Add(new RotateTransform3D(elevation));
        pitch.Children.Add(new TranslateTransform3D(0, 0.110, 0));
        var head = new Model3DGroup { Transform = pitch }; head.Children.Add(Template.Children[2]);
        var turret = new Model3DGroup { Transform = yaw }; turret.Children.Add(Template.Children[1]); turret.Children.Add(head);
        Model.Children.Add(Template.Children[0]); Model.Children.Add(turret);
        double lat = station.Latitude * Math.PI / 180, lon = station.Longitude * Math.PI / 180;
        var up = new Vector3D(Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), -Math.Cos(lat) * Math.Sin(lon));
        var east = new Vector3D(-Math.Sin(lon), 0, -Math.Cos(lon));
        var south = Vector3D.CrossProduct(east, up);
        var anchor = EarthCoordinates.Surface(station.Latitude, station.Longitude, station.AltitudeKm + 0.002 * SatelliteOrbit.EarthRadiusKm);
        const double size = 0.26; // illustrative identification scale
        var pose = new MatrixTransform3D(new Matrix3D(east.X * size, east.Y * size, east.Z * size, 0,
            up.X * size, up.Y * size, up.Z * size, 0, south.X * size, south.Y * size, south.Z * size, 0,
            anchor.X, anchor.Y, anchor.Z, 1));
        pose.Freeze(); Model.Transform = pose;
    }

    public void Update(GroundAntennaAim aim)
    {
        if (azimuth.Angle != -aim.AzimuthDegrees) azimuth.Angle = -aim.AzimuthDegrees;
        if (elevation.Angle != aim.ElevationDegrees - 90) elevation.Angle = aim.ElevationDegrees - 90;
    }

    private static Model3DGroup Build()
    {
        Material Surface(Color color, double shine)
        {
            var material = new MaterialGroup();
            material.Children.Add(SceneGeometry.SatelliteSurface(new SolidColorBrush(color), false));
            material.Children.Add(new SpecularMaterial(Brushes.White, shine)); material.Freeze(); return material;
        }
        var steel = Surface(Color.FromRgb(174, 182, 189), 65);
        var reflector = Surface(Color.FromRgb(221, 225, 228), 35);
        var dark = Surface(Color.FromRgb(30, 36, 40), 25);
        var concrete = Surface(Color.FromRgb(86, 93, 99), 8);
        var foundation = new Model3DGroup(); var yoke = new Model3DGroup(); var dish = new Model3DGroup();
        void Box(Model3DGroup group, Point3D center, Vector3D size, Material material)
            => group.Children.Add(SceneGeometry.Box(center, size, material));
        void Rod(Model3DGroup group, Point3D a, Point3D b, double width, Material material)
        {
            var y = b - a; double length = y.Length; y.Normalize();
            var x = Vector3D.CrossProduct(y, Math.Abs(y.Y) < 0.9 ? new(0, 1, 0) : new(1, 0, 0)); x.Normalize();
            var z = Vector3D.CrossProduct(x, y);
            var rod = SceneGeometry.Box(new(0, 0.5, 0), new(1, 1, 1), material).Clone();
            rod.Transform = new MatrixTransform3D(new(x.X * width, x.Y * width, x.Z * width, 0,
                y.X * length, y.Y * length, y.Z * length, 0, z.X * width, z.Y * width, z.Z * width, 0, a.X, a.Y, a.Z, 1));
            group.Children.Add(rod);
        }
        Box(foundation, new(0, 0.002, 0), new(0.064, 0.004, 0.058), concrete);
        foreach (double x in new[] { -1.0, 1.0 }) foreach (double z in new[] { -1.0, 1.0 })
        {
            var foot = new Point3D(x * 0.025, 0.004, z * 0.021);
            Rod(foundation, foot, new(x * 0.010, 0.048, z * 0.009), 0.003, steel);
            Rod(foundation, foot, new(-x * 0.010, 0.043, z * 0.009), 0.0014, steel);
            Box(foundation, foot, new(0.012, 0.003, 0.012), steel);
        }
        foundation.Children.Add(SceneGeometry.Lathe([(0.014, 0.008), (0.011, 0.043), (0.014, 0.044)], steel, 24));
        Box(foundation, new(0, 0.025, 0.014), new(0.008, 0.018, 0.001), dark);
        // Access stair and safety rails on the fixed foundation.
        for (int step = 0; step < 8; step++)
            Box(foundation, new(0, 0.004 + step * 0.0048, 0.040 - step * 0.0035), new(0.012, 0.0015, 0.005), steel);
        foreach (double side in new[] { -1.0, 1.0 })
        {
            Rod(foundation, new(side * 0.007, 0.013, 0.041), new(side * 0.007, 0.051, 0.014), 0.00065, steel);
            for (int post = 0; post < 3; post++)
                Rod(foundation, new(side * 0.007, 0.004 + post * 0.014, 0.040 - post * 0.010),
                    new(side * 0.007, 0.015 + post * 0.014, 0.040 - post * 0.010), 0.00065, steel);
        }
        foundation.Children.Add(SceneGeometry.Lathe([(0, 0.044), (0.019, 0.044), (0.019, 0.051), (0, 0.051)], dark));
        Box(yoke, new(0, 0.054, 0), new(0.047, 0.006, 0.025), steel);
        foreach (double side in new[] { -1.0, 1.0 })
        {
            Rod(yoke, new(side * 0.024, 0.055, 0), new(side * 0.052, 0.110, 0), 0.006, steel);
            Box(yoke, new(side * 0.052, 0.110, 0), new(0.011, 0.014, 0.014), dark);
        }
        foreach (double side in new[] { -1.0, 1.0 })
        {
            Rod(yoke, new(side * 0.020, 0.055, -0.012), new(side * 0.052, 0.110, 0), 0.0035, steel);
            Rod(yoke, new(side * 0.020, 0.055, 0.012), new(side * 0.052, 0.110, 0), 0.0035, steel);
            Rod(yoke, new(side * 0.052, 0.110, 0), new(side * 0.040, 0.075, -0.021), 0.002, steel);
        }
        Box(yoke, new(0, 0.073, -0.027), new(0.078, 0.002, 0.016), steel);
        for (int post = 0; post <= 6; post++)
            Rod(yoke, new(-0.038 + post * 0.0127, 0.074, -0.034), new(-0.038 + post * 0.0127, 0.086, -0.034), 0.0006, steel);
        Rod(yoke, new(-0.038, 0.086, -0.034), new(0.038, 0.086, -0.034), 0.0007, steel);
        const double radius = 0.046, depth = 0.014;
        Point3D Bowl(double r, double angle, double offset = 0) => new(r * Math.Cos(angle), depth * r * r / (radius * radius) + offset, r * Math.Sin(angle));
        dish.Children.Add(SceneGeometry.Lathe(Enumerable.Range(0, 25).Select(i => (i * radius / 24, depth * i * i / (24 * 24))).ToArray(), reflector, 96));
        dish.Children.Add(SceneGeometry.Lathe(Enumerable.Range(0, 25).Select(i => (i * radius / 24, depth * i * i / (24 * 24) - 0.0012)).ToArray(), steel, 96));
        for (int ring = 1; ring <= 5; ring++) for (int segment = 0; segment < 48; segment++)
            Rod(dish, Bowl(radius * ring / 5, segment * Math.PI / 24, 0.0004),
                Bowl(radius * ring / 5, (segment + 1) * Math.PI / 24, 0.0004), ring == 5 ? 0.001 : 0.00035, steel);
        for (int spoke = 0; spoke < 12; spoke++) for (int segment = 0; segment < 5; segment++)
        {
            double a = spoke * Math.PI / 6;
            Rod(dish, Bowl(radius * segment / 5, a, 0.0004), Bowl(radius * (segment + 1) / 5, a, 0.0004), 0.00045, steel);
            if (segment > 0) Rod(dish, Bowl(radius * segment / 5, a, 0.0004), Bowl(radius * (segment + 1) / 5, a + Math.PI / 6, 0.0004), 0.00025, steel);
        }
        // Deep rear frame: paired curved ribs, concentric chords and diagonal web members.
        for (int spoke = 0; spoke < 16; spoke++)
        {
            double a = spoke * Math.PI / 8, next = (spoke + 1) * Math.PI / 8;
            for (int segment = 1; segment <= 5; segment++)
            {
                double r0 = radius * (segment - 1) / 5, r1 = radius * segment / 5;
                double offset0 = -0.010 * (1 - r0 / radius) - 0.002;
                double offset1 = -0.010 * (1 - r1 / radius) - 0.002;
                var inner = Bowl(r0, a, offset0); var outer = Bowl(r1, a, offset1);
                Rod(dish, inner, outer, 0.0008, steel);
                Rod(dish, outer, Bowl(r1, next, offset1), 0.00065, steel);
                Rod(dish, inner, Bowl(r1, next, offset1), 0.00045, steel);
                Rod(dish, outer, Bowl(r1, a, -0.0012), 0.0005, steel);
            }
        }
        Box(dish, new(0, -0.012, 0), new(0.028, 0.009, 0.024), steel);
        Box(dish, new(0, -0.006, 0), new(0.108, 0.005, 0.007), steel);
        for (int leg = 0; leg < 3; leg++) Rod(dish, Bowl(radius * 0.94, leg * Math.PI * 2 / 3), new(0, 0.044, 0), 0.0012, steel);
        Box(dish, new(0, 0.044, 0), new(0.005, 0.009, 0.005), steel);
        Box(dish, new(0, -0.004, 0), new(0.033, 0.007, 0.025), steel);
        var result = new Model3DGroup();
        foreach (var group in new[] { foundation, yoke, dish }) result.Children.Add(SceneGeometry.MergeWithAtlas(group, false));
        result.Freeze(); return result;
    }
}
