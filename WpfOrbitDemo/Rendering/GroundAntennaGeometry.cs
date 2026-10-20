using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfOrbitDemo.Models;

namespace WpfOrbitDemo.Rendering;

// Generic solid-reflector tracking antenna archetype on an azimuth/elevation pedestal; dimensions are illustrative.
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
        pitch.Children.Add(new TranslateTransform3D(0, 0.110, 0)); // elevation axis high enough that the rim clears the turret at 10°
        var head = new Model3DGroup { Transform = pitch }; head.Children.Add(Template.Children[2]);
        var turret = new Model3DGroup { Transform = yaw }; turret.Children.Add(Template.Children[1]); turret.Children.Add(head);
        Model.Children.Add(Template.Children[0]); Model.Children.Add(turret);
        double lat = station.Latitude * Math.PI / 180, lon = station.Longitude * Math.PI / 180;
        var up = new Vector3D(Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), -Math.Cos(lat) * Math.Sin(lon));
        var east = new Vector3D(-Math.Sin(lon), 0, -Math.Cos(lon));
        var south = Vector3D.CrossProduct(east, up);
        const double size = 0.26; // identification-size model; 0.35 → 0.26 (2026-10-20, team request)
        var pose = new MatrixTransform3D(new Matrix3D(east.X * size, east.Y * size, east.Z * size, 0,
            up.X * size, up.Y * size, up.Z * size, 0, south.X * size, south.Y * size, south.Z * size, 0,
            up.X * 1.002, up.Y * 1.002, up.Z * 1.002, 1));
        pose.Freeze(); Model.Transform = pose;
    }

    public void Update(GroundAntennaAim aim)
    {
        if (azimuth.Angle != -aim.AzimuthDegrees) azimuth.Angle = -aim.AzimuthDegrees;
        if (elevation.Angle != aim.ElevationDegrees - 90) elevation.Angle = aim.ElevationDegrees - 90;
    }

    private static Model3DGroup Build()
    {
        Material Surface(Color color) => Painted(new SolidColorBrush(color));
        static Material Painted(Brush brush)
        {
            var material = new MaterialGroup(); material.Children.Add(SceneGeometry.SatelliteSurface(brush, false));
            material.Freeze(); return material;
        }
        var white = Surface(Color.FromRgb(236, 238, 240));
        var paint = Surface(Color.FromRgb(214, 218, 222));
        var grey = Surface(Color.FromRgb(150, 156, 162));
        var dark = Surface(Color.FromRgb(34, 38, 42));
        var concrete = Painted(SatellitePatterns.Brushed(Color.FromRgb(120, 124, 126)));
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
        // Foundation: concrete pad, cylindrical pedestal with an access door.
        Box(foundation, new(0, 0.002, 0), new(0.074, 0.004, 0.074), concrete);
        foundation.Children.Add(SceneGeometry.Lathe([(0.022, 0.004), (0.022, 0.006), (0.018, 0.008), (0.018, 0.046), (0, 0.046)], paint, 32));
        Box(foundation, new(0, 0.018, 0.0182), new(0.010, 0.016, 0.001), grey);
        // Azimuth turret: turntable, cross beam and two yoke arms outside the dish radius, elevation bearings at y = 0.110.
        yoke.Children.Add(SceneGeometry.Lathe([(0, 0.046), (0.024, 0.046), (0.024, 0.054), (0, 0.054)], grey, 32));
        Box(yoke, new(0, 0.057, 0), new(0.118, 0.006, 0.022), paint);
        foreach (double side in new[] { -1.0, 1.0 })
        {
            Box(yoke, new(side * 0.055, 0.084, 0), new(0.006, 0.050, 0.018), paint);
            Box(yoke, new(side * 0.055, 0.110, 0), new(0.010, 0.010, 0.012), dark);
        }
        // Elevation head (rotates about local X through its origin; boresight +Y): solid paraboloid reflector,
        // rim, four feed struts, feed horn at the focus (0, 0.044, 0), rear hub, elevation axle and counterweight.
        const double radius = 0.046, depth = 0.014;
        Point3D Bowl(double r, double angle) => new(r * Math.Cos(angle), depth * r * r / (radius * radius), r * Math.Sin(angle));
        dish.Children.Add(SceneGeometry.Lathe(Enumerable.Range(0, 21).Select(i => (i * radius / 20, depth * i * i / 400.0)).ToArray(), white, 48));
        dish.Children.Add(SceneGeometry.Lathe(Enumerable.Range(0, 21).Select(i => (i * radius / 20, depth * i * i / 400.0 - 0.0015)).ToArray(), paint, 48));
        dish.Children.Add(SceneGeometry.Lathe([(radius, depth - 0.0015), (radius + 0.0012, depth - 0.0008), (radius + 0.0012, depth + 0.0004), (radius, depth)], grey, 48));
        for (int leg = 0; leg < 4; leg++) Rod(dish, Bowl(radius * 0.86, Math.PI / 4 + leg * Math.PI / 2), new(0, 0.041, 0), 0.0011, grey);
        dish.Children.Add(SceneGeometry.Lathe([(0.0025, 0.048), (0.004, 0.044), (0.006, 0.039), (0.0045, 0.039)], dark, 16));
        dish.Children.Add(SceneGeometry.Lathe([(0, -0.0015), (0.012, -0.0015), (0.012, -0.010), (0, -0.010)], paint, 24));
        Box(dish, new(0, -0.002, 0), new(0.104, 0.004, 0.006), grey);
        Box(dish, new(0, -0.014, -0.010), new(0.020, 0.008, 0.012), dark);
        var result = new Model3DGroup();
        foreach (var group in new[] { foundation, yoke, dish }) result.Children.Add(SceneGeometry.MergeWithAtlas(group, false));
        result.Freeze(); return result;
    }
}