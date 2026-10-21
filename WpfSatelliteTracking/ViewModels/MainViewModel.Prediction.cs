using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfSatelliteTracking.Models;
using WpfSatelliteTracking.Rendering;

namespace WpfSatelliteTracking.ViewModels;

public sealed partial class MainViewModel
{
    private GroundPrediction? pinnedPrediction;
    private readonly List<(GroundPrediction Forecast, TranslateTransform3D Actual)> otherPredictions = new();
    public string PredictionDetails { get; private set; } = "";
    private readonly Model3DGroup predictionHost = new();
    private readonly TranslateTransform3D actualGroundMarker = new();
    private bool playToPrediction;
    private string predictionTimeInput = "";
    public string PredictionTimeInput { get => predictionTimeInput; set { predictionTimeInput=value; Notify(); } }
    public string PredictionStatus { get; private set; } = "예측 시각을 입력해 지표 지점을 고정하세요. 빈 입력은 10분 뒤입니다.";
    public ICommand PinPredictionCommand { get; private set; } = null!;
    public ICommand ReplayPredictionCommand { get; private set; } = null!;
    public ICommand ClearPredictionCommand { get; private set; } = null!;
    private void InitializePredictionCommands()
    {
        PinPredictionCommand = new ActionCommand(PinPrediction);
        ReplayPredictionCommand = new ActionCommand(ReplayPrediction);
        ClearPredictionCommand = new ActionCommand(() =>
        {
            pinnedPrediction=null; otherPredictions.Clear(); PredictionDetails=""; Notify(nameof(PredictionDetails)); playToPrediction=false; predictionHost.Children.Clear();
            PredictionStatus="고정 예측을 지웠습니다."; Notify(nameof(PredictionStatus));
        });
    }
    private void PinPrediction()
    {
        DateTimeOffset target;
        if (string.IsNullOrWhiteSpace(PredictionTimeInput)) target=SimulationTime.AddMinutes(10);
        else if (DateTime.TryParseExact(PredictionTimeInput,"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var local))
            target=new DateTimeOffset(DateTime.SpecifyKind(local,DateTimeKind.Unspecified),TimeSpan.FromHours(9));
        else { PredictionStatus="KST yyyy-MM-dd HH:mm:ss 형식으로 입력하세요."; Notify(nameof(PredictionStatus)); return; }
        target = new DateTimeOffset(target.Ticks - target.Ticks % TimeSpan.TicksPerSecond, target.Offset);
        if (target<=SimulationTime || target>SatelliteCatalog.MaximumTime)
        { PredictionStatus="현재 이후이며 TLE 사용 범위 안인 시각을 입력하세요."; Notify(nameof(PredictionStatus)); return; }
        pinnedPrediction=GroundPrediction.Create(SelectedOrbit,SimulationTime,target);
        playToPrediction=false; predictionHost.Children.Clear();
        otherPredictions.Clear(); PredictionDetails=""; Notify(nameof(PredictionDetails));
        var fixedPoint=EarthCoordinates.Surface(pinnedPrediction.Latitude,pinnedPrediction.Longitude,0.010*SatelliteOrbit.EarthRadiusKm);
        var marker=SceneGeometry.Sphere(0.0035,SceneGeometry.MarkerMaterial(Colors.Gold),16,8);
        marker.Transform=new TranslateTransform3D(fixedPoint.X,fixedPoint.Y,fixedPoint.Z);
        predictionHost.Children.Add(marker);
        var actual=SceneGeometry.Sphere(0.0022,SceneGeometry.MarkerMaterial(Colors.Cyan),16,8);
        actual.Transform=actualGroundMarker; predictionHost.Children.Add(actual);
        double trackDuration = Math.Min((target-SimulationTime).TotalSeconds, SelectedOrbit.PeriodSeconds);
        double trackStart = (target-SatelliteCatalog.ReferenceEpoch).TotalSeconds-trackDuration;
        predictionHost.Children.Add(SceneGeometry.GroundTrack(SelectedOrbit,trackStart,trackDuration,Colors.Gold));
        if (ShowAllOrbits)
            foreach (var orbit in Orbits)
            {
                if (orbit == SelectedOrbit) continue;
                var forecast = GroundPrediction.Create(orbit, SimulationTime, target);
                var point = EarthCoordinates.Surface(forecast.Latitude, forecast.Longitude, 0.010*SatelliteOrbit.EarthRadiusKm);
                var predicted = SceneGeometry.Sphere(0.0035, SceneGeometry.MarkerMaterial(Colors.Gold), 12, 6);
                predicted.Transform = new TranslateTransform3D(point.X, point.Y, point.Z);
                predictionHost.Children.Add(predicted);
                var transform = new TranslateTransform3D();
                var moving = SceneGeometry.Sphere(0.0022, SceneGeometry.MarkerMaterial(Colors.Cyan), 12, 6);
                moving.Transform = transform; predictionHost.Children.Add(moving);
                otherPredictions.Add((forecast, transform));
            }
        PredictionTimeInput=target.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture);
        UpdatePinnedPrediction();
    }
    private void ReplayPrediction()
    {
        if (pinnedPrediction is not { } forecast)
        { PredictionStatus="먼저 예측 지점을 고정하세요."; Notify(nameof(PredictionStatus)); return; }
        SelectedOrbit=forecast.Orbit;
        SetSimulationTime(forecast.CreatedAt);
        FocusEarthSystemCommand.Execute(null); EarthFixedView=true; fitOverview=false;
        yaw=Math.PI/2+(forecast.Longitude+CurrentSun.EarthRotationDegrees)*Math.PI/180;
        pitch=forecast.Latitude*Math.PI/180; distance=3.4;
        NotifyCamera();
        playToPrediction=true; playing=true; realTime=false;
        previousTime=clock.Elapsed.TotalSeconds; Notify(nameof(PlaybackText));
    }
    private void UpdatePinnedPrediction()
    {
        if (pinnedPrediction is not { } forecast) return;
        int index=Array.IndexOf(Orbits,forecast.Orbit);
        if (index<0 || satelliteTransforms.Count<=index) return;
        var matrix=satelliteTransforms[index].Matrix;
        var actual=(X:matrix.OffsetX,Y:matrix.OffsetY,Z:matrix.OffsetZ);
        var geo=EarthCoordinates.Geodetic(EarthCoordinates.ToEarthFixed(actual,CurrentSun.EarthRotationDegrees));
        var p=EarthCoordinates.Surface(geo.Latitude,geo.Longitude,0.010*SatelliteOrbit.EarthRadiusKm);
        actualGroundMarker.OffsetX=p.X; actualGroundMarker.OffsetY=p.Y; actualGroundMarker.OffsetZ=p.Z;
        var marker=earthPose.Transform(SceneGeometry.Point(forecast.SurfacePoint));
        double error=GroundPrediction.SceneErrorMeters(actual,SimulationTime,(marker.X,marker.Y,marker.Z));
        double remaining=(forecast.Arrival-SimulationTime).TotalSeconds;
        string result=Math.Abs(remaining)<=0.001 ? (error<=1 ? $"일치 · 렌더링 지표 위치 오차 {error:0.000} m" : $"불일치 · 렌더링 지표 위치 오차 {error:0.000} m")
            : remaining>0 ? $"도착까지 {remaining:0.0}초 · 현재 지표 거리 {error/1000:0.0} km" : "예측 시각 경과 · 예측 시각까지 재생을 눌러 다시 검증하세요.";
        PredictionStatus=$"{forecast.Orbit.Name} · 고정 {forecast.Arrival.ToOffset(TimeSpan.FromHours(9)):MM-dd HH:mm:ss} KST\n위도 {forecast.Latitude:0.0000}° / 경도 {forecast.Longitude:0.0000}°\n금색: 고정 예측 · 청록: 재생 중 실제 지표 투영\n{result}";
        if (otherPredictions.Count > 0)
        {
            var results = new List<(string Name, double Error)> { (forecast.Orbit.Name, error) };
            foreach (var entry in otherPredictions)
            {
                int satelliteIndex = Array.IndexOf(Orbits, entry.Forecast.Orbit);
                var pose = satelliteTransforms[satelliteIndex].Matrix;
                var position = (X:pose.OffsetX, Y:pose.OffsetY, Z:pose.OffsetZ);
                var geodetic = EarthCoordinates.Geodetic(EarthCoordinates.ToEarthFixed(position, CurrentSun.EarthRotationDegrees));
                var surface = EarthCoordinates.Surface(geodetic.Latitude, geodetic.Longitude, 0.010*SatelliteOrbit.EarthRadiusKm);
                entry.Actual.OffsetX=surface.X; entry.Actual.OffsetY=surface.Y; entry.Actual.OffsetZ=surface.Z;
                var anchor=earthPose.Transform(SceneGeometry.Point(entry.Forecast.SurfacePoint));
                results.Add((entry.Forecast.Orbit.Name,GroundPrediction.SceneErrorMeters(position,SimulationTime,(anchor.X,anchor.Y,anchor.Z))));
            }
            int matched=results.Count(r=>r.Error<=1);
            double maximum=results.Max(r=>r.Error);
            PredictionStatus=$"전체 {results.Count}대 · 고정 {forecast.Arrival.ToOffset(TimeSpan.FromHours(9)):MM-dd HH:mm:ss} KST\n금색: 예측 · 청록: 실제 지표 투영\n"+
                (Math.Abs(remaining)<=0.001 ? $"도착 · 일치 {matched}/{results.Count}대 (1 m 이내)\n최대 오차 {maximum:0.000000} m" : $"도착까지 {remaining:0.0}초");
            PredictionDetails=Math.Abs(remaining)<=0.001 ? string.Join("\n",results.Select(r=>$"{r.Name}: {r.Error:0.000000} m")) : "";
            Notify(nameof(PredictionDetails));
        }
        Notify(nameof(PredictionStatus));
    }
}
