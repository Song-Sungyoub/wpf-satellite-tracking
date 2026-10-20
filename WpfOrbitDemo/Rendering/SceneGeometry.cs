using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using WpfOrbitDemo.Models;

namespace WpfOrbitDemo.Rendering;

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

    // All inertial orbit rings of one ground-track group (each member has its own plane) merged into one mesh.
    public static GeometryModel3D OrbitGroup(IReadOnlyList<CircularOrbit> orbits, Color color)
    {
        var mesh = new MeshGeometry3D();
        const int steps = 256, sides = 4;
        foreach (var orbit in orbits)
        {
            int start = mesh.Positions.Count;
            for (int s = 0; s <= steps; s++)
            {
                double angle = s * Math.PI * 2 / steps;
                var p = Point(orbit.PositionAtAngle(angle));
                var radial = (Vector3D)p; radial.Normalize();
                var tangent = Point(orbit.PositionAtAngle(angle + 0.001)) - p; tangent.Normalize();
                var normal = Vector3D.CrossProduct(radial, tangent); normal.Normalize();
                for (int j = 0; j < sides; j++)
                {
                    double t = j * 2 * Math.PI / sides;
                    mesh.Positions.Add(p + 0.0022 * (radial * Math.Cos(t) + normal * Math.Sin(t)));
                }
            }
            for (int s = 0; s < steps; s++)
                for (int j = 0; j < sides; j++)
                {
                    int a = start + s * sides + j, b = start + s * sides + (j + 1) % sides;
                    foreach (int k in new[] { a, b, a + sides, b, b + sides, a + sides }) mesh.TriangleIndices.Add(k);
                }
        }
        mesh.Freeze();
        var material = MarkerMaterial(color);
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    // Earth-fixed ground track (sub-satellite path) over one repeat cycle, as a thin ribbon just above the clouds;
    // parent it to the rotating Earth group. Earth-fixed = inertial rotated by -rotation about +Y.
    public static GeometryModel3D GroundTrack(CircularOrbit orbit, double startSeconds, double cycleSeconds, double rotationAtStartDegrees, Color color)
    {
        var mesh = new MeshGeometry3D();
        int steps = (int)Math.Ceiling(cycleSeconds / 20);
        const double radius = 1.0092, halfWidth = 0.0011;
        Vector3D Fixed(double t)
        {
            var p = orbit.Position(startSeconds + t);
            double theta = (rotationAtStartDegrees + SatelliteOperations.EarthRotationDegreesPerSecond * t) * Math.PI / 180;
            var v = new Vector3D(p.X * Math.Cos(theta) - p.Z * Math.Sin(theta), p.Y, p.X * Math.Sin(theta) + p.Z * Math.Cos(theta));
            v.Normalize(); return v;
        }
        var previous = Fixed(0);
        for (int s = 0; s <= steps; s++)
        {
            var point = Fixed(s * cycleSeconds / steps);
            var next = Fixed((s + 1) * cycleSeconds / steps);
            var side = Vector3D.CrossProduct(point, next - point); side.Normalize();
            mesh.Positions.Add((Point3D)(point * radius + side * halfWidth)); mesh.Positions.Add((Point3D)(point * radius - side * halfWidth));
            if (s > 0) { int i = (s - 1) * 2; foreach (int k in new[] { i, i + 2, i + 1, i + 1, i + 2, i + 3 }) mesh.TriangleIndices.Add(k); }
            previous = point;
        }
        mesh.Freeze();
        var material = MarkerMaterial(color);
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material }; model.Freeze();
        return model;
    }

    public static GeometryModel3D Orbit(CircularOrbit orbit, Color color, bool fullRing = false)
    {
        var mesh = new MeshGeometry3D();
        const int steps = 512, sides = 6;
        double gap = fullRing ? 0 : 0.16;
        for (int s = 0; s <= steps; s++)
        {
            double angle = orbit.InitialPhaseDegrees * Math.PI / 180 + gap + s * (Math.PI * 2 - 2 * gap) / steps;
            var p = Point(orbit.PositionAtAngle(angle));
            var radial = (Vector3D)p; radial.Normalize();
            var tangent = Point(orbit.PositionAtAngle(angle + 0.001)) - p; tangent.Normalize();
            var normal = Vector3D.CrossProduct(radial, tangent); normal.Normalize();
            for (int j = 0; j < sides; j++)
            {
                double t = j * 2 * Math.PI / sides;
                mesh.Positions.Add(p + 0.003 * (radial * Math.Cos(t) + normal * Math.Sin(t)));
            }
        }
        for (int s = 0; s < steps; s++)
            for (int j = 0; j < sides; j++)
            {
                int a = s * sides + j, b = s * sides + (j + 1) % sides;
                foreach (int k in new[] { a, b, a + sides, b, b + sides, a + sides }) mesh.TriangleIndices.Add(k);
            }
        mesh.Freeze();
        var material = MarkerMaterial(color);
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
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
        var clouds = Sphere(1.008, material, 128, 64); clouds.Freeze(); return clouds;
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
            var sphere = Sphere(layer.Radius, material, 96, 48);
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
            for (int k = 0; k < brushes.Count; k++)
            {
                var outer = new Rect(k % columns * cell, k / columns * cell, cell, cell);
                dc.DrawRectangle(brushes[k], null, outer); // padding bleeds the same surface
                dc.DrawRectangle(brushes[k], null, new Rect(outer.X + pad, outer.Y + pad, cell - 2 * pad, cell - 2 * pad));
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

    internal static Model3DGroup MergeSatelliteParts(Model3DGroup satellite)
    {
        var meshes = new Dictionary<(Material Front, Material? Back), MeshGeometry3D>();
        void AppendParts(Model3D part, Matrix3D parent)
        {
            var matrix = part.Transform.Value; matrix.Append(parent);
            if (part is Model3DGroup group)
            {
                foreach (var child in group.Children) AppendParts(child, matrix);
                return;
            }
            var model = (GeometryModel3D)part;
            var source = (MeshGeometry3D)model.Geometry;
            var key = (model.Material, model.BackMaterial ?? model.Material);
            if (!meshes.TryGetValue(key, out var mesh)) meshes.Add(key, mesh = new MeshGeometry3D());
            int start = mesh.Positions.Count;
            foreach (var p in source.Positions) mesh.Positions.Add(matrix.Transform(p));
            foreach (int index in source.TriangleIndices) mesh.TriangleIndices.Add(start + index);
            for (int i = 0; i < source.Positions.Count; i++)
                mesh.TextureCoordinates.Add(source.TextureCoordinates.Count == source.Positions.Count ? source.TextureCoordinates[i] : new Point());
            // WPF regenerates normals for the merged mesh; face-split box vertices preserve hard edges.
        }
        AppendParts(satellite, Matrix3D.Identity);
        var result = new Model3DGroup();
        foreach (var (material, mesh) in meshes)
        {
            mesh.Freeze();
            result.Children.Add(new GeometryModel3D(mesh, material.Front) { BackMaterial = material.Back });
        }
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

    public static Matrix3D SatellitePose(CircularOrbit orbit, double seconds)
    {
        var position = Point(orbit.Position(seconds));
        var up = (Vector3D)position; up.Normalize();
        var forward = Point(orbit.Position(seconds + 1)) - Point(orbit.Position(seconds - 1)); forward.Normalize();
        var right = Vector3D.CrossProduct(up, forward); right.Normalize();
        return new Matrix3D(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0, position.X, position.Y, position.Z, 1);
    }

    public static Model3DGroup Antenna(GroundStation station)
        => new GroundAntennaGeometry(station).Model;

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

    public static GeometryModel3D SpaceBackground()
    {
        // A sphere surrounds the camera, providing the same directional backdrop as a cubemap skybox.
        const int width = 4096, height = 2048;
        var visual = new DrawingVisual();
        var random = new Random(731);
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(3, 7, 17)), null, new Rect(0, 0, width, height));
            var haze = new RadialGradientBrush(Color.FromArgb(30, 55, 64, 125), Colors.Transparent);
            drawing.DrawEllipse(haze, null, new Point(2100, 1040), 1400, 460);
            for (int i = 0; i < 2800; i++)
            {
                double x = random.NextDouble() * width;
                double y = Math.Acos(1 - 2 * random.NextDouble()) / Math.PI * height;
                double size = i % 90 == 0 ? 1.6 : 0.4 + random.NextDouble() * 0.5;
                byte brightness = (byte)random.Next(100, 240);
                var brush = new SolidColorBrush(Color.FromRgb(brightness, brightness, 255));
                drawing.DrawEllipse(brush, null, new Point(x, y), size, size);
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(Brushes.Black));
        material.Children.Add(new EmissiveMaterial(new ImageBrush(bitmap)));
        material.Freeze();
        var sphere = Sphere(500, material, 64, 32);
        sphere.Material = null; sphere.BackMaterial = material; sphere.Freeze();
        return sphere;
    }

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
