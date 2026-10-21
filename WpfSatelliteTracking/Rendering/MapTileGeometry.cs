using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using WpfSatelliteTracking.Models;

namespace WpfSatelliteTracking.Rendering;

public static class MapTileGeometry
{
    public static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var image = decoder.Frames[0];
        if (image.PixelWidth != 256 || image.PixelHeight != 256) throw new InvalidDataException("타일 치수는 256×256이어야 합니다.");
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        byte[] pixels = new byte[256 * 256 * 4]; converted.CopyPixels(pixels, 256 * 4, 0);
        for (int p = 0; p < pixels.Length; p += 4)
            if (Math.Max(pixels[p], Math.Max(pixels[p + 1], pixels[p + 2])) < 8) pixels[p] = pixels[p + 1] = pixels[p + 2] = pixels[p + 3] = 0;
        var result = BitmapSource.Create(256, 256, 96, 96, PixelFormats.Pbgra32, null, pixels, 256 * 4); result.Freeze(); return result;
    }
    public static Material CreateNightMaterial(Material nightMaterial)
    {
        var sourceBrush = (ImageBrush)((EmissiveMaterial)((MaterialGroup)nightMaterial).Children[1]).Brush;
        var nightBrush = new ImageBrush(sourceBrush.ImageSource) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        var localNight = new MaterialGroup();
        localNight.Children.Add(new DiffuseMaterial(nightBrush) { Color = Colors.Black, AmbientColor = Colors.Black });
        localNight.Children.Add(new EmissiveMaterial(nightBrush));
        return localNight;
    }
    public static Model3DGroup Create(MapTile tile, BitmapSource image, Material nightMaterial, Material? sharedNightMaterial = null)
    {
        tile.Validate();
        var day = Mesh(tile, 1.000005, false); var night = Mesh(tile, 1.000007, true);
        var material = new DiffuseMaterial(new ImageBrush(image)); material.Freeze();
        var localNight = sharedNightMaterial ?? CreateNightMaterial(nightMaterial);
        var group = new Model3DGroup(); group.Children.Add(new GeometryModel3D(day, material)); group.Children.Add(new GeometryModel3D(night, localNight)); return group;
    }
    private static MeshGeometry3D Mesh(MapTile tile, double radius, bool globalUv)
    {
        const int steps = 16;
        var mesh = new MeshGeometry3D(); double n = 1 << tile.Zoom;
        for (int y = 0; y <= steps; y++)
            for (int x = 0; x <= steps; x++)
            {
                double u = (double)x / steps, v = (double)y / steps;
                double longitude = (tile.X + u) / n * 2 * Math.PI - Math.PI;
                double latitude = MapTile.Latitude((tile.Y + v) / n) * Math.PI / 180;
                var normal = new Vector3D(Math.Cos(latitude) * Math.Cos(longitude), Math.Sin(latitude), -Math.Cos(latitude) * Math.Sin(longitude));
                mesh.Positions.Add(SceneGeometry.Point(EarthCoordinates.Surface(latitude * 180 / Math.PI, longitude * 180 / Math.PI, (radius - 1) * SatelliteOrbit.EarthRadiusKm))); mesh.Normals.Add(normal);
                mesh.TextureCoordinates.Add(globalUv ? new Point((longitude + Math.PI) / (2 * Math.PI), (Math.PI / 2 - latitude) / Math.PI) : new Point(u, v));
            }
        for (int y = 0; y < steps; y++)
            for (int x = 0; x < steps; x++)
            {
                int a = y * (steps + 1) + x, b = a + steps + 1;
                foreach (int index in new[] { a, b, a + 1, a + 1, b, b + 1 }) mesh.TriangleIndices.Add(index);
            }
        mesh.Freeze(); return mesh;
    }
}
