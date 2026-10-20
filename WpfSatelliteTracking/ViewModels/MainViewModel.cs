using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Windows;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using WpfSatelliteTracking.Models;
using WpfSatelliteTracking.Rendering;

namespace WpfSatelliteTracking.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Stopwatch clock = new();
    private bool rendering;
    private TimeSpan lastRenderingTime = TimeSpan.MinValue;
    private double informationTime = double.NegativeInfinity, sampleTime, sampleWorkMilliseconds;
    private double nightRequestTime = double.NegativeInfinity;
    private int sampleFrames;
    private readonly SatelliteLabel[] labelCache;
    private readonly List<Rect> celestialLabelBounds = new(10);
    private readonly SatelliteOperations operations;
    private readonly SatelliteEffects satelliteEffects;
    private readonly Point3D[] antennaAnchors;
    private readonly GroundAntennaGeometry[] groundAntennas;
    private readonly GroundAntennaAim[] stationAims;
    private readonly RotateTransform3D earthPose;
    private bool capturing, showCommunications = true;
    private string contactSearchMessage = "";
    public bool ShowCommunications
    {
        get => showCommunications;
        set { if (showCommunications == value) return; showCommunications = value; Notify(); UpdateScene(); }
    }
    public bool IsCapturing => capturing;
    public string CaptureButtonText => capturing ? "촬영 중지" : "선택 위성 촬영 시작";
    public string CaptureStatus => solarView || followedCelestial >= 0 ? "천체 보기에서는 통신·촬영 효과 숨김"
        : capturing ? (SelectedOrbit.Mission == SatelliteMission.A ? "A형 · 우측 35° 측면 관측 빔 / 스캔 중" : "B형 · 하향 화각 12° × 8° 관측 중") : "촬영 대기 · 센서 설정은 예시";
    public string CommunicationStatus => contactSearchMessage.Length > 0 ? contactSearchMessage
        : string.Join("\n", GroundStations.Select((s, i) =>
        {
            int target = scheduler.Targets[i]; var servo = servos[i];
            string state = target < 0 ? (servo.Elevation > 89.5 ? "천정 대기" : "천정으로 복귀 중")
                : servo.Locked ? $"교신 {Orbits[target].Name} · {scheduler.Desired[i].ElevationDegrees:0.0}°" : $"포착 중 {Orbits[target].Name}";
            return $"{s.Name}: {state} · 범위 안 {scheduler.InView[i]}대\n  안테나 방위 {servo.Azimuth:0}° / 고각 {servo.Elevation:0}°";
        }));
    // Ground network: every station tracks a different satellite inside its 10° mask (see StationScheduler), slewing
    // on and off target and parking at zenith; links are drawn once its antenna is on target. Coverage mode only adds
    // the visibility cones.
    private bool showCoverage;
    private readonly StationScheduler scheduler;
    private readonly AntennaServo[] servos;
    private double servoTime = double.NaN;
    private readonly StationCoverage[] coverages = new StationCoverage[3];
    // Earth-fixed repeat ground tracks (one per group) under the rotating Earth; only the selected satellite's group
    // is shown, since a full repeat cycle of every group would cover the globe.
    private readonly Dictionary<int, GeometryModel3D> groundTracks = [];
    private readonly Model3DGroup groundTrackHost = new();
    private bool showGroundTracks;
    public bool ShowGroundTracks { get => showGroundTracks; set { if (SetFlag(ref showGroundTracks, value)) UpdateGroundTrack(); } }
    private void UpdateGroundTrack()
    {
        groundTrackHost.Children.Clear();
        if (!showGroundTracks || groundTracks.Count == 0) return;
        int group = orbitRepresentatives.First(r => r == selectedIndex || Orbits[r].SharesPlaneWith(SelectedOrbit));
        groundTrackHost.Children.Add(groundTracks[group]);
    }
    private readonly CoverageLinks coverageLinks = new(3);
    private readonly AxisAngleRotation3D coverageRotation = new(new Vector3D(0, 1, 0), 0);
    private readonly Model3DGroup coverageGroup = new();
    public bool ShowCoverage
    {
        get => showCoverage;
        set { if (!SetFlag(ref showCoverage, value)) return; ShowInEarthViews(coverageGroup, value); UpdateScene(); }
    }
    private void UpdateStations(double now)
    {
        scheduler.Update(Orbits, OrbitTime, CurrentSun.EarthRotationDegrees, selectedIndex);
        double wall = double.IsNaN(servoTime) ? 0 : Math.Clamp(now - servoTime, 0, 0.1);
        servoTime = now;
        for (int s = 0; s < GroundStations.Length; s++)
        {
            stationAims[s] = servos[s].Update(scheduler.Targets[s], scheduler.Desired[s], wall);
            groundAntennas[s].Update(stationAims[s]);
            coverages[s].SetActive(scheduler.Targets[s] >= 0);
        }
    }
    public double CompositionFps { get; private set; }
    public double AverageUpdateMilliseconds { get; private set; }
    public string FrameStatus { get; private set; } = "WPF FPS 측정 중…";
    private readonly FrameTiming frameTiming = new();
    private bool measuringFrames;
    private CircularOrbit? measuredOrbit;
    public string FrameMeasurementStatus { get; private set; } = "실제 축척 · 600배 · 촬영/통신 켜고 재생 후 측정";
    public string FrameMeasurementButton => measuringFrames ? "프레임 측정 종료" : "프레임 측정 시작";
    public ICommand MeasureFramesCommand { get; }
    private bool MeasurementConditions => playing && IsTrueScale && Speed == 600 && capturing && ShowCommunications && !solarView && followedCelestial < 0;
    private void FinishMeasurement(string reason)
    {
        measuringFrames = false;
        FrameMeasurementStatus = $"{measuredOrbit?.Name} · {reason}\n{frameTiming.Summary}";
        Notify(nameof(FrameMeasurementStatus)); Notify(nameof(FrameMeasurementButton));
    }
    public bool NightUploadPending => nightLights.HasPendingUpload;
    private readonly AxisAngleRotation3D earthRotation = new(new Vector3D(0, 1, 0), 0);
    private readonly List<MatrixTransform3D> satelliteTransforms = [];
    private readonly List<GeometryModel3D> orbitModels = [];
    private readonly List<int> orbitRepresentatives = [];
    private readonly List<Model3DGroup> satelliteModels = [];
    private readonly MatrixTransform3D?[] wingTransforms, dishTransforms;
    private readonly double[] wingAngles;
    private readonly PointLight sunLight = new(Colors.White, new Point3D(18, 0, 0))
    { ConstantAttenuation = 1, LinearAttenuation = 0, QuadraticAttenuation = 0, Range = 1000000 };
    private bool isTrueScale;
    private readonly Model3DGroup distantScene = new(), nearSatelliteScene = new();
    private readonly Model3DGroup earthLocalScene = new(), solarScene = new(), planetOrbits = new();
    private readonly MatrixTransform3D[] solarTransforms = Enumerable.Range(0, 9).Select(_ => new MatrixTransform3D()).ToArray();
    private readonly ScaleTransform3D planetOrbitScale = new();
    private bool solarView, earthOverview;
    private bool fullSolarSystem = true;
    private int planetOrbitYear;
    private Point3D solarEarthPosition;
    private readonly Point3D[] solarBodyPositions = new Point3D[10];
    public bool IsSolarSystemView => solarView;
    // Active view for highlighting the matching view button; notified only when it changes.
    private string focusMode = "";
    public string FocusMode => celestialOverview ? (fullSolarSystem ? "Solar" : "Inner") : followedCelestial == 0 ? "Sun"
        : followedCelestial > 0 ? "Celestial" : followGroundStation ? "Ground" : followSatellite ? "Satellite" : earthOverview ? "Earth" : "Default";
    public bool IsEarthFocus => FocusMode == "Earth";
    public bool IsSolarFocus => FocusMode == "Solar";
    public bool IsInnerFocus => FocusMode == "Inner";
    public bool IsSunFocus => FocusMode == "Sun";
    public bool IsGroundFocus => FocusMode == "Ground";
    public bool IsSatelliteFocus => FocusMode == "Satellite";
    private void NotifyFocusMode()
    {
        if (focusMode == FocusMode) return;
        focusMode = FocusMode;
        Notify(nameof(FocusMode)); Notify(nameof(IsEarthFocus)); Notify(nameof(IsSolarFocus)); Notify(nameof(IsInnerFocus));
        Notify(nameof(IsSunFocus)); Notify(nameof(IsGroundFocus)); Notify(nameof(IsSatelliteFocus));
    }
    public string ViewDescription => solarView ? "태양계 · 태양 중심 / 행성 8개 공전 궤도" : "지구 주변 · 달 / 인공위성 50대";
    private readonly TranslateTransform3D renderTranslation = new();
    private readonly ScaleTransform3D[] bodyScales = [new(), new(), new(), new()];
    private readonly double[] satellitePhysicalScales;
    private CelestialState celestialState = null!;
    private readonly NightLights nightLights = new();
    private readonly MoonRelief moonRelief = new();
    private readonly MapTileManager tileManager = new();
    private readonly Model3DGroup tileLayer = new();
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<ScaleTransform3D> stationScales = [];
    private GeometryModel3D clouds = null!;
    private Material cloudMaterial = null!, nightMaterial = null!, tileNightMaterial = null!;
    private CancellationTokenSource? tileCancellation;
    private string tileRequestKey = "", tileStatus = "오프라인 지도 · 대전·오사카·상하이 주변 자료 포함";
    private double groundLatitude, groundLongitude;
    private bool disposed;
    public Task TilesLoadingTask { get; private set; } = Task.CompletedTask;
    public string TileStatus { get => tileStatus; private set { tileStatus = value; Notify(); } }
    // Regional close-up: day tile (1.000005) and night overlay (1.000007) are ~1 cm apart, so a 1% near plane z-fights.
    // The surface stays ~one distance away and station models are far smaller, so 20% keeps depth precision without clipping.
    public double CameraNearPlane => UseNearSatelliteLayer ? 0.005 : followGroundStation ? Math.Max(0.000001, distance * (distance < 0.1 ? 0.2 : 0.01))
        : IsTrueScale && celestialOverview ? Math.Max(0.05, distance * 0.001)
        : IsTrueScale ? Math.Max(0.000001, Math.Min(0.05, distance * 0.005)) : 0.05;
    public double CameraFarPlane => Math.Max(IsTrueScale ? 100000 : 1000, distance * 4);
    public double NearSatellitePlane => Math.Max(1e-10, SceneScale.Units(SceneScale.SatelliteSpanMeters(SelectedOrbit.Mission) / 1000) * 0.01);
    public bool UseNearSatelliteLayer => !solarView && IsTrueScale && followSatellite && distance < 0.0025;
    public Model3D MainScene => solarView ? solarScene : UseNearSatelliteLayer ? distantScene : earthLocalScene;
    public Model3D? SkyScene => showStars ? Scene.Children[3] : null;

    // Display / load toggles. Hidden layers are removed (or lose their material) and skip their per-frame work.
    private bool showOrbits = true, showClouds = true, showAtmosphere = true, showNightLights = true, showStars = true,
        showMoon = true, showSatelliteLabels = true, animateAllSatellites, tilesShown;
    public bool ShowOrbits { get => showOrbits; set { if (SetFlag(ref showOrbits, value)) UpdateOrbitMaterials(); } }
    private bool showAllOrbits;
    public bool ShowAllOrbits { get => showAllOrbits; set { if (SetFlag(ref showAllOrbits, value)) UpdateOrbitMaterials(); } }
    public bool ShowClouds { get => showClouds; set { if (SetFlag(ref showClouds, value)) ApplyGlobalLayers(); } }
    public bool ShowNightLights
    {
        get => showNightLights;
        set { if (!SetFlag(ref showNightLights, value)) return; nightLights.Invalidate(); ApplyGlobalLayers(); UpdateScene(); }
    }
    public bool ShowAtmosphere { get => showAtmosphere; set { if (SetFlag(ref showAtmosphere, value)) ShowInEarthViews(Scene.Children[6], value); } }
    public bool ShowStars { get => showStars; set { if (SetFlag(ref showStars, value)) Notify(nameof(SkyScene)); } }
    public bool ShowMoon { get => showMoon; set { if (!SetFlag(ref showMoon, value)) return; ShowInEarthViews(Scene.Children[8], value); UpdateScene(); } }
    public bool ShowSatelliteLabels { get => showSatelliteLabels; set { if (SetFlag(ref showSatelliteLabels, value)) UpdateLabels(); } }
    public bool AnimateAllSatellites { get => animateAllSatellites; set { if (SetFlag(ref animateAllSatellites, value)) UpdateScene(); } }
    private bool SetFlag(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return false;
        field = value; Notify(name); return true;
    }
    private void ShowInEarthViews(Model3D model, bool visible)
    {
        foreach (var group in new[] { earthLocalScene, distantScene })
        {
            if (!visible) group.Children.Remove(model);
            else if (!group.Children.Contains(model)) group.Children.Add(model);
        }
    }
    // Regional tiles replace the global night/cloud layers; the toggles hide them independently.
    private void ApplyGlobalLayers()
    {
        nightLights.Model.Material = tilesShown || !showNightLights ? null : nightMaterial;
        clouds.Material = tilesShown || !showClouds ? null : cloudMaterial;
    }
    public Model3D? NearSatelliteScene => UseNearSatelliteLayer ? nearSatelliteScene : null;
    private Point3D RenderOrigin => IsTrueScale ? cameraTarget : new Point3D();
    public Point3D RenderCameraPosition => (Point3D)(CameraPosition - RenderOrigin);
    private double BodyRadius(int index) => IsTrueScale ? SceneScale.Radius(index) : solarView ? SceneScale.ObservationRadius(index) : CelestialGeometry.Radii[index];
    private double SatelliteFocusDistance => IsTrueScale ? SceneScale.Units(SceneScale.SatelliteSpanMeters(SelectedOrbit.Mission) / 1000) * 4
        : (SelectedOrbit.Mission == SatelliteMission.B ? 0.9 : 0.55) * SceneScale.ObservationSatelliteScale;
    public int ScaleModeIndex { get => IsTrueScale ? 1 : 0; set { if (value is 0 or 1) IsTrueScale = value == 1; } }
    public string ScaleDescription => IsTrueScale ? "실제 비율 · A형 폭 12m / B형 폭 8m (가정)\n위치는 근사 · 궤도선/지상국은 안내 표시"
        : "관측용 · 태양계 거리는 1 AU = 18로 통일\n지구 주변은 별도 확대 · 천체 크기 과장";
    public bool IsTrueScale
    {
        get => isTrueScale;
        set
        {
            if (isTrueScale == value) return;
            double reference = followSatellite ? SatelliteFocusDistance : followedCelestial >= 0 ? BodyRadius(followedCelestial)
                : celestialOverview ? (IsTrueScale ? SceneScale.Units(SceneScale.AstronomicalUnitKm) : 18)
                : earthOverview ? IsTrueScale ? 66 : 4.8 : 1;
            isTrueScale = value;
            double newReference = followSatellite ? SatelliteFocusDistance : followedCelestial >= 0 ? BodyRadius(followedCelestial)
                : celestialOverview ? (IsTrueScale ? SceneScale.Units(SceneScale.AstronomicalUnitKm) : 18)
                : earthOverview ? IsTrueScale ? 66 : 4.8 : 1;
            distance *= newReference / reference;
            for (int i = 0; i < bodyScales.Length; i++)
                bodyScales[i].ScaleX = bodyScales[i].ScaleY = bodyScales[i].ScaleZ = BodyRadius(i) / CelestialGeometry.Radii[i];
            nightLights.Invalidate();
            UpdateScene(); NotifyCamera();
            Notify(); Notify(nameof(ScaleModeIndex)); Notify(nameof(ScaleDescription)); Notify(nameof(SelectedInfo));
        }
    }
    private readonly List<Model3DGroup> litAssets = [], eclipsedAssets = [];
    private readonly bool[] eclipsed;
    private readonly TranslateTransform3D[] celestialTransforms = [new(), new(), new(), new()];
    // Tidal locking: keep the Moon's near side (texture longitude 0, local +X) toward Earth: tilt about Z, then turn about Y.
    // Libration is ignored, so the same face is seen on every date.
    private readonly AxisAngleRotation3D moonFacing = new(new Vector3D(0, 1, 0), 0), moonTilt = new(new Vector3D(0, 0, 1), 0);
    private readonly Point3D[] celestialPositions = new Point3D[4];
    private double celestialUpdateTime = double.NegativeInfinity;
    private bool celestialOverview;
    private int followedCelestial = -1;
    private DateTimeOffset epoch = DateTimeOffset.UtcNow;
    private bool realTime;
    private string timeInput = "", timeError = "";
    private readonly Color[] colors;
    private double viewportWidth = 800, viewportHeight = 500;
    private bool fitOverview = true;
    private int selectedIndex;
    private double previousTime, simulationSeconds, speed = 120, yaw = -2.5, pitch = 0.35, distance = 5.1;
    private bool playing = true;
    private Point3D cameraTarget;
    private bool followSatellite;
    private bool followGroundStation;
    public event PropertyChangedEventHandler? PropertyChanged;
    public Model3DGroup Scene { get; } = new();
    public GroundStation[] GroundStations { get; } =
    [
        // City-centre coordinates for illustration; not facility locations.
        new("대전", 36.3504, 127.3845),
        new("오사카", 34.6937, 135.5023),
        new("상하이", 31.2304, 121.4737)
    ];
    public CircularOrbit[] Orbits { get; } = SatelliteConstellation.Create();
    public ICollectionView SatellitesView { get; }
    public ICommand TogglePlaybackCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand FocusGroundStationsCommand { get; }
    public ICommand FocusSatelliteCommand { get; }
    public ICommand SelectOrbitCommand { get; }
    public ICommand ApplyTimeCommand { get; }
    public ICommand CurrentTimeCommand { get; }
    public ICommand FocusStationCommand { get; }
    public ICommand FocusCelestialsCommand { get; }
    public ICommand FocusInnerPlanetsCommand { get; }
    public ICommand FocusSunCommand { get; }
    public ICommand FocusEarthSystemCommand { get; }
    public ICommand FocusCelestialCommand { get; }
    public ICommand ToggleCaptureCommand { get; }
    public ICommand FindContactCommand { get; }
    public IReadOnlyList<CelestialLabel> CelestialLabels { get; } =
    [new(0, "태양", Brushes.Orange), new(1, "달", Brushes.LightGray), new(2, "수성", Brushes.Tan), new(3, "금성", Brushes.Wheat), new(4, "지구", Brushes.LightSkyBlue),
     new(5, "화성", Brushes.Coral), new(6, "목성", Brushes.Bisque), new(7, "토성", Brushes.Khaki), new(8, "천왕성", Brushes.PaleTurquoise), new(9, "해왕성", Brushes.CornflowerBlue)];
    public sealed class CelestialLabel(int index, string name, Brush color) : INotifyPropertyChanged
    {
        public int Index { get; } = index;
        public string Name { get; } = name;
        public Brush Color { get; } = color;
        public double Left { get; private set; }
        public double Top { get; private set; }
        public Visibility Visibility { get; private set; } = Visibility.Collapsed;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Place(bool visible, double left = 0, double top = 0)
        {
            var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (Visibility != visibility) { Visibility = visibility; PropertyChanged?.Invoke(this, new(nameof(Visibility))); }
            if (!visible) return;
            if (Left != left) { Left = left; PropertyChanged?.Invoke(this, new(nameof(Left))); }
            if (Top != top) { Top = top; PropertyChanged?.Invoke(this, new(nameof(Top))); }
        }
    }
    public DateTimeOffset SimulationTime => epoch.AddSeconds(simulationSeconds);
    // Orbits are defined from a fixed reference epoch so repeat ground tracks stay over the stations on any date.
    private double OrbitTime => (epoch - SatelliteConstellation.ReferenceEpoch).TotalSeconds + simulationSeconds;
    public SolarState CurrentSun { get; private set; } = SolarLighting.At(DateTimeOffset.UtcNow);
    public string SimulationTimeText => SimulationTime.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss 'KST'", CultureInfo.InvariantCulture);
    public string TimeInput { get => timeInput; set { timeInput = value; Notify(); } }
    public string TimeError { get => timeError; private set { timeError = value; Notify(); } }
    public string TimeMode => !playing ? "일시정지" : realTime ? "현재 시각 · 실시간" : "시뮬레이션";
    public string SolarInfo => $"☀ 태양 직하점\n위도 {CurrentSun.LatitudeDegrees:0.0}° · 경도 {CurrentSun.LongitudeDegrees:0.0}°";
    public bool SelectedEclipsed => eclipsed[selectedIndex];
    public CircularOrbit SelectedOrbit
    {
        get => Orbits[selectedIndex];
        set
        {
            int index = Array.IndexOf(Orbits, value);
            if (index < 0 || selectedIndex == index) return;
            selectedIndex = index; capturing = false; contactSearchMessage = "";
            Notify(); Notify(nameof(SelectedInfo)); Notify(nameof(IsCapturing)); Notify(nameof(CaptureButtonText));
            if (IsTrueScale && followSatellite) distance = SatelliteFocusDistance;
            UpdateOrbitMaterials(); UpdateGroundTrack(); UpdateScene();
        }
    }
    public string SelectedInfo => $"선택: {SelectedOrbit.Name}\n{SelectedOrbit.GroupName}\n고도 {SelectedOrbit.AltitudeKm:N0} km · 경사각 {SelectedOrbit.InclinationDegrees:0.#}°\n가정한 전개 폭 {SceneScale.SatelliteSpanMeters(SelectedOrbit.Mission):0} m\n{(SelectedEclipsed ? "● 지구 그림자 안 · 일식" : "☀ 태양광 수신")}";
    public ObservableCollection<SatelliteLabel> SatelliteLabels { get; } = [];
    public sealed class SatelliteLabel(CircularOrbit orbit, Brush color) : INotifyPropertyChanged
    {
        public CircularOrbit Orbit { get; } = orbit;
        public string ShortName { get; } = orbit.Name.Split(' ')[0];
        public Brush Color { get; } = color;
        public string Name { get; private set; } = "";
        public double Left { get; private set; }
        public double Top { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(string name, double left, double top)
        {
            if (Name != name) { Name = name; PropertyChanged?.Invoke(this, new(nameof(Name))); }
            if (Left != left) { Left = left; PropertyChanged?.Invoke(this, new(nameof(Left))); }
            if (Top != top) { Top = top; PropertyChanged?.Invoke(this, new(nameof(Top))); }
        }
    }
    public string PlaybackText => playing ? "일시정지" : "재생";
    public string ElapsedText => TimeSpan.FromSeconds(simulationSeconds).ToString(@"hh\:mm\:ss");
    public double Speed
    {
        get => speed;
        set { if (!double.IsFinite(value)) return; speed = Math.Clamp(value, 1, 600); if (speed != 1) realTime = false; Notify(); Notify(nameof(TimeMode)); }
    }
    public Point3D CameraPosition
    {
        get
        {
            var position = (Vector3D)(cameraTarget + new Vector3D(distance * Math.Cos(pitch) * Math.Sin(yaw), distance * Math.Sin(pitch), distance * Math.Cos(pitch) * Math.Cos(yaw)));
            double minimum = followGroundStation ? 1.0005 : 1.06;
            if (position.Length < minimum) { position.Normalize(); position *= minimum; }
            return (Point3D)position;
        }
    }
    public Vector3D CameraDirection => cameraTarget - CameraPosition;

    public MainViewModel()
    {
        operations = new SatelliteOperations(GroundStations);
        satelliteEffects = new SatelliteEffects(GroundStations.Length);
        antennaAnchors = new Point3D[GroundStations.Length];
        groundAntennas = new GroundAntennaGeometry[GroundStations.Length]; stationAims = new GroundAntennaAim[GroundStations.Length];
        scheduler = new StationScheduler(GroundStations); servos = GroundStations.Select(_ => new AntennaServo()).ToArray();
        Color[] palette = [Color.FromRgb(74, 222, 226), Color.FromRgb(255, 188, 95), Color.FromRgb(184, 145, 255),
            Color.FromRgb(103, 226, 151), Color.FromRgb(255, 132, 173), Color.FromRgb(235, 240, 255)];
        colors = Orbits.Select(orbit => palette[orbit.ClusterId is int plane ? (orbit.Mission == SatelliteMission.A ? (plane - 1) % 3 : 3 + (plane - 1) % 2) : 5]).ToArray();
        // One label per shared orbit plane (first member), plus the selected satellite.
        planeRepresentative = Orbits.Select((orbit, i) => orbit.ClusterId is null || !Orbits.Take(i).Any(orbit.SharesPlaneWith)).ToArray();
        eclipsed = new bool[Orbits.Length];
        satellitePhysicalScales = new double[Orbits.Length];
        wingTransforms = new MatrixTransform3D?[Orbits.Length]; dishTransforms = new MatrixTransform3D?[Orbits.Length];
        wingAngles = new double[Orbits.Length];
        SatellitesView = CollectionViewSource.GetDefaultView(Orbits);
        SatellitesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CircularOrbit.GroupName)));
        labelCache = Orbits.Select((orbit, i) =>
        {
            var brush = new SolidColorBrush(colors[i]); brush.Freeze();
            return new SatelliteLabel(orbit, brush);
        }).ToArray();
        TogglePlaybackCommand = new ActionCommand(() => { playing = !playing; realTime = false; Notify(nameof(PlaybackText)); Notify(nameof(TimeMode)); });
        MeasureFramesCommand = new ActionCommand(() =>
        {
            if (measuringFrames) { FinishMeasurement("측정 종료"); return; }
            if (!MeasurementConditions) { FrameMeasurementStatus = "실제 축척 · 600배 · 촬영/통신 켜고 재생하세요."; Notify(nameof(FrameMeasurementStatus)); return; }
            frameTiming.Reset(); measuredOrbit = SelectedOrbit; measuringFrames = true;
            FrameMeasurementStatus = $"{SelectedOrbit.Name} · 실제 축척/50대/600배 · 측정 중";
            Notify(nameof(FrameMeasurementStatus)); Notify(nameof(FrameMeasurementButton));
        });
        ToggleCaptureCommand = new ActionCommand(() =>
        {
            capturing = !capturing; contactSearchMessage = "";
            if (capturing) FocusOperations(); else UpdateScene();
            Notify(nameof(IsCapturing)); Notify(nameof(CaptureButtonText));
        });
        FindContactCommand = new ActionCommand(() =>
        {
            double remaining = (new DateTimeOffset(2100, 12, 31, 23, 59, 59, TimeSpan.Zero) - SimulationTime).TotalSeconds;
            var delay = operations.NextContactDelay(SelectedOrbit, OrbitTime, CurrentSun.EarthRotationDegrees, Math.Clamp(remaining, 0, 86400));
            if (delay is null) { contactSearchMessage = "다음 24시간/지원 시각 안에 교신 가능한 지점이 없습니다."; Notify(nameof(CommunicationStatus)); return; }
            simulationSeconds += delay.Value; playing = realTime = false; previousTime = clock.Elapsed.TotalSeconds;
            contactSearchMessage = ""; TimeError = "";
            TimeInput = SimulationTime.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); Notify(nameof(TimeInput));
            FocusOperations(); Notify(nameof(PlaybackText));
        });
        ApplyTimeCommand = new ActionCommand(() =>
        {
            if (!DateTime.TryParseExact(TimeInput, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                || local.Year is < 2000 or > 2100)
            { TimeError = "2000~2100년의 yyyy-MM-dd HH:mm:ss 형식으로 입력하세요."; return; }
            var time = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromHours(9));
            if (time.UtcDateTime.Year < 2000) { TimeError = "UTC 기준 2000년 이후 시각을 입력하세요."; return; }
            SetSimulationTime(time);
        });
        CurrentTimeCommand = new ActionCommand(() => SetSimulationTime(DateTimeOffset.UtcNow, true));
        TimeInput = SimulationTime.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        SelectOrbitCommand = new ActionCommand(value => { if (value is CircularOrbit orbit) SelectedOrbit = orbit; });
        ResetCommand = new ActionCommand(() =>
        {
            solarView = earthOverview = false;
            celestialOverview = false; followedCelestial = -1;
            followSatellite = false;
            followGroundStation = false;
            realTime = false;
            simulationSeconds = 0; yaw = -2.5; pitch = 0.35; distance = 5.1; cameraTarget = new Point3D();
            fitOverview = true; FitOverview(); UpdateOrbitMaterials(); UpdateScene(); NotifyCamera();
        });
        FocusGroundStationsCommand = new ActionCommand(() => FocusGround(null));
        FocusStationCommand = new ActionCommand(value => { if (value is GroundStation station) FocusGround(station); });
        FocusEarthSystemCommand = new ActionCommand(() =>
        {
            solarView = celestialOverview = followSatellite = followGroundStation = false;
            earthOverview = true;
            followedCelestial = -1; cameraTarget = new Point3D(); yaw = -2.5; pitch = 0.35;
            fitOverview = true; FitOverview();
            UpdateOrbitMaterials(); UpdateScene();
            var moon = celestialPositions[1]; yaw = Math.Atan2(moon.X, moon.Z) + Math.PI / 2;
            NotifyCamera();
        });
        FocusCelestialsCommand = new ActionCommand(() => FocusSolarOverview(true));
        FocusInnerPlanetsCommand = new ActionCommand(() => FocusSolarOverview(false));
        FocusCelestialCommand = new ActionCommand(value =>
        {
            if (value is not CelestialLabel body) return;
            if (body.Index == 4) { FocusEarthSystemCommand.Execute(null); return; }
            solarView = body.Index != 1;
            earthOverview = false;
            if (solarView) RebuildPlanetOrbits();
            followSatellite = followGroundStation = celestialOverview = fitOverview = false;
            followedCelestial = body.Index;
            UpdateScene();
            var offset = DisplayBodyPosition(0) - cameraTarget;
            yaw = body.Index == 0 ? 0.5 : Math.Atan2(offset.X, offset.Z) + 0.45;
            pitch = 0.3; distance = BodyRadius(body.Index) * (body.Index == 7 ? 9 : 5);
            if (body.Index == 1)
            {
                // View the Moon from Earth: the tidally locked near side (and its maria "rabbit") with the current phase.
                var toEarth = new Point3D() - cameraTarget; toEarth.Normalize();
                yaw = Math.Atan2(toEarth.X, toEarth.Z); pitch = Math.Asin(Math.Clamp(toEarth.Y, -1, 1));
            }
            NotifyCamera();
        });
        FocusSunCommand = new ActionCommand(() => FocusCelestialCommand.Execute(CelestialLabels[0]));
        FocusSatelliteCommand = new ActionCommand(() =>
        {
            solarView = earthOverview = false;
            celestialOverview = false; followedCelestial = -1;
            fitOverview = false; followSatellite = true; distance = SatelliteFocusDistance;
            followGroundStation = false;
            var pose = SceneGeometry.SatellitePose(SelectedOrbit, OrbitTime);
            var offset = pose.Transform(SelectedOrbit.Mission == SatelliteMission.A ? new Vector3D(0.9, -1, 0.6) : new Vector3D(0.8, -0.6, 0.8)); offset.Normalize();
            yaw = Math.Atan2(offset.X, offset.Z); pitch = Math.Asin(offset.Y);
            UpdateOrbitMaterials(); UpdateScene(); Notify(nameof(PlaybackText));
        });
        Scene.Children.Add(new AmbientLight(Color.FromRgb(20, 24, 35)));
        Scene.Children.Add(sunLight);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.UriSource = new Uri("pack://application:,,,/WpfSatelliteTracking;component/Assets/earth.jpg"); bitmap.DecodePixelWidth = 5400; bitmap.EndInit(); bitmap.Freeze();
        var brush = new ImageBrush(bitmap); brush.Freeze();
        var material = new DiffuseMaterial(brush); material.Freeze();
        var earth = SceneGeometry.Sphere(1, material, 128, 64);
        earthPose = new RotateTransform3D(earthRotation);
        var earthSystem = new Model3DGroup { Transform = earthPose };
        earthSystem.Children.Add(earth);
        foreach (var station in GroundStations)
        {
            var assembly = new GroundAntennaGeometry(station); var antenna = assembly.Model;
            groundAntennas[stationScales.Count] = assembly;
            var basePoint = antenna.Transform.Transform(new Point3D()); var normal = (Vector3D)basePoint; normal.Normalize();
            var scale = new ScaleTransform3D(1, 1, 1, normal.X, normal.Y, normal.Z);
            var group = new Model3DGroup { Transform = scale }; group.Children.Add(antenna);
            stationGroups.Add(group); stationScales.Add(scale); earthSystem.Children.Add(group);
        }
        earthSystem.Children.Add(tileLayer);
        nightMaterial = nightLights.Model.Material;
        tileNightMaterial = MapTileGeometry.CreateNightMaterial(nightMaterial);
        earthSystem.Children.Add(nightLights.Model);
        var cloudAsset = SceneGeometry.Clouds();
        clouds = new GeometryModel3D(cloudAsset.Geometry, cloudAsset.Material); cloudMaterial = clouds.Material; earthSystem.Children.Add(clouds);
        earthSystem.Children.Add(groundTrackHost); // last, so the earlier earth-system child indices stay stable
        Scene.Children.Add(earthSystem);
        Scene.Children.Add(SceneGeometry.SpaceBackground());
        var orbitLayer = new Model3DGroup();
        var satelliteLayer = new Model3DGroup();
        Scene.Children.Add(orbitLayer); Scene.Children.Add(satelliteLayer);
        var templates = new Dictionary<Color, (Model3DGroup Lit, Model3DGroup Shadow)>();
        var lowTemplates = new Dictionary<Color, (Model3DGroup Lit, Model3DGroup Shadow)>();
        modelSpans = new double[Orbits.Length]; shownAssets = new Model3D?[Orbits.Length];
        var pieceCache = new Dictionary<Model3D, SatelliteBatch.Piece[]>();
        SatelliteBatch.Piece[] Pieces(Model3D template) => pieceCache.TryGetValue(template, out var p) ? p : pieceCache[template] = SatelliteBatch.Flatten(template);
        for (int i = 0; i < Orbits.Length; i++)
        {
            if (planeRepresentative[i])
            {
                // One merged mesh for the group's inertial rings, and its Earth-fixed repeat ground track.
                var members = Orbits.Where(o => o == Orbits[i] || o.SharesPlaneWith(Orbits[i])).ToArray();
                var orbitModel = SceneGeometry.OrbitGroup(members, colors[i]);
                orbitModels.Add(orbitModel); orbitRepresentatives.Add(i); orbitLayer.Children.Add(orbitModel);
                double start = 0, rotation = SolarLighting.At(SatelliteConstellation.ReferenceEpoch).EarthRotationDegrees;
                groundTracks[i] = SceneGeometry.GroundTrack(Orbits[i], start, SatelliteConstellation.CycleSeconds(Orbits[i].Mission), rotation, colors[i]);
            }
            var satellite = new Model3DGroup();
            if (!templates.TryGetValue(colors[i], out var template))
            {
                template = (SceneGeometry.Satellite(colors[i], mission: Orbits[i].Mission), SceneGeometry.Satellite(colors[i], true, Orbits[i].Mission));
                templates.Add(colors[i], template);
                lowTemplates.Add(colors[i], (SceneGeometry.LowDetailSatellite(colors[i], false, Orbits[i].Mission), SceneGeometry.LowDetailSatellite(colors[i], true, Orbits[i].Mission)));
            }
            // Static geometry for the shared batch: full model in its rest pose, and the distant proxy.
            staticPieces[i] = (Pieces(template.Lit), Pieces(template.Shadow), Pieces(lowTemplates[colors[i]].Lit), Pieces(lowTemplates[colors[i]].Shadow));
            modelSpans[i] = template.Lit.Bounds.SizeX;
            if (Orbits[i].Mission == SatelliteMission.A)
            {
                var wings = new MatrixTransform3D(); var dish = new MatrixTransform3D(TypeASatelliteGeometry.AntennaPose(0, false));
                wingTransforms[i] = wings; dishTransforms[i] = dish;
                litAssets.Add(TypeASatelliteGeometry.CreateInstance(template.Lit, wings, dish));
                eclipsedAssets.Add(TypeASatelliteGeometry.CreateInstance(template.Shadow, wings, dish));
            }
            else
            {
                litAssets.Add((Model3DGroup)SceneGeometry.CopySatelliteInstance(template.Lit));
                eclipsedAssets.Add((Model3DGroup)SceneGeometry.CopySatelliteInstance(template.Shadow));
            }
            satellitePhysicalScales[i] = SceneScale.Units(SceneScale.SatelliteSpanMeters(Orbits[i].Mission) / 1000) / template.Lit.Bounds.SizeX;
            satellite.Children.Add(litAssets[i]);
            var transform = new MatrixTransform3D();
            satellite.Transform = transform; satelliteTransforms.Add(transform); satelliteModels.Add(satellite); satelliteLayer.Children.Add(satellite);
        }
        satelliteLayer.Children.Add(staticBatch.Model); // after the per-satellite groups, so satellite i stays child i
        Scene.Children.Add(SceneGeometry.Atmosphere());
        for (int i = 0; i < celestialTransforms.Length; i++)
        {
            var body = i == 1 ? moonRelief.Model : CelestialGeometry.Create(i);
            var transform = new Transform3DGroup(); transform.Children.Add(bodyScales[i]);
            if (i == 1) { transform.Children.Add(new RotateTransform3D(moonTilt)); transform.Children.Add(new RotateTransform3D(moonFacing)); }
            transform.Children.Add(celestialTransforms[i]);
            body.Transform = transform; Scene.Children.Add(body);
        }
        for (int i = 0; i < Scene.Children.Count; i++)
        {
            if (i != 3 && (i < 7 || i == 8))
            {
                earthLocalScene.Children.Add(Scene.Children[i]);
                if (i != 5) distantScene.Children.Add(Scene.Children[i]);
            }
        }
        earthLocalScene.Children.Add(satelliteEffects.Scene); distantScene.Children.Add(satelliteEffects.Scene);
        earthLocalScene.Children.Add(coverageLinks.Scene); distantScene.Children.Add(coverageLinks.Scene);
        // Coverage is added only while the mode is on, after everything else so its translucent surfaces blend last.
        coverageGroup.Transform = new RotateTransform3D(coverageRotation);
        double highestShell = Orbits.Max(orbit => orbit.Radius);
        for (int s = 0; s < GroundStations.Length; s++)
        {
            coverages[s] = new StationCoverage(GroundStations[s], highestShell, Orbits.Min(orbit => orbit.AltitudeKm), s);
            coverageGroup.Children.Add(coverages[s].Model);
        }
        solarScene.Children.Add(new AmbientLight(Color.FromRgb(20, 24, 35)));
        solarScene.Children.Add(new PointLight(Colors.White, new Point3D()) { ConstantAttenuation = 1, Range = 1000000 });
        for (int i = 0; i < solarTransforms.Length; i++)
        {
            var model = i == 3 ? new GeometryModel3D(earth.Geometry, earth.Material) : CelestialGeometry.Create(i == 0 ? 0 : i + 1);
            model.Transform = solarTransforms[i]; solarScene.Children.Add(model);
        }
        planetOrbits.Transform = planetOrbitScale; solarScene.Children.Add(planetOrbits);
        var saturnRings = CelestialGeometry.SaturnRings().Clone(); saturnRings.Transform = solarTransforms[6]; solarScene.Children.Add(saturnRings);
        RebuildPlanetOrbits();
        nearSatelliteScene.Children.Add(Scene.Children[0]); nearSatelliteScene.Children.Add(sunLight); nearSatelliteScene.Children.Add(satelliteLayer);
        Scene.Transform = distantScene.Transform = nearSatelliteScene.Transform = earthLocalScene.Transform = solarScene.Transform = renderTranslation;
        FitOverview(); UpdateOrbitMaterials();
        UpdateScene();
    }

    public void Start()
    {
        if (disposed || rendering) return;
        previousTime = sampleTime = clock.Elapsed.TotalSeconds;
        sampleFrames = 0; sampleWorkMilliseconds = 0; lastRenderingTime = TimeSpan.MinValue;
        CompositionFps = AverageUpdateMilliseconds = 0;
        FrameStatus = "WPF FPS 측정 중…"; Notify(nameof(FrameStatus));
        clock.Start(); rendering = true; CompositionTarget.Rendering += OnRendering;
    }
    public void SetRenderingActive(bool active)
    {
        if (active) Start();
        else if (rendering)
        {
            if (measuringFrames) FinishMeasurement("숨김/최소화로 중단");
            CompositionTarget.Rendering -= OnRendering; rendering = false; clock.Stop();
            CompositionFps = AverageUpdateMilliseconds = 0;
            FrameStatus = "숨김 / 최소화 · 렌더링 일시 중지"; Notify(nameof(FrameStatus));
        }
    }
    private void OnRendering(object? sender, EventArgs e)
    {
        var renderTime = ((RenderingEventArgs)e).RenderingTime;
        if (renderTime == lastRenderingTime) return;
        lastRenderingTime = renderTime;
        long workStart = Stopwatch.GetTimestamp();
        nightLights.TryPresent(); // Retry a ready mask even while simulation playback is paused.
        moonRelief.TryPresent();
        double now = clock.Elapsed.TotalSeconds;
        if (playing)
        {
            simulationSeconds = realTime ? (DateTimeOffset.UtcNow - epoch).TotalSeconds : simulationSeconds + (now - previousTime) * speed;
            double limit = (new DateTimeOffset(2100, 12, 31, 23, 59, 59, TimeSpan.Zero) - epoch).TotalSeconds;
            if (simulationSeconds >= limit)
            {
                simulationSeconds = limit; playing = false; realTime = false;
                TimeError = "지원 시각의 끝에 도달해 정지했습니다."; Notify(nameof(PlaybackText));
            }
            UpdateScene(true);
        }
        else if (servos.Any(servo => !servo.Locked && !(servo.Target < 0 && servo.Elevation >= 90)))
        {
            // Paused: the antennas still finish slewing on or off target (orbit time is frozen).
            UpdateStations(now); RefreshEffects(now);
            if (now - informationTime >= 0.2) { informationTime = now; Notify(nameof(CommunicationStatus)); }
        }
        previousTime = now;
        sampleFrames++;
        double frameWork = Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;
        if (measuringFrames)
        {
            if (!MeasurementConditions || measuredOrbit != SelectedOrbit) FinishMeasurement("조건 변경으로 중단");
            else
            {
                bool contact = false;
                foreach (var station in operations.Contacts) contact |= station.Connected;
                frameTiming.Add(now, frameWork, contact);
            }
        }
        sampleWorkMilliseconds += frameWork;
        double elapsed = now - sampleTime;
        if (elapsed >= 1)
        {
            CompositionFps = sampleFrames / elapsed;
            AverageUpdateMilliseconds = sampleWorkMilliseconds / sampleFrames;
            FrameStatus = $"WPF {CompositionFps:F0} FPS · 장면 갱신 {AverageUpdateMilliseconds:F2} ms · GPU Tier {RenderCapability.Tier >> 16}";
            Notify(nameof(FrameStatus));
            if (measuringFrames) { FrameMeasurementStatus = $"{SelectedOrbit.Name} · 실제 축척/50대/600배 · 측정 중\n{frameTiming.Summary}"; Notify(nameof(FrameMeasurementStatus)); }
            sampleFrames = 0; sampleWorkMilliseconds = 0; sampleTime = now;
        }
    }
    private void UpdateScene(bool animationFrame = false)
    {
        double now = clock.Elapsed.TotalSeconds;
        bool updateBodies = !animationFrame || now - celestialUpdateTime >= 0.2;
        if (updateBodies)
        {
            celestialState = CelestialPositions.At(SimulationTime);
            if (solarView && planetOrbitYear != SimulationTime.Year) RebuildPlanetOrbits();
        }
        double previousRotation = CurrentSun.EarthRotationDegrees;
        CurrentSun = SolarLighting.At(SimulationTime);
        if (IsTrueScale) CurrentSun = SolarLighting.WithInertialSun(CurrentSun, celestialState.SunAu);
        earthRotation.Angle = coverageRotation.Angle = CurrentSun.EarthRotationDegrees;
        var sunlight = CurrentSun.InertialDirection;
        if (updateBodies)
        {
            celestialUpdateTime = now;
            var bodies = celestialState;
            double unitsPerAu = IsTrueScale ? SceneScale.Units(SceneScale.AstronomicalUnitKm) : 18;
            celestialPositions[0] = IsTrueScale ? (Point3D)((Vector3D)SceneGeometry.Point(bodies.SunAu) * unitsPerAu)
                : new Point3D(sunlight.X * 18, sunlight.Y * 18, sunlight.Z * 18);
            // All bodies receive light from the displayed Sun, rather than the Earth-to-Sun direction.
            sunLight.Position = celestialPositions[0];
            celestialPositions[1] = (Point3D)((Vector3D)SceneGeometry.Point(bodies.MoonEarthRadii)
                * (IsTrueScale ? SceneScale.Units(SceneScale.LunarReferenceRadiusKm) : 4.2 / 60.2666));
            celestialPositions[2] = (Point3D)((Vector3D)SceneGeometry.Point(bodies.MercuryAu) * unitsPerAu);
            celestialPositions[3] = (Point3D)((Vector3D)SceneGeometry.Point(bodies.VenusAu) * unitsPerAu);
            if (solarView)
            {
                if (celestialOverview && !solarScene.Children.Contains(planetOrbits)) solarScene.Children.Insert(11, planetOrbits);
                else if (!celestialOverview) solarScene.Children.Remove(planetOrbits);
                var sun = SceneGeometry.Point(bodies.SunAu);
                solarBodyPositions[0] = new Point3D();
                for (int planet = 0; planet < bodies.HeliocentricAu.Length; planet++)
                {
                    int body = planet == 0 ? 4 : planet < 3 ? planet + 1 : planet + 2;
                    solarBodyPositions[body] = (Point3D)((Vector3D)SceneGeometry.Point(bodies.HeliocentricAu[planet]) * unitsPerAu);
                }
                solarEarthPosition = (Point3D)(-(Vector3D)sun * unitsPerAu);
                for (int i = 0; i < solarTransforms.Length; i++)
                {
                    int body = i == 0 ? 0 : i + 1;
                    double radius = BodyRadius(body);
                    double sourceRadius = i == 3 ? 1 : CelestialGeometry.Radii[body];
                    var pose = Matrix3D.Identity; pose.Scale(new Vector3D(radius / sourceRadius, radius / sourceRadius, radius / sourceRadius));
                    pose.Translate((Vector3D)(i == 3 ? solarEarthPosition : solarBodyPositions[body])); solarTransforms[i].Matrix = pose;
                }
                planetOrbitScale.ScaleX = planetOrbitScale.ScaleY = planetOrbitScale.ScaleZ = unitsPerAu;
            }
            for (int i = 0; i < celestialTransforms.Length; i++)
            {
                celestialTransforms[i].OffsetX = celestialPositions[i].X;
                celestialTransforms[i].OffsetY = celestialPositions[i].Y;
                celestialTransforms[i].OffsetZ = celestialPositions[i].Z;
            }
            var moonDirection = (Vector3D)celestialPositions[1]; moonDirection.Normalize();
            moonFacing.Angle = Math.Atan2(moonDirection.Z, -moonDirection.X) * 180 / Math.PI;
            moonTilt.Angle = Math.Asin(Math.Clamp(-moonDirection.Y, -1, 1)) * 180 / Math.PI;
            // Re-bake crater relief for the displayed Sun direction in Moon-local axes (inverse of turn, then inverse of tilt).
            var toSun = celestialPositions[0] - celestialPositions[1];
            double facing = moonFacing.Angle * Math.PI / 180, cf = Math.Cos(facing), sf = Math.Sin(facing);
            double tilt = moonTilt.Angle * Math.PI / 180, ct = Math.Cos(tilt), st = Math.Sin(tilt);
            double tx = cf * toSun.X - sf * toSun.Z, tz = sf * toSun.X + cf * toSun.Z;
            if (showMoon) moonRelief.RequestUpdate((ct * tx + st * toSun.Y, -st * tx + ct * toSun.Y, tz));
        }
        // Explicit changes while paused / at 1x recompute synchronously; during fast playback a UI action would otherwise
        // stall the UI thread for the full 2048x1024 mask, so it takes the background path like animation frames.
        if (!showNightLights) { } // hidden: no mask computation or 8 MiB uploads
        else if (!animationFrame && (!playing || speed <= 1)) nightLights.Update(SimulationTime, CurrentSun);
        else if (!animationFrame || now - nightRequestTime >= 0.2)
        {
            // At 600x a 30-simulated-second threshold otherwise rewrites 8 MiB up to 20 times/s.
            // Five requests/s retain the 2048px map and keep satellite/camera animation at full cadence.
            nightRequestTime = now;
            nightLights.RequestUpdate(SimulationTime, CurrentSun);
        }
        if (followGroundStation)
        {
            UpdateGroundTarget();
            yaw += ((CurrentSun.EarthRotationDegrees - previousRotation + 540) % 360 - 180) * Math.PI / 180;
        }
        for (int i = 0; i < Orbits.Length; i++)
        {
            var pose = SceneGeometry.SatellitePose(Orbits[i], OrbitTime);
            bool shadow = SolarLighting.IsEclipsed(Orbits[i].Position(OrbitTime), CurrentSun);
            // Joints change a transform per satellite per frame; by default only the selected one articulates, the rest hold their pose.
            if ((individual[i] || i == selectedIndex) && wingTransforms[i] is { } wings && dishTransforms[i] is { } dish)
            {
                var sun = CurrentSun.InertialDirection;
                double localY = sun.X * pose.M21 + sun.Y * pose.M22 + sun.Z * pose.M23;
                double localZ = sun.X * pose.M31 + sun.Y * pose.M32 + sun.Z * pose.M33;
                wingAngles[i] = SatelliteArticulation.SolarArrayAngle(localY, localZ, wingAngles[i], shadow);
                var wingPose = Matrix3D.Identity;
                wingPose.Rotate(new Quaternion(new Vector3D(1, 0, 0), wingAngles[i]));
                if (wings.Matrix != wingPose) wings.Matrix = wingPose;
                var dishPose = TypeASatelliteGeometry.AntennaPose(OrbitTime + Orbits[i].InitialPhaseDegrees / 2,
                    capturing && i == selectedIndex);
                if (dish.Matrix != dishPose) dish.Matrix = dishPose;
            }
            double scale = IsTrueScale ? satellitePhysicalScales[i] : SceneScale.ObservationSatelliteScale;
            pose.ScalePrepend(new Vector3D(scale, scale, scale));
            satelliteTransforms[i].Matrix = pose;
            eclipsed[i] = shadow;
        }
        UpdateVisibility();
        operations.Update(SelectedOrbit, OrbitTime, CurrentSun.EarthRotationDegrees, capturing && !solarView && followedCelestial < 0);
        UpdateStations(now);
        RefreshEffects(now);
        if (celestialOverview)
        {
            UpdateCelestialTarget(); if (fitOverview) FitOverview(); NotifyCamera(!animationFrame);
        }
        else if (followedCelestial >= 0) { cameraTarget = DisplayBodyPosition(followedCelestial); NotifyCamera(!animationFrame); }
        else if (followSatellite) { cameraTarget = SceneGeometry.Point(SelectedOrbit.Position(OrbitTime)); NotifyCamera(!animationFrame); }
        else if (followGroundStation) NotifyCamera(!animationFrame);
        else UpdateLabels();
        if (!animationFrame || now - informationTime >= 0.2)
        {
            informationTime = now;
            Notify(nameof(ElapsedText));
            Notify(nameof(SimulationTimeText)); Notify(nameof(SolarInfo)); Notify(nameof(SelectedInfo)); Notify(nameof(SelectedEclipsed)); Notify(nameof(TimeMode));
            Notify(nameof(CaptureStatus)); Notify(nameof(CommunicationStatus));
        }
    }
    public void SetSimulationTime(DateTimeOffset time, bool useRealTime = false)
    {
        _ = SolarLighting.At(time);
        contactSearchMessage = "";
        epoch = time.ToUniversalTime(); simulationSeconds = 0; realTime = useRealTime; playing = useRealTime;
        if (useRealTime) Speed = 1;
        TimeError = ""; TimeInput = SimulationTime.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        previousTime = clock.Elapsed.TotalSeconds;
        if (solarView) RebuildPlanetOrbits();
        UpdateScene(); Notify(nameof(PlaybackText));
    }
    public void Rotate(double dx, double dy)
    {
        if (followGroundStation && distance < 0.1)
        {
            double degreesPerPixel = distance * 2 * Math.Tan(21 * Math.PI / 180) * 180 / Math.PI / viewportWidth;
            groundLatitude = Math.Clamp(groundLatitude + dy * degreesPerPixel, -84, 84);
            groundLongitude = (groundLongitude - dx * degreesPerPixel / Math.Cos(groundLatitude * Math.PI / 180) + 540) % 360 - 180;
            UpdateGroundTarget(true); NotifyCamera(); return;
        }
        yaw -= dx * 0.008; pitch = Math.Clamp(pitch + dy * 0.008, -1.4, 1.4); NotifyCamera();
    }
    public void Zoom(int delta)
    {
        fitOverview = false;
        double minimum = celestialOverview ? 5 : followedCelestial >= 0 ? BodyRadius(followedCelestial) * (followedCelestial == 7 ? 5.52 : 2.4) : cameraTarget == new Point3D() ? 3.4
            : followSatellite ? (IsTrueScale ? SatelliteFocusDistance * 0.6 : 0.35 * SceneScale.ObservationSatelliteScale) : 0.003;
        double previousDistance = distance;
        distance = Math.Clamp(distance * Math.Exp(-delta * 0.001), minimum, earthOverview ? IsTrueScale ? 1000 : 60 : IsTrueScale && (celestialOverview || followedCelestial >= 0) ? 10000000
            : celestialOverview || followedCelestial >= 0 ? 10000 : 15); NotifyCamera();
        if (followGroundStation && previousDistance >= 0.1 && distance < 0.1) { UpdateGroundTarget(true); NotifyCamera(); }
    }
    public void SetViewportSize(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        viewportWidth = width; viewportHeight = height;
        if (fitOverview) FitOverview(); NotifyCamera();
    }
    private void FitOverview()
    {
        double halfAngle = Math.Min(21 * Math.PI / 180, Math.Atan(Math.Tan(21 * Math.PI / 180) * viewportHeight / viewportWidth));
        double radius = earthOverview ? IsTrueScale ? 66 : 4.8 : 1.5;
        if (celestialOverview)
        {
            radius = (IsTrueScale ? SceneScale.Units(SceneScale.AstronomicalUnitKm) : 18) * (fullSolarSystem ? 31.5 : 1.75);
        }
        distance = radius / Math.Sin(halfAngle) * 1.04;
    }
    private void UpdateCelestialTarget()
    {
        cameraTarget = new Point3D();
    }
    private Point3D DisplayBodyPosition(int body) => solarView ? solarBodyPositions[body] : celestialPositions[body];
    private void FocusSolarOverview(bool includeOuterPlanets)
    {
        fullSolarSystem = includeOuterPlanets;
        solarView = true; earthOverview = false; RebuildPlanetOrbits();
        followSatellite = followGroundStation = false; followedCelestial = -1;
        celestialOverview = true; fitOverview = true;
        yaw = 0.5; pitch = 1.0;
        UpdateScene(); FitOverview(); NotifyCamera();
    }
    private void FocusOperations()
    {
        solarView = earthOverview = celestialOverview = followGroundStation = fitOverview = false;
        followSatellite = true;
        followedCelestial = -1;
        var normal = (Vector3D)SceneGeometry.Point(SelectedOrbit.Position(OrbitTime)); normal.Normalize();
        cameraTarget = SceneGeometry.Point(SelectedOrbit.Position(OrbitTime)); distance = 0.9;
        yaw = Math.Atan2(normal.X, normal.Z) + 0.9; pitch = Math.Clamp(Math.Asin(normal.Y) + 0.2, -1.35, 1.35);
        UpdateOrbitMaterials(); UpdateScene(); NotifyCamera();
    }
    private void RebuildPlanetOrbits()
    {
        planetOrbitYear = SimulationTime.Year;
        planetOrbits.Children.Clear();
        Color[] colors = [Colors.DeepSkyBlue, Colors.SlateGray, Colors.Goldenrod, Colors.Coral, Colors.Bisque, Colors.Khaki, Colors.PaleTurquoise, Colors.CornflowerBlue];
        for (int i = 0; i < 8; i++) planetOrbits.Children.Add(SceneGeometry.PlanetOrbit(CelestialPositions.OrbitPath(i, SimulationTime), colors[i]));
    }
    private void UpdateOrbitMaterials()
    {
        for (int i = 0; i < orbitModels.Count; i++)
        {
            int representative = orbitRepresentatives[i];
            bool selected = representative == selectedIndex || Orbits[representative].SharesPlaneWith(SelectedOrbit);
            double brightness = selected ? 0.9 : 0.45;
            var c = colors[representative];
            // Each satellite has its own plane (repeat ground tracks), so by default only the selected group's rings show.
            var material = followSatellite || !showOrbits || (!selected && !showAllOrbits) ? null : SceneGeometry.MarkerMaterial(Color.FromRgb((byte)(c.R * brightness), (byte)(c.G * brightness), (byte)(c.B * brightness)));
            orbitModels[i].Material = material; orbitModels[i].BackMaterial = material;
        }
    }
    public bool SelectModel(Model3D model)
    {
        int index = satelliteModels.FindIndex(s => SceneGeometry.Contains(s, model));
        if (index < 0) return false;
        SelectedOrbit = Orbits[index]; return true;
    }
    // Picking inside the shared static batch resolves the owning satellite from the hit vertex.
    public bool SelectModel(Model3D model, int vertexIndex)
    {
        if (SelectModel(model)) return true;
        if (staticBatch.OwnerOf(model, vertexIndex) is not int index) return false;
        SelectedOrbit = Orbits[index]; return true;
    }
    // Draw only what can be seen: satellites behind the camera, off screen or behind the Earth are skipped, and ground
    // antennas on the far side of the Earth are not drawn. Visible satellites are drawn static inside one shared batch
    // (full model, or the proxy below ~24 px); they become individual articulated models only when few are on screen
    // (zoomed in), when "all satellites move" is on, or when selected.
    public const int IndividualSatelliteLimit = 8;
    private readonly List<Model3DGroup> stationGroups = [];
    private readonly double[] modelSpans;
    private readonly Model3D?[] shownAssets;
    private readonly bool[] planeRepresentative;
    private readonly bool[] individual = new bool[SatelliteConstellation.Count], visibleNow = new bool[SatelliteConstellation.Count];
    private readonly double[] pixelSizes = new double[SatelliteConstellation.Count];
    private readonly (SatelliteBatch.Piece[] Lit, SatelliteBatch.Piece[] Shadow, SatelliteBatch.Piece[] LowLit, SatelliteBatch.Piece[] LowShadow)[] staticPieces
        = new (SatelliteBatch.Piece[], SatelliteBatch.Piece[], SatelliteBatch.Piece[], SatelliteBatch.Piece[])[SatelliteConstellation.Count];
    private readonly SatelliteBatch staticBatch = new();
    private double batchSeconds = double.NaN;
    private bool batchTrueScale;
    private static readonly Model3DGroup HiddenAsset = CreateHiddenAsset();
    private static Model3DGroup CreateHiddenAsset() { var empty = new Model3DGroup(); empty.Freeze(); return empty; }
    private void UpdateVisibility()
    {
        if (modelSpans is null || satelliteModels.Count < Orbits.Length || staticPieces[^1].Lit is null || solarView) return;
        var camera = CameraPosition;
        var forward = CameraDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0)); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double focal = viewportWidth / (2 * Math.Tan(21 * Math.PI / 180));
        for (int i = 0; i < Orbits.Length; i++)
        {
            var position = SceneGeometry.Point(Orbits[i].Position(OrbitTime));
            var ray = position - camera;
            double depth = Vector3D.DotProduct(ray, forward);
            double size = modelSpans[i] * (IsTrueScale ? satellitePhysicalScales[i] : SceneScale.ObservationSatelliteScale);
            bool visible = depth > -size;
            if (visible)
            {
                // Occluded when the camera-to-satellite segment passes through the globe.
                double fraction = Math.Clamp(-Vector3D.DotProduct((Vector3D)camera, ray) / ray.LengthSquared, 0, 1);
                visible = ((Vector3D)(camera + ray * fraction)).Length > 0.999;
            }
            double pixels = depth > 1e-12 ? size * focal / depth : double.PositiveInfinity;
            if (visible && depth > 1e-12)
            {
                double x = Vector3D.DotProduct(ray, right) * focal / depth, y = Vector3D.DotProduct(ray, up) * focal / depth;
                visible = Math.Abs(x) < viewportWidth / 2 + pixels && Math.Abs(y) < viewportHeight / 2 + pixels;
            }
            visibleNow[i] = visible; pixelSizes[i] = pixels;
        }
        int visibleCount = visibleNow.Count(v => v);
        bool few = visibleCount <= IndividualSatelliteLimit;
        for (int i = 0; i < Orbits.Length; i++)
        {
            bool alone = visibleNow[i] && (few || animateAllSatellites || i == selectedIndex);
            individual[i] = alone;
            Model3D asset = !alone ? HiddenAsset : eclipsed[i] ? eclipsedAssets[i] : litAssets[i];
            if (!ReferenceEquals(shownAssets[i], asset)) { shownAssets[i] = asset; satelliteModels[i].Children[0] = asset; }
            if (visibleNow[i] && !alone)
            {
                var pieces = staticPieces[i];
                staticBatch.Add(i, pixelSizes[i] < 24 ? (eclipsed[i] ? pieces.LowShadow : pieces.LowLit) : (eclipsed[i] ? pieces.Shadow : pieces.Lit),
                    satelliteTransforms[i].Matrix);
            }
        }
        bool posesChanged = OrbitTime != batchSeconds || IsTrueScale != batchTrueScale;
        batchSeconds = OrbitTime; batchTrueScale = IsTrueScale;
        staticBatch.Commit(posesChanged);
        for (int i = 0; i < stationGroups.Count; i++)
        {
            var normal = earthPose.Transform(SatelliteOperations.StationPosition(GroundStations[i], 0) is var s ? new Vector3D(s.X, s.Y, s.Z) : default);
            bool facing = Vector3D.DotProduct(normal, camera - (Point3D)normal) > -0.05;
            bool shown = stationGroups[i].Children.Count > 0;
            if (facing && !shown) stationGroups[i].Children.Add(groundAntennas[i].Model);
            else if (!facing && shown) stationGroups[i].Children.Clear();
        }
    }
    private void UpdateLabels()
    {
        var camera = CameraPosition;
        var forward = CameraDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0)); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double focal = viewportWidth / (2 * Math.Tan(21 * Math.PI / 180));
        int visibleIndex = 0;
        for (int i = 0; i < Orbits.Length; i++)
        {
            var label = labelCache[i];
            bool representative = planeRepresentative[i] || i == selectedIndex;
            if (!showSatelliteLabels || !representative || celestialOverview || followedCelestial >= 0 || (followSatellite && i != selectedIndex)) { SatelliteLabels.Remove(label); continue; }
            var position = SceneGeometry.Point(Orbits[i].Position(OrbitTime));
            var ray = position - camera;
            double depth = Vector3D.DotProduct(ray, forward);
            if (depth <= 0) { SatelliteLabels.Remove(label); continue; }
            double fraction = Math.Clamp(-Vector3D.DotProduct((Vector3D)camera, ray) / ray.LengthSquared, 0, 1);
            if (((Vector3D)(camera + ray * fraction)).Length < 1.045) { SatelliteLabels.Remove(label); continue; }
            double x = viewportWidth / 2 + Vector3D.DotProduct(ray, right) * focal / depth;
            double y = viewportHeight / 2 - Vector3D.DotProduct(ray, up) * focal / depth;
            if (x < 0 || x > viewportWidth || y < 0 || y > viewportHeight) { SatelliteLabels.Remove(label); continue; }
            label.Update((i == selectedIndex ? "● " : "") + label.ShortName + (followSatellite ? " · 추적" : ""),
                followSatellite ? 12 : Math.Clamp(x + 20, 0, Math.Max(0, viewportWidth - 110)), followSatellite ? 12 : Math.Clamp(y - 24, 0, Math.Max(0, viewportHeight - 30)));
            if (!SatelliteLabels.Contains(label)) SatelliteLabels.Insert(Math.Min(visibleIndex, SatelliteLabels.Count), label);
            visibleIndex++;
        }
        celestialLabelBounds.Clear();
        for (int i = 0; i < CelestialLabels.Count; i++)
        {
            if ((solarView ? i == 1 : i != 1) || (i == 1 && !showMoon)) { CelestialLabels[i].Place(false); continue; }
            if (celestialOverview && !fullSolarSystem && i >= 6) { CelestialLabels[i].Place(false); continue; }
            if (i == 4 && !celestialOverview) { CelestialLabels[i].Place(false); continue; }
            var position = i == 4 ? solarEarthPosition : DisplayBodyPosition(i);
            var ray = position - camera;
            double depth = Vector3D.DotProduct(ray, forward);
            double fraction = Math.Clamp(-Vector3D.DotProduct((Vector3D)camera, ray) / ray.LengthSquared, 0, 1);
            if (depth <= 0 || (!solarView && i != 4 && ((Vector3D)(camera + ray * fraction)).Length < 1.01)) { CelestialLabels[i].Place(false); continue; }
            double x = viewportWidth / 2 + Vector3D.DotProduct(ray, right) * focal / depth;
            double y = viewportHeight / 2 - Vector3D.DotProduct(ray, up) * focal / depth;
            if (x < 0 || x > viewportWidth || y < 0 || y > viewportHeight) { CelestialLabels[i].Place(false); continue; }
            double top = Math.Clamp(y - 24, 0, Math.Max(0, viewportHeight - 28));
            double left = Math.Clamp(x + 16, 0, Math.Max(0, viewportWidth - 65));
            var bounds = new Rect(left, top, 65, 24);
            for (int attempt = 0; attempt < 30; attempt++)
            {
                bool overlaps = false;
                foreach (var occupied in celestialLabelBounds) if (bounds.IntersectsWith(occupied)) { overlaps = true; break; }
                if (!overlaps) break;
                top += 28;
                if (top > viewportHeight - 28) { top = 8; left = Math.Clamp(left + 70, 0, Math.Max(0, viewportWidth - 65)); }
                bounds = new Rect(left, top, 65, 24);
            }
            celestialLabelBounds.Add(bounds);
            CelestialLabels[i].Place(true, left, top);
        }
    }
    private void RefreshEffects(double now)
    {
        for (int i = 0; i < antennaAnchors.Length; i++)
            antennaAnchors[i] = earthPose.Transform(stationScales[i].Transform(groundAntennas[i].FeedPoint));
        // Station links come from the network schedule; the effect class only draws the selected satellite's imaging.
        satelliteEffects.Update(operations, antennaAnchors, false, now, CameraDirection.Length);
        bool links = ShowCommunications && !solarView && followedCelestial < 0;
        for (int s = 0; s < antennaAnchors.Length; s++)
        {
            int target = scheduler.Targets[s];
            coverageLinks.Update(s, antennaAnchors[s], links && target >= 0 && servos[s].Locked ? SceneGeometry.Point(Orbits[target].Position(OrbitTime)) : null,
                now, CameraDirection.Length);
        }
    }    private void NotifyCamera(bool updateTiles = true)
    {
        var origin = RenderOrigin;
        renderTranslation.OffsetX = -origin.X; renderTranslation.OffsetY = -origin.Y; renderTranslation.OffsetZ = -origin.Z;
        Notify(nameof(CameraPosition)); Notify(nameof(RenderCameraPosition)); Notify(nameof(CameraDirection)); Notify(nameof(CameraNearPlane));
        Notify(nameof(CameraFarPlane)); Notify(nameof(NearSatellitePlane)); Notify(nameof(MainScene)); Notify(nameof(NearSatelliteScene)); Notify(nameof(SkyScene));
        Notify(nameof(IsSolarSystemView)); Notify(nameof(ViewDescription)); NotifyFocusMode();
        UpdateLabels();
        double scale = followGroundStation ? Math.Min(1, distance / 0.75) : 1;
        bool stationScaleChanged = false;
        foreach (var stationScale in stationScales)
        {
            if (stationScale.ScaleX == scale) continue;
            stationScale.ScaleX = stationScale.ScaleY = stationScale.ScaleZ = scale; stationScaleChanged = true;
        }
        if (stationScaleChanged) RefreshEffects(clock.Elapsed.TotalSeconds);
        UpdateVisibility();
        // Earth rotation changes the camera, but not the Earth-fixed tile footprint.
        if (updateTiles) QueueMapTiles();
    }
    private void FocusGround(GroundStation? station)
    {
        solarView = earthOverview = false;
        celestialOverview = false; followedCelestial = -1;
        fitOverview = false; followSatellite = false; followGroundStation = true;
        groundLatitude = station?.Latitude ?? GroundStations.Average(s => s.Latitude);
        groundLongitude = station?.Longitude ?? GroundStations.Average(s => s.Longitude);
        distance = station is null ? 0.75 : 0.012; tileRequestKey = "";
        UpdateGroundTarget(true); UpdateOrbitMaterials(); UpdateScene(); NotifyCamera(); Notify(nameof(PlaybackText));
    }
    private void UpdateGroundTarget(bool align = false)
    {
        double lat = groundLatitude * Math.PI / 180;
        double lon = (groundLongitude + CurrentSun.EarthRotationDegrees) * Math.PI / 180;
        cameraTarget = new Point3D(Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), -Math.Cos(lat) * Math.Sin(lon));
        if (align) { yaw = Math.PI / 2 + lon; pitch = lat; }
    }
    private void QueueMapTiles()
    {
        if (disposed) return;
        if (!followGroundStation || distance >= 0.1)
        {
            tileCancellation?.Cancel(); tileRequestKey = ""; tileLayer.Children.Clear();
            tilesShown = false; ApplyGlobalLayers();
            TileStatus = "오프라인 지도 · 대전·오사카·상하이 주변 자료 포함"; return;
        }
        var plan = MapTilePlan.Create(groundLatitude, groundLongitude, distance, viewportWidth, viewportHeight);
        string key = string.Join(';', plan);
        if (key == tileRequestKey) return;
        tileRequestKey = key; tileCancellation?.Cancel(); tileCancellation?.Dispose();
        tileCancellation = new CancellationTokenSource();
        TileStatus = $"L{plan[0].Zoom} 타일 {plan.Count}개 로딩 중…";
        TilesLoadingTask = LoadMapTilesAsync(plan, key, tileCancellation.Token);
    }
    private async Task LoadMapTilesAsync(IReadOnlyList<MapTile> plan, string key, CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token).ConfigureAwait(false);
            using var slots = new SemaphoreSlim(4);
            var results = await Task.WhenAll(plan.Select(async tile =>
            {
                await slots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    byte[] bytes = await tileManager.GetAsync(tile, token).ConfigureAwait(false);
                    BitmapSource image = MapTileGeometry.Decode(bytes);
                    return (Tile: tile, Image: (BitmapSource?)image);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException or OperationCanceledException && !token.IsCancellationRequested)
                { return (Tile: tile, Image: (BitmapSource?)null); }
                finally { slots.Release(); }
            })).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (disposed || token.IsCancellationRequested || tileRequestKey != key) return;
                tileLayer.Children.Clear();
                foreach (var item in results)
                    if (item.Image is not null) tileLayer.Children.Add(MapTileGeometry.Create(item.Tile, item.Image, nightMaterial, tileNightMaterial));
                bool active = tileLayer.Children.Count > 0;
                tilesShown = active; ApplyGlobalLayers();
                TileStatus = active ? $"오프라인 L{plan[0].Zoom} · {tileLayer.Children.Count}/{plan.Count}개 표시" + (tileLayer.Children.Count < plan.Count ? " · 자료 밖 영역은 기본 지도" : " · 사전 준비 NASA GIBS 자료") : "포함된 로컬 자료가 없는 영역 · 기본 지도 유지";
            }, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!disposed && !token.IsCancellationRequested)
                await dispatcher.InvokeAsync(() => { if (tileRequestKey == key) TileStatus = "로컬 지도 읽기 실패 · 기본 지도 유지"; });
        }
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; SetRenderingActive(false); nightLights.Dispose(); moonRelief.Dispose();
        tileCancellation?.Cancel(); tileCancellation?.Dispose(); tileManager.Dispose();
    }

    private sealed class ActionCommand(Action<object?> action) : ICommand
    {
        public ActionCommand(Action action) : this(_ => action()) { }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action(parameter);
    }
}
