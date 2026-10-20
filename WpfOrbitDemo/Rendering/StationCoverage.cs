using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfOrbitDemo.Models;

namespace WpfOrbitDemo.Rendering;

// Ground-station coverage in Earth-fixed coordinates: the 10° elevation visibility cone from the station up to the
// highest constellation shell, its rim, and the sub-satellite footprint on the ground for the given altitude.
// Geometry only; brightens while a satellite is inside. Illustrative mask (no terrain, no antenna slew limits).
internal sealed class StationCoverage
{
    private static readonly Color Tint = Color.FromRgb(70, 240, 180);
    // Light veils: the camera often sits inside all three overlapping cones in the ground-station view.
    private static readonly Material IdleCone = Glow(60), ActiveCone = Glow(150), IdleRim = Glow(110), ActiveRim = Glow(230),
        IdleFootprint = Glow(12), ActiveFootprint = Glow(26);
    private readonly GeometryModel3D cone, rim, footprint, footprintRim;
    private bool? active;
    public Model3DGroup Model { get; } = new();

    public StationCoverage(GroundStation station, double shellRadius, double footprintAltitudeKm, int layer = 0)
    {
        double lift = 0.0006 * layer; // separate overlapping footprints of neighbouring stations (no z-fighting)
        double lat = station.Latitude * Math.PI / 180, lon = station.Longitude * Math.PI / 180;
        var up = new Vector3D(Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), -Math.Cos(lat) * Math.Sin(lon));
        var east = new Vector3D(-Math.Sin(lon), 0, -Math.Cos(lon));
        var north = Vector3D.CrossProduct(up, east);
        var apex = (Point3D)up;
        double elevation = SatelliteOperations.MinimumElevationDegrees * Math.PI / 180;
        const int segments = 120;
        Point3D ShellPoint(double a)
        {
            var d = (north * Math.Cos(a) + east * Math.Sin(a)) * Math.Cos(elevation) + up * Math.Sin(elevation);
            double b = Vector3D.DotProduct((Vector3D)apex, d);
            double t = -b + Math.Sqrt(b * b - 1 + shellRadius * shellRadius);
            return apex + d * t;
        }
        var shell = new Point3D[segments + 1];
        for (int k = 0; k <= segments; k++) shell[k] = ShellPoint(k * 2 * Math.PI / segments);
        // Cone as 12 hair-thin tapering ribs, not a filled wall: WPF does not depth-sort translucent faces, so
        // overlapping cone walls of neighbouring stations hide each other in wedges.
        var coneMesh = new MeshGeometry3D();
        for (int rib = 0; rib < 12; rib++)
        {
            double a = rib * Math.PI / 6;
            int start = coneMesh.Positions.Count;
            coneMesh.Positions.Add(apex); coneMesh.Positions.Add(ShellPoint(a)); coneMesh.Positions.Add(ShellPoint(a + 0.004));
            coneMesh.TriangleIndices.Add(start); coneMesh.TriangleIndices.Add(start + 1); coneMesh.TriangleIndices.Add(start + 2);
        }
        coneMesh.Freeze();
        // Rim: a ribbon on the cone just below its top edge.
        var rimMesh = new MeshGeometry3D();
        for (int k = 0; k <= segments; k++)
        {
            var toApex = apex - shell[k]; toApex.Normalize();
            rimMesh.Positions.Add(shell[k]); rimMesh.Positions.Add(shell[k] + toApex * 0.006);
            if (k == 0) continue;
            int i = (k - 1) * 2;
            foreach (int index in new[] { i, i + 2, i + 1, i + 1, i + 2, i + 3 }) rimMesh.TriangleIndices.Add(index);
        }
        rimMesh.Freeze();
        // Footprint: ground points that see a satellite at this altitude at >= 10° (central angle lambda).
        double r = (CircularOrbit.EarthRadiusKm + footprintAltitudeKm) / CircularOrbit.EarthRadiusKm;
        double lambda = Math.Acos(Math.Cos(elevation) / r) - elevation;
        Point3D OnGround(double angle, double azimuth, double radius)
        {
            var direction = up * Math.Cos(angle) + (north * Math.Cos(azimuth) + east * Math.Sin(azimuth)) * Math.Sin(angle);
            return (Point3D)(direction * radius);
        }
        var capMesh = new MeshGeometry3D();
        const int rings = 8;
        for (int ring = 0; ring <= rings; ring++)
            for (int k = 0; k <= segments; k++) capMesh.Positions.Add(OnGround(lambda * ring / rings, k * 2 * Math.PI / segments, 1.010 + lift));
        for (int ring = 0; ring < rings; ring++)
            for (int k = 0; k < segments; k++)
            {
                int a = ring * (segments + 1) + k, b = a + segments + 1;
                foreach (int index in new[] { a, b, a + 1, a + 1, b, b + 1 }) capMesh.TriangleIndices.Add(index);
            }
        capMesh.Freeze();
        var edgeMesh = new MeshGeometry3D();
        for (int k = 0; k <= segments; k++)
        {
            double azimuth = k * 2 * Math.PI / segments;
            edgeMesh.Positions.Add(OnGround(lambda, azimuth, 1.0115 + lift)); edgeMesh.Positions.Add(OnGround(lambda - 0.004, azimuth, 1.0115 + lift));
            if (k == 0) continue;
            int i = (k - 1) * 2;
            foreach (int index in new[] { i, i + 2, i + 1, i + 1, i + 2, i + 3 }) edgeMesh.TriangleIndices.Add(index);
        }
        edgeMesh.Freeze();
        footprint = new GeometryModel3D(capMesh, IdleFootprint) { BackMaterial = IdleFootprint };
        footprintRim = new GeometryModel3D(edgeMesh, IdleRim) { BackMaterial = IdleRim };
        cone = new GeometryModel3D(coneMesh, IdleCone) { BackMaterial = IdleCone };
        rim = new GeometryModel3D(rimMesh, IdleRim) { BackMaterial = IdleRim };
        // Opaque-ish rims first, translucent surfaces last so they blend over them.
        Model.Children.Add(footprintRim); Model.Children.Add(rim); Model.Children.Add(footprint); Model.Children.Add(cone);
    }

    public void SetActive(bool value)
    {
        if (active == value) return;
        active = value;
        cone.Material = cone.BackMaterial = value ? ActiveCone : IdleCone;
        rim.Material = rim.BackMaterial = value ? ActiveRim : IdleRim;
        footprint.Material = footprint.BackMaterial = value ? ActiveFootprint : IdleFootprint;
        footprintRim.Material = footprintRim.BackMaterial = value ? ActiveRim : IdleRim;
    }

    private static Material Glow(byte alpha)
    {
        // Transparent diffuse + emissive (as the atmosphere): a black or missing diffuse layer darkens what is seen through the veil.
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(Brushes.Transparent));
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(alpha, Tint.R, Tint.G, Tint.B))));
        material.Freeze(); return material;
    }
}

