using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace WpfSatelliteTracking.Models;

public sealed class MapTileManager : IDisposable
{
    private readonly ZipArchive package;
    private readonly Dictionary<string, (int Bytes, string Hash)> tiles = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private bool disposed;
    private const int MaxBytes = 1024 * 1024;

    public MapTileManager() : this(typeof(MapTileManager).Assembly.GetManifestResourceStream("WpfSatelliteTracking.OfflineMapTiles")
        ?? throw new InvalidDataException("로컬 지도 자료가 포함되지 않았습니다.")) { }

    public MapTileManager(Stream source)
    {
        package = new ZipArchive(source, ZipArchiveMode.Read);
        try
        {
            var manifest = package.GetEntry("manifest.json") ?? throw new InvalidDataException("지도 자료 목록이 없습니다.");
            if (manifest.Length > MaxBytes) throw new InvalidDataException("지도 자료 목록이 너무 큽니다.");
            using var stream = manifest.Open(); using var document = JsonDocument.Parse(stream);
            if (document.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("지원하지 않는 지도 자료 형식입니다.");
            foreach (var tile in document.RootElement.GetProperty("tiles").EnumerateArray())
                tiles.Add(tile.GetProperty("name").GetString()!, (tile.GetProperty("bytes").GetInt32(), tile.GetProperty("sha256").GetString()!));
        }
        catch { package.Dispose(); throw; }
    }

    public Task<byte[]> GetAsync(MapTile tile, CancellationToken cancellationToken)
    {
        tile.Validate();
        return Task.Run(() =>
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                string name = $"{tile.Zoom}-{tile.X}-{tile.Y}.jpg";
                if (!tiles.TryGetValue(name, out var metadata)) throw new FileNotFoundException("로컬 지도 자료 범위 밖입니다.", name);
                var entry = package.GetEntry(name) ?? throw new InvalidDataException("지도 자료가 누락됐습니다.");
                if (metadata.Bytes is <= 2 or > MaxBytes || entry.Length != metadata.Bytes) throw new InvalidDataException("지도 타일 크기가 올바르지 않습니다.");
                var bytes = new byte[metadata.Bytes];
                using var stream = entry.Open(); stream.ReadExactly(bytes);
                cancellationToken.ThrowIfCancellationRequested();
                if (bytes[0] != 255 || bytes[1] != 216 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(metadata.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("로컬 지도 무결성 검사 실패");
                return bytes;
            }
        }, cancellationToken);
    }

    public void Dispose() { lock (sync) { disposed = true; package.Dispose(); } }
}
