using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfSatelliteTracking.Models;

namespace WpfSatelliteTracking.Rendering;

// Reuse shapes/transforms. Only the selected spacecraft gets a beam and footprints.
public sealed class SatelliteEffects
{
    public Model3DGroup Scene { get; } = new();
    private readonly Model3DGroup[] links;
    private readonly MatrixTransform3D[] linkTransforms, uplinks, downlinks;
    private readonly Model3DGroup imaging = new();
    private readonly MeshGeometry3D surface = new(), beam = new();
    private readonly MatrixTransform3D[] border = new MatrixTransform3D[32], scan = new MatrixTransform3D[8];
    private readonly MatrixTransform3D[] beamEdges = new MatrixTransform3D[4];
    private readonly GeometryModel3D surfaceModel, beamModel;
    private readonly Material typeASurface = Material(Color.FromArgb(65, 255, 176, 65));
    private readonly Material typeBSurface = Material(Color.FromArgb(65, 80, 225, 255));
    private readonly Material typeABeam = Material(Color.FromArgb(22, 255, 176, 65));
    private readonly Material typeBBeam = Material(Color.FromArgb(22, 80, 225, 255));
    private SatelliteMission? previousMission;
    private readonly GeometryModel3D[] borderModels = new GeometryModel3D[32];
    private static readonly MeshGeometry3D cylinder = Cylinder();

