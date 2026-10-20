using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace WpfSatelliteTracking.Rendering;

// Type B: generic box-bus Earth-observation satellite archetype (not a model of any specific spacecraft).
// Local frame: +Y zenith, -Y nadir, +Z along track. A gold-blanketed cube bus, two two-segment solar wings on
// booms along ±X, top-deck antennas, and nadir instrument modules (a gold camera block with a lens and a silver
// electronics box). Fixed geometry; dimensions are illustrative.
internal static class TypeBSatelliteGeometry
{
    public static Model3DGroup Create(Color accent, bool eclipsed)
    {
        Material Surface(Brush brush)
        {
            var material = new MaterialGroup(); material.Children.Add(SceneGeometry.SatelliteSurface(brush, eclipsed));
            material.Freeze(); return material;
        }
        var gold = Surface(SatellitePatterns.Blanket(Color.FromRgb(214, 168, 56), Color.FromRgb(132, 90, 20)));
        var silver = Surface(SatellitePatterns.Brushed(Color.FromRgb(200, 204, 210)));
        var black = Surface(new SolidColorBrush(Color.FromRgb(14, 16, 20)));
        var glass = Surface(SceneGeometry.BakeBrush(new RadialGradientBrush(Color.FromRgb(46, 70, 104), Color.FromRgb(6, 10, 20)), 64, 64));
        var cells = Surface(SatellitePatterns.SolarCells(Color.FromRgb(30, 46, 120)));
        var frame = Surface(new SolidColorBrush(Color.FromRgb(140, 146, 154)));
        var marker = Surface(new SolidColorBrush(accent));
        var parts = new Model3DGroup();
        void Box(double x, double y, double z, double sx, double sy, double sz, Material material)
            => parts.Children.Add(SceneGeometry.Box(new(x, y, z), new(sx, sy, sz), material));
        void Add(GeometryModel3D model, double x, double y, double z)
        {
            model = model.Clone(); model.Transform = new TranslateTransform3D(x, y, z); parts.Children.Add(model);
        }

        // Bus with silver frame edges on the top deck.
        Box(0, 0, 0, 0.080, 0.080, 0.080, gold);
        foreach (double s in new[] { -1.0, 1.0 })
        {
            Box(s * 0.039, 0.041, 0, 0.003, 0.003, 0.082, frame);
            Box(0, 0.041, s * 0.039, 0.082, 0.003, 0.003, frame);
        }
        // Top deck: two whip antennas, a small tripod mast and the colour tag.
        foreach (double s in new[] { -1.0, 1.0 }) Box(s * 0.024, 0.056, -0.024, 0.0015, 0.028, 0.0015, frame);
        Add(SceneGeometry.Lathe([(0.006, 0), (0.006, 0.010), (0.004, 0.012)], silver, 16), 0.020, 0.040, 0.022);
        Box(-0.020, 0.0415, 0.022, 0.014, 0.003, 0.010, marker);
        // Nadir instruments: gold camera block with lens barrel, silver electronics box, small sensor.
        Box(-0.008, -0.056, 0.004, 0.040, 0.032, 0.044, gold);
        Add(SceneGeometry.Lathe([(0.014, 0), (0.014, -0.010), (0.016, -0.012), (0.011, -0.012)], black, 32), -0.008, -0.072, 0.004);
        Add(SceneGeometry.Lathe([(0, -0.011), (0.011, -0.011)], glass, 32), -0.008, -0.072, 0.004);
        Box(0.026, -0.054, -0.012, 0.026, 0.028, 0.030, silver);
        Add(SceneGeometry.Lathe([(0.004, 0), (0.004, -0.008)], black, 12), 0.026, -0.068, 0.012);
        // Two solar wings along ±X: boom, yoke and two hinged segments each.
        foreach (double side in new[] { -1.0, 1.0 })
        {
            Box(side * 0.050, 0.010, 0, 0.020, 0.003, 0.003, frame);
            Box(side * 0.061, 0.010, 0, 0.003, 0.003, 0.040, frame);
            for (int segment = 0; segment < 2; segment++)
            {
                double x = side * (0.1015 + segment * 0.079);
                Box(x, 0.010, 0, 0.076, 0.003, 0.058, frame);
                Box(x, 0.0118, 0, 0.072, 0.0005, 0.054, cells);
                Box(x, 0.0082, 0, 0.072, 0.0005, 0.054, frame);
            }
        }
        return SceneGeometry.MergeWithAtlas(parts, eclipsed);
    }
}
