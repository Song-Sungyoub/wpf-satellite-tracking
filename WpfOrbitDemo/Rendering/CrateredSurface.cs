using System.Windows.Media;
using System.Windows.Media.Imaging;
using V = (double X, double Y, double Z);

namespace WpfOrbitDemo.Rendering;

// Procedural albedo maps for airless cratered bodies in the SceneGeometry.Sphere UV layout
// (u = 0.5 is longitude 0, +longitude = east, v = 0 is the north pole).
// shortcut: maria/craters are placed at approximate real positions; not LRO/MESSENGER imagery or measured albedo maps.
internal static class CrateredSurface
{
    internal sealed record Profile(int Width, (double Lat, double Lon, double Radius, double Depth)[] Maria,
        (double Lat, double Lon, double Radius, double Rays)[] Rayed, (double R, double G, double B) Highland, (double R, double G, double B) Mare,
        double HighlandAlbedo, double MareAlbedo, int SmallCraters, int Seed);

    internal sealed record SurfaceMaps(int Width, int Height, byte[] Pixels, float[] Elevation);

    // Moon: near-side maria traced by overlapping soft discs (latitude, east longitude, angular radius, darkness) so the
    // "rabbit" seen from Earth (Fecunditatis/Nectaris ears, Tranquillitatis head, Serenitatis/Imbrium body, Nubium/Humorum) reads.
    public static readonly Profile Moon = new(2048,
    // Radii follow the real basin sizes (e.g. Imbrium ~1,120 km across ≈ 18.5° radius) so neighbouring seas connect.
    [
        (32.8, -15.6, 17, 1), (44.5, -31.5, 4, 0.9), (28.0, 17.5, 10.5, 1), (38, 29, 4.5, 0.8), (8.5, 31.4, 12, 1), (2, 24, 6, 0.9),
        (17.0, 59.1, 8.5, 1), (-3, 53, 8, 0.95), (-12, 50, 8, 0.9), (-15.2, 35.5, 5.5, 0.95), (-21.3, -16.6, 11, 0.9), (-14, -12, 5, 0.85),
        (-24.4, -38.6, 6.5, 0.95), (-10.0, -23.0, 6, 0.85), (13.3, 3.6, 4.5, 0.85), (2, 1, 3.5, 0.8), (12, -8, 4.5, 0.8), (26, 0, 3.5, 0.75),
        (7.5, -30.9, 8, 0.85), (57, -40, 5, 0.8), (58, -20, 5, 0.8), (57, 0, 5, 0.8), (56, 18, 4.5, 0.75), (54, 35, 4, 0.7),
        (50, -55, 8, 0.9), (40, -62, 9, 0.95), (30, -58, 11, 1), (18, -58, 12, 1), (6, -55, 11, 0.95), (-3, -50, 10, 0.95),
        (-12, -43, 7, 0.9), (1, 65, 3, 0.8), (7, 68, 3, 0.8),
        (27.3, 147.9, 3.5, 0.7), (-19.4, -92.8, 4, 0.7), (-1.3, 87.3, 4, 0.7), (13.3, 86.1, 3.5, 0.7), (-38.9, 93, 5, 0.5), (-33.7, 163.5, 3, 0.5),
    ],
    [(-43.3, -11.2, 0.75, 28), (9.6, -20.1, 0.8, 12), (8.1, -38.0, 0.28, 7), (23.7, -47.4, 0.33, 5), (-8.9, 15.4, 0.3, 4)],
    (200, 195, 186), (186, 184, 184), 0.80, 0.40, 5200, 1969);

    // Mercury: no dark maria, only faint smooth plains (Caloris and the northern plains), dense craters and a few rayed craters.
    public static readonly Profile Mercury = new(1024,
        [(31.5, 162.7, 14, 0.35), (70, 0, 18, 0.25), (65, 120, 12, 0.25), (-20, -60, 10, 0.2)],
        [(58, 16.8, 0.5, 14), (-11.4, -31.5, 0.35, 9), (-48, 25, 0.4, 8), (20, -110, 0.3, 6)],
        (172, 160, 146), (150, 142, 132), 0.74, 0.58, 4200, 4879);

    public static BitmapSource CreateTexture(Profile profile)
    {
        var maps = CreateMaps(profile);
        var texture = BitmapSource.Create(maps.Width, maps.Height, 96, 96, PixelFormats.Bgra32, null, maps.Pixels, maps.Width * 4); texture.Freeze();
        return texture;
    }

