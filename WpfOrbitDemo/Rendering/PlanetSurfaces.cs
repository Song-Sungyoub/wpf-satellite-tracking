using System.Windows.Media;
using System.Windows.Media.Imaging;
using V = (double X, double Y, double Z);
using static WpfOrbitDemo.Rendering.CrateredSurface;

namespace WpfOrbitDemo.Rendering;

// Procedural planet surfaces in the SceneGeometry.Sphere UV layout. Features sit near their real latitudes/longitudes,
// but colours, contrast and turbulence are illustrative; no spacecraft imagery is embedded.
internal static class PlanetSurfaces
{
    public static BitmapSource Create(int body) => body switch
    {
        0 => Render(1024, Sun),
        2 => CrateredSurface.CreateTexture(CrateredSurface.Mercury),
        3 => Render(1024, Venus),
        5 => Render(1024, Mars),
        6 => Render(1024, Jupiter), // distant views; CreateDetailed swaps in the 4096 px close-up map after startup
        7 => Render(2048, Saturn),
        8 => Render(512, Uranus),
        9 => Render(1024, Neptune),
        _ => throw new ArgumentOutOfRangeException(nameof(body))
    };

    // High-resolution Jupiter for close-ups: the same vortex field, fine enough to see swirls when zoomed in;
    // mipmapped minification hides them again from afar. Built off the UI thread at reduced parallelism.
    public static BitmapSource CreateDetailed(int body, CancellationToken token) => body == 6
        ? Render(4096, Jupiter, Math.Max(1, Environment.ProcessorCount / 2), token)
        : throw new ArgumentOutOfRangeException(nameof(body));

