using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WpfSatelliteTracking.Models;

namespace WpfSatelliteTracking.Rendering;

public sealed class NightLights : IDisposable
{
    public const int Width = 2048, Height = 1024;
    private readonly byte[] source = new byte[Width * Height * 4];
    private readonly byte[] pixels = new byte[Width * Height * 4];
    private readonly byte[] backgroundPixels = new byte[Width * Height * 4];
    private readonly CancellationTokenSource cancellation = new();
    private int version;
    private bool disposed;
    private byte[]? pendingPixels;
    private DateTimeOffset pendingTime;
    public bool HasPendingUpload => pendingPixels is not null;
    public Task UpdateTask { get; private set; } = Task.CompletedTask;
    private readonly double[] cosLongitude = new double[Width], sinLongitude = new double[Width];
    private DateTimeOffset? previousUpdate;
    public WriteableBitmap Texture { get; } = new(Width, Height, 96, 96, PixelFormats.Pbgra32, null);
    public GeometryModel3D Model { get; }

    public NightLights()
    {
        var image = new BitmapImage(); image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/WpfSatelliteTracking;component/Assets/earth-night.jpg");
        image.DecodePixelWidth = Width; image.EndInit(); image.Freeze();
        if (image.PixelWidth != Width || image.PixelHeight != Height) throw new InvalidOperationException("야간 지도는 2:1 등거리 원통도법 이미지여야 합니다.");
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        converted.CopyPixels(source, Width * 4, 0);
        for (int x = 0; x < Width; x++)
        {
            double lon = (x + 0.5) / Width * 2 * Math.PI - Math.PI;
            cosLongitude[x] = Math.Cos(lon); sinLongitude[x] = Math.Sin(lon);
        }
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new ImageBrush(Texture)) { Color = Colors.Black, AmbientColor = Colors.Black });
        material.Children.Add(new EmissiveMaterial(new ImageBrush(Texture)));
        Model = SceneGeometry.Sphere(1.001, material, 128, 64);
    }

    public void Update(DateTimeOffset time, SolarState sun)
    {
        version++; // An explicit time change must supersede any pending animation result.
        pendingPixels = null;
        if (disposed || !NeedsUpdate(time)) return;
        Calculate(pixels, sun, CancellationToken.None);
        QueueUpload(pixels, time);
    }

    public void RequestUpdate(DateTimeOffset time, SolarState sun)
    {
        if (disposed || HasPendingUpload || !UpdateTask.IsCompleted || !NeedsUpdate(time)) return;
        UpdateTask = UpdateAsync(time, sun, ++version, cancellation.Token);
    }

    private bool NeedsUpdate(DateTimeOffset time) => previousUpdate is not DateTimeOffset previous || Math.Abs((time - previous).TotalSeconds) >= 30;

    public void Invalidate()
    {
        version++; pendingPixels = null; previousUpdate = null;
    }

    private async Task UpdateAsync(DateTimeOffset time, SolarState sun, int requestVersion, CancellationToken token)
    {
        try
        {
            await Task.Run(() => Calculate(backgroundPixels, sun, token), token).ConfigureAwait(false);
            await Texture.Dispatcher.InvokeAsync(() =>
            {
                if (!disposed && version == requestVersion) QueueUpload(backgroundPixels, time);
            }, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException) { }
    }

    private void QueueUpload(byte[] buffer, DateTimeOffset time)
    {
        pendingPixels = buffer; pendingTime = time;
        TryPresent();
    }

    public bool TryPresent()
    {
        if (disposed || pendingPixels is null) return false;
        // WritePixels implicitly waits forever for the render thread. Never wait for a GPU copy on the UI thread.
        if (!Texture.TryLock(new Duration(TimeSpan.Zero)))
        {
            return false;
        }
        try
        {
            Debug.Assert(Texture.BackBufferStride == Width * 4);
            Marshal.Copy(pendingPixels, 0, Texture.BackBuffer, pendingPixels.Length);
            Texture.AddDirtyRect(new Int32Rect(0, 0, Width, Height));
        }
        finally { Texture.Unlock(); }
        previousUpdate = pendingTime;
        pendingPixels = null;
        return true;
    }

    private void Calculate(byte[] buffer, SolarState sun, CancellationToken token)
    {
        var direction = sun.EarthFixedDirection;
        for (int y = 0; y < Height; y++)
        {
            token.ThrowIfCancellationRequested();
            double lat = Math.PI / 2 - (y + 0.5) / Height * Math.PI;
            double cos = Math.Cos(lat), north = Math.Sin(lat) * direction.Y;
            for (int x = 0; x < Width; x++)
            {
                double dot = cos * (cosLongitude[x] * direction.X - sinLongitude[x] * direction.Z) + north;
                double night = SolarLighting.NightWeight(dot);
                int p = (y * Width + x) * 4;
                buffer[p] = (byte)(source[p] * night); buffer[p + 1] = (byte)(source[p + 1] * night);
                buffer[p + 2] = (byte)(source[p + 2] * night); buffer[p + 3] = (byte)(255 * night);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; version++; pendingPixels = null; cancellation.Cancel(); cancellation.Dispose();
    }
}
