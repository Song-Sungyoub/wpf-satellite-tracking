using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using WpfSatelliteTracking.Models;

namespace WpfSatelliteTracking.Rendering;

public static class SceneGeometry
{
    public static GeometryModel3D Sphere(double radius, Material material, int slices = 96, int stacks = 48)
    {
        var mesh = new MeshGeometry3D();
        for (int y = 0; y <= stacks; y++)
        {
            double lat = Math.PI / 2 - Math.PI * y / stacks;
            for (int x = 0; x <= slices; x++)
            {
                double lon = 2 * Math.PI * x / slices - Math.PI;
                var normal = new Vector3D(Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), -Math.Cos(lat) * Math.Sin(lon));
                mesh.Positions.Add(new Point3D(normal.X * radius, normal.Y * radius, normal.Z * radius));
                mesh.Normals.Add(normal);
                mesh.TextureCoordinates.Add(new Point((double)x / slices, (double)y / stacks));
            }
        }
        for (int y = 0; y < stacks; y++)
            for (int x = 0; x < slices; x++)
            {
                int a = y * (slices + 1) + x, b = a + slices + 1;
                foreach (int index in new[] { a, b, a + 1, a + 1, b, b + 1 }) mesh.TriangleIndices.Add(index);
            }
        mesh.Freeze();
        return new GeometryModel3D(mesh, material);
    }

    // An open, time-stamped SGP4 path, not a fixed Keplerian ring.
    public static GeometryModel3D PredictedOrbit(SatelliteOrbit orbit, double startSeconds, Color color, double? durationSeconds = null, double width = 0.0013)
        => Trajectory(PredictedOrbitMesh(orbit, startSeconds, durationSeconds ?? orbit.PeriodSeconds, width), color);
    // Frozen mesh only: safe to build on a background thread and hand to the UI thread.
    public static MeshGeometry3D PredictedOrbitMesh(SatelliteOrbit orbit, double startSeconds, double durationSeconds, double width = 0.0013)
        => TrajectoryMesh(Enumerable.Range(0, 193).Select(i => Point(orbit.Position(startSeconds + durationSeconds * i / 192))).ToArray(), width);

    // Each future point uses its OWN sidereal angle before projection onto WGS84.
    // Parent this path to earthPose once; never apply the scene's current rotation twice.
    // The same future path at orbit altitude in Earth-fixed coordinates: it lies directly above the ground track.
    // Parent it to the Earth pose, like the ground track.
    public static MeshGeometry3D EarthFixedOrbitMesh(SatelliteOrbit orbit, double startSeconds, double duration, double width = 0.0013)
    {
        int samples = Math.Max(192, (int)Math.Ceiling(duration / Math.Max(20, orbit.PeriodSeconds / 300)));
        return TrajectoryMesh(Enumerable.Range(0, samples + 1).Select(i => Point(orbit.EarthFixedPosition(startSeconds + duration * i / samples))).ToArray(), width);
    }
    public static GeometryModel3D GroundTrack(SatelliteOrbit orbit, double startSeconds, double duration, Color color)
        => Trajectory(GroundTrackMesh(orbit, startSeconds, duration), color);
    public static MeshGeometry3D GroundTrackMesh(SatelliteOrbit orbit, double startSeconds, double duration)
    {
        // Sample absolute timestamps rather than shifting every vertex with the rolling window.
        // Shared future times therefore retain exactly the same polyline on the map after refresh.
        // About 300 samples per revolution: 20 s for LEO, coarser for MEO/GEO.
        double stepSeconds = Math.Max(20, Math.Round(orbit.PeriodSeconds / 300 / 20) * 20);
        var times = new List<double> { startSeconds };
        double endSeconds = startSeconds + duration;
        for (double t = (Math.Floor(startSeconds / stepSeconds) + 1) * stepSeconds; t < endSeconds; t += stepSeconds)
            times.Add(t);
        times.Add(endSeconds);
        var points = times.Select(t =>
        {
            var geo = EarthCoordinates.Geodetic(orbit.EarthFixedPosition(t));
            return Point(EarthCoordinates.Surface(geo.Latitude, geo.Longitude, 0.0092 * SatelliteOrbit.EarthRadiusKm));
        }).ToArray();
        return TrajectoryMesh(points, 0.0010);
    }
    public static GeometryModel3D Trajectory(MeshGeometry3D mesh, Color color)
    {
        var material = MarkerMaterial(color);
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }
    private static MeshGeometry3D TrajectoryMesh(Point3D[] points, double width)
    {
        var mesh = new MeshGeometry3D();
        for (int i = 0; i < points.Length; i++)
        {
            var tangent = points[Math.Min(i + 1, points.Length - 1)] - points[Math.Max(0, i - 1)];
            var side = Vector3D.CrossProduct((Vector3D)points[i], tangent);
            if (side.LengthSquared < 1e-20) side = new Vector3D(1, 0, 0);
            side.Normalize();
            mesh.Positions.Add(points[i] + side * width); mesh.Positions.Add(points[i] - side * width);
            if (i > 0) { int n = (i - 1) * 2; foreach (int k in new[]{n,n+2,n+1,n+1,n+2,n+3}) mesh.TriangleIndices.Add(k); }
        }
        mesh.Freeze(); return mesh;
    }
    public static GeometryModel3D EarthSphere(double radius, Material material, int slices = 128, int stacks = 64)
    {
        var sphere = Sphere(radius, material, slices, stacks);
        var mesh = ((MeshGeometry3D)sphere.Geometry).Clone();
        for (int i = 0; i < mesh.Positions.Count; i++)
        {
            var uv = mesh.TextureCoordinates[i];
            mesh.Positions[i] = Point(EarthCoordinates.Surface(90 - uv.Y * 180, uv.X * 360 - 180, (radius - 1) * SatelliteOrbit.EarthRadiusKm));
        }
        mesh.Freeze(); sphere.Geometry = mesh; return sphere;
    }

    public static Point3D Point((double X, double Y, double Z) value) => new(value.X, value.Y, value.Z);

    public static GeometryModel3D PlanetOrbit((double X, double Y, double Z)[] path, Color color)
    {
        var mesh = new MeshGeometry3D();
        double thickness = 0.0025 * Math.Max(1, path.Max(p => ((Vector3D)Point(p)).Length));
        for (int i = 0; i < path.Length; i++)
        {
            var point = Point(path[i]);
            var radial = (Vector3D)point; radial.Normalize();
            var tangent = Point(path[(i + 1) % (path.Length - 1)]) - point; tangent.Normalize();
            var normal = Vector3D.CrossProduct(radial, tangent); normal.Normalize();
            for (int j = 0; j < 6; j++)
                mesh.Positions.Add(point + thickness * (radial * Math.Cos(j * Math.PI / 3) + normal * Math.Sin(j * Math.PI / 3)));
            if (i == 0) continue;
            for (int j = 0; j < 6; j++)
            {
                int a = (i - 1) * 6 + j, b = (i - 1) * 6 + (j + 1) % 6;
                foreach (int index in new[] { a, b, a + 6, b, b + 6, a + 6 }) mesh.TriangleIndices.Add(index);
            }
        }
        mesh.Freeze();
        var material = MarkerMaterial(color);
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material }; model.Freeze(); return model;
    }

    public static GeometryModel3D Clouds()
    {
        const int width = 2048, height = 1024;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double value = 0, weight = 0.5;
                for (int cells = 8; cells <= 128; cells *= 2)
                {
                    double sx = (double)x / width * cells + 0.8 * Math.Sin((double)y / height * 18);
                    double sy = (double)y / height * cells;
                    int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy);
                    double fx = sx - Math.Floor(sx), fy = sy - iy;
                    fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
                    double a = Noise(ix, iy, cells) * (1 - fx) + Noise(ix + 1, iy, cells) * fx;
                    double b = Noise(ix, iy + 1, cells) * (1 - fx) + Noise(ix + 1, iy + 1, cells) * fx;
                    value += (a * (1 - fy) + b * fy) * weight; weight *= 0.5;
                }
                byte alpha = (byte)(Math.Clamp((value - 0.48) * 4, 0, 1) * 120);
                int index = (y * width + x) * 4;
                pixels[index] = pixels[index + 1] = pixels[index + 2] = pixels[index + 3] = alpha;
            }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4); bitmap.Freeze();
        var material = new DiffuseMaterial(new ImageBrush(bitmap)); material.Freeze();
        var clouds = EarthSphere(1.008, material, 128, 64); clouds.Freeze(); return clouds;
    }

    private static double Noise(int x, int y, int cells)
    {
        uint h = unchecked((uint)(((x % cells + cells) % cells) * 374761393 + y * 668265263));
        h = unchecked((h ^ (h >> 13)) * 1274126177); h ^= h >> 16;
        return (h & 0xffff) / 65535.0;
    }

    public static Model3DGroup Atmosphere()
    {
        var group = new Model3DGroup();
        foreach (var layer in new[] { (Radius: 1.006, Alpha: (byte)55), (Radius: 1.012, Alpha: (byte)18) })
        {
            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(Brushes.Transparent));
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(layer.Alpha, 65, 155, 255))));
            material.Freeze();
            var sphere = EarthSphere(layer.Radius, material, 96, 48);
            sphere.Material = null; sphere.BackMaterial = material; sphere.Freeze(); group.Children.Add(sphere);
        }
        group.Freeze(); return group;
    }

    public static bool Contains(Model3D root, Model3D target) => ReferenceEquals(root, target)
        || root is Model3DGroup group && group.Children.Any(child => Contains(child, target));

    // Unique model identities preserve picking while frozen meshes/materials are shared across the fleet.
    public static Model3D CopySatelliteInstance(Model3D template)
    {
        Model3D instance = template is GeometryModel3D geometry
            ? new GeometryModel3D(geometry.Geometry, geometry.Material) { BackMaterial = geometry.BackMaterial }
            : new Model3DGroup { Children = new Model3DCollection(((Model3DGroup)template).Children.Select(CopySatelliteInstance)) };
        instance.Transform = template.Transform;
        instance.Freeze();
        return instance;
    }

    public static Model3DGroup Satellite(Color accent, bool eclipsed = false, SatelliteMission mission = SatelliteMission.A)
        => mission == SatelliteMission.A ? TypeASatelliteGeometry.Create(accent, eclipsed) : TypeBSatelliteGeometry.Create(accent, eclipsed);
    // Distant level of detail: a few boxes and one coarse lathe per type in one merged mesh, matching the full models'
    // silhouettes and colours (white capsule/trunk + long dark wings; gold cube + blue wings) at a few pixels.
    internal static Model3DGroup LowDetailSatellite(Color accent, bool eclipsed, SatelliteMission mission)
    {
        var body = SatelliteSurface(new SolidColorBrush(mission == SatelliteMission.A ? Color.FromRgb(232, 234, 236) : Color.FromRgb(210, 164, 54)), eclipsed);
        var panel = SatelliteSurface(new SolidColorBrush(mission == SatelliteMission.A ? Color.FromRgb(20, 24, 44) : Color.FromRgb(34, 50, 124)), eclipsed);
        var marker = SatelliteSurface(new SolidColorBrush(accent), eclipsed);
        var parts = new Model3DGroup();
        if (mission == SatelliteMission.A)
        {
            parts.Children.Add(Lathe([(0, -0.035), (0.040, -0.035), (0.044, 0.035), (0.028, 0.096), (0.016, 0.110), (0, 0.110)], body, 8));
            foreach (double side in new[] { -1.0, 1.0 }) parts.Children.Add(Box(new(side * 0.122, 0, 0), new(0.150, 0.003, 0.050), panel));
            parts.Children.Add(Box(new(0, 0.020, 0.041), new(0.014, 0.008, 0.004), marker));
        }
        else
        {
            parts.Children.Add(Box(new(0, 0, 0), new(0.080, 0.080, 0.080), body));
            parts.Children.Add(Box(new(0, -0.058, 0), new(0.060, 0.036, 0.044), body));
            foreach (double side in new[] { -1.0, 1.0 }) parts.Children.Add(Box(new(side * 0.140, 0.010, 0), new(0.160, 0.003, 0.058), panel));
            parts.Children.Add(Box(new(-0.020, 0.0415, 0.022), new(0.014, 0.004, 0.010), marker));
        }
        return MergeWithAtlas(parts, eclipsed);
    }
    // Batch a part group into ONE mesh with ONE material: every distinct surface brush is packed into a texture atlas and
    // the UVs remapped into its cell. WPF has no instancing and draws each MaterialGroup layer as a separate pass, so this
    // cuts a Type A from ~38 draws to 6. Double-sided parts get explicit reversed triangles instead of a BackMaterial pass.
    // Trade-off: the specular highlight layer is dropped.
    internal static Model3DGroup MergeWithAtlas(Model3DGroup parts, bool eclipsed)
    {
        var items = new List<(GeometryModel3D Model, Matrix3D Matrix)>();
        void Collect(Model3D part, Matrix3D parent)
        {
            var matrix = part.Transform.Value; matrix.Append(parent);
            if (part is Model3DGroup group) { foreach (var child in group.Children) Collect(child, matrix); return; }
            items.Add(((GeometryModel3D)part, matrix));
        }
        Collect(parts, Matrix3D.Identity);
        // SatelliteSurface keeps the surface brush on its emissive layer (diffuse turns black when eclipsed); groups may nest.
        static Brush? Emissive(Material material) => material switch
        {
            EmissiveMaterial emissive => emissive.Brush,
            MaterialGroup group => group.Children.Select(Emissive).FirstOrDefault(brush => brush is not null),
            _ => null
        };
        static Brush SurfaceBrush(Material material) => Emissive(material) ?? (material as DiffuseMaterial)?.Brush ?? Brushes.Gray;
        var brushes = new List<Brush>();
        var cells = new int[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var brush = SurfaceBrush(items[i].Model.Material);
            int index = brushes.FindIndex(b => ReferenceEquals(b, brush));
            if (index < 0) { index = brushes.Count; brushes.Add(brush); }
            cells[i] = index;
        }
        const int cell = 256, pad = 6;
        int columns = (int)Math.Ceiling(Math.Sqrt(brushes.Count)), rows = (int)Math.Ceiling(brushes.Count / (double)columns);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // These are opaque solid parts. Fill the entire atlas, including unused
            // cells and brush-edge padding, so WPF keeps opaque depth occlusion.
            dc.DrawRectangle(Brushes.Gray, null, new Rect(0, 0, columns * cell, rows * cell));
            for (int k = 0; k < brushes.Count; k++)
            {
                var outer = new Rect(k % columns * cell, k / columns * cell, cell, cell);
                dc.DrawRectangle(brushes[k], null, outer); // padding bleeds the same surface
                dc.DrawRectangle(brushes[k], null, new Rect(outer.X + pad, outer.Y + pad, cell - 2 * pad, cell - 2 * pad));
            }
        }
        var atlas = new RenderTargetBitmap(columns * cell, rows * cell, 96, 96, PixelFormats.Pbgra32);
        atlas.Render(visual); atlas.Freeze();
        var atlasBrush = new ImageBrush(atlas) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        atlasBrush.Freeze();
        var mesh = new MeshGeometry3D();
        for (int i = 0; i < items.Count; i++)
        {
            var (model, matrix) = items[i];
            var source = (MeshGeometry3D)model.Geometry;
            int k = cells[i];
            double u0 = (k % columns * cell + pad) / (double)(columns * cell), v0 = (k / columns * cell + pad) / (double)(rows * cell);
            double du = (cell - 2 * pad) / (double)(columns * cell), dv = (cell - 2 * pad) / (double)(rows * cell);
            bool hasUv = source.TextureCoordinates.Count == source.Positions.Count;
            for (int side = 0; side < (model.BackMaterial is null ? 1 : 2); side++)
            {
                int start = mesh.Positions.Count;
                for (int p = 0; p < source.Positions.Count; p++)
                {
                    mesh.Positions.Add(matrix.Transform(source.Positions[p]));
                    var uv = hasUv ? source.TextureCoordinates[p] : new Point(0.5, 0.5);
                    mesh.TextureCoordinates.Add(new Point(u0 + Math.Clamp(uv.X, 0, 1) * du, v0 + Math.Clamp(uv.Y, 0, 1) * dv));
                }
                var indices = source.TriangleIndices;
                for (int t = 0; t + 2 < indices.Count; t += 3)
                {
                    mesh.TriangleIndices.Add(start + indices[t]);
                    if (side == 0) { mesh.TriangleIndices.Add(start + indices[t + 1]); mesh.TriangleIndices.Add(start + indices[t + 2]); }
                    else { mesh.TriangleIndices.Add(start + indices[t + 2]); mesh.TriangleIndices.Add(start + indices[t + 1]); }
                }
            }
        }
        // Explicit area-weighted normals (what WPF would generate) so batched copies can be transformed instead of regenerated.
        var normals = new Vector3D[mesh.Positions.Count];
        for (int t = 0; t + 2 < mesh.TriangleIndices.Count; t += 3)
        {
            int a = mesh.TriangleIndices[t], b = mesh.TriangleIndices[t + 1], c = mesh.TriangleIndices[t + 2];
            var face = Vector3D.CrossProduct(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            normals[a] += face; normals[b] += face; normals[c] += face;
        }
        foreach (var n in normals) { var unit = n; if (unit.LengthSquared > 1e-30) unit.Normalize(); mesh.Normals.Add(unit); }
        mesh.Freeze();
        var result = new Model3DGroup();
        result.Children.Add(new GeometryModel3D(mesh, SatelliteSurface(atlasBrush, eclipsed)));
        result.Freeze();
        return result;
    }

    internal static Material SatelliteSurface(Brush brush, bool eclipsed)
    {
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(eclipsed ? Brushes.Black : brush));
        byte brightness = eclipsed ? (byte)28 : (byte)65;
        material.Children.Add(new EmissiveMaterial(brush) { Color = Color.FromRgb(brightness, brightness, brightness) });
        material.Freeze(); return material;
    }

    internal static GeometryModel3D Box(Point3D center, Vector3D size, Material material)
    {
        var mesh = new MeshGeometry3D();
        Vector3D[] normals = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
        foreach (var normal in normals)
        {
            var u = Math.Abs(normal.Y) > 0.5 ? new Vector3D(1, 0, 0) : Vector3D.CrossProduct(new Vector3D(0, 1, 0), normal);
            var v = Vector3D.CrossProduct(normal, u);
            int first = mesh.Positions.Count;
            foreach (var uv in new[] { new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1) })
            {
                var corner = normal + u * (uv.X * 2 - 1) + v * (uv.Y * 2 - 1);
                mesh.Positions.Add(center + new Vector3D(corner.X * size.X / 2, corner.Y * size.Y / 2, corner.Z * size.Z / 2));
                mesh.Normals.Add(normal); mesh.TextureCoordinates.Add(uv);
            }
            foreach (int index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(first + index);
        }
        mesh.Freeze();
        var model = new GeometryModel3D(mesh, material); model.Freeze(); return model;
    }

    public static Matrix3D SatellitePose(SatelliteOrbit orbit, double seconds)
    {
        var position = Point(orbit.Position(seconds));
        var up = (Vector3D)position; up.Normalize();
        var forward = Point(orbit.Position(seconds + 1)) - Point(orbit.Position(seconds - 1)); forward.Normalize();
        var right = Vector3D.CrossProduct(up, forward); right.Normalize();
        return new Matrix3D(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0, position.X, position.Y, position.Z, 1);
    }

    internal static GeometryModel3D Lathe((double Radius, double Height)[] profile, Material material, int sides = 32)
    {
        var mesh = new MeshGeometry3D();
        for (int row = 0; row < profile.Length; row++)
            for (int j = 0; j <= sides; j++)
            {
                var p = profile[row];
                double a = j * 2 * Math.PI / sides;
                mesh.Positions.Add(new Point3D(p.Radius * Math.Cos(a), p.Height, p.Radius * Math.Sin(a)));
                mesh.TextureCoordinates.Add(new Point(j / (double)sides, row / (double)Math.Max(1, profile.Length - 1)));
            }
        for (int row = 0; row < profile.Length - 1; row++)
            for (int j = 0; j < sides; j++)
            {
                int a = row * (sides + 1) + j, b = a + sides + 1;
                foreach (int index in new[] { a, a + 1, b, a + 1, b + 1, b }) mesh.TriangleIndices.Add(index);
            }
        mesh.Freeze();
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    public static Model3DGroup SpaceBackground() => StarField.Create();

    // Vector (Drawing/gradient) brushes on 3D meshes are re-rasterised whenever their on-screen size changes, which with
    // 44 moving satellites happens every frame. Bake them once into bitmaps, like the planet surfaces.
    internal static ImageBrush BakeBrush(Drawing drawing, int width, int height)
    {
        var bounds = drawing.Bounds;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(width / bounds.Width, height / bounds.Height));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            dc.DrawDrawing(drawing);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        var brush = new ImageBrush(bitmap); brush.Freeze();
        return brush;
    }
    internal static ImageBrush BakeBrush(Brush fill, int width, int height)
        => BakeBrush(new GeometryDrawing(fill, null, new RectangleGeometry(new Rect(0, 0, width, height))), width, height);

    public static Material MarkerMaterial(Color color)
    {
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(Brushes.Black));
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(color)));
        material.Freeze();
        return material;
    }
}
