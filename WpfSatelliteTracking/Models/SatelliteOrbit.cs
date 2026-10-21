using SGPdotNET.Observation;
using SGPdotNET.TLE;

namespace WpfSatelliteTracking.Models;

public enum SatelliteMission { A, B }

// UTC -> SGP4 TEME kilometers -> right-handed WPF scene (X, Z, -Y), in Earth radii.
// Mission selects an illustrative mesh/sensor only; it is not inferred mission metadata.
public sealed class SatelliteOrbit
{
    public const double EarthRadiusKm = EarthCoordinates.EquatorialRadiusKm;
    private readonly Satellite satellite;
    private readonly object sync = new();
    private double lastSeconds = double.NaN;
    private (double X, double Y, double Z) lastPosition;
    public Tle Elements => satellite.Tle;
    public string Name => satellite.Name;
    public uint NoradId => Elements.NoradNumber;
    public DateTimeOffset TleEpoch => new(Elements.Epoch);
    public SatelliteMission Mission { get; }
    public double InclinationDegrees => Elements.Inclination.Degrees;
    public double NodeDegrees => Elements.RightAscendingNode.Degrees;
    public double InitialPhaseDegrees => Elements.MeanAnomaly.Degrees;
    public double PeriodSeconds => 86400 / Elements.MeanMotionRevPerDay;
    public double AltitudeKm => (Radius - 1) * EarthRadiusKm;
    public double Radius => Math.Cbrt(398600.8 * Math.Pow(PeriodSeconds / (2 * Math.PI), 2)) / EarthRadiusKm;

    public SatelliteOrbit(string name, string line1, string line2, SatelliteMission mission = SatelliteMission.B)
    {
        ValidateLine(line1, '1'); ValidateLine(line2, '2');
        satellite = new Satellite(name, line1, line2); Mission = mission;
        if (Elements.MeanMotionRevPerDay <= 0 || Elements.Eccentricity is < 0 or >= 1)
            throw new ArgumentException("지원하지 않는 TLE 궤도입니다.");
    }
    private static void ValidateLine(string line, char number)
    {
        if (line.Length != 69 || line[0] != number || !char.IsDigit(line[68]))
            throw new ArgumentException("TLE 행 형식이 올바르지 않습니다.");
        int sum = line.Take(68).Sum(c => char.IsAsciiDigit(c) ? c - '0' : c == '-' ? 1 : 0);
        if (sum % 10 != line[68] - '0') throw new ArgumentException("TLE 체크섬이 맞지 않습니다.");
    }
    public (double X, double Y, double Z) Position(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (sync)
        {
            if (seconds == lastSeconds) return lastPosition;
            var p = satellite.Predict(SatelliteCatalog.ReferenceEpoch.AddSeconds(seconds).UtcDateTime).Position;
            lastPosition = (p.X / EarthRadiusKm, p.Z / EarthRadiusKm, -p.Y / EarthRadiusKm);
            if (!double.IsFinite(lastPosition.X + lastPosition.Y + lastPosition.Z)) throw new InvalidOperationException("SGP4 위치 계산 실패");
            lastSeconds = seconds; return lastPosition;
        }
    }
    public (double X, double Y, double Z) EarthFixedPosition(double seconds)
        => EarthCoordinates.ToEarthFixed(Position(seconds), EarthCoordinates.GmstDegrees(SatelliteCatalog.ReferenceEpoch.AddSeconds(seconds)));
}
