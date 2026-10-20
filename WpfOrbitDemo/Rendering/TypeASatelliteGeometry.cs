using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfOrbitDemo.Models;

namespace WpfOrbitDemo.Rendering;

// Type A: generic capsule-and-trunk spacecraft archetype (no logos, not a model of any specific vehicle).
// Local frame: +Y zenith, -Y nadir, +Z along track. A white conical capsule sits on a cylindrical service trunk,
// two long four-segment solar wings hinge about local X at the trunk's mid-height, and a small side-looking
// sensor panel under the trunk tilts toward the look direction. Dimensions are illustrative.
internal static class TypeASatelliteGeometry
{
    // Sensor joint frame: local +Y is the look direction, the panel lies in the joint's X–Z plane (Z ≈ along track).
    internal static Matrix3D AntennaPose(double seconds, bool capturing)
    {
        var y = (Vector3D)SceneGeometry.Point(SatelliteArticulation.TypeASensorDirection(seconds, capturing));
        var x = Vector3D.CrossProduct(y, new Vector3D(0, 0, 1)); x.Normalize();
        var z = Vector3D.CrossProduct(x, y);
        return new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, -0.048, 0, 1);
    }

    internal static Model3DGroup CreateInstance(Model3DGroup template, MatrixTransform3D wings, MatrixTransform3D antenna)
    {
        Model3DGroup Joint(int index, MatrixTransform3D transform)
            => new() { Children = new Model3DCollection(((Model3DGroup)template.Children[index]).Children.Select(SceneGeometry.CopySatelliteInstance)), Transform = transform };
        var instance = new Model3DGroup();
        instance.Children.Add(SceneGeometry.CopySatelliteInstance(template.Children[0]));
        instance.Children.Add(Joint(1, wings));
        instance.Children.Add(Joint(2, antenna));
        return instance;
    }

    public static Model3DGroup Create(Color accent, bool eclipsed)
    {
        Material Surface(Brush brush)
        {
            var material = new MaterialGroup(); material.Children.Add(SceneGeometry.SatelliteSurface(brush, eclipsed));
            material.Freeze(); return material;
        }
        var white = Surface(SatellitePatterns.Blanket(Color.FromRgb(238, 240, 242), Color.FromRgb(186, 190, 196)));
        var trunkSkin = Surface(SatellitePatterns.Radiator());
        var shield = Surface(new SolidColorBrush(Color.FromRgb(58, 52, 48)));
        var silver = Surface(SatellitePatterns.Brushed(Color.FromRgb(190, 196, 204)));
        var dark = Surface(new SolidColorBrush(Color.FromRgb(26, 28, 32)));
        var cells = Surface(SatellitePatterns.SolarCells(Color.FromRgb(14, 18, 36)));
        var frame = Surface(new SolidColorBrush(Color.FromRgb(132, 138, 146)));
        var array = Surface(SatellitePatterns.PatchArray());
        var marker = Surface(new SolidColorBrush(accent));

        var body = new Model3DGroup(); var wings = new Model3DGroup(); var antenna = new Model3DGroup();
        void Box(Model3DGroup group, double x, double y, double z, double sx, double sy, double sz, Material material)
            => group.Children.Add(SceneGeometry.Box(new(x, y, z), new(sx, sy, sz), material));
        void Add(Model3DGroup group, GeometryModel3D model) => group.Children.Add(model);

        // Service trunk (y -0.035 … 0.030) and capsule (heat-shield ring, conical shell, docking nose).
        Add(body, SceneGeometry.Lathe([(0, -0.035), (0.040, -0.035), (0.040, 0.030), (0.044, 0.030)], trunkSkin, 32));
        Add(body, SceneGeometry.Lathe([(0.044, 0.030), (0.047, 0.032), (0.046, 0.037), (0.044, 0.038)], shield, 32));
        Add(body, SceneGeometry.Lathe([(0.044, 0.038), (0.031, 0.088), (0.028, 0.096)], white, 32));
        Add(body, SceneGeometry.Lathe([(0.028, 0.096), (0.024, 0.100), (0.024, 0.108), (0.016, 0.110), (0, 0.110)], silver, 32));
        // Side details: thruster pods around the capsule, a hatch, and the colour tag on the trunk.
        for (int k = 0; k < 4; k++)
        {
            double a = Math.PI / 4 + k * Math.PI / 2;
            var pod = SceneGeometry.Box(new(0.040 * Math.Cos(a), 0.058, 0.040 * Math.Sin(a)), new(0.008, 0.010, 0.008), dark).Clone();
            pod.Transform = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), -a * 180 / Math.PI), 0.040 * Math.Cos(a), 0.058, 0.040 * Math.Sin(a));
            body.Children.Add(pod);
        }
        Box(body, 0, 0.068, 0.037, 0.016, 0.018, 0.004, dark);                 // hatch
        Box(body, 0, 0.020, 0.041, 0.014, 0.008, 0.003, marker);               // colour tag
        Box(body, 0, -0.040, 0, 0.010, 0.010, 0.010, frame);                   // sensor mast root

        // Solar wings: four segments per side on a short boom, hinged about local X at y = 0.
        foreach (double side in new[] { -1.0, 1.0 })
        {
            Box(wings, side * 0.046, 0, 0, 0.014, 0.004, 0.006, frame);
            for (int segment = 0; segment < 4; segment++)
            {
                double x = side * (0.0715 + segment * 0.036);
                Box(wings, x, 0, 0, 0.034, 0.002, 0.050, frame);
                Box(wings, x, 0.0012, 0, 0.032, 0.0004, 0.048, cells);
                Box(wings, x, -0.0012, 0, 0.032, 0.0004, 0.048, frame);
            }
        }

        // Side-looking sensor panel in its joint frame: 0.036 across × 0.064 along track, active face toward +Y.
        Box(antenna, 0, 0, 0, 0.038, 0.004, 0.066, frame);
        Box(antenna, 0, 0.0023, 0, 0.034, 0.0005, 0.062, array);

        var result = new Model3DGroup();
        result.Children.Add(SceneGeometry.MergeWithAtlas(body, eclipsed));
        result.Children.Add(SceneGeometry.MergeWithAtlas(wings, eclipsed));
        var head = SceneGeometry.MergeWithAtlas(antenna, eclipsed).Clone();
        head.Transform = new MatrixTransform3D(AntennaPose(0, false)); head.Freeze();
        result.Children.Add(head);
        result.Freeze(); return result;
    }
}