    private static BitmapSource Render(int width, Func<V, double, double, (double R, double G, double B)> shade,
        int parallelism = -1, CancellationToken token = default)
    {
        int height = width / 2;
        var pixels = new byte[width * height * 4];
        Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = token }, y =>
        {
            for (int x = 0; x < width; x++)
            {
                var v = Pixel(x, y, width, height);
                double lat = Math.Asin(Math.Clamp(v.Y, -1, 1)) / Rad, lon = Math.Atan2(-v.Z, v.X) / Rad;
                var (r, g, b) = shade(v, lat, lon);
                int p = (y * width + x) * 4;
                pixels[p] = Byte(b); pixels[p + 1] = Byte(g); pixels[p + 2] = Byte(r); pixels[p + 3] = 255;
            }
        });
        var texture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); texture.Freeze();
        return texture;
    }

    private static (double, double, double) Mix((double R, double G, double B) a, (double R, double G, double B) b, double t)
        => (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    private static (double, double, double) Scale((double R, double G, double B) c, double s) => (c.R * s, c.G * s, c.B * s);
    private static double Spot(V v, V center, double radius) => Smooth(Math.Clamp(1 - Angle(v, center) / radius, 0, 1));
    // Elliptical feature on a latitude/longitude grid (degrees), returns 0..1 with soft edge.
    private static double Oval(double lat, double lon, double cLat, double cLon, double halfLon, double halfLat)
    {
        double dLon = ((lon - cLon + 540) % 360 - 180) / halfLon, dLat = (lat - cLat) / halfLat;
        return Smooth(Math.Clamp(1 - Math.Sqrt(dLon * dLon + dLat * dLat), 0, 1));
    }

    private static readonly V[] SunSpots = [Unit(14, 30), Unit(16, 36), Unit(-11, -70), Unit(-9, -64), Unit(22, 150), Unit(-18, 105)];
    private static (double, double, double) Sun(V v, double lat, double lon)
    {
        // Granulation (fine cells), supergranulation and bright faculae; sunspot umbra/penumbra in the activity belts.
        double granule = Fbm(v, 60, 2, 101), super = Fbm(v, 9, 3, 103);
        double faculae = Math.Pow(Math.Max(0, Fbm(v, 14, 3, 107) - 0.55) / 0.45, 2) * (Math.Abs(lat) < 40 ? 1 : 0.3);
        var color = Mix((255, 150, 40), (255, 236, 170), 0.45 + 0.6 * (granule - 0.5) + 0.25 * (super - 0.5) + 0.35 * faculae);
        double umbra = 0, penumbra = 0;
        foreach (var s in SunSpots) { umbra = Math.Max(umbra, Spot(v, s, 1.2 * Rad)); penumbra = Math.Max(penumbra, Spot(v, s, 2.6 * Rad)); }
        color = Mix(color, (200, 95, 25), Math.Min(1, penumbra * 1.6) * 0.7);
        return Mix(color, (70, 25, 5), Math.Min(1, umbra * 2));
    }

    private static (double, double, double) Venus(V v, double lat, double lon)
    {
        // Featureless in visible light apart from faint, sheared Y-shaped cloud bands.
        double shear = lat + 9 * Math.Cos((lon + 40) * Rad) * Math.Cos(lat * Rad) + 6 * (Fbm(v, 3, 4, 201) - 0.5);
        double bands = 0.5 + 0.5 * Math.Sin(shear * 0.16) * (0.4 + 0.6 * Fbm(v, 2, 3, 203));
        double haze = Fbm(v, 6, 4, 207);
        var color = Mix((214, 182, 120), (240, 222, 175), 0.55 + 0.25 * (bands - 0.5) + 0.25 * (haze - 0.5));
        return Scale(color, 0.94 + 0.06 * Math.Cos(lat * Rad));
    }

    private static readonly (V Center, double Radius, double Dark)[] MarsDark =
    [
        (Unit(9, 69), 13 * Rad, 0.9), (Unit(45, -30), 14 * Rad, 0.75), (Unit(-25, -40), 18 * Rad, 0.75), (Unit(-2, 0), 9 * Rad, 0.8),
        (Unit(-20, 15), 14 * Rad, 0.7), (Unit(-25, 100), 15 * Rad, 0.65), (Unit(-15, 160), 14 * Rad, 0.6), (Unit(20, -90), 7 * Rad, 0.3),
        (Unit(-28, -95), 10 * Rad, 0.55), (Unit(55, 80), 12 * Rad, 0.45),
    ];
    private static readonly V Hellas = Unit(-42, 70), Olympus = Unit(18.6, -133.8);
    private static (double, double, double) Mars(V v, double lat, double lon)
    {
        double warp = 0.8 * (Fbm(v, 3, 4, 301) - 0.5);
        double dark = 0;
        foreach (var d in MarsDark)
            dark = Math.Max(dark, d.Dark * Smooth(Math.Clamp((d.Radius * (0.85 + warp) - Angle(v, d.Center)) / (0.5 * d.Radius) + 0.5, 0, 1)));
        dark = Math.Clamp(dark + 0.25 * (Fbm(v, 10, 4, 303) - 0.5), 0, 1);
        // Valles Marineris: a narrow dark chasm along ~-8° from -110° to -40° longitude.
        if (lon is > -112 and < -38)
        {
            double canyon = Smooth(Math.Clamp(1 - Math.Abs(lat + 8 + 1.5 * Math.Sin(lon * 0.13) + 1.2 * (Fbm(v, 9, 3, 311) - 0.5)) / 0.9, 0, 1));
            double broken = Smooth(Math.Clamp((Fbm(v, 14, 3, 313) - 0.35) / 0.3, 0, 1)); // chasms, not one continuous line
            dark = Math.Max(dark, 0.5 * canyon * broken);
        }
        var ground = Mix((196, 112, 70), (226, 152, 102), Fbm(v, 5, 5, 305));
        var color = Mix(ground, (108, 70, 52), dark);
        color = Mix(color, (232, 176, 128), 0.6 * Spot(v, Hellas, 15 * Rad) + 0.3 * Spot(v, Olympus, 3 * Rad));
        // Polar caps with ragged edges (north ~78°, south ~-82°).
        double edge = 4 * (Fbm(v, 8, 3, 307) - 0.5);
        double cap = Math.Max(Smooth(Math.Clamp((lat - 76 - edge) / 3, 0, 1)), Smooth(Math.Clamp((-lat - 80 - edge) / 3, 0, 1)));
        return Mix(color, (240, 236, 232), cap);
    }

    // Jupiter belts (dark) and zones (light) by latitude: (south edge, north edge, colour).
    private static readonly (double From, double To, (double R, double G, double B) Color)[] JupiterBands =
    [
        (-90, -48, (150, 140, 128)), (-48, -40, (176, 152, 128)), (-40, -34, (226, 214, 192)), (-34, -27, (172, 128, 96)),
        (-27, -20, (234, 222, 200)), (-20, -7, (164, 112, 80)), (-7, 7, (236, 222, 196)), (7, 18, (156, 104, 74)),
        (18, 24, (232, 220, 198)), (24, 31, (178, 134, 102)), (31, 37, (226, 214, 194)), (37, 43, (186, 156, 128)), (43, 90, (152, 142, 130)),
    ];
    // Vortices (latitude, longitude, half-width in longitude/latitude degrees, swirl in radians; sign = rotation sense):
    // the Great Red Spot, the white ovals, eddies along belt edges and small polar cyclones.
    private static readonly (double Lat, double Lon, double Rx, double Ry, double Swirl)[] Vortices = CreateVortices();
    private static (double, double, double, double, double)[] CreateVortices()
    {
        var list = new List<(double, double, double, double, double)> { (-22.4, 60, 13, 7, 3.2) };
        foreach (double lon in new[] { -150.0, -60, 20, 110 }) list.Add((-40.5, lon, 3.6, 2.6, 2.4));
        var random = new Random(5150);
        double[] edges = [-48, -40, -34, -27, -20, -7, 7, 18, 24, 31, 37, 43];
        for (int i = 0; i < 170; i++)
        {
            double lat = edges[random.Next(edges.Length)] + (random.NextDouble() - 0.5) * 3;
            double size = 0.7 + 2.3 * Math.Pow(random.NextDouble(), 2);
            list.Add((lat, random.NextDouble() * 360 - 180, size * 1.6, size, (random.Next(2) * 2 - 1) * (1.6 + 1.8 * random.NextDouble())));
        }
        for (int i = 0; i < 90; i++)
        {
            double size = 1 + 2.5 * random.NextDouble();
            list.Add(((random.Next(2) * 2 - 1) * (55 + 30 * random.NextDouble()), random.NextDouble() * 360 - 180, size * 1.3, size, (random.Next(2) * 2 - 1) * 2.5));
        }
        return list.ToArray();
    }
    // Rotate the sample point around each nearby vortex by an angle that fades with elliptical distance, so the band
    // pattern evaluated at the warped point curls into spirals.
    private static (double Lat, double Lon) Swirl(double lat, double lon)
    {
        foreach (var (cLat, cLon, rx, ry, swirl) in Vortices)
        {
            double dy = lat - cLat;
            if (Math.Abs(dy) > ry * 2) continue;
            double dx = (lon - cLon + 540) % 360 - 180;
            if (Math.Abs(dx) > rx * 2) continue;
            double d2 = (dx / rx) * (dx / rx) + (dy / ry) * (dy / ry);
            if (d2 > 4) continue;
            double angle = swirl * Math.Exp(-1.6 * d2), c = Math.Cos(angle), s = Math.Sin(angle);
            // Rotate in a frame scaled to the ellipse so oval vortices stay oval.
            double ux = dx / rx, uy = dy / ry;
            lon = cLon + (c * ux - s * uy) * rx; lat = cLat + (s * ux + c * uy) * ry;
        }
        return (lat, lon);
    }

    private static (double, double, double) Jupiter(V v, double lat, double lon)
    {
        var (wLat, wLon) = Swirl(lat, lon);
        v = Unit(Math.Clamp(wLat, -90, 90), wLon); lat = wLat; lon = wLon;
        // Turbulent band edges: warp the latitude with noise and eddies that grow toward the belt boundaries.
        double turbulence = 4.5 * (Fbm(v, 5, 5, 401) - 0.5) + 2.0 * (Fbm(v, 16, 3, 403) - 0.5);
        double w = lat + turbulence;
        // Real belts and zones are close in brightness; pull every band toward a common tan and add in-band streaks.
        var color = Mix(Band(JupiterBands, w, 2.4), (206, 182, 152), 0.35);
        double streaks = Fbm((v.X * 0.35, v.Y * 6, v.Z * 0.35), 3, 4, 411); // stretched along longitude by zonal winds
        color = Scale(color, 0.9 + 0.12 * Fbm(v, 12, 4, 405) + 0.1 * (streaks - 0.5));
        // Festoons: blue-grey plumes trailing from the NEB's southern edge into the equatorial zone.
        double festoon = Math.Pow(Math.Max(0, Math.Sin(lon * Rad * 11 + 3 * Fbm(v, 4, 2, 407))), 6) * Smooth(Math.Clamp(1 - Math.Abs(w - 5) / 3, 0, 1));
        color = Mix(color, (110, 110, 125), 0.55 * festoon);
        // Great Red Spot (~22°S) with a pale collar, and a string of white ovals at ~40°S.
        double grs = Oval(lat + 1.2 * (Fbm(v, 10, 2, 409) - 0.5), lon, -22.4, 60, 12, 6);
        double collar = Oval(lat, lon, -22.4, 60, 15, 8) - grs;
        color = Mix(color, (242, 232, 214), 0.6 * Math.Max(0, collar));
        // Spiral arms inside the spot follow the swirled coordinates; a darker core marks the calm centre.
        double arms = 0.85 + 0.3 * (Fbm(v, 22, 3, 413) - 0.5);
        color = Mix(color, Scale((196, 98, 66), arms), Math.Min(1, grs * 1.4));
        color = Mix(color, (170, 80, 58), 0.4 * Oval(lat, lon, -22.4, 60, 4, 2));
        foreach (double ovalLon in new[] { -150.0, -60, 20, 110 }) color = Mix(color, (245, 240, 232), 0.85 * Oval(lat, lon, -40.5, ovalLon, 3.2, 2.2));
        return color;
    }

    private static (double, double, double) Saturn(V v, double lat, double lon)
    {
        double w = lat + 0.9 * (Fbm(v, 5, 4, 501) - 0.5);
        double belts = 0.5 + 0.28 * Math.Sin(w * 0.42) + 0.14 * Math.Sin(w * 0.97 + 1) + 0.06 * Math.Sin(w * 2.3);
        var color = Mix((198, 164, 108), (236, 216, 166), belts);
        color = Mix(color, (240, 226, 186), 0.5 * Smooth(Math.Clamp(1 - Math.Abs(w) / 12, 0, 1))); // bright equatorial zone
        color = Scale(color, 0.94 + 0.08 * Fbm(v, 10, 4, 503));
        // North polar hexagon: blue-grey cap bounded by a six-sided jet near 78°N.
        double hex = 78 + 1.6 * Math.Cos(6 * lon * Rad);
        color = Mix(color, (182, 172, 146), Smooth(Math.Clamp((lat - hex) / 1.5 + 0.5, 0, 1)) * 0.4);
        return Mix(color, (170, 160, 140), Smooth(Math.Clamp((-lat - 70) / 12, 0, 1)) * 0.5);
    }

    private static (double, double, double) Uranus(V v, double lat, double lon)
    {
        double bands = Math.Sin(lat * 0.11) * 0.02 + 0.02 * (Fbm(v, 4, 3, 601) - 0.5);
        var color = Scale((168, 216, 222), 0.96 + bands);
        return Mix(color, (205, 236, 236), 0.4 * Smooth(Math.Clamp((lat - 50) / 30, 0, 1))); // brighter polar cap
    }

    private static (double, double, double) Neptune(V v, double lat, double lon)
    {
        double w = lat + 2 * (Fbm(v, 4, 4, 701) - 0.5);
        double bands = 0.5 + 0.25 * Math.Sin(w * 0.15) + 0.1 * Math.Sin(w * 0.5);
        var color = Mix((44, 74, 178), (78, 118, 220), bands);
        color = Scale(color, 0.94 + 0.1 * Fbm(v, 8, 4, 703));
        color = Mix(color, (28, 42, 118), Oval(lat, lon, -20, 40, 14, 6) * 0.85); // Great Dark Spot
        // Bright high-altitude cirrus streaks stretched along longitude.
        foreach (double streakLat in new[] { -25.0, 28, -55 })
        {
            double streak = Smooth(Math.Clamp(1 - Math.Abs(lat - streakLat - 1.5 * (Fbm(v, 6, 2, 705) - 0.5)) / 1.4, 0, 1));
            color = Mix(color, (232, 238, 250), 0.8 * streak * Math.Min(1, 3 * Math.Pow(Fbm(v, 3, 3, 707 + (int)streakLat), 3)));
        }
        return color;
    }

    private static (double, double, double) Band((double From, double To, (double R, double G, double B) Color)[] bands, double lat, double blend)
    {
        for (int i = 0; i < bands.Length; i++)
        {
            var (from, to, color) = bands[i];
            if (lat > to) continue;
            // Blend into the neighbouring band across a soft edge.
            if (i > 0 && lat - from < blend) return Mix(bands[i - 1].Color, color, Smooth(Math.Clamp(0.5 + (lat - from) / (2 * blend), 0, 1)));
            if (i < bands.Length - 1 && to - lat < blend) return Mix(color, bands[i + 1].Color, Smooth(Math.Clamp(0.5 - (to - lat) / (2 * blend), 0, 1)));
            return color;
        }
        return bands[^1].Color;
    }
}
