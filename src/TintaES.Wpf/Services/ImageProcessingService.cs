using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

public sealed class ImageProcessingService
{
    public BitmapSource CleanText(BitmapSource source, IEnumerable<ComicRegion> regions)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] original = new byte[stride * height];
        converted.CopyPixels(original, stride, 0);
        byte[] output = (byte[])original.Clone();

        foreach (ComicRegion region in regions.Where(region =>
                     region.IsEnabled
                     && region.CleanupMode is "solid" or "texture"))
        {
            PixelRect rect = ToPixelRect(region.TextBox, width, height).Expand(0.06, width, height);
            if (rect.Width < 2 || rect.Height < 2)
            {
                continue;
            }

            (byte R, byte G, byte B, double Variance) sampled = SampleRing(original, stride, width, height, rect);
            bool useSolid = region.CleanupMode == "solid";

            if (useSolid)
            {
                (byte R, byte G, byte B) color = sampled.Variance < 950 || !TryParseColor(region.Style.BackgroundColor, out var predicted)
                    ? (sampled.R, sampled.G, sampled.B)
                    : predicted;
                FillSolid(output, stride, rect, color);
            }
            else
            {
                FillTexture(original, output, stride, rect);
            }
        }

        BitmapSource result = BitmapSource.Create(
            width,
            height,
            source.DpiX > 0 ? source.DpiX : 96,
            source.DpiY > 0 ? source.DpiY : 96,
            PixelFormats.Bgra32,
            null,
            output,
            stride);
        result.Freeze();
        return result;
    }

    private static PixelRect ToPixelRect(NormalizedRect box, int width, int height)
    {
        int x = Math.Clamp((int)Math.Floor(box.X / 1000 * width), 0, width - 1);
        int y = Math.Clamp((int)Math.Floor(box.Y / 1000 * height), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(box.Right / 1000 * width), x + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(box.Bottom / 1000 * height), y + 1, height);
        return new PixelRect(x, y, right - x, bottom - y);
    }

    private static (byte R, byte G, byte B, double Variance) SampleRing(
        byte[] pixels,
        int stride,
        int imageWidth,
        int imageHeight,
        PixelRect rect)
    {
        int margin = Math.Max(3, (int)Math.Round(Math.Min(rect.Width, rect.Height) * 0.12));
        int left = Math.Max(0, rect.X - margin);
        int top = Math.Max(0, rect.Y - margin);
        int right = Math.Min(imageWidth - 1, rect.Right + margin);
        int bottom = Math.Min(imageHeight - 1, rect.Bottom + margin);
        int step = Math.Max(1, Math.Min(right - left, bottom - top) / 32);
        var samples = new List<(byte R, byte G, byte B)>();

        for (int x = left; x <= right; x += step)
        {
            samples.Add(ReadPixel(pixels, stride, x, top));
            samples.Add(ReadPixel(pixels, stride, x, bottom));
        }
        for (int y = top; y <= bottom; y += step)
        {
            samples.Add(ReadPixel(pixels, stride, left, y));
            samples.Add(ReadPixel(pixels, stride, right, y));
        }

        if (samples.Count == 0)
        {
            return (255, 255, 255, 0);
        }

        byte medianR = Median(samples.Select(sample => sample.R));
        byte medianG = Median(samples.Select(sample => sample.G));
        byte medianB = Median(samples.Select(sample => sample.B));
        double variance = samples.Average(sample =>
            (Math.Pow(sample.R - medianR, 2)
             + Math.Pow(sample.G - medianG, 2)
             + Math.Pow(sample.B - medianB, 2)) / 3);
        return (medianR, medianG, medianB, variance);
    }

    private static void FillSolid(byte[] output, int stride, PixelRect rect, (byte R, byte G, byte B) color)
    {
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
            {
                int index = y * stride + x * 4;
                output[index] = color.B;
                output[index + 1] = color.G;
                output[index + 2] = color.R;
                output[index + 3] = 255;
            }
        }
    }

    private static void FillTexture(byte[] original, byte[] output, int stride, PixelRect rect)
    {
        int maxX = stride / 4 - 1;
        int topY = Math.Max(0, rect.Y - 1);
        int bottomY = Math.Min(original.Length / stride - 1, rect.Bottom);
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
            {
                double[] distances = [y - rect.Y + 1, rect.Bottom - y, x - rect.X + 1, rect.Right - x];
                (int X, int Y)[] points =
                [
                    (x, topY),
                    (x, bottomY),
                    (Math.Max(0, rect.X - 1), y),
                    (Math.Min(maxX, rect.Right), y)
                ];
                double totalWeight = 0;
                double red = 0;
                double green = 0;
                double blue = 0;
                for (int sample = 0; sample < points.Length; sample++)
                {
                    double weight = 1 / Math.Pow(Math.Max(1, distances[sample]), 1.7);
                    (byte r, byte g, byte b) = ReadPixel(original, stride, points[sample].X, points[sample].Y);
                    totalWeight += weight;
                    red += r * weight;
                    green += g * weight;
                    blue += b * weight;
                }

                int index = y * stride + x * 4;
                output[index] = (byte)(blue / totalWeight);
                output[index + 1] = (byte)(green / totalWeight);
                output[index + 2] = (byte)(red / totalWeight);
                output[index + 3] = 255;
            }
        }
    }

    private static (byte R, byte G, byte B) ReadPixel(byte[] pixels, int stride, int x, int y)
    {
        int index = y * stride + x * 4;
        return (pixels[index + 2], pixels[index + 1], pixels[index]);
    }

    private static byte Median(IEnumerable<byte> values)
    {
        byte[] sorted = values.OrderBy(value => value).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static bool TryParseColor(string? value, out (byte R, byte G, byte B) color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 7
            || value[0] != '#'
            || !byte.TryParse(value.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out byte red)
            || !byte.TryParse(value.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out byte green)
            || !byte.TryParse(value.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out byte blue))
        {
            return false;
        }
        color = (red, green, blue);
        return true;
    }

    private sealed record PixelRect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;

        public PixelRect Expand(double ratio, int imageWidth, int imageHeight)
        {
            int padding = Math.Max(2, (int)Math.Round(Math.Min(Width, Height) * ratio));
            int left = Math.Max(0, X - padding);
            int top = Math.Max(0, Y - padding);
            int right = Math.Min(imageWidth, Right + padding);
            int bottom = Math.Min(imageHeight, Bottom + padding);
            return new PixelRect(left, top, right - left, bottom - top);
        }
    }
}
