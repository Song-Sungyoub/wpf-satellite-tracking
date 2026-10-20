namespace WpfSatelliteTracking.Models;

public enum SatelliteMission { A, B }

public sealed class CircularOrbit
{
    public const double EarthRadiusKm = 6371;
    public string Name { get; }
    public SatelliteMission Mission { get; }
    public int? ClusterId { get; }
    // ClusterId is the ground-track group within the satellite type; (Mission, ClusterId) identifies satellites that
    // share one repeat ground track (each member has its own plane).
    public string GroupName => ClusterId is int cluster
        ? $"{Mission}형 {cluster}궤도군 · {(Mission == SatelliteMission.A ? SatelliteConstellation.APerGroup : SatelliteConstellation.BPerGroup)}대"
        : $"{Mission}형";
    public bool SharesPlaneWith(CircularOrbit other) => ClusterId is not null && Mission == other.Mission && ClusterId == other.ClusterId;
    public double AltitudeKm { get; }
    public double InclinationDegrees { get; }
    public double NodeDegrees { get; }
    public double InitialPhaseDegrees { get; }
    public double Radius => (EarthRadiusKm + AltitudeKm) / EarthRadiusKm;
    public double PeriodSeconds => 2 * Math.PI * Math.Sqrt(Math.Pow(EarthRadiusKm + AltitudeKm, 3) / 398600.4418);

    public CircularOrbit(string name, double altitudeKm, double inclinationDegrees, double nodeDegrees, double initialPhaseDegrees = 0,
        SatelliteMission mission = SatelliteMission.A, int? clusterId = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("위성 이름이 필요합니다.", nameof(name));
        if (!double.IsFinite(altitudeKm) || altitudeKm <= 0) throw new ArgumentOutOfRangeException(nameof(altitudeKm));
        if (!double.IsFinite(inclinationDegrees) || inclinationDegrees < 0 || inclinationDegrees > 180) throw new ArgumentOutOfRangeException(nameof(inclinationDegrees));
        if (!double.IsFinite(nodeDegrees)) throw new ArgumentOutOfRangeException(nameof(nodeDegrees));
        if (!double.IsFinite(initialPhaseDegrees)) throw new ArgumentOutOfRangeException(nameof(initialPhaseDegrees));
        if (!Enum.IsDefined(mission)) throw new ArgumentOutOfRangeException(nameof(mission));
        if (clusterId <= 0) throw new ArgumentOutOfRangeException(nameof(clusterId));
        Mission = mission; ClusterId = clusterId;
        (Name, AltitudeKm, InclinationDegrees, NodeDegrees, InitialPhaseDegrees) = (name, altitudeKm, inclinationDegrees, nodeDegrees, initialPhaseDegrees);
    }

    // shortcut: 원형 2체 궤도만 계산, 실제 TLE 추적이 필요하면 SGP4 전파기로 교체.
    public (double X, double Y, double Z) Position(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        return PositionAtAngle(seconds % PeriodSeconds / PeriodSeconds * 2 * Math.PI + InitialPhaseDegrees * Math.PI / 180);
    }

    public (double X, double Y, double Z) PositionAtAngle(double angle)
    {
        if (!double.IsFinite(angle)) throw new ArgumentOutOfRangeException(nameof(angle));
        double i = InclinationDegrees * Math.PI / 180, node = NodeDegrees * Math.PI / 180;
        double x = Radius * Math.Cos(angle), z = Radius * Math.Sin(angle);
        // Y is north; positive phase travels eastward around the north axis.
        return (x * Math.Cos(node) - z * Math.Cos(i) * Math.Sin(node),
                z * Math.Sin(i), -x * Math.Sin(node) - z * Math.Cos(i) * Math.Cos(node));
    }
}
