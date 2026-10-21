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

public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
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
    private readonly Point3D[] antennaAnchors;
    private readonly GroundAntennaGeometry[] groundAntennas;
    private readonly GroundAntennaAim[] stationAims;
    private readonly RotateTransform3D earthPose;
    private bool showCommunications = true;
    private string contactSearchMessage = "";
    public bool ShowCommunications
    {
        get => showCommunications;
        set { if (showCommunications == value) return; showCommunications = value; Notify(); UpdateScene(); }
    }
    public string CommunicationStatus => contactSearchMessage.Length > 0 ? contactSearchMessage
        : string.Join("\n", GroundStations.Select((s, i) =>
        {
            int target = scheduler.Targets[i]; var servo = servos[i];
            string state = target < 0 ? (servo.Elevation > 89.5 ? "천정 대기" : "천정으로 복귀 중")
                : servo.Locked ? $"{(scheduler.Desired[i].ElevationDegrees >= SatelliteOperations.XBandMinimumElevationDegrees ? "S·X" : "S")} 교신 {Orbits[target].Name} · 고각 {scheduler.Desired[i].ElevationDegrees:0}°" : $"포착 중 {Orbits[target].Name}";
            return $"{s.Name}: {state}";
        }));
    // Ground network: every station tracks a different satellite inside its 10° mask (see StationScheduler), slewing
    // on and off target and parking at zenith; links are drawn once its antenna is on target. Coverage mode only adds
    // the visibility cones.
    private bool showCoverage;
    private readonly StationScheduler scheduler;
    private readonly AntennaServo[] servos;
    private double servoTime = double.NaN;
    private readonly StationCoverage[] coverages = new StationCoverage[3];
    // Future ground track of the selected satellite, stored in Earth-fixed coordinates.
    private readonly Model3DGroup groundTrackHost = new();
    private bool showGroundTracks;
    public bool ShowGroundTracks { get => showGroundTracks; set { if (SetFlag(ref showGroundTracks, value)) UpdateGroundTrack(); } }
    private double trajectorySeconds = double.NaN, trajectoryWallTime = double.NegativeInfinity;
    private int trajectorySelection = -1;
    private bool trajectoryAll, trajectoryGround;
    private void UpdateGroundTrack() => RefreshTrajectories(true);
    private void RefreshTrajectories(bool force = false)
    {
        if (orbitModels.Count != Orbits.Length) return;
        // Settings, selection, visible-set and time-jump requests (force) invalidate an in-flight result;
        // the rolling playback refresh does not, so a slow build is still applied before the next one starts.
        bool invalidate = force || selectedIndex != trajectorySelection || trajectoryAll != showAllOrbits || trajectoryGround != followGroundStation;
        if (!invalidate && (Math.Abs(OrbitTime - trajectorySeconds) < 60 || clock.Elapsed.TotalSeconds - trajectoryWallTime < 1)) return;
        trajectorySeconds = OrbitTime; trajectoryWallTime = clock.Elapsed.TotalSeconds; trajectorySelection = selectedIndex; trajectoryAll = showAllOrbits; trajectoryGround = followGroundStation;
        // SGP4 sampling and mesh building run off the UI thread (one job at a time); the UI thread only swaps frozen meshes.
        if (invalidate) trajectoryVersion++;
        if (!trajectoryTask.IsCompleted) { trajectoryQueued = true; return; }
        trajectoryTask = ProcessTrajectoryRequestsAsync();
    }
    private Task trajectoryTask = Task.CompletedTask;
    private readonly Model3DGroup orbitLayer = new();
    private bool orbitsEarthFixed = true;
    public bool OrbitsEarthFixed
    {
        get => orbitsEarthFixed;
        set
        {
            if (!SetFlag(ref orbitsEarthFixed, value)) return;
            Notify(nameof(IsEarthFixedPathSelected)); Notify(nameof(IsInertialPathSelected));
            RefreshTrajectories(true);
        }
    }
    // Ignore the radio button's uncheck notification: only the newly selected option changes the mode.
    public bool IsEarthFixedPathSelected { get => OrbitsEarthFixed; set { if (value) OrbitsEarthFixed = true; } }
    public bool IsInertialPathSelected { get => !OrbitsEarthFixed; set { if (value) OrbitsEarthFixed = false; } }
    private long trajectoryVersion;
    private bool trajectoryQueued;
    private async Task ProcessTrajectoryRequestsAsync()
    {
        do
        {
            trajectoryQueued = false;
            await BuildTrajectoriesAsync();
        } while (trajectoryQueued && !disposed);
    }
    private async Task BuildTrajectoriesAsync()
    {
        long version = trajectoryVersion;
        double start = OrbitTime, endSeconds = (SatelliteCatalog.MaximumTime - SatelliteCatalog.ReferenceEpoch).TotalSeconds;
        double width = followGroundStation ? 0.0004 : 0.0013;
        bool orbits = showOrbits && endSeconds > start, tracks = showGroundTracks && endSeconds > start, earthFixed = orbitsEarthFixed;
        int selected = selectedIndex;
        var paths = Enumerable.Range(0, Orbits.Length).Where(i => i == selected || (showAllOrbits && IsShown(i))).ToArray();
        var otherTracks = tracks && showAllOrbits ? paths.Where(i => i != selected).ToArray() : [];
        double Duration(int i) => Math.Min(Orbits[i].PeriodSeconds, endSeconds - start);
        var result = await Task.Run(() => (
            Orbits: paths.Select(i => (i, !orbits ? Frozen(new MeshGeometry3D())
                : earthFixed ? SceneGeometry.EarthFixedOrbitMesh(Orbits[i], start, Duration(i), width)
                : SceneGeometry.PredictedOrbitMesh(Orbits[i], start, Duration(i), width))).ToArray(),
            Tracks: otherTracks.Select(i => (i, SceneGeometry.GroundTrackMesh(Orbits[i], start, Duration(i)))).ToArray(),
            Selected: tracks ? SceneGeometry.GroundTrackMesh(Orbits[selected], start, Duration(selected)) : null));
        if (disposed || version != trajectoryVersion) return;
        foreach (var (i, mesh) in result.Orbits) orbitModels[i].Geometry = mesh;
        // Earth-fixed paths ride on the Earth pose (like the ground track); inertial rings stay fixed in space.
        orbitLayer.Transform = earthFixed ? earthPose : Transform3D.Identity;
        groundTrackHost.Children.Clear();
        GroundTrackTimeLabels.Clear();
        groundTrackAnchors.Clear();
        foreach (var (i, mesh) in result.Tracks)
        {
            var color = colors[i];
            groundTrackHost.Children.Add(SceneGeometry.Trajectory(mesh, Color.FromRgb((byte)(color.R * 0.65), (byte)(color.G * 0.65), (byte)(color.B * 0.65))));
        }
        if (result.Selected is { } selectedTrack)
        {
            groundTrackHost.Children.Add(SceneGeometry.Trajectory(selectedTrack, Colors.LimeGreen));
            if (selected == selectedIndex) BuildGroundTrackTimes(start, Duration(selected));
        }
        UpdateGroundTrackTimeLabels();
    }
    private static MeshGeometry3D Frozen(MeshGeometry3D mesh) { mesh.Freeze(); return mesh; }
    private bool earthFixedView;
    public bool EarthFixedView { get => earthFixedView; set { if (SetFlag(ref earthFixedView, value)) { NotifyCamera(); } } }
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
        scheduler.Update(Orbits, OrbitTime, CurrentSun.EarthRotationDegrees, selectedIndex, IsTracked);
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
    private SatelliteOrbit? measuredOrbit;
    public string FrameMeasurementStatus { get; private set; } = "실제 축척 · 600배 · 통신 켜고 재생 후 측정";
    public string FrameMeasurementButton => measuringFrames ? "프레임 측정 종료" : "프레임 측정 시작";
    public ICommand MeasureFramesCommand { get; }
    private bool MeasurementConditions => playing && IsTrueScale && Speed == 600 && ShowCommunications && !solarView && followedCelestial < 0;
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
    private readonly DirectionalLight antennaInspectionLight = new(Colors.Black, new Vector3D(0, -1, 0));
    private readonly AmbientLight ambientLight = new(Color.FromRgb(20, 24, 35));
    private readonly AmbientLight solarAmbientLight = new(Color.FromRgb(20, 24, 35));
    private readonly PointLight solarPointLight = new(Colors.White, new Point3D()) { ConstantAttenuation = 1, Range = 1000000 };
    private readonly PointLight sunLight = new(Colors.White, new Point3D(18, 0, 0))
    { ConstantAttenuation = 1, LinearAttenuation = 0, QuadraticAttenuation = 0, Range = 1000000 };
    private bool isTrueScale;
    private readonly Model3DGroup distantScene = new(), nearSatelliteScene = new();
    private readonly Model3DGroup earthLocalScene = new(), solarScene = new(), planetOrbits = new();
    private readonly MatrixTransform3D[] solarTransforms = Enumerable.Range(0, 9).Select(_ => new MatrixTransform3D()).ToArray();
    private readonly ScaleTransform3D planetOrbitScale = new();
    private enum ViewMode { Default, Earth, SolarOverview, Celestial, Satellite, GroundStation, GroundAntenna }
    private ViewMode viewMode;
    private bool solarView => viewMode == ViewMode.SolarOverview || (viewMode == ViewMode.Celestial && followedCelestial != 1);
    private bool earthOverview => viewMode == ViewMode.Earth;
    private bool celestialOverview => viewMode == ViewMode.SolarOverview;
    private bool followSatellite => viewMode == ViewMode.Satellite;
    private bool followGroundStation => viewMode is ViewMode.GroundStation or ViewMode.GroundAntenna;
    private bool antennaView => viewMode == ViewMode.GroundAntenna;
    private void SetViewMode(ViewMode mode, int celestial = -1)
    {
        viewMode = mode;
        followedCelestial = mode == ViewMode.Celestial ? celestial : -1;
    }
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
    public bool IsGroundFocus => viewMode == ViewMode.GroundStation;
    public bool IsAntennaFocus => antennaView;
    public bool IsSatelliteFocus => FocusMode == "Satellite";
    private void NotifyFocusMode()
    {
        Notify(nameof(IsAntennaFocus)); Notify(nameof(IsGroundFocus));
        if (focusMode == FocusMode) return;
        focusMode = FocusMode;
        Notify(nameof(FocusMode)); Notify(nameof(IsEarthFocus)); Notify(nameof(IsSolarFocus)); Notify(nameof(IsInnerFocus));
        Notify(nameof(IsSunFocus)); Notify(nameof(IsAntennaFocus)); Notify(nameof(IsGroundFocus)); Notify(nameof(IsSatelliteFocus));
    }
    public string ViewDescription => antennaView ? "안테나 모델 · 접시 / 후면 트러스 / 방위·고각 추적" : solarView ? "태양계 · 태양 중심 / 행성 8개 공전 궤도" : "지구 주변 · 달 / 실제 TLE 위성";
    private readonly TranslateTransform3D renderTranslation = new();
    private readonly ScaleTransform3D[] bodyScales = [new(), new(), new(), new()];
    private readonly double[] satellitePhysicalScales;
    private CelestialState celestialState = null!;
    private readonly NightLights nightLights = new();
    private readonly MoonRelief moonRelief = new();
    private readonly MapTileLoader tileLoader = new();
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
    private readonly Model3D stars = SceneGeometry.SpaceBackground();
    private readonly Model3D atmosphere = SceneGeometry.Atmosphere();
    public Model3D? SkyScene => showStars ? stars : null;

    // Display / load toggles. Hidden layers are removed (or lose their material) and skip their per-frame work.
    private bool showOrbits = true, showClouds = true, showAtmosphere = true, showNightLights = true, showStars = true,
        showMoon = true, showSatelliteLabels = true, animateAllSatellites, tilesShown;
    public bool ShowOrbits { get => showOrbits; set { if (SetFlag(ref showOrbits, value)) { RefreshTrajectories(true); UpdateOrbitMaterials(); } } }
    private bool showAllOrbits;
    public bool ShowAllOrbits { get => showAllOrbits; set { if (SetFlag(ref showAllOrbits, value)) { RefreshTrajectories(true); UpdateOrbitMaterials(); } } }
    public bool ShowClouds { get => showClouds; set { if (SetFlag(ref showClouds, value)) ApplyGlobalLayers(); } }
    private bool showSolarShading = true;
    public bool ShowSolarShading
    {
        get => showSolarShading;
        set
        {
            if (!SetFlag(ref showSolarShading, value)) return;
            ambientLight.Color = solarAmbientLight.Color = value ? Color.FromRgb(20, 24, 35) : Colors.White;
            sunLight.Color = solarPointLight.Color = value ? Colors.White : Colors.Black;
            nightLights.Invalidate(); ApplyGlobalLayers(); UpdateScene();
        }
    }
    public bool ShowNightLights
    {
        get => showNightLights;
        set { if (!SetFlag(ref showNightLights, value)) return; nightLights.Invalidate(); ApplyGlobalLayers(); UpdateScene(); }
    }
    public bool ShowAtmosphere { get => showAtmosphere; set { if (SetFlag(ref showAtmosphere, value)) ShowInEarthViews(atmosphere, value); } }
    public bool ShowStars { get => showStars; set { if (SetFlag(ref showStars, value)) Notify(nameof(SkyScene)); } }
    public bool ShowMoon { get => showMoon; set { if (!SetFlag(ref showMoon, value)) return; ShowInEarthViews(moonRelief.Model, value); UpdateScene(); } }
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
        nightLights.Model.Material = tilesShown || !showNightLights || !showSolarShading ? null : nightMaterial;
        foreach (Model3DGroup tile in tileLayer.Children)
            ((GeometryModel3D)tile.Children[1]).Material = showSolarShading && showNightLights ? tileNightMaterial : null;
        clouds.Material = tilesShown || !showClouds ? null : cloudMaterial;
    }
    public Model3D? NearSatelliteScene => UseNearSatelliteLayer ? nearSatelliteScene : null;
    private Point3D RenderOrigin => IsTrueScale ? cameraTarget : new Point3D();
    public Point3D RenderCameraPosition => (Point3D)(CameraPosition - RenderOrigin);
    private double BodyRadius(int index) => IsTrueScale ? SceneScale.Radius(index) : solarView ? SceneScale.ObservationRadius(index) : CelestialGeometry.Radii[index];
    private double SatelliteFocusDistance => IsTrueScale ? SceneScale.Units(SceneScale.SatelliteSpanMeters(SelectedOrbit.Mission) / 1000) * 4
        : (SelectedOrbit.Mission == SatelliteMission.B ? 0.9 : 0.55) * SceneScale.ObservationSatelliteScale;
    public int ScaleModeIndex { get => IsTrueScale ? 1 : 0; set { if (value is 0 or 1) IsTrueScale = value == 1; } }
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
            Notify(); Notify(nameof(ScaleModeIndex)); Notify(nameof(SelectedInfo));
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
    private int followedCelestial = -1;
    private DateTimeOffset epoch = SatelliteCatalog.InitialTime;
    private bool realTime;
    private string timeInput = "", timeError = "";
    private readonly Color[] colors;
    private double viewportWidth = 800, viewportHeight = 500;
    private bool fitOverview = true;
    private int selectedIndex;
    private double previousTime, simulationSeconds, speed = 120, yaw = -2.5, pitch = 0.35, distance = 5.1;
    private bool playing = true;
    private Point3D cameraTarget;
    public event PropertyChangedEventHandler? PropertyChanged;
    public Model3DGroup Scene { get; } = new();
    public GroundStation[] GroundStations { get; } =
    [
        // City-centre coordinates for illustration; not facility locations.
        new("대전", 36.3504, 127.3845),
        new("오사카", 34.6937, 135.5023),
        new("상하이", 31.2304, 121.4737)
    ];
    public SatelliteOrbit[] Orbits { get; } = SatelliteCatalog.Create();
    // Checked satellites are drawn, labelled and tracked; the selected one is always shown.
    private readonly bool[] satelliteEnabled;
    private bool IsShown(int index) => satelliteEnabled[index] || index == selectedIndex;
    // Stations auto-track checked LEO satellites (MEO/GEO stay in view for hours); the selected satellite is always eligible.
    private bool IsTracked(int index) => index == selectedIndex || (satelliteEnabled[index] && Orbits[index].Regime == OrbitRegime.Leo);
    public static string RegimeName(OrbitRegime regime) => regime switch { OrbitRegime.Leo => "저궤도", OrbitRegime.Meo => "중궤도", _ => "정지궤도" };
    private int RegimeCount(OrbitRegime regime) => Orbits.Count(o => o.Regime == regime);
    private bool? RegimeChecked(OrbitRegime regime)
    {
        var states = Orbits.Select((o, i) => (o, i)).Where(x => x.o.Regime == regime).Select(x => satelliteEnabled[x.i]).ToArray();
        return states.Length == 0 ? false : states.All(e => e) ? true : states.Any(e => e) ? null : false;
    }
    private void SetRegime(OrbitRegime regime, bool? value)
    {
        bool on = value ?? true;
        if (!on && RegimeChecked(regime) is null) on = true;
        for (int i = 0; i < Orbits.Length; i++) if (Orbits[i].Regime == regime) SetSatelliteEnabled(i, on, false);
        OnSatelliteSetChanged();
    }
    public bool? LeoChecked { get => RegimeChecked(OrbitRegime.Leo); set => SetRegime(OrbitRegime.Leo, value); }
    public bool? MeoChecked { get => RegimeChecked(OrbitRegime.Meo); set => SetRegime(OrbitRegime.Meo, value); }
    public bool? GeoChecked { get => RegimeChecked(OrbitRegime.Geo); set => SetRegime(OrbitRegime.Geo, value); }
    public string LeoLabel => $"저궤도 {RegimeCount(OrbitRegime.Leo)}";
    public string MeoLabel => $"중궤도 {RegimeCount(OrbitRegime.Meo)}";
    public string GeoLabel => $"정지궤도 {RegimeCount(OrbitRegime.Geo)}";
    public SatelliteItem[] SatelliteItems { get; }
    public sealed class SatelliteItem(MainViewModel owner, int index) : INotifyPropertyChanged
    {
        public SatelliteOrbit Orbit => owner.Orbits[index];
        public string Name => Orbit.Name;
        public bool IsChecked { get => owner.satelliteEnabled[index]; set => owner.SetSatelliteEnabled(index, value); }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void NotifyChecked() => PropertyChanged?.Invoke(this, new(nameof(IsChecked)));
    }
    public SatelliteItem SelectedSatelliteItem { get => SatelliteItems[selectedIndex]; set { if (value is not null) SelectedOrbit = value.Orbit; } }
    public bool? AllSatellitesChecked
    {
        get => satelliteEnabled.All(e => e) ? true : satelliteEnabled.Any(e => e) ? null : false;
        set
        {
            // A click on the mixed state selects all (WPF would otherwise go from mixed to unchecked).
            bool all = value ?? true;
            if (!all && AllSatellitesChecked is null) all = true;
            for (int i = 0; i < satelliteEnabled.Length; i++) SetSatelliteEnabled(i, all, false);
            OnSatelliteSetChanged();
        }
    }
    private void SetSatelliteEnabled(int index, bool value, bool apply = true)
    {
        if (satelliteEnabled[index] == value) return;
        satelliteEnabled[index] = value; SatelliteItems[index].NotifyChecked();
        if (apply) OnSatelliteSetChanged();
    }
    private void OnSatelliteSetChanged()
    {
        // Move the selection to a checked satellite when the selected one is unchecked.
        if (!satelliteEnabled[selectedIndex] && Array.IndexOf(satelliteEnabled, true) is int first and >= 0) SelectedOrbit = Orbits[first];
        Notify(nameof(AllSatellitesChecked)); Notify(nameof(CatalogSummary));
        Notify(nameof(LeoChecked)); Notify(nameof(MeoChecked)); Notify(nameof(GeoChecked));
        RefreshTrajectories(true); UpdateOrbitMaterials(); UpdateScene();
    }
    public ICommand TogglePlaybackCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand FocusGroundStationsCommand { get; }
    public ICommand FocusAntennaCommand { get; }
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
    public ICommand FindContactCommand { get; }
    public ICommand PredictPassesCommand { get; }
    public ICommand LoadTleCommand { get; }
    public string PassPredictionStatus { get; private set; } = "";
    public string CatalogSummary => $"공개 TLE {Orbits.Length}대 · 표시 {satelliteEnabled.Count(e => e)}대";
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
    // Internal elapsed seconds share a UTC origin; each TLE retains its own propagation epoch.
    private double OrbitTime => (epoch - SatelliteCatalog.ReferenceEpoch).TotalSeconds + simulationSeconds;
    public SolarState CurrentSun { get; private set; } = SolarLighting.At(SatelliteCatalog.InitialTime);
    public string SimulationTimeText => SimulationTime.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss 'KST'", CultureInfo.InvariantCulture);
    public string TimeInput { get => timeInput; set { timeInput = value; Notify(); } }
    public string TimeError { get => timeError; private set { timeError = value; Notify(); } }
    public string TimeMode => !playing ? "일시정지" : realTime ? "현재 시각 · 실시간" : "시뮬레이션";
    public bool SelectedEclipsed => eclipsed[selectedIndex];
    public SatelliteOrbit SelectedOrbit
    {
        get => Orbits[selectedIndex];
        set
        {
            int index = Array.IndexOf(Orbits, value);
            if (index < 0 || selectedIndex == index) return;
            selectedIndex = index; PassPredictionStatus = "선택 위성 변경 · 다음 통과를 다시 계산하세요."; Notify(nameof(PassPredictionStatus)); contactSearchMessage = "";
            Notify(); Notify(nameof(SelectedInfo)); Notify(nameof(SelectedSatelliteItem));
            if (IsTrueScale && followSatellite) distance = SatelliteFocusDistance;
            UpdateOrbitMaterials(); UpdateGroundTrack(); UpdateScene();
        }
    }
    public string SelectedInfo => $"{RegimeName(SelectedOrbit.Regime)} · NORAD {SelectedOrbit.NoradId} · 고도 {SelectedOrbit.AltitudeKm:N0} km · 경사각 {SelectedOrbit.InclinationDegrees:0.#}°\n"
        +$"TLE {SelectedOrbit.TleEpoch:MM-dd HH:mm} UTC ({(SimulationTime - SelectedOrbit.TleEpoch).TotalDays:+0.0;-0.0;0.0}일) · {(SelectedEclipsed ? "지구 그림자" : "일조")}";
    public ObservableCollection<SatelliteLabel> SatelliteLabels { get; } = [];
    public sealed class SatelliteLabel(SatelliteOrbit orbit, Brush color) : INotifyPropertyChanged
    {
        public SatelliteOrbit Orbit { get; } = orbit;
        public string ShortName { get; } = LabelName(orbit.Name);
        // Compact but distinct: "NOAA 20 (JPSS-1)" -> "NOAA 20", "GPS BIIR-5 (PRN 22)" -> "GPS PRN 22",
        // "GSAT0101 (GALILEO-PFM)" -> "GALILEO-PFM", "DMSP 5D-3 F16 (USA 172)" -> "DMSP F16".
        public static string LabelName(string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(name, @"^(.*?)\s*\((.*)\)\s*$");
            string main = match.Success ? match.Groups[1].Value : name, inner = match.Success ? match.Groups[2].Value : "";
            main = System.Text.RegularExpressions.Regex.Replace(main, @"\s+", " ").Trim();
            if (main.StartsWith("GPS") && inner.StartsWith("PRN")) return "GPS " + inner;
            if (main.StartsWith("GSAT") && inner.Length > 0) return inner;
            if (main.StartsWith("DMSP 5D-3 ")) return "DMSP " + main["DMSP 5D-3 ".Length..];
            return main;
        }
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
            var offset = new Vector3D(distance * Math.Cos(pitch) * Math.Sin(yaw), distance * Math.Sin(pitch), distance * Math.Cos(pitch) * Math.Cos(yaw));
            var position = (Vector3D)(followSatellite ? SatelliteCamera.Position(cameraTarget, offset) : cameraTarget + offset);
            double minimum = followGroundStation ? 1.0005 : 1.06;
            if (position.Length < minimum) { position.Normalize(); position *= minimum; }
            return (Point3D)position;
        }
    }
    public Vector3D CameraDirection => cameraTarget - CameraPosition;
    public Vector3D CameraUpDirection => antennaView ? (Vector3D)cameraTarget : new Vector3D(0, 1, 0);

    public MainViewModel()
    {
        InitializePredictionCommands();
        operations = new SatelliteOperations(GroundStations);
        antennaAnchors = new Point3D[GroundStations.Length];
        groundAntennas = new GroundAntennaGeometry[GroundStations.Length]; stationAims = new GroundAntennaAim[GroundStations.Length];
        scheduler = new StationScheduler(GroundStations); servos = GroundStations.Select(_ => new AntennaServo()).ToArray();
        Color[] palette = [Color.FromRgb(74, 222, 226), Color.FromRgb(255, 188, 95), Color.FromRgb(184, 145, 255),
            Color.FromRgb(103, 226, 151), Color.FromRgb(255, 132, 173), Color.FromRgb(235, 240, 255)];
        colors = Orbits.Select(orbit => palette[orbit.NoradId % (uint)palette.Length]).ToArray();
        // One label per shared orbit plane (first member), plus the selected satellite.
        labelEligible = Enumerable.Repeat(true, Orbits.Length).ToArray();
        satelliteEnabled = Enumerable.Repeat(true, Orbits.Length).ToArray();
        SatelliteItems = Orbits.Select((_, i) => new SatelliteItem(this, i)).ToArray();
        eclipsed = new bool[Orbits.Length];
        satellitePhysicalScales = new double[Orbits.Length];
        wingTransforms = new MatrixTransform3D?[Orbits.Length]; dishTransforms = new MatrixTransform3D?[Orbits.Length];
        wingAngles = new double[Orbits.Length];
        labelCache = Orbits.Select((orbit, i) =>
        {
            var brush = new SolidColorBrush(colors[i]); brush.Freeze();
            return new SatelliteLabel(orbit, brush);
        }).ToArray();
        TogglePlaybackCommand = new ActionCommand(() => { playing = !playing; realTime = false; Notify(nameof(PlaybackText)); Notify(nameof(TimeMode)); });
        MeasureFramesCommand = new ActionCommand(() =>
        {
            if (measuringFrames) { FinishMeasurement("측정 종료"); return; }
            if (!MeasurementConditions) { FrameMeasurementStatus = "실제 축척 · 600배 · 통신 켜고 재생하세요."; Notify(nameof(FrameMeasurementStatus)); return; }
            frameTiming.Reset(); measuredOrbit = SelectedOrbit; measuringFrames = true;
            FrameMeasurementStatus = $"{SelectedOrbit.Name} · 실제 축척/TLE 위성/600배 · 측정 중";
            Notify(nameof(FrameMeasurementStatus)); Notify(nameof(FrameMeasurementButton));
        });
        PredictPassesCommand = new ActionCommand(async () =>
        {
            var orbit = SelectedOrbit; var time = SimulationTime;
            PassPredictionStatus = "다음 24시간 통과 예측 중…"; Notify(nameof(PassPredictionStatus));
            try
            {
                var text = await Task.Run(() => string.Join("\n", GroundStations.Select(station =>
                {
                    var kst = TimeSpan.FromHours(9);
                    var pass = PassPredictor.Next(orbit, station, time, SatelliteCatalog.MaximumTime, SatelliteOperations.SBandMinimumElevationDegrees);
                    if (pass is null) return $"{station.Name}: 다음 24시간/자료 범위 안 통과 없음";
                    if (pass.AlreadyVisible && pass.ClippedEnd) return $"{station.Name}: 검색 범위 내내 가시 · 최고 {pass.MaxElevation:0.0}°";
                    // X-band window inside this S-band pass (10° mask), if the pass climbs that high.
                    var x = pass.MaxElevation >= SatelliteOperations.XBandMinimumElevationDegrees
                        ? PassPredictor.Next(orbit, station, pass.Acquisition, pass.Loss, SatelliteOperations.XBandMinimumElevationDegrees) : null;
                    return $"{station.Name}: S {pass.Acquisition.ToOffset(kst):MM-dd HH:mm:ss} ~ {pass.Loss.ToOffset(kst):HH:mm:ss} KST · 최고 {pass.MaxElevation:0.0}°"
                        + (pass.AlreadyVisible ? " · 현재 가시 중" : "") + (pass.ClippedEnd ? " · 종료는 검색범위 밖" : "")
                        + (x is null ? "\n  X 없음 (고각 10° 미만)" : $"\n  X {x.Acquisition.ToOffset(kst):HH:mm:ss} ~ {x.Loss.ToOffset(kst):HH:mm:ss}");
                })));
                if (!disposed && orbit == SelectedOrbit)
                    PassPredictionStatus = $"{orbit.Name} · 기준 {time.ToOffset(TimeSpan.FromHours(9)):MM-dd HH:mm:ss} KST\n" + text;
            }
            catch (Exception ex) { if (!disposed) PassPredictionStatus = "통과 계산 실패: " + ex.Message; }
            if (!disposed) Notify(nameof(PassPredictionStatus));
        });
        LoadTleCommand = new ActionCommand(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "TLE 자료 (*.tle;*.txt)|*.tle;*.txt", Title = "실제 TLE 자료 선택 (새 창으로 적용)" };
            if (dialog.ShowDialog() != true) return;
            try
            {
                _ = SatelliteCatalog.Parse(File.ReadAllText(dialog.FileName));
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                start.ArgumentList.Add("--tle"); start.ArgumentList.Add(dialog.FileName);
                Process.Start(start);
            }
            catch (Exception ex) { TimeError = "TLE 파일 적용 실패: " + ex.Message; }
        });
        FindContactCommand = new ActionCommand(() =>
        {
            double remaining = (SatelliteCatalog.MaximumTime - SimulationTime).TotalSeconds;
            var delay = operations.NextContactDelay(SelectedOrbit, OrbitTime, Math.Clamp(remaining, 0, 86400));
            if (delay is null) { contactSearchMessage = "다음 24시간/지원 시각 안에 교신 가능한 지점이 없습니다."; Notify(nameof(CommunicationStatus)); return; }
            simulationSeconds += delay.Value; playing = realTime = false; previousTime = clock.Elapsed.TotalSeconds;
            RefreshTrajectories(true);
            contactSearchMessage = ""; TimeError = "";
            TimeInput = SimulationTime.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); Notify(nameof(TimeInput));
            FocusOperations(); Notify(nameof(PlaybackText));
        });
        ApplyTimeCommand = new ActionCommand(() =>
        {
            if (!DateTime.TryParseExact(TimeInput, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                || local.Year is < 2000 or > 2100)
            { TimeError = "yyyy-MM-dd HH:mm:ss 형식으로 입력하세요. 사용 범위는 TLE 기준 ±7일입니다."; return; }
            var time = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromHours(9));
            if (time.UtcDateTime.Year < 2000) { TimeError = "UTC 기준 2000년 이후 시각을 입력하세요."; return; }
            SetSimulationTime(time);
        });
        CurrentTimeCommand = new ActionCommand(() => SetSimulationTime(DateTimeOffset.UtcNow, true));
        TimeInput = SimulationTime.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        SelectOrbitCommand = new ActionCommand(value => { if (value is SatelliteOrbit orbit) SelectedOrbit = orbit; });
        ResetCommand = new ActionCommand(() =>
        {
            SetViewMode(ViewMode.Default);
            realTime = false;
            simulationSeconds = 0; yaw = -2.5; pitch = 0.35; distance = 5.1; cameraTarget = new Point3D();
            RefreshTrajectories(true);
            fitOverview = true; FitOverview(); UpdateOrbitMaterials(); UpdateScene(); NotifyCamera();
        });
        FocusGroundStationsCommand = new ActionCommand(() => FocusGround(null));
        FocusAntennaCommand = new ActionCommand(FocusAntenna);
        FocusStationCommand = new ActionCommand(value => { if (value is GroundStation station) FocusGround(station); });
        FocusEarthSystemCommand = new ActionCommand(() =>
        {
            SetViewMode(ViewMode.Earth); cameraTarget = new Point3D(); yaw = -2.5; pitch = 0.35;
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
            SetViewMode(ViewMode.Celestial, body.Index);
            if (solarView) RebuildPlanetOrbits();
            fitOverview = false;
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
            SetViewMode(ViewMode.Satellite);
            fitOverview = false; distance = SatelliteFocusDistance;
            var pose = SceneGeometry.SatellitePose(SelectedOrbit, OrbitTime);
            var offset = pose.Transform(SelectedOrbit.Mission == SatelliteMission.A ? new Vector3D(0.9, -1, 0.6) : new Vector3D(0.8, -0.6, 0.8)); offset.Normalize();
            yaw = Math.Atan2(offset.X, offset.Z); pitch = Math.Asin(offset.Y);
            UpdateOrbitMaterials(); UpdateScene(); Notify(nameof(PlaybackText));
        });
        Scene.Children.Add(ambientLight);
        Scene.Children.Add(sunLight);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.UriSource = new Uri("pack://application:,,,/WpfSatelliteTracking;component/Assets/earth.jpg"); bitmap.DecodePixelWidth = 5400; bitmap.EndInit(); bitmap.Freeze();
        var brush = new ImageBrush(bitmap); brush.Freeze();
        var material = new DiffuseMaterial(brush); material.Freeze();
        var earth = SceneGeometry.EarthSphere(1, material, 128, 64);
        earthPose = new RotateTransform3D(earthRotation);
        var earthSystem = new Model3DGroup { Transform = earthPose };
        earthSystem.Children.Add(earth);
        foreach (var station in GroundStations)
        {
            var assembly = new GroundAntennaGeometry(station); var antenna = assembly.Model;
            groundAntennas[stationScales.Count] = assembly;
            var basePoint = antenna.Transform.Transform(new Point3D()); var normal = (Vector3D)SceneGeometry.Point(EarthCoordinates.Surface(station.Latitude, station.Longitude, station.AltitudeKm));
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
        earthSystem.Children.Add(predictionHost);
        earthSystem.Children.Add(groundTrackHost); // last, so the earlier earth-system child indices stay stable
        Scene.Children.Add(earthSystem);
        Scene.Children.Add(stars);
        var satelliteLayer = new Model3DGroup();
        Scene.Children.Add(orbitLayer); Scene.Children.Add(satelliteLayer);
        var templates = new Dictionary<(Color, SatelliteMission), (Model3DGroup Lit, Model3DGroup Shadow)>();
        var lowTemplates = new Dictionary<(Color, SatelliteMission), (Model3DGroup Lit, Model3DGroup Shadow)>();
        modelSpans = new double[Orbits.Length]; shownAssets = new Model3D?[Orbits.Length];
        var pieceCache = new Dictionary<Model3D, SatelliteBatch.Piece[]>();
        SatelliteBatch.Piece[] Pieces(Model3D template) => pieceCache.TryGetValue(template, out var p) ? p : pieceCache[template] = SatelliteBatch.Flatten(template);
        for (int i = 0; i < Orbits.Length; i++)
        {
            if (labelEligible[i])
            {
                orbitModels.Add(new GeometryModel3D(new MeshGeometry3D(), SceneGeometry.MarkerMaterial(colors[i])));
                orbitRepresentatives.Add(i); orbitLayer.Children.Add(orbitModels[i]);
            }
            var satellite = new Model3DGroup();
            var templateKey = (colors[i], Orbits[i].Mission);
            if (!templates.TryGetValue(templateKey, out var template))
            {
                template = (SceneGeometry.Satellite(colors[i], mission: Orbits[i].Mission), SceneGeometry.Satellite(colors[i], true, Orbits[i].Mission));
                templates.Add(templateKey, template);
                lowTemplates.Add(templateKey, (SceneGeometry.LowDetailSatellite(colors[i], false, Orbits[i].Mission), SceneGeometry.LowDetailSatellite(colors[i], true, Orbits[i].Mission)));
            }
            // Static geometry for the shared batch: full model in its rest pose, and the distant proxy.
            staticPieces[i] = (Pieces(template.Lit), Pieces(template.Shadow), Pieces(lowTemplates[templateKey].Lit), Pieces(lowTemplates[templateKey].Shadow));
            modelSpans[i] = template.Lit.Bounds.SizeX;
            if (Orbits[i].Mission == SatelliteMission.A)
            {
                var wings = new MatrixTransform3D(); var dish = new MatrixTransform3D(TypeASatelliteGeometry.AntennaPose(0));
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
        Scene.Children.Add(atmosphere);
        for (int i = 0; i < celestialTransforms.Length; i++)
        {
            var body = i == 1 ? moonRelief.Model : CelestialGeometry.Create(i);
            var transform = new Transform3DGroup(); transform.Children.Add(bodyScales[i]);
            if (i == 1) { transform.Children.Add(new RotateTransform3D(moonTilt)); transform.Children.Add(new RotateTransform3D(moonFacing)); }
            transform.Children.Add(celestialTransforms[i]);
            body.Transform = transform; Scene.Children.Add(body);
        }
        // Explicit membership keeps layer composition independent of Scene child order.
        foreach (var model in new Model3D[] { ambientLight, sunLight, earthSystem, orbitLayer, satelliteLayer, atmosphere, moonRelief.Model })
        {
            earthLocalScene.Children.Add(model);
            if (model != satelliteLayer) distantScene.Children.Add(model);
        }
        earthLocalScene.Children.Add(antennaInspectionLight); distantScene.Children.Add(antennaInspectionLight);
        earthLocalScene.Children.Add(coverageLinks.Scene); distantScene.Children.Add(coverageLinks.Scene);
        // Coverage is added only while the mode is on, after everything else so its translucent surfaces blend last.
        coverageGroup.Transform = new RotateTransform3D(coverageRotation);
        // Coverage is drawn for the LEO shell; MEO/GEO cones would dwarf the globe.
        var leo = Orbits.Where(orbit => orbit.Regime == OrbitRegime.Leo).DefaultIfEmpty(Orbits.MinBy(orbit => orbit.Radius)!).ToArray();
        double highestShell = leo.Max(orbit => orbit.Radius);
        for (int s = 0; s < GroundStations.Length; s++)
        {
            coverages[s] = new StationCoverage(GroundStations[s], highestShell, leo.Min(orbit => orbit.AltitudeKm), s);
            coverageGroup.Children.Add(coverages[s].Model);
        }
        solarScene.Children.Add(solarAmbientLight);
        solarScene.Children.Add(solarPointLight);
        for (int i = 0; i < solarTransforms.Length; i++)
        {
            var model = i == 3 ? new GeometryModel3D(earth.Geometry, earth.Material) : CelestialGeometry.Create(i == 0 ? 0 : i + 1);
            model.Transform = solarTransforms[i]; solarScene.Children.Add(model);
        }
        planetOrbits.Transform = planetOrbitScale; solarScene.Children.Add(planetOrbits);
        var saturnRings = CelestialGeometry.SaturnRings().Clone(); saturnRings.Transform = solarTransforms[6]; solarScene.Children.Add(saturnRings);
        RebuildPlanetOrbits();
        nearSatelliteScene.Children.Add(ambientLight); nearSatelliteScene.Children.Add(sunLight); nearSatelliteScene.Children.Add(satelliteLayer);
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
    private void AdvanceSimulation(double elapsedWallSeconds, DateTimeOffset wallNow)
    {
        if (!playing) return;
        simulationSeconds = realTime ? (wallNow - epoch).TotalSeconds : simulationSeconds + elapsedWallSeconds * speed;
        if (playToPrediction && pinnedPrediction is { } forecast && SimulationTime >= forecast.Arrival)
        {
            epoch = forecast.Arrival; simulationSeconds = 0;
            playing = realTime = playToPrediction = false; Notify(nameof(PlaybackText));
        }
        double limit = (SatelliteCatalog.MaximumTime - epoch).TotalSeconds;
        if (simulationSeconds >= limit)
        {
            simulationSeconds = limit; playing = false; realTime = false;
            TimeError = "지원 시각의 끝에 도달해 정지했습니다."; Notify(nameof(PlaybackText));
        }
        UpdateScene(true);
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
            AdvanceSimulation(now - previousTime, DateTimeOffset.UtcNow);
        }
        else
        {
            // Simulation time is paused, but servo motion and communication pulses
            // use wall-clock time. Keep effects moving after the last servo locks.
            if (servos.Any(servo => !servo.Locked && !(servo.Target < 0 && servo.Elevation >= 90))) UpdateStations(now);
            RefreshEffects(now);
            if (now - informationTime >= 0.2)
            {
                informationTime = now; Notify(nameof(CommunicationStatus)); UpdateSkyPlot(now);
            }
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
            if (measuringFrames) { FrameMeasurementStatus = $"{SelectedOrbit.Name} · 실제 축척/TLE 위성/600배 · 측정 중\n{frameTiming.Summary}"; Notify(nameof(FrameMeasurementStatus)); }
            sampleFrames = 0; sampleWorkMilliseconds = 0; sampleTime = now;
        }
    }
    private void UpdateScene(bool animationFrame = false)
    {
        double now = clock.Elapsed.TotalSeconds;
        RefreshTrajectories();
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
            // Observation scale compresses the lunar distance (60.3 Earth radii) to 10, still beyond GEO (6.6) and GPS (4.2).
            celestialPositions[1] = (Point3D)((Vector3D)SceneGeometry.Point(bodies.MoonEarthRadii)
                * (IsTrueScale ? SceneScale.Units(SceneScale.LunarReferenceRadiusKm) : 10 / 60.2666));
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
        if (!showNightLights || !showSolarShading) { } // hidden: no mask computation or 8 MiB uploads
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
        }
        if (followGroundStation || (EarthFixedView && !solarView && !followSatellite && followedCelestial < 0))
            yaw += ((CurrentSun.EarthRotationDegrees - previousRotation + 540) % 360 - 180) * Math.PI / 180;
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
                var dishPose = TypeASatelliteGeometry.AntennaPose(OrbitTime + Orbits[i].InitialPhaseDegrees / 2);
                if (dish.Matrix != dishPose) dish.Matrix = dishPose;
            }
            double scale = IsTrueScale ? satellitePhysicalScales[i] : SceneScale.ObservationSatelliteScale;
            pose.ScalePrepend(new Vector3D(scale, scale, scale));
            satelliteTransforms[i].Matrix = pose;
            eclipsed[i] = shadow;
        }
        UpdatePinnedPrediction();
        UpdateVisibility();
        operations.Update(SelectedOrbit, OrbitTime, CurrentSun.EarthRotationDegrees);
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
            Notify(nameof(SimulationTimeText)); Notify(nameof(SelectedInfo)); Notify(nameof(SelectedEclipsed)); Notify(nameof(TimeMode));
            Notify(nameof(CommunicationStatus)); UpdateSkyPlot(now);
        }
    }
    public void SetSimulationTime(DateTimeOffset time, bool useRealTime = false)
    {
        if (time < SatelliteCatalog.MinimumTime || time > SatelliteCatalog.MaximumTime)
        { TimeError = $"TLE 기준 ±7일 범위만 지원: {SatelliteCatalog.MinimumTime:u} ~ {SatelliteCatalog.MaximumTime:u}. satellites.tle를 갱신하세요."; return; }
        _ = SolarLighting.At(time);
        playToPrediction = false;
        contactSearchMessage = "";
        PassPredictionStatus = "시각 변경 · 다음 통과를 다시 계산하세요."; Notify(nameof(PassPredictionStatus));
        epoch = time.ToUniversalTime(); simulationSeconds = 0; realTime = useRealTime; playing = useRealTime;
        if (useRealTime) Speed = 1;
        TimeError = ""; TimeInput = SimulationTime.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        previousTime = clock.Elapsed.TotalSeconds;
        if (solarView) RebuildPlanetOrbits();
        RefreshTrajectories(true);
        UpdateScene(); Notify(nameof(PlaybackText));
    }
    public void Rotate(double dx, double dy)
    {
        if (followGroundStation && !antennaView && distance < 0.1)
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
        double minimum = antennaView ? 0.035 : celestialOverview ? 5 : followedCelestial >= 0 ? BodyRadius(followedCelestial) * (followedCelestial == 7 ? 5.52 : 2.4) : cameraTarget == new Point3D() ? 3.4
            : followSatellite ? (IsTrueScale ? SatelliteFocusDistance * 0.6 : 0.35 * SceneScale.ObservationSatelliteScale) : 0.003;
        double previousDistance = distance;
        distance = Math.Clamp(distance * Math.Exp(-delta * 0.001), minimum, earthOverview ? IsTrueScale ? 1000 : 60 : IsTrueScale && (celestialOverview || followedCelestial >= 0) ? 10000000
            : celestialOverview || followedCelestial >= 0 ? 10000 : 15); NotifyCamera();
        if (followGroundStation && !antennaView && previousDistance >= 0.1 && distance < 0.1) { UpdateGroundTarget(true); NotifyCamera(); }
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
        SetViewMode(ViewMode.SolarOverview); RebuildPlanetOrbits();
        fitOverview = true;
        yaw = 0.5; pitch = 1.0;
        UpdateScene(); FitOverview(); NotifyCamera();
    }
    private void FocusOperations()
    {
        SetViewMode(ViewMode.Satellite);
        fitOverview = false;
        var normal = (Vector3D)SceneGeometry.Point(SelectedOrbit.Position(OrbitTime)); normal.Normalize();
        cameraTarget = SceneGeometry.Point(SelectedOrbit.Position(OrbitTime));
        // The observation-scale framing would make a physical-size satellite subpixel.
        distance = IsTrueScale ? SatelliteFocusDistance : 0.9;
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
        RefreshTrajectories();
        for (int i = 0; i < orbitModels.Count; i++)
        {
            int representative = orbitRepresentatives[i];
            bool selected = representative == selectedIndex;
            if (!IsShown(representative)) { orbitModels[i].Material = orbitModels[i].BackMaterial = null; continue; }
            double brightness = selected ? 0.9 : 0.45;
            var c = colors[representative];
            // Never infer shared orbital planes from names or display models.
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
    private readonly bool[] labelEligible;
    private readonly bool[] individual = new bool[SatelliteCatalog.Count], visibleNow = new bool[SatelliteCatalog.Count];
    private readonly double[] pixelSizes = new double[SatelliteCatalog.Count];
    private readonly (SatelliteBatch.Piece[] Lit, SatelliteBatch.Piece[] Shadow, SatelliteBatch.Piece[] LowLit, SatelliteBatch.Piece[] LowShadow)[] staticPieces
        = new (SatelliteBatch.Piece[], SatelliteBatch.Piece[], SatelliteBatch.Piece[], SatelliteBatch.Piece[])[SatelliteCatalog.Count];
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
        var right = Vector3D.CrossProduct(forward, CameraUpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double focal = viewportWidth / (2 * Math.Tan(21 * Math.PI / 180));
        for (int i = 0; i < Orbits.Length; i++)
        {
            if (!IsShown(i)) { visibleNow[i] = false; pixelSizes[i] = 0; continue; }
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
            bool shadow = showSolarShading && eclipsed[i];
            Model3D asset = !alone ? HiddenAsset : shadow ? eclipsedAssets[i] : litAssets[i];
            if (!ReferenceEquals(shownAssets[i], asset)) { shownAssets[i] = asset; satelliteModels[i].Children[0] = asset; }
            if (visibleNow[i] && !alone)
            {
                var pieces = staticPieces[i];
                staticBatch.Add(i, pixelSizes[i] < 24 ? (shadow ? pieces.LowShadow : pieces.LowLit) : (shadow ? pieces.Shadow : pieces.Lit),
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
    private readonly List<(int Index, double X, double Y, double Depth)> labelCandidates = new(SatelliteCatalog.Count);
    private readonly List<Rect> labelBounds = new(SatelliteCatalog.Count);
    private void UpdateLabels()
    {
        UpdateGroundTrackTimeLabels();
        var camera = CameraPosition;
        var forward = CameraDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, CameraUpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double focal = viewportWidth / (2 * Math.Tan(21 * Math.PI / 180));
        labelCandidates.Clear();
        for (int i = 0; i < Orbits.Length; i++)
        {
            var label = labelCache[i];
            bool representative = labelEligible[i] || i == selectedIndex;
            if (!showSatelliteLabels || !representative || !IsShown(i) || celestialOverview || followedCelestial >= 0 || (followSatellite && i != selectedIndex)) { SatelliteLabels.Remove(label); continue; }
            var position = SceneGeometry.Point(Orbits[i].Position(OrbitTime));
            var ray = position - camera;
            double depth = Vector3D.DotProduct(ray, forward);
            if (depth <= 0) { SatelliteLabels.Remove(label); continue; }
            double fraction = Math.Clamp(-Vector3D.DotProduct((Vector3D)camera, ray) / ray.LengthSquared, 0, 1);
            if (((Vector3D)(camera + ray * fraction)).Length < 1.045) { SatelliteLabels.Remove(label); continue; }
            double x = viewportWidth / 2 + Vector3D.DotProduct(ray, right) * focal / depth;
            double y = viewportHeight / 2 - Vector3D.DotProduct(ray, up) * focal / depth;
            if (x < 0 || x > viewportWidth || y < 0 || y > viewportHeight) { SatelliteLabels.Remove(label); continue; }
            labelCandidates.Add((i, x, y, depth));
        }
        // Declutter: the selected satellite first, then nearer satellites; a label that would overlap one already placed is hidden.
        labelCandidates.Sort((a, b) => a.Index == selectedIndex ? -1 : b.Index == selectedIndex ? 1 : a.Depth.CompareTo(b.Depth));
        labelBounds.Clear();
        foreach (var (i, x, y, _) in labelCandidates)
        {
            var label = labelCache[i];
            string text = (i == selectedIndex ? "● " : "") + label.ShortName + (followSatellite ? " · 추적" : "");
            double width = 16 + 7.2 * text.Length, height = 26;
            double left = followSatellite ? 12 : Math.Clamp(x + 20, 0, Math.Max(0, viewportWidth - width));
            double top = followSatellite ? 12 : Math.Clamp(y - 24, 0, Math.Max(0, viewportHeight - height));
            var bounds = new Rect(left, top, width, height);
            if (labelBounds.Any(r => r.IntersectsWith(bounds))) { SatelliteLabels.Remove(label); continue; }
            labelBounds.Add(bounds);
            label.Update(text, left, top);
            if (!SatelliteLabels.Contains(label)) SatelliteLabels.Add(label);
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
        // Station links come from the network schedule.
        bool links = ShowCommunications && !solarView && followedCelestial < 0;
        for (int s = 0; s < antennaAnchors.Length; s++)
        {
            int target = scheduler.Targets[s];
            coverageLinks.Update(s, antennaAnchors[s], links && target >= 0 && servos[s].Locked ? SceneGeometry.Point(Orbits[target].Position(OrbitTime)) : null,
                now, CameraDirection.Length, scheduler.Desired[s].ElevationDegrees >= SatelliteOperations.XBandMinimumElevationDegrees);
        }
    }
    private void NotifyCamera(bool updateTiles = true)
    {
        antennaInspectionLight.Color = antennaView ? Color.FromRgb(225, 228, 235) : Colors.Black;
        var lightDirection = CameraDirection;
        var lightRight = Vector3D.CrossProduct(lightDirection, CameraUpDirection);
        if (lightRight.LengthSquared > 0) lightRight.Normalize();
        antennaInspectionLight.Direction = lightDirection + lightRight * lightDirection.Length * 0.65;
        Notify(nameof(CameraUpDirection));
        var origin = RenderOrigin;
        renderTranslation.OffsetX = -origin.X; renderTranslation.OffsetY = -origin.Y; renderTranslation.OffsetZ = -origin.Z;
        Notify(nameof(CameraPosition)); Notify(nameof(RenderCameraPosition)); Notify(nameof(CameraDirection)); Notify(nameof(CameraNearPlane));
        Notify(nameof(CameraFarPlane)); Notify(nameof(NearSatellitePlane)); Notify(nameof(MainScene)); Notify(nameof(NearSatelliteScene)); Notify(nameof(SkyScene));
        Notify(nameof(IsSolarSystemView)); Notify(nameof(ViewDescription)); NotifyFocusMode();
        UpdateLabels();
        double scale = followGroundStation && !antennaView ? Math.Min(1, distance / 0.75) : 1;
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
    private void FocusAntenna()
    {
        var station = groundLatitude == 0 && groundLongitude == 0 ? GroundStations[0]
            : GroundStations.MinBy(s => Math.Pow(s.Latitude - groundLatitude, 2) + Math.Pow(s.Longitude - groundLongitude, 2))!;
        groundLatitude = station.Latitude; groundLongitude = station.Longitude;
        SetViewMode(ViewMode.GroundAntenna); fitOverview = false;
        distance = 0.105;
        UpdateGroundTarget(true);
        yaw += 0.65; pitch = Math.Clamp(pitch - 0.65, -1.4, 1.4);
        UpdateOrbitMaterials(); UpdateScene(); NotifyCamera();
    }
    private void FocusGround(GroundStation? station)
    {
        SetViewMode(ViewMode.GroundStation);
        // The regional overview is a geographical pass forecast, just like the globe view.
        // Show the ground track alongside the optional inertial paths in this view.
        if (station is null)
        {
            ShowGroundTracks = true;
            ShowGroundTrackTimes = true;
        }
        RefreshTrajectories(true);
        fitOverview = false;
        groundLatitude = station?.Latitude ?? GroundStations.Average(s => s.Latitude);
        groundLongitude = station?.Longitude ?? GroundStations.Average(s => s.Longitude);
        distance = station is null ? 0.75 : 0.012; tileRequestKey = "";
        UpdateGroundTarget(true); UpdateOrbitMaterials(); UpdateScene(); NotifyCamera(); Notify(nameof(PlaybackText));
    }
    private void UpdateGroundTarget(bool align = false)
    {
        double lat = groundLatitude * Math.PI / 180;
        double lon = (groundLongitude + CurrentSun.EarthRotationDegrees) * Math.PI / 180;
        cameraTarget = SceneGeometry.Point(EarthCoordinates.ToInertial(EarthCoordinates.Surface(groundLatitude, groundLongitude), CurrentSun.EarthRotationDegrees));
        if (antennaView) cameraTarget = (Point3D)((Vector3D)cameraTarget * 1.026);
        if (align) { yaw = Math.PI / 2 + lon; pitch = lat; }
    }
    private void QueueMapTiles()
    {
        if (disposed) return;
        if (!followGroundStation || antennaView || distance >= 0.1)
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
            var results = await tileLoader.LoadAsync(plan, token).ConfigureAwait(false);
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
        tileCancellation?.Cancel(); tileCancellation?.Dispose(); tileLoader.Dispose();
    }

    private sealed class ActionCommand(Action<object?> action) : ICommand
    {
        public ActionCommand(Action action) : this(_ => action()) { }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action(parameter);
    }
}
