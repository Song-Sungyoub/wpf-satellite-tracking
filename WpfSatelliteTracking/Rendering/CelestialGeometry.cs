using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace WpfSatelliteTracking.Rendering;

public static class CelestialGeometry
{
    private static readonly RotateTransform3D SaturnTilt = new(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 27));

    public static GeometryModel3D SaturnRings()
    {
        var mesh = new MeshGeometry3D();
        double radius = Radii[7];
        const int segments = 256, textureWidth = 4096;
        var pixels = new byte[textureWidth * 4];
        for (int x = 0; x < textureWidth; x++)
        {
            double r = 1.24 + 1.08 * x / (textureWidth - 1);
            var (red, green, blue, alpha) = RingSample(r);
            int p = x * 4;
            pixels[p] = (byte)Math.Clamp(blue, 0, 255); pixels[p + 1] = (byte)Math.Clamp(green, 0, 255);
            pixels[p + 2] = (byte)Math.Clamp(red, 0, 255); pixels[p + 3] = (byte)Math.Clamp(alpha * 255, 0, 255);
        }
        foreach (var (inner, outer) in new[] { (1.24, 1.52), (1.53, 1.95), (2.03, 2.26), (2.29, 2.32) })
        {
            int start = mesh.Positions.Count;
            for (int i = 0; i <= segments; i++)
            {
                double a = i * 2 * Math.PI / segments;
                foreach (double r in new[] { inner, outer })
                {
                    mesh.Positions.Add(SaturnTilt.Transform(new Point3D(radius * r * Math.Cos(a), 0, radius * r * Math.Sin(a))));
                    mesh.Normals.Add(SaturnTilt.Transform(new Vector3D(0, 1, 0)));
                    mesh.TextureCoordinates.Add(new Point((r - 1.24) / 1.08, 0.5));
                }
                if (i == 0) continue;
                int k = start + (i - 1) * 2;
                foreach (int index in new[] { k, k + 2, k + 1, k + 1, k + 2, k + 3 }) mesh.TriangleIndices.Add(index);
            }
        }
        var texture = BitmapSource.Create(textureWidth, 1, 96, 96, PixelFormats.Bgra32, null, pixels, textureWidth * 4); texture.Freeze();
        // Every ring UV has V = 0.5; the default bounding-box mapping would collapse to zero height and draw nothing.
        var brush = new ImageBrush(texture) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality); // Filtered minification limits ringlet shimmer from afar.
        brush.Freeze();
        mesh.Freeze();
        // Diffuse N·L goes black when sunlight grazes the ring plane; a faint emissive term stands in for scattered/transmitted light.
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(brush));
        material.Children.Add(new EmissiveMaterial(brush) { Color = Color.FromRgb(70, 70, 70) });
        material.Freeze();
        // shortcut: illustrative ring bands, opacity and fixed pole; use measured optical depth/pole ephemeris for scientific views.
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material }; model.Freeze(); return model;
    }
    // Radial colour/opacity at r Saturn radii: faint C ring, dense bright B ring, translucent A ring with the Encke gap, thin F ring.
    // Approximate ring boundaries only; ringlets are deterministic noise, not measured profiles.
    private static (double Red, double Green, double Blue, double Alpha) RingSample(double r)
    {
        double fine = RingNoise(r * 1400, 11), mid = RingNoise(r * 380, 23), broad = RingNoise(r * 70, 37);
        double ringlets = 0.5 * fine + 0.3 * mid + 0.2 * broad; // 0..1
        double brightness, alpha;
        (double R, double G, double B) tint;
        if (r < 1.527)
        {
            double plateau = r > 1.43 && r < 1.50 && Math.Abs(RingNoise(r * 160, 5) - 0.5) < 0.18 ? 0.18 : 0;
            tint = (150, 136, 120); brightness = 0.45 + 0.25 * ringlets + plateau; alpha = 0.16 + 0.16 * ringlets + plateau * 1.5;
            alpha *= Fade(r - 1.24, 0.004); // inner rim
        }
        else if (r < 1.99)
        {
            double inner = Math.Clamp((r - 1.527) / 0.12, 0, 1);
            tint = (230, 208, 172); brightness = 0.78 + 0.16 * inner + 0.16 * (ringlets - 0.5); alpha = 0.78 + 0.18 * inner + 0.08 * (ringlets - 0.5);
            alpha *= Fade(1.95 - r, 0.002); // sharp outer B edge at the Cassini Division
        }
        else if (r < 2.275)
        {
            tint = (206, 193, 170); brightness = 0.74 + 0.14 * (ringlets - 0.5); alpha = 0.62 + 0.10 * (ringlets - 0.5);
            alpha *= (1 - Bump(r, 2.214, 0.0045)) * Fade(r - 2.03, 0.003) * Fade(2.26 - r, 0.004); // Encke gap and A ring rims
        }
        else
        {
            tint = (222, 216, 204); brightness = 0.95; alpha = 0.6 * Bump(r, 2.305, 0.0018); // F ring
        }
        return (tint.R * brightness, tint.G * brightness, tint.B * brightness, Math.Clamp(alpha, 0, 1));
    }
    private static double Smooth(double t) => t * t * (3 - 2 * t);
    private static double Fade(double distanceInside, double width) => Smooth(Math.Clamp(distanceInside / width, 0, 1));
    private static double Bump(double r, double center, double halfWidth) => Smooth(Math.Clamp(1 - Math.Abs(r - center) / halfWidth, 0, 1));
    private static double RingNoise(double t, int seed)
    {
        int i = (int)Math.Floor(t); double f = t - i; f = f * f * (3 - 2 * f);
        static double Hash(int n, int s) { uint h = unchecked((uint)(n * 374761393 + s * 668265263)); h = unchecked((h ^ (h >> 13)) * 1274126177); return ((h ^ (h >> 16)) & 0xffff) / 65535.0; }
        return Hash(i, seed) * (1 - f) + Hash(i + 1, seed) * f;
    }
    public static readonly double[] Radii = [1.8, 0.273, 0.383, 0.95, 1, 0.532, 10.97, 9.14, 3.98, 3.86];
    // The Sun, Mercury and Venus appear in both the Earth and solar scenes: generate each frozen surface once.
    private static readonly Material?[] Materials = new Material?[10];
    private static Material SurfaceMaterial(int body)
    {
        if (Materials[body] is { } cached) return cached;
        var texture = body == 1 ? CrateredSurface.CreateTexture(CrateredSurface.Moon) : PlanetSurfaces.Create(body);
        var brush = new ImageBrush(texture);
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();
        Material material;
        if (body == 0)
        {
            var glow = new MaterialGroup(); glow.Children.Add(new DiffuseMaterial(Brushes.Black)); glow.Children.Add(new EmissiveMaterial(brush)); material = glow;
        }
        else material = new DiffuseMaterial(brush);
        material.Freeze();
        return Materials[body] = material;
    }
    // Close-up detail (Jupiter's vortices) is too slow for startup: build it in the background a few seconds later and
    // replace the model's material on its own dispatcher. The frozen material is safe to hand across threads.
    private static void SwapInDetailedSurface(GeometryModel3D model, int body)
    {
        var dispatcher = model.Dispatcher;
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var brush = new ImageBrush(PlanetSurfaces.CreateDetailed(body, CancellationToken.None));
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            brush.Freeze();
            var material = new DiffuseMaterial(brush); material.Freeze();
            return material;
        }).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && !dispatcher.HasShutdownStarted)
                dispatcher.InvokeAsync(() => { Materials[body] = task.Result; model.Material = task.Result; }, System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }
    public static GeometryModel3D Create(int body)
    {
        if (body is < 0 or > 9 || body == 4) throw new ArgumentOutOfRangeException(nameof(body));
        // Static illustrative surfaces; no NASA imagery, displacement or per-frame texture generation.
        bool detailed = body is 1 or 6 or 7;
        var model = SceneGeometry.Sphere(Radii[body], SurfaceMaterial(body), detailed ? 128 : 64, detailed ? 64 : 32);
        if (body == 6) SwapInDetailedSurface(model, body);
        if (body == 7)
        {
            var mesh = ((MeshGeometry3D)model.Geometry).Clone();
            for (int i = 0; i < mesh.Positions.Count; i++)
            {
                var p = mesh.Positions[i]; var n = mesh.Normals[i];
                mesh.Positions[i] = SaturnTilt.Transform(new Point3D(p.X, p.Y * 0.902, p.Z));
                n = SaturnTilt.Transform(new Vector3D(n.X, n.Y / 0.902, n.Z)); n.Normalize();
                mesh.Normals[i] = n;
            }
            mesh.Freeze(); model.Geometry = mesh;
        }
        return model;
    }
}
