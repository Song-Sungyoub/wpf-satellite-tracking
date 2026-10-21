namespace WpfSatelliteTracking.Models;

public sealed record GroundStation
{
    public string Name { get; }
    public double Latitude { get; }
    public double Longitude { get; }
    public double AltitudeKm { get; }

    public GroundStation(string name, double latitude, double longitude, double altitudeKm = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("지상국 이름이 필요합니다.", nameof(name));
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90) throw new ArgumentOutOfRangeException(nameof(latitude));
        if (!double.IsFinite(longitude) || longitude is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(longitude));
        if (!double.IsFinite(altitudeKm) || altitudeKm < -0.5) throw new ArgumentOutOfRangeException(nameof(altitudeKm));
        (Name, Latitude, Longitude, AltitudeKm) = (name, latitude, longitude, altitudeKm);
    }
}