// Procedural surface patterns shared by the spacecraft and antenna models (baked once into bitmaps).
internal static class SatellitePatterns
{
    public static Brush Blanket(Color light, Color dark)
    {
        var drawing = new DrawingGroup(); var random = new Random(7);
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(light), null, new Rect(0, 0, 100, 100));
            for (int i = 0; i < 70; i++)
            {
                double x = random.Next(100), y = random.Next(100), w = 6 + random.Next(26), h = 2 + random.Next(8);
                byte a = (byte)random.Next(18, 60);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(a, dark.R, dark.G, dark.B)), null, new Rect(x, y, w, h)); // blanket creases
            }
            var seam = new Pen(new SolidColorBrush(dark), 0.8);
            dc.DrawLine(seam, new(50, 0), new(50, 100)); dc.DrawLine(seam, new(0, 50), new(100, 50));
        }
        drawing.Freeze(); return SceneGeometry.BakeBrush(drawing, 256, 256);
    }

    public static Brush SolarCells(Color cell)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(150, 156, 168)), null, new Rect(0, 0, 120, 70));
            var fill = new SolidColorBrush(cell); var bus = new Pen(new SolidColorBrush(Color.FromRgb(70, 82, 110)), 0.3);
            for (int y = 0; y < 7; y++) for (int x = 0; x < 12; x++)
            {
                var r = new Rect(x * 10 + 0.5, y * 10 + 0.5, 9, 9);
                dc.DrawRectangle(fill, null, r); dc.DrawLine(bus, new(r.Left, r.Top + 4.5), new(r.Right, r.Top + 4.5));
            }
            var tab = new SolidColorBrush(Color.FromRgb(196, 160, 80));
            for (int y = 0; y < 7; y += 3) dc.DrawRectangle(tab, null, new Rect(58, y * 10 + 3, 4, 3)); // interconnect tabs
        }
        drawing.Freeze(); return SceneGeometry.BakeBrush(drawing, 384, 224);
    }

    public static Brush PatchArray()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(214, 216, 210)), null, new Rect(0, 0, 50, 160));
            var patch = new SolidColorBrush(Color.FromRgb(176, 150, 92));
            for (int y = 0; y < 32; y++) for (int x = 0; x < 10; x++) dc.DrawRectangle(patch, null, new Rect(x * 5 + 1.4, y * 5 + 1.4, 2.2, 2.2));
        }
        drawing.Freeze(); return SceneGeometry.BakeBrush(drawing, 128, 384);
    }

    public static Brush Radiator()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(236, 238, 240)), null, new Rect(0, 0, 100, 100));
            var line = new Pen(new SolidColorBrush(Color.FromRgb(200, 204, 210)), 1);
            for (int i = 1; i < 10; i++) dc.DrawLine(line, new(i * 10, 0), new(i * 10, 100));
        }
        drawing.Freeze(); return SceneGeometry.BakeBrush(drawing, 128, 128);
    }

    public static Brush Brushed(Color color)
    {
        var drawing = new DrawingGroup(); var random = new Random(11);
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 100, 100));
            for (int i = 0; i < 60; i++)
            {
                byte d = (byte)random.Next(0, 24);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, (byte)Math.Max(0, color.R - d), (byte)Math.Max(0, color.G - d), (byte)Math.Max(0, color.B - d))), null,
                    new Rect(0, random.Next(100), 100, 0.6 + random.NextDouble()));
            }
        }
        drawing.Freeze(); return SceneGeometry.BakeBrush(drawing, 128, 128);
    }
}