// Link lines and up/down pulses between each station's antenna feed and the satellite it is talking to.
internal sealed class CoverageLinks
{
    private readonly Model3DGroup[] links;
    private readonly MatrixTransform3D[] lines, uplinks, downlinks;
    public Model3DGroup Scene { get; } = new();

    public CoverageLinks(int stationCount)
    {
        links = new Model3DGroup[stationCount];
        lines = new MatrixTransform3D[stationCount]; uplinks = new MatrixTransform3D[stationCount]; downlinks = new MatrixTransform3D[stationCount];
        var lineMaterial = Glow(Color.FromRgb(70, 240, 180)); var up = Glow(Colors.White); var down = Glow(Colors.DeepSkyBlue);
        var cylinder = Cylinder(); var sphere = (MeshGeometry3D)SceneGeometry.Sphere(1, up, 10, 6).Geometry;
        for (int i = 0; i < stationCount; i++)
        {
            lines[i] = new(); uplinks[i] = new(); downlinks[i] = new();
            links[i] = new Model3DGroup();
            links[i].Children.Add(new GeometryModel3D(cylinder, lineMaterial) { BackMaterial = lineMaterial, Transform = lines[i] });
            links[i].Children.Add(new GeometryModel3D(sphere, up) { Transform = uplinks[i] });
            links[i].Children.Add(new GeometryModel3D(sphere, down) { Transform = downlinks[i] });
        }
    }

    public void Update(int station, Point3D ground, Point3D? satellite, double animationSeconds, double cameraDistance)
    {
        bool present = Scene.Children.Contains(links[station]);
        if (satellite is not Point3D target)
        {
            if (present) Scene.Children.Remove(links[station]);
            return;
        }
        if (!present) Scene.Children.Add(links[station]);
        double width = Math.Clamp(cameraDistance * 0.0006, 0.00004, 0.0025), phase = (animationSeconds * 0.8 + station * 0.31) % 1;
        lines[station].Matrix = Segment(ground, target, width);
        uplinks[station].Matrix = Pulse(ground + (target - ground) * phase, width * 3);
        downlinks[station].Matrix = Pulse(ground + (target - ground) * (1 - phase), width * 3);
    }

    private static Matrix3D Pulse(Point3D p, double radius) => new(radius, 0, 0, 0, 0, radius, 0, 0, 0, 0, radius, 0, p.X, p.Y, p.Z, 1);
    private static Matrix3D Segment(Point3D a, Point3D b, double radius)
    {
        var y = b - a; double length = y.Length; if (length < 1e-12) return Pulse(a, 0);
        y.Normalize(); var x = Vector3D.CrossProduct(y, Math.Abs(y.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0)); x.Normalize();
        var z = Vector3D.CrossProduct(x, y);
        return new(x.X * radius, x.Y * radius, x.Z * radius, 0, y.X * length, y.Y * length, y.Z * length, 0, z.X * radius, z.Y * radius, z.Z * radius, 0, a.X, a.Y, a.Z, 1);
    }
    private static MeshGeometry3D Cylinder()
    {
        var mesh = new MeshGeometry3D();
        for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; mesh.Positions.Add(new(Math.Cos(a), 0, Math.Sin(a))); mesh.Positions.Add(new(Math.Cos(a), 1, Math.Sin(a))); }
        for (int i = 0; i < 8; i++) { int a = i * 2, b = (i + 1) % 8 * 2; foreach (int k in new[] { a, b, a + 1, a + 1, b, b + 1 }) mesh.TriangleIndices.Add(k); }
        mesh.Freeze(); return mesh;
    }
    private static Material Glow(Color color)
    {
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(Brushes.Black));
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(color)));
        material.Freeze(); return material;
    }
}
