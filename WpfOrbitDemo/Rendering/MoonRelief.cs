using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using V = (double X, double Y, double Z);

namespace WpfOrbitDemo.Rendering;

// Moon surface with relief shading baked for the current Sun direction (in Moon-local coordinates).
// WPF has no normal maps, so the albedo texture is multiplied by (perturbed N·L) / (sphere N·L): craters cast soft shading
// near the terminator and flatten out at full illumination. Re-shading runs in the background and only when the
// local Sun direction has moved more than ~1.5°; upload follows NightLights: never wait for the render thread.
public sealed class MoonRelief : IDisposable
{
    private readonly CrateredSurface.SurfaceMaps maps;
    private readonly byte[] buffer;
    private readonly double[] cosLat, sinLat, cosLon, sinLon;
    private readonly CancellationTokenSource cancellation = new();
    private V shadedSun = (0, 0, 0);
    private bool pending, disposed, busy;
    public WriteableBitmap Texture { get; }
    public GeometryModel3D Model { get; }

    public MoonRelief()
    {
        maps = CrateredSurface.CreateMaps(CrateredSurface.Moon);
        buffer = new byte[maps.Pixels.Length];
        cosLat = new double[maps.Height]; sinLat = new double[maps.Height];
        cosLon = new double[maps.Width]; sinLon = new double[maps.Width];
        for (int y = 0; y < maps.Height; y++) { double lat = Math.PI / 2 - (y + 0.5) / maps.Height * Math.PI; cosLat[y] = Math.Cos(lat); sinLat[y] = Math.Sin(lat); }
        for (int x = 0; x < maps.Width; x++) { double lon = (x + 0.5) / maps.Width * 2 * Math.PI - Math.PI; cosLon[x] = Math.Cos(lon); sinLon[x] = Math.Sin(lon); }
        Texture = new WriteableBitmap(maps.Width, maps.Height, 96, 96, PixelFormats.Bgra32, null);
        Texture.WritePixels(new Int32Rect(0, 0, maps.Width, maps.Height), maps.Pixels, maps.Width * 4, 0); // plain albedo until the first shading
        var brush = new ImageBrush(Texture);
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        Model = SceneGeometry.Sphere(CelestialGeometry.Radii[1], new DiffuseMaterial(brush), 128, 64);
    }

    public void RequestUpdate(V localSun)
    {
        double length = Math.Sqrt(localSun.X * localSun.X + localSun.Y * localSun.Y + localSun.Z * localSun.Z);
        if (disposed || busy || pending || !double.IsFinite(length) || length < 1e-12) return;
        var sun = (localSun.X / length, localSun.Y / length, localSun.Z / length);
        if (sun.Item1 * shadedSun.X + sun.Item2 * shadedSun.Y + sun.Item3 * shadedSun.Z > Math.Cos(1.5 * Math.PI / 180)) return;
        busy = true;
        var token = cancellation.Token;
        Task.Run(() => Shade(sun, token), token).ContinueWith(task =>
        {
            busy = false;
            if (task.IsCompletedSuccessfully && !disposed) { shadedSun = sun; pending = true; TryPresent(); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public bool TryPresent()
    {
        if (disposed || !pending) return false;
        if (!Texture.TryLock(new Duration(TimeSpan.Zero))) return false; // retry next frame instead of blocking the UI thread
        try
        {
            Marshal.Copy(buffer, 0, Texture.BackBuffer, buffer.Length);
            Texture.AddDirtyRect(new Int32Rect(0, 0, maps.Width, maps.Height));
        }
        finally { Texture.Unlock(); }
        pending = false;
        return true;
    }

    private void Shade(V sun, CancellationToken token)
    {
        int width = maps.Width, height = maps.Height;
        double dLat = Math.PI / height, dLon = 2 * Math.PI / width;
        var elevation = maps.Elevation; var source = maps.Pixels;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, y =>
        {
            int up = Math.Max(0, y - 1), down = Math.Min(height - 1, y + 1);
            double cl = Math.Max(0.05, cosLat[y]), sl = sinLat[y];
            for (int x = 0; x < width; x++)
            {
                double co = cosLon[x], so = sinLon[x];
                // Sphere normal and local east/north tangents in the SceneGeometry.Sphere layout.
                double nx = cosLat[y] * co, ny = sl, nz = -cosLat[y] * so;
                double flat = nx * sun.X + ny * sun.Y + nz * sun.Z;
                double ratio = 1;
                if (flat > 0)
                {
                    int left = (x + width - 1) % width, right = (x + 1) % width, i = y * width + x;
                    double east = (elevation[y * width + right] - elevation[y * width + left]) / (2 * dLon * cl);
                    double north = (elevation[up * width + x] - elevation[down * width + x]) / ((down - up) * dLat);
                    double sunEast = -so * sun.X - co * sun.Z;
                    double sunNorth = -sl * co * sun.X + cosLat[y] * sun.Y + sl * so * sun.Z;
                    double lit = flat - east * sunEast - north * sunNorth;
                    ratio = Math.Clamp(lit / Math.Max(flat, 0.03), 0, 3);
                }
                int p = (y * width + x) * 4;
                buffer[p] = (byte)Math.Min(255, source[p] * ratio); buffer[p + 1] = (byte)Math.Min(255, source[p + 1] * ratio);
                buffer[p + 2] = (byte)Math.Min(255, source[p + 2] * ratio); buffer[p + 3] = 255;
            }
        });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; pending = false; cancellation.Cancel(); cancellation.Dispose();
    }
}
