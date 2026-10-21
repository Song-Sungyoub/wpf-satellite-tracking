using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfSatelliteTracking.Models;
using WpfSatelliteTracking.Rendering;

namespace WpfSatelliteTracking.ViewModels;

public sealed partial class MainViewModel
{
    public ObservableCollection<CelestialLabel> GroundTrackTimeLabels { get; } = new();
    private readonly List<Point3D> groundTrackAnchors = new();
    private bool showGroundTrackTimes = true;
    public bool ShowGroundTrackTimes
    {
        get => showGroundTrackTimes;
        set { if (SetFlag(ref showGroundTrackTimes, value)) RefreshTrajectories(true); }
    }

    private void BuildGroundTrackTimes(double duration)
    {
        if (!showGroundTrackTimes) return;
        // Absolute UTC boundaries keep a timestamp's geographical anchor stable across refreshes.
        long step = TimeSpan.TicksPerMinute * 10;
        long first = (SimulationTime.UtcTicks / step + 1) * step;
        var end = SimulationTime.AddSeconds(duration);
        for (var time = new DateTimeOffset(first, TimeSpan.Zero); time <= end; time = time.AddMinutes(10))
        {
            var geo = EarthCoordinates.Geodetic(SelectedOrbit.EarthFixedPosition((time - SatelliteCatalog.ReferenceEpoch).TotalSeconds));
            var point = EarthCoordinates.Surface(geo.Latitude, geo.Longitude, 0.0092 * SatelliteOrbit.EarthRadiusKm);
            var local = time.ToOffset(TimeSpan.FromHours(9));
            string latitude = $"{Math.Abs(geo.Latitude):0.0}°{(geo.Latitude >= 0 ? "N" : "S")}";
            string longitude = $"{Math.Abs(geo.Longitude):0.0}°{(geo.Longitude >= 0 ? "E" : "W")}";
            GroundTrackTimeLabels.Add(new CelestialLabel(GroundTrackTimeLabels.Count,
                $"{local:MM-dd HH:mm} KST\n{latitude}  {longitude}", Brushes.LightGreen));
            groundTrackAnchors.Add(SceneGeometry.Point(point));
            var tick = SceneGeometry.Sphere(0.0022, SceneGeometry.MarkerMaterial(Colors.LightGreen), 8, 4);
            tick.Transform = new TranslateTransform3D(point.X, point.Y, point.Z);
            groundTrackHost.Children.Add(tick);
        }
    }

    private void UpdateGroundTrackTimeLabels()
    {
        if (GroundTrackTimeLabels.Count == 0) return;
        var camera = CameraPosition;
        var forward = CameraDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, CameraUpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double focal = viewportWidth / (2 * Math.Tan(21 * Math.PI / 180));
        var occupied = new List<Rect>();
        for (int i = 0; i < GroundTrackTimeLabels.Count; i++)
        {
            var label = GroundTrackTimeLabels[i];
            label.Place(false);
            if (!showGroundTracks || !showGroundTrackTimes || solarView || followedCelestial >= 0 || followSatellite || antennaView) continue;
            var ray = earthPose.Transform(groundTrackAnchors[i]) - camera;
            double depth = Vector3D.DotProduct(ray, forward);
            double fraction = Math.Clamp(-Vector3D.DotProduct((Vector3D)camera, ray) / ray.LengthSquared, 0, 1);
            if (depth <= 0 || ((Vector3D)(camera + ray * fraction)).Length < 0.999) continue;
            double x = viewportWidth / 2 + Vector3D.DotProduct(ray, right) * focal / depth;
            double y = viewportHeight / 2 - Vector3D.DotProduct(ray, up) * focal / depth;
            const double width = 158, height = 40;
            if (x < 0 || x > viewportWidth || y < 0 || y > viewportHeight) continue;
            var bounds = new Rect(Math.Clamp(x + 5, 0, Math.Max(0, viewportWidth - width)),
                Math.Clamp(y - height - 5, 0, Math.Max(0, viewportHeight - height)), width, height);
            if (occupied.Any(r => r.IntersectsWith(bounds))) continue;
            occupied.Add(bounds);
            label.Place(true, bounds.Left, bounds.Top);
        }
    }
}
