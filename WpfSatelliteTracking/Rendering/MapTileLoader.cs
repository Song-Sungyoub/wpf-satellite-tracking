using System.IO;
using System.Windows.Media.Imaging;
using WpfSatelliteTracking.Models;

namespace WpfSatelliteTracking.Rendering;

// Loads and decodes frozen tile images off the UI thread. The caller owns request
// cancellation and applies the result to its scene on the dispatcher.
public sealed class MapTileLoader : IDisposable
{
    private readonly MapTileManager tileManager = new();

    public async Task<(MapTile Tile, BitmapSource? Image)[]> LoadAsync(IReadOnlyList<MapTile> plan, CancellationToken token)
    {
        using var slots = new SemaphoreSlim(4);
        return await Task.WhenAll(plan.Select(async tile =>
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                byte[] bytes = await tileManager.GetAsync(tile, token).ConfigureAwait(false);
                BitmapSource image = MapTileGeometry.Decode(bytes);
                return (Tile: tile, Image: (BitmapSource?)image);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException or OperationCanceledException && !token.IsCancellationRequested)
            { return (Tile: tile, Image: (BitmapSource?)null); }
            finally { slots.Release(); }
        })).ConfigureAwait(false);
    }

    public void Dispose() => tileManager.Dispose();
}
