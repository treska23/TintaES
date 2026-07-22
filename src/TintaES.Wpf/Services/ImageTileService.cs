using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

public sealed class ImageTileService
{
    private const int MaximumTileWidth = 2200;
    private const int MaximumTileHeight = 1300;
    private const int Overlap = 180;

    public IReadOnlyList<ComicImageTile> CreateTiles(BitmapSource source)
    {
        IReadOnlyList<(int Start, int Length)> horizontal = BuildRanges(source.PixelWidth, MaximumTileWidth, Overlap);
        IReadOnlyList<(int Start, int Length)> vertical = BuildRanges(source.PixelHeight, MaximumTileHeight, Overlap);
        var raw = new List<(int X, int Y, int Width, int Height, byte[] Bytes)>();

        foreach ((int y, int height) in vertical)
        {
            foreach ((int x, int width) in horizontal)
            {
                var cropped = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
                var encoder = new JpegBitmapEncoder { QualityLevel = 94 };
                encoder.Frames.Add(BitmapFrame.Create(cropped));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                raw.Add((x, y, width, height, stream.ToArray()));
            }
        }

        return raw.Select((tile, index) => new ComicImageTile(
            index + 1,
            raw.Count,
            tile.X,
            tile.Y,
            tile.Width,
            tile.Height,
            source.PixelWidth,
            source.PixelHeight,
            tile.Bytes)).ToList();
    }

    public static IReadOnlyList<(int Start, int Length)> BuildRanges(int totalLength, int maximumLength, int overlap)
    {
        if (totalLength <= maximumLength)
        {
            return [(0, totalLength)];
        }

        int effective = Math.Max(1, maximumLength - overlap);
        int count = Math.Max(2, (int)Math.Ceiling((totalLength - overlap) / (double)effective));
        double step = (totalLength - maximumLength) / (double)(count - 1);
        var ranges = new List<(int Start, int Length)>(count);
        for (int index = 0; index < count; index++)
        {
            int start = (int)Math.Round(index * step);
            start = Math.Clamp(start, 0, totalLength - maximumLength);
            if (ranges.Count == 0 || ranges[^1].Start != start)
            {
                ranges.Add((start, Math.Min(maximumLength, totalLength - start)));
            }
        }
        return ranges;
    }
}
