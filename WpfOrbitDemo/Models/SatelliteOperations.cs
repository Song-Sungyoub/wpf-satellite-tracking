using V = (double X, double Y, double Z);

namespace WpfOrbitDemo.Models;

public readonly record struct StationContact(GroundStation Station, V Position, double ElevationDegrees, double RangeKm)
{
    public bool Connected => ElevationDegrees >= SatelliteOperations.MinimumElevationDegrees;
}

// Geometry only: no radio device, image acquisition, file I/O or network connection.
public sealed class SatelliteOperations(GroundStation[] stations)
{
    public const double MinimumElevationDegrees = 10;
    public const double EarthRotationDegreesPerSecond = 360.98564736629 / 86400;
    public StationContact[] Contacts { get; } = new StationContact[stations.Length];
    public V SatellitePosition { get; private set; }
    public V[] Footprint { get; } = new V[4];
    public bool HasFootprint { get; private set; }
    public SatelliteMission Mission { get; private set; }

    public void Update(CircularOrbit orbit, double seconds, double earthRotationDegrees, bool capturing)
    {
        SatellitePosition = orbit.Position(seconds); Mission = orbit.Mission;
        for (int i = 0; i < stations.Length; i++)
        {
            var position = StationPosition(stations[i], earthRotationDegrees);
            var ray = Subtract(SatellitePosition, position);
            Contacts[i] = new(stations[i], position, Elevation(position, ray), Length(ray) * CircularOrbit.EarthRadiusKm);
        }
        HasFootprint = false;
        if (!capturing) return;
        var up = Unit(SatellitePosition);
        var forward = Unit(Subtract(orbit.Position(seconds + 1), orbit.Position(seconds - 1)));
        var across = Unit(Cross(up, forward));
        // Illustrative sensor settings, not measured specifications of Type A/Type B spacecraft.
        double look = (Mission == SatelliteMission.A ? 35 : 0) * Math.PI / 180;
        double acrossHalf = (Mission == SatelliteMission.A ? 4 : 6) * Math.PI / 180;
        double alongHalf = (Mission == SatelliteMission.A ? 2 : 4) * Math.PI / 180;
        var direction = Add(Scale(up, -Math.Cos(look)), Scale(across, Math.Sin(look)));
        var sensorAcross = Add(Scale(across, Math.Cos(look)), Scale(up, Math.Sin(look)));
        for (int i = 0; i < 4; i++)
        {
            double x = i < 2 ? -1 : 1, y = i is 0 or 3 ? -1 : 1;
            var ray = Unit(Add(direction, Add(Scale(sensorAcross, x * Math.Tan(acrossHalf)), Scale(forward, y * Math.Tan(alongHalf)))));
            if (!IntersectEarth(SatellitePosition, ray, out var point)) return;
            Footprint[i] = point;
        }
        HasFootprint = true;
    }

    public double? NextContactDelay(CircularOrbit orbit, double seconds, double rotationDegrees, double maximumSeconds)
    {
        if (!double.IsFinite(maximumSeconds) || maximumSeconds < 0 || maximumSeconds > 86400) throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
        for (double delay = 0; delay <= maximumSeconds; delay += 15)
        {
            var satellite = orbit.Position(seconds + delay);
            foreach (var station in stations)
            {
                var ground = StationPosition(station, rotationDegrees + delay * EarthRotationDegreesPerSecond);
                if (Elevation(ground, Subtract(satellite, ground)) >= MinimumElevationDegrees) return delay;
            }
        }
        return null;
    }

    public static V StationPosition(GroundStation station, double rotationDegrees)
    {
        double lat = station.Latitude * Math.PI / 180, lon = (station.Longitude + rotationDegrees) * Math.PI / 180;
        return (Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), -Math.Cos(lat) * Math.Sin(lon));
    }
    private static double Elevation(V normal, V ray) => Math.Asin(Math.Clamp(Dot(Unit(ray), normal), -1, 1)) * 180 / Math.PI;
    public static bool IntersectEarth(V origin, V direction, out V point)
    {
        direction = Unit(direction);
        double b = Dot(origin, direction), discriminant = b * b - Dot(origin, origin) + 1;
        if (b >= 0 || discriminant < 0) { point = default; return false; }
        double distance = -b - Math.Sqrt(discriminant);
        if (distance < 0) { point = default; return false; }
        point = Unit(Add(origin, Scale(direction, distance))); return true;
    }
    private static V Add(V a, V b) => (a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static V Subtract(V a, V b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static V Scale(V a, double scale) => (a.X * scale, a.Y * scale, a.Z * scale);
    private static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static V Cross(V a, V b) => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static double Length(V a) => Math.Sqrt(Dot(a, a));
    private static V Unit(V a)
    {
        double length = Length(a);
        if (!double.IsFinite(length) || length < 1e-12) throw new ArgumentException("Invalid direction vector");
        return Scale(a, 1 / length);
    }
}
