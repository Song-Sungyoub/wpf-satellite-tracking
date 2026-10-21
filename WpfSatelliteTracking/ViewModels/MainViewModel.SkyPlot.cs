using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using WpfSatelliteTracking.Models;
using WpfSatelliteTracking.Rendering;

namespace WpfSatelliteTracking.ViewModels;

// Antenna sky plot: azimuth/elevation polar chart (north up, east right, zenith in the centre, horizon on the rim).
// Each station shows its antenna pointing and the -5/+15 min look-angle track of the satellite it is assigned to.
public sealed partial class MainViewModel
{
    public const double SkyPlotSize = 200;
    private const double SkyPlotRadius = SkyPlotSize / 2 - 12;
    private double skyTrackTime = double.NegativeInfinity;
    private double skyTrackOrbitTime = double.NaN;

    public sealed class SkyStation(string name, Color color) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public Brush Color { get; } = Frozen(new SolidColorBrush(color));
        public PointCollection Track { get; private set; } = Frozen(new PointCollection());
        public double MarkerLeft { get; private set; }
        public double MarkerTop { get; private set; }
        public Visibility TrackVisibility { get; private set; } = Visibility.Collapsed;
        internal int Target { get; set; } = -2;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void SetTrack(PointCollection track)
        {
            Track = Frozen(track); TrackVisibility = track.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            PropertyChanged?.Invoke(this, new(nameof(Track))); PropertyChanged?.Invoke(this, new(nameof(TrackVisibility)));
        }
        internal void SetMarker(Point point)
        {
            if (MarkerLeft == point.X - 5 && MarkerTop == point.Y - 5) return;
            MarkerLeft = point.X - 5; MarkerTop = point.Y - 5;
            PropertyChanged?.Invoke(this, new(nameof(MarkerLeft))); PropertyChanged?.Invoke(this, new(nameof(MarkerTop)));
        }
        private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
    }

    private SkyStation[]? skyStations;
    public SkyStation[] SkyStations => skyStations ??= GroundStations
        .Select((s, i) => new SkyStation(s.Name, StationCoverage.StationTints[i % StationCoverage.StationTints.Length])).ToArray();

    public static Point SkyPoint(double azimuthDegrees, double elevationDegrees)
    {
        double r = Math.Clamp((90 - elevationDegrees) / 90, 0, 1) * SkyPlotRadius, a = azimuthDegrees * Math.PI / 180;
        return new Point(SkyPlotSize / 2 + r * Math.Sin(a), SkyPlotSize / 2 - r * Math.Cos(a));
    }

    private void UpdateSkyPlot(double now)
    {
        var stations = SkyStations;
        // Tracks change slowly: rebuild every 2 s, on a target change, or after a time jump.
        bool rebuild = now - skyTrackTime >= 2 || Math.Abs(OrbitTime - skyTrackOrbitTime) > 600 * 2.5;
        for (int s = 0; s < stations.Length; s++)
        {
            int target = scheduler.Targets[s];
            stations[s].SetMarker(SkyPoint(servos[s].Azimuth, servos[s].Elevation));
            if (!rebuild && target == stations[s].Target) continue;
            stations[s].Target = target;
            var track = new PointCollection();
            if (target >= 0)
                for (double t = -300; t <= 900; t += 30)
                {
                    double seconds = OrbitTime + t;
                    var look = EarthCoordinates.LookAngles(GroundStations[s], Orbits[target].Position(seconds),
                        EarthCoordinates.GmstDegrees(SatelliteCatalog.ReferenceEpoch.AddSeconds(seconds)));
                    if (look.Elevation >= 0) track.Add(SkyPoint(look.Azimuth, look.Elevation));
                }
            stations[s].SetTrack(track);
        }
        if (rebuild) { skyTrackTime = now; skyTrackOrbitTime = OrbitTime; }
    }
}