    public SatelliteEffects(int stationCount)
    {
        links = new Model3DGroup[stationCount];
        linkTransforms = new MatrixTransform3D[stationCount]; uplinks = new MatrixTransform3D[stationCount]; downlinks = new MatrixTransform3D[stationCount];
        var linkMaterial = Material(Color.FromRgb(80, 225, 180));
        var upMaterial = Material(Colors.White); var downMaterial = Material(Colors.DeepSkyBlue);
        var sphere = (MeshGeometry3D)SceneGeometry.Sphere(1, upMaterial, 10, 6).Geometry;
        for (int i = 0; i < links.Length; i++)
        {
            links[i] = new Model3DGroup();
            links[i].Children.Add(Line(linkMaterial, out linkTransforms[i]));
            uplinks[i] = new(); downlinks[i] = new();
            links[i].Children.Add(new GeometryModel3D(sphere, upMaterial) { Transform = uplinks[i] });
            links[i].Children.Add(new GeometryModel3D(sphere, downMaterial) { Transform = downlinks[i] });
        }
        for (int i = 0; i < 81; i++) surface.Positions.Add(new Point3D());
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            int k = y * 9 + x;
            foreach (int index in new[] { k, k + 9, k + 1, k + 1, k + 9, k + 10 }) surface.TriangleIndices.Add(index);
        }
        for (int i = 0; i < 5; i++) beam.Positions.Add(new Point3D());
        for (int i = 1; i <= 4; i++) foreach (int index in new[] { 0, i, i == 4 ? 1 : i + 1 }) beam.TriangleIndices.Add(index);
        surfaceModel = new(surface, typeASurface) { BackMaterial = typeASurface };
        beamModel = new(beam, typeABeam) { BackMaterial = typeABeam };
        imaging.Children.Add(surfaceModel);
        var edgeMaterial = Material(Colors.Orange);
        for (int i = 0; i < border.Length; i++) { borderModels[i] = Line(edgeMaterial, out border[i]); imaging.Children.Add(borderModels[i]); }
        for (int i = 0; i < scan.Length; i++) imaging.Children.Add(Line(Material(Colors.White), out scan[i]));
        imaging.Children.Add(beamModel);
        for (int i = 0; i < beamEdges.Length; i++) imaging.Children.Add(Line(Material(Colors.White), out beamEdges[i]));
    }

    public void Update(SatelliteOperations state, IReadOnlyList<Point3D> antennaAnchors, bool communications, double animationSeconds, double cameraDistance)
    {
        double width = Math.Clamp(cameraDistance * 0.0006, 0.00004, 0.0025);
        var satellite = SceneGeometry.Point(state.SatellitePosition);
        double phase = animationSeconds * 0.8 % 1;
        for (int i = 0; i < links.Length; i++)
        {
            bool active = communications && state.Contacts[i].Connected;
            SetVisible(links[i], active);
            if (!active) continue;
            var ground = antennaAnchors[i];
            linkTransforms[i].Matrix = Segment(ground, satellite, width);
            uplinks[i].Matrix = Pulse(ground + (satellite - ground) * phase, width * 3);
            downlinks[i].Matrix = Pulse(ground + (satellite - ground) * (1 - phase), width * 3);
        }
        SetVisible(imaging, state.HasFootprint);
        if (!state.HasFootprint) return;
        if (previousMission != state.Mission)
        {
            previousMission = state.Mission;
            var fill = state.Mission == SatelliteMission.A ? typeASurface : typeBSurface;
            var sides = state.Mission == SatelliteMission.A ? typeABeam : typeBBeam;
            surfaceModel.Material = surfaceModel.BackMaterial = fill; beamModel.Material = beamModel.BackMaterial = sides;
            var edge = Material(state.Mission == SatelliteMission.A ? Colors.Orange : Colors.Cyan);
            foreach (var model in borderModels) model.Material = model.BackMaterial = edge;
        }
        var a = SceneGeometry.Point(state.Footprint[0]); var b = SceneGeometry.Point(state.Footprint[1]);
        var c = SceneGeometry.Point(state.Footprint[2]); var d = SceneGeometry.Point(state.Footprint[3]);
        // A curved guide avoids a flat footprint disappearing inside the globe.
        for (int y = 0; y <= 8; y++) for (int x = 0; x <= 8; x++)
        {
            double v = y / 8.0, u = x / 8.0;
            surface.Positions[y * 9 + x] = OnSurface((a + (b - a) * v) + ((d + (c - d) * v) - (a + (b - a) * v)) * u);
        }
        beam.Positions[0] = satellite;
        beam.Positions[1] = OnSurface(a); beam.Positions[2] = OnSurface(b); beam.Positions[3] = OnSurface(c); beam.Positions[4] = OnSurface(d);
        for (int i = 0; i < beamEdges.Length; i++) beamEdges[i].Matrix = Segment(satellite, beam.Positions[i + 1], width * 0.65);
        for (int edge = 0; edge < 4; edge++)
        {
            var from = SceneGeometry.Point(state.Footprint[edge]); var to = SceneGeometry.Point(state.Footprint[(edge + 1) % 4]);
            for (int j = 0; j < 8; j++) border[edge * 8 + j].Matrix = Segment(OnSurface(from + (to - from) * (j / 8.0)), OnSurface(from + (to - from) * ((j + 1) / 8.0)), width);
        }
        double sweep = state.Mission == SatelliteMission.A ? animationSeconds * 0.6 % 1 : 0.5;
        var left = a + (b - a) * sweep; var right = d + (c - d) * sweep;
        for (int j = 0; j < 8; j++) scan[j].Matrix = Segment(OnSurface(left + (right - left) * (j / 8.0)), OnSurface(left + (right - left) * ((j + 1) / 8.0)), width * 1.3);
    }

    private void SetVisible(Model3DGroup model, bool visible)
    {
        bool present = Scene.Children.Contains(model);
        if (visible && !present) Scene.Children.Add(model);
        else if (!visible && present) Scene.Children.Remove(model);
    }
    // shortcut: lift the visual guide above clouds (1.008); use depth-sorted overlays for exact surface placement.
    private static Point3D OnSurface(Point3D p) { var normal = (Vector3D)p; normal.Normalize(); return (Point3D)(normal * 1.009); }
    private static Matrix3D Pulse(Point3D p, double radius) => new(radius, 0, 0, 0, 0, radius, 0, 0, 0, 0, radius, 0, p.X, p.Y, p.Z, 1);
    private static Matrix3D Segment(Point3D a, Point3D b, double radius)
    {
        var y = b - a; double length = y.Length; if (length < 1e-12) return Pulse(a, 0);
        y.Normalize(); var x = Vector3D.CrossProduct(y, Math.Abs(y.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0)); x.Normalize();
        var z = Vector3D.CrossProduct(x, y);
        return new(x.X * radius, x.Y * radius, x.Z * radius, 0, y.X * length, y.Y * length, y.Z * length, 0, z.X * radius, z.Y * radius, z.Z * radius, 0, a.X, a.Y, a.Z, 1);
    }
    private static GeometryModel3D Line(Material material, out MatrixTransform3D transform)
    { transform = new(); return new(cylinder, material) { BackMaterial = material, Transform = transform }; }
    private static Material Material(Color color)
    {
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(color.A, 0, 0, 0))));
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(color)));
        material.Freeze(); return material;
    }
    private static MeshGeometry3D Cylinder()
    {
        var mesh = new MeshGeometry3D();
        for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; mesh.Positions.Add(new(Math.Cos(a), 0, Math.Sin(a))); mesh.Positions.Add(new(Math.Cos(a), 1, Math.Sin(a))); }
        for (int i = 0; i < 8; i++) { int a = i * 2, b = (i + 1) % 8 * 2; foreach (int k in new[] { a, b, a + 1, a + 1, b, b + 1 }) mesh.TriangleIndices.Add(k); }
        mesh.Freeze(); return mesh;
    }
}
