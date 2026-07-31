using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

/// <summary>
/// Corrige fondos planos que el inpainting haya reconstruido con un color incorrecto. Es habitual
/// en cartuchos negros con letras blancas: el modelo puede devolver blanco porque alrededor hay
/// papel claro. La reparación estima el color dominante del propio cartucho en la imagen original
/// y sustituye únicamente los píxeles incluidos en la máscara de borrado.
/// </summary>
public sealed class FlatRegionBackgroundRepairService
{
    public BitmapSource Repair(
        BitmapSource original,
        BitmapSource cleaned,
        BitmapSource mask,
        IReadOnlyList<ComicRegion> regions)
    {
        BitmapSource originalBgra = ConvertTo(original, PixelFormats.Bgra32);
        BitmapSource cleanedBgra = ConvertTo(cleaned, PixelFormats.Bgra32);
        BitmapSource maskGray = ConvertTo(mask, PixelFormats.Gray8);

        int width = originalBgra.PixelWidth;
        int height = originalBgra.PixelHeight;
        if (cleanedBgra.PixelWidth != width
            || cleanedBgra.PixelHeight != height
            || maskGray.PixelWidth != width
            || maskGray.PixelHeight != height)
        {
            return cleaned;
        }

        int colorStride = width * 4;
        int maskStride = width;
        var originalPixels = new byte[colorStride * height];
        var outputPixels = new byte[colorStride * height];
        var maskPixels = new byte[maskStride * height];
        originalBgra.CopyPixels(originalPixels, colorStride, 0);
        cleanedBgra.CopyPixels(outputPixels, colorStride, 0);
        maskGray.CopyPixels(maskPixels, maskStride, 0);

        bool changed = false;
        foreach (ComicRegion region in regions.Where(region => region.IsEnabled))
        {
            if (!TryEstimateFlatBackground(
                    region,
                    originalPixels,
                    maskPixels,
                    width,
                    height,
                    out BgraColor background))
            {
                continue;
            }

            changed |= RestoreMaskedBackground(
                region,
                background,
                outputPixels,
                maskPixels,
                width,
                height);
        }

        if (!changed)
        {
            return cleaned;
        }

        BitmapSource result = BitmapSource.Create(
            width,
            height,
            cleanedBgra.DpiX > 0 ? cleanedBgra.DpiX : 96,
            cleanedBgra.DpiY > 0 ? cleanedBgra.DpiY : 96,
            PixelFormats.Bgra32,
            null,
            outputPixels,
            colorStride);
        result.Freeze();
        return result;
    }

    private static bool TryEstimateFlatBackground(
        ComicRegion region,
        byte[] originalPixels,
        byte[] maskPixels,
        int width,
        int height,
        out BgraColor background)
    {
        background = default;
        NormalizedRect sampleRegion = (region.BubbleBox ?? region.RenderBox).Clamp();
        PixelRect sample = ToPixelRect(sampleRegion, width, height);
        if (sample.Width < 5 || sample.Height < 5)
        {
            return false;
        }

        int step = Math.Max(1, Math.Min(sample.Width, sample.Height) / 75);
        var colors = new List<BgraColor>();
        for (int y = sample.Top; y < sample.Bottom; y += step)
        {
            for (int x = sample.Left; x < sample.Right; x += step)
            {
                int pixel = y * width + x;
                if (maskPixels[pixel] >= 48)
                {
                    continue;
                }

                int offset = pixel * 4;
                colors.Add(new BgraColor(
                    originalPixels[offset],
                    originalPixels[offset + 1],
                    originalPixels[offset + 2],
                    255));
            }
        }

        // Algunas máscaras defectuosas cubren el cartucho completo. En ese caso tomamos una
        // muestra del original sin consultar la máscara; el color dominante sigue venciendo a las
        // letras porque estas ocupan una fracción pequeña del área.
        if (colors.Count < 24)
        {
            colors.Clear();
            for (int y = sample.Top; y < sample.Bottom; y += step)
            {
                for (int x = sample.Left; x < sample.Right; x += step)
                {
                    int offset = (y * width + x) * 4;
                    colors.Add(new BgraColor(
                        originalPixels[offset],
                        originalPixels[offset + 1],
                        originalPixels[offset + 2],
                        255));
                }
            }
        }

        if (colors.Count < 24)
        {
            return false;
        }

        byte medianBlue = Median(colors.Select(color => color.Blue));
        byte medianGreen = Median(colors.Select(color => color.Green));
        byte medianRed = Median(colors.Select(color => color.Red));
        var median = new BgraColor(medianBlue, medianGreen, medianRed, 255);

        int close = 0;
        var distances = new List<int>(colors.Count);
        foreach (BgraColor color in colors)
        {
            int distance = Math.Max(
                Math.Abs(color.Red - median.Red),
                Math.Max(
                    Math.Abs(color.Green - median.Green),
                    Math.Abs(color.Blue - median.Blue)));
            distances.Add(distance);
            if (distance <= 28)
            {
                close++;
            }
        }

        distances.Sort();
        int p80 = distances[(int)Math.Floor((distances.Count - 1) * 0.80)];
        double dominantRatio = close / (double)colors.Count;
        if (dominantRatio < 0.62 || p80 > 42)
        {
            return false;
        }

        background = median;
        return true;
    }

    private static bool RestoreMaskedBackground(
        ComicRegion region,
        BgraColor background,
        byte[] outputPixels,
        byte[] maskPixels,
        int width,
        int height)
    {
        NormalizedRect repairRegion = (region.BubbleBox ?? region.RenderBox).Clamp();
        PixelRect repair = ToPixelRect(repairRegion, width, height);
        bool changed = false;

        for (int y = repair.Top; y < repair.Bottom; y++)
        {
            for (int x = repair.Left; x < repair.Right; x++)
            {
                int pixel = y * width + x;
                byte mask = maskPixels[pixel];
                if (mask == 0)
                {
                    continue;
                }

                double coverage = Math.Min(1, mask / 72d);
                int offset = pixel * 4;
                outputPixels[offset] = Blend(outputPixels[offset], background.Blue, coverage);
                outputPixels[offset + 1] = Blend(outputPixels[offset + 1], background.Green, coverage);
                outputPixels[offset + 2] = Blend(outputPixels[offset + 2], background.Red, coverage);
                outputPixels[offset + 3] = 255;
                changed = true;
            }
        }

        return changed;
    }

    private static BitmapSource ConvertTo(BitmapSource source, PixelFormat format)
    {
        if (source.Format == format)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(source, format, null, 0);
        converted.Freeze();
        return converted;
    }

    private static PixelRect ToPixelRect(NormalizedRect box, int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(box.X / 1000 * width), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(box.Y / 1000 * height), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(box.Right / 1000 * width), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(box.Bottom / 1000 * height), top + 1, height);
        return new PixelRect(left, top, right, bottom);
    }

    private static byte Median(IEnumerable<byte> values)
    {
        byte[] sorted = values.OrderBy(value => value).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static byte Blend(byte current, byte target, double coverage) =>
        (byte)Math.Clamp(
            (int)Math.Round(current + (target - current) * coverage),
            byte.MinValue,
            byte.MaxValue);

    private readonly record struct BgraColor(byte Blue, byte Green, byte Red, byte Alpha);
    private readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}
