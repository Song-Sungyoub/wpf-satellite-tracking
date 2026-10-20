namespace WpfOrbitDemo.Models;

public sealed record SolarState(double EarthRotationDegrees, double LatitudeDegrees, double LongitudeDegrees,
    (double X, double Y, double Z) EarthFixedDirection, (double X, double Y, double Z) InertialDirection);

public static class SolarLighting
{
    private const double Radians = Math.PI / 180;

    public static SolarState WithInertialSun(SolarState state, (double X, double Y, double Z) direction)
    {
        double length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z);
        if (!double.IsFinite(length) || length <= 0) throw new ArgumentOutOfRangeException(nameof(direction));
        var inertial = (X: direction.X / length, Y: direction.Y / length, Z: direction.Z / length);
        double angle = state.EarthRotationDegrees * Radians;
        var fixedDirection = (X: Math.Cos(angle) * inertial.X - Math.Sin(angle) * inertial.Z,
            Y: inertial.Y, Z: Math.Sin(angle) * inertial.X + Math.Cos(angle) * inertial.Z);
        return new SolarState(state.EarthRotationDegrees, Math.Asin(inertial.Y) / Radians,
            Math.Atan2(-fixedDirection.Z, fixedDirection.X) / Radians, fixedDirection, inertial);
    }

    public static SolarState At(DateTimeOffset time)
    {
        var utc = time.UtcDateTime;
        if (utc.Year is < 2000 or > 2100) throw new ArgumentOutOfRangeException(nameof(time), "지원 연도: 2000~2100");
        // shortcut: NOAA 연주기 근사식과 평균 항성시, 정밀 천문·관제에는 검증된 천체력으로 교체.
        double gamma = 2 * Math.PI / (DateTime.IsLeapYear(utc.Year) ? 366 : 365) * (utc.DayOfYear - 1 + (utc.TimeOfDay.TotalHours - 12) / 24);
        double equation = 229.18 * (0.000075 + 0.001868 * Math.Cos(gamma) - 0.032077 * Math.Sin(gamma)
            - 0.014615 * Math.Cos(2 * gamma) - 0.040849 * Math.Sin(2 * gamma));
        double declination = 0.006918 - 0.399912 * Math.Cos(gamma) + 0.070257 * Math.Sin(gamma)
            - 0.006758 * Math.Cos(2 * gamma) + 0.000907 * Math.Sin(2 * gamma)
            - 0.002697 * Math.Cos(3 * gamma) + 0.00148 * Math.Sin(3 * gamma);
        double longitude = Wrap((720 - utc.TimeOfDay.TotalMinutes - equation) / 4 + 180) - 180;
        double days = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
        double rotation = Wrap(280.46061837 + 360.98564736629 * days);
        return new SolarState(rotation, declination / Radians, longitude,
            Direction(declination, longitude * Radians), Direction(declination, (longitude + rotation) * Radians));
    }

    private static (double X, double Y, double Z) Direction(double latitude, double longitude) =>
        (Math.Cos(latitude) * Math.Cos(longitude), Math.Sin(latitude), -Math.Cos(latitude) * Math.Sin(longitude));
    private static double Wrap(double degrees) => (degrees % 360 + 360) % 360;

    public static double NightWeight(double sunDotNormal)
    {
        if (!double.IsFinite(sunDotNormal)) throw new ArgumentOutOfRangeException(nameof(sunDotNormal));
        double t = Math.Clamp(-sunDotNormal / 0.105, 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static bool IsEclipsed((double X, double Y, double Z) position, SolarState sun)
    {
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z)) throw new ArgumentOutOfRangeException(nameof(position));
        var s = sun.InertialDirection;
        double along = position.X * s.X + position.Y * s.Y + position.Z * s.Z;
        double perpendicularSquared = position.X * position.X + position.Y * position.Y + position.Z * position.Z - along * along;
        // shortcut: 평행 태양광의 원통형 본영, 반영·원뿔형 그림자·달 가림은 정밀 일식 모델 도입 시 처리.
        return along < 0 && perpendicularSquared < 1;
    }
}