    // Albedo colour plus an elevation map (in body radii) for relief shading: crater bowls, rims, central peaks, ejecta and low maria.
    public static SurfaceMaps CreateMaps(Profile profile)
    {
        int width = profile.Width, height = width / 2;
        var albedo = new float[width * height];
        var mare = new float[width * height];
        var elevation = new float[width * height];
        var centers = profile.Maria.Select(m => (V: Unit(m.Lat, m.Lon), Radius: m.Radius * Rad, m.Depth)).ToArray();
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                var v = Pixel(x, y, width, height);
                double m = 0, warp = double.NaN;
                foreach (var c in centers)
                {
                    double d = Angle(v, c.V);
                    if (d > c.Radius * 1.9) continue;
                    // Domain-warped, ragged shorelines so neighbouring discs merge into irregular seas (noise depends only on the texel).
                    if (double.IsNaN(warp)) warp = 0.9 * (Fbm(v, 2.5, 4, 17) - 0.5) + 0.35 * (Fbm(v, 12, 3, 29) - 0.5);
                    double edge = c.Radius * (0.92 + warp);
                    m = Math.Max(m, c.Depth * Smooth(Math.Clamp((edge - d) / (0.55 * c.Radius) + 0.5, 0, 1)));
                }
                if (m > 0) m *= 0.88 + 0.24 * (Fbm(v, 7, 3, 53) - 0.5); // patchy basalt flows
                double highland = profile.HighlandAlbedo + 0.26 * (Fbm(v, 3, 5, 3) - 0.5) + 0.08 * (Fbm(v, 24, 3, 7) - 0.5);
                double basalt = profile.MareAlbedo + 0.14 * (Fbm(v, 6, 4, 41) - 0.5);
                int i = y * width + x;
                mare[i] = (float)m;
                albedo[i] = (float)(highland + (basalt - highland) * m);
                elevation[i] = (float)(0.005 * (Fbm(v, 5, 5, 61) - 0.5) - 0.002 * m); // rolling highlands, low flat maria
            }
        });
        var random = new Random(profile.Seed);
        AddCraters(albedo, mare, elevation, width, random, 30, 2.5, 6.5);
        AddCraters(albedo, mare, elevation, width, random, 320, 0.8, 2.5);
        AddCraters(albedo, mare, elevation, width, random, profile.SmallCraters, Math.Max(0.18, 360.0 / width), 0.8);
        foreach (var r in profile.Rayed) AddRays(albedo, width, Unit(r.Lat, r.Lon), r.Radius * Rad, r.Rays * Rad);

        var pixels = new byte[width * height * 4];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x, p = i * 4;
                double a = Math.Clamp(albedo[i], 0, 1.25), m = mare[i];
                pixels[p] = Byte(a * (profile.Highland.B + (profile.Mare.B - profile.Highland.B) * m));
                pixels[p + 1] = Byte(a * (profile.Highland.G + (profile.Mare.G - profile.Highland.G) * m));
                pixels[p + 2] = Byte(a * (profile.Highland.R + (profile.Mare.R - profile.Highland.R) * m)); pixels[p + 3] = 255;
            }
        });
        return new SurfaceMaps(width, height, pixels, elevation);
    }

    // Bowl-shaped albedo profile; most craters are old and eroded (low contrast), a few fresh ones keep bright rims and ejecta.
    // Elevation: parabolic bowl (depth/diameter ~0.2 small, ~0.1 large), raised rim, fading ejecta and a central peak on large craters.
    private static void AddCraters(float[] albedo, float[] mare, float[] elevation, int width, Random random, int count, double minDegrees, double maxDegrees)
    {
        for (int n = 0; n < count; n++)
        {
            double lat = Math.Asin(2 * random.NextDouble() - 1) / Rad, lon = random.NextDouble() * 360 - 180;
            var c = Unit(lat, lon);
            if (random.NextDouble() < 0.75 * mare[Index(c, width)]) continue; // fewer craters on younger maria
            double radius = minDegrees * Math.Pow(maxDegrees / minDegrees, Math.Pow(random.NextDouble(), 2.2)) * Rad;
            bool fresh = random.NextDouble() < 0.08;
            double age = fresh ? 1 : 0.35 + 0.5 * random.NextDouble();
            double floor = 1 - age * (0.05 + 0.05 * random.NextDouble());
            double depth = (radius > 1.5 * Rad ? 0.1 : 0.2) * 2 * radius * (0.4 + 0.6 * age);
            bool peak = radius > 1.2 * Rad;
            Stamp(c, radius * 1.7, width, (i, _, d) =>
            {
                double t = d / radius;
                double factor = t < 0.7 ? floor : t < 1.0 ? floor - age * 0.05 * Smooth((t - 0.7) / 0.3)
                    : t < 1.15 ? 1 + age * (fresh ? 0.2 : 0.03) * (1 - Math.Abs(t - 1.07) / 0.08)
                    : 1 + (fresh ? 0.14 : 0.02 * age) * (1.7 - t) / 0.55;
                albedo[i] *= (float)factor;
                double rim = (t - 1) / 0.12, ejecta = Math.Max(0, (1.7 - t) / 0.7);
                double h = (t < 1 ? -depth * (1 - t * t) : 0) + 0.22 * depth * Math.Exp(-rim * rim) + (t > 1 ? 0.08 * depth * ejecta * ejecta : 0);
                if (peak) h += 0.4 * depth * Math.Exp(-(t / 0.16) * (t / 0.16));
                elevation[i] += (float)h;
            });
        }
    }

    private static void AddRays(float[] albedo, int width, V c, double radius, double length)
    {
        var east = Normalize(Cross((0, 1, 0), c));
        var north = Cross(c, east);
        Stamp(c, length, width, (i, v, d) =>
        {
            double t = d / radius;
            double halo = t < 1 ? 0.12 : t < 3 ? 0.30 * (3 - t) / 2 : 0;
            double azimuth = Math.Atan2(Dot(v, north), Dot(v, east));
            double streak = Math.Pow(Periodic((azimuth + Math.PI) / (2 * Math.PI) * 56, 56, 7), 7);
            double fall = Math.Pow(Math.Clamp(1 - d / length, 0, 1), 1.6);
            albedo[i] += (float)(halo + 0.45 * streak * fall * (t > 1.2 ? 1 : 0));
        });
    }

    // Visit every texel within an angular radius of a centre vector.
    private static void Stamp(V c, double reach, int width, Action<int, V, double> apply)
    {
        int height = width / 2;
        double lat = Math.Asin(Math.Clamp(c.Y, -1, 1));
        int y0 = Math.Max(0, (int)((Math.PI / 2 - lat - reach) / Math.PI * height)), y1 = Math.Min(height - 1, (int)((Math.PI / 2 - lat + reach) / Math.PI * height) + 1);
        bool pole = Math.Abs(lat) + reach > 85 * Rad;
        double lon = Math.Atan2(-c.Z, c.X), span = pole ? Math.PI : reach / Math.Cos(Math.Abs(lat) + reach);
        int xc = (int)((lon + Math.PI) / (2 * Math.PI) * width), xs = pole ? width / 2 : (int)(span / (2 * Math.PI) * width) + 2;
        for (int y = y0; y <= y1; y++)
            for (int dx = -xs; dx <= xs; dx++)
            {
                int x = ((xc + dx) % width + width) % width;
                var v = Pixel(x, y, width, height);
                double d = Angle(v, c);
                if (d < reach) apply(y * width + x, v, d);
            }
    }

    private static int Index(V v, int width)
    {
        int height = width / 2;
        int y = Math.Clamp((int)((Math.PI / 2 - Math.Asin(Math.Clamp(v.Y, -1, 1))) / Math.PI * height), 0, height - 1);
        int x = Math.Clamp((int)((Math.Atan2(-v.Z, v.X) + Math.PI) / (2 * Math.PI) * width), 0, width - 1);
        return y * width + x;
    }

    // Shared sphere-texture helpers (also used by PlanetSurfaces).
    internal const double Rad = Math.PI / 180;
    internal static byte Byte(double value) => (byte)Math.Clamp(value, 0, 255);
    internal static V Pixel(int x, int y, int width, int height)
    {
        double lon = (x + 0.5) / width * 2 * Math.PI - Math.PI, lat = Math.PI / 2 - (y + 0.5) / height * Math.PI;
        return (Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), -Math.Cos(lat) * Math.Sin(lon));
    }
    internal static V Unit(double latDegrees, double lonDegrees)
    {
        double lat = latDegrees * Rad, lon = lonDegrees * Rad;
        return (Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), -Math.Cos(lat) * Math.Sin(lon));
    }
    internal static double Angle(V a, V b) => Math.Acos(Math.Clamp(Dot(a, b), -1, 1));
    internal static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static V Cross(V a, V b) => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    internal static V Normalize(V a) { double l = Math.Sqrt(Dot(a, a)); return l < 1e-12 ? (1, 0, 0) : (a.X / l, a.Y / l, a.Z / l); }
    internal static double Smooth(double t) => t * t * (3 - 2 * t);

    // 3D value-noise fBm on the unit sphere (no seam or polar pinching), result 0..1.
    internal static double Fbm(V v, double frequency, int octaves, int seed)
    {
        double sum = 0, amplitude = 0.5, total = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += amplitude * Noise(v.X * frequency, v.Y * frequency, v.Z * frequency, seed + o);
            total += amplitude; amplitude *= 0.5; frequency *= 2;
        }
        return sum / total;
    }
    private static double Noise(double x, double y, double z, int seed)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y), iz = (int)Math.Floor(z);
        double fx = Smooth(x - ix), fy = Smooth(y - iy), fz = Smooth(z - iz);
        double Lerp(double a, double b, double t) => a + (b - a) * t;
        double H(int dx, int dy, int dz) => Hash(ix + dx, iy + dy, iz + dz, seed);
        return Lerp(Lerp(Lerp(H(0, 0, 0), H(1, 0, 0), fx), Lerp(H(0, 1, 0), H(1, 1, 0), fx), fy),
                    Lerp(Lerp(H(0, 0, 1), H(1, 0, 1), fx), Lerp(H(0, 1, 1), H(1, 1, 1), fx), fy), fz);
    }
    private static double Periodic(double t, int period, int seed)
    {
        int i = (int)Math.Floor(t); double f = Smooth(t - i);
        return Hash(((i % period) + period) % period, 0, 0, seed) * (1 - f) + Hash((((i + 1) % period) + period) % period, 0, 0, seed) * f;
    }
    private static double Hash(int x, int y, int z, int seed)
    {
        uint h = unchecked((uint)(x * 374761393 + y * 668265263 + z * 2147483647 + seed * 144665));
        h = unchecked((h ^ (h >> 13)) * 1274126177); h ^= h >> 16;
        return (h & 0xffff) / 65535.0;
    }
}
