
namespace WpfOrbitDemo.Models;

public readonly record struct MapTile(int Zoom, int X, int Y)
{
    public void Validate()
    {
        if (Zoom is < 5 or > 12 || X < 0 || Y < 0 || X >= 1 << Zoom || Y >= 1 << Zoom) throw new ArgumentOutOfRangeException(nameof(MapTile));
    }
    public (double West, double North, double East, double South) Bounds
    {
        get
        {
            Validate(); double n = 1 << Zoom;
            return (X / n * 360 - 180, Latitude(Y / n), (X + 1) / n * 360 - 180, Latitude((Y + 1) / n));
        }
    }
    public static double Latitude(double mercatorY) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * mercatorY))) * 180 / Math.PI;
}

public static class MapTilePlan
{
    public static IReadOnlyList<MapTile> Create(double latitude, double longitude, double distance, double width, double height)
    {
        if (!double.IsFinite(latitude) || Math.Abs(latitude) > 85.05112878 || !double.IsFinite(longitude)
            || !double.IsFinite(distance) || distance <= 0 || distance > 0.1 || !double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(latitude));
        double cos = Math.Cos(latitude * Math.PI / 180);
        double span = 2 * distance * CircularOrbit.EarthRadiusKm * 1000 * Math.Tan(21 * Math.PI / 180) * 1.5;
        int zoom = Math.Clamp((int)Math.Ceiling(Math.Log2(156543.033928 * cos / (span / width))), 5, 12);
        while (zoom > 5 && span * Math.Max(1, height / width) / (40075016.686 * cos / (1 << zoom)) > 4) zoom--;
        int n = 1 << zoom;
        double tx = ((longitude + 180) % 360 + 360) % 360 / 360 * n;
        double lat = latitude * Math.PI / 180;
        double ty = (1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2 * n;
        double tileMeters = 40075016.686 * cos / n;
        double rx = span / tileMeters / 2 + 0.25;
        double ry = span * height / width / tileMeters / 2 + 0.25;
        var tiles = new List<MapTile>();
        for (int y = Math.Max(0, (int)Math.Floor(ty - ry)); y <= Math.Min(n - 1, (int)Math.Floor(ty + ry)); y++)
            for (int x = (int)Math.Floor(tx - rx); x <= (int)Math.Floor(tx + rx); x++) tiles.Add(new MapTile(zoom, (x % n + n) % n, y));
        return tiles.Distinct().ToArray();
    }
}
