using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

public sealed record DialogueOnlyResult(
    BitmapSource CleanedBitmap,
    BitmapSource MaskBitmap,
    IReadOnlyList<ComicRegion> Regions);

/// <summary>
/// El motor orgánico puede detectar/borrar letras que no queremos traducir todavía
/// (onomatopeyas, carteles o lecturas dudosas). Esta capa restaura esos píxeles desde
/// la imagen original y conserva únicamente el borrado asociado a regiones que parecen
/// pertenecer a un bocadillo. Así una lectura omitida nunca deja un agujero en blanco.
/// </summary>
public sealed class DialogueOnlyResultService
{
    public DialogueOnlyResult Build(
        BitmapSource original,
        BitmapSource cleaned,
        BitmapSource mask,
        IReadOnlyList<ComicRegion> regions,
        bool includeAllDetectedText = false)
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
            throw new InvalidOperationException("La imagen original, el fondo limpio y la máscara deben tener el mismo tamaño.");
        }

        int colorStride = width * 4;
        int maskStride = width;
        var originalPixels = new byte[colorStride * height];
        var cleanedPixels = new byte[colorStride * height];
        var maskPixels = new byte[maskStride * height];
        originalBgra.CopyPixels(originalPixels, colorStride, 0);
        cleanedBgra.CopyPixels(cleanedPixels, colorStride, 0);
        maskGray.CopyPixels(maskPixels, maskStride, 0);

        ComicRegion[] kept = regions
            .Where(region => includeAllDetectedText
                ? region.Confidence >= 0.30
                : IsSpeechBubbleCandidate(region, originalPixels, width, height))
            .ToArray();
        for (int index = 0; index < kept.Length; index++)
        {
            kept[index].Order = index + 1;
        }

        var allowed = new byte[width * height];
        foreach (ComicRegion region in kept)
        {
            // La máscara del motor dilata ligeramente los glifos; damos un pequeño margen
            // alrededor del bloque OCR. CleanupPolygon conserva el contorno orgánico
            // calculado antes de ampliar la superficie de rotulación al bocadillo completo:
            // el texto puede crecer, pero la reconstrucción nunca se vuelve rectangular.
            NormalizedRect box = region.TextBox.Expand(0.18, 0.24);
            int left = Math.Clamp((int)Math.Floor(box.X / 1000 * width), 0, width - 1);
            int top = Math.Clamp((int)Math.Floor(box.Y / 1000 * height), 0, height - 1);
            int right = Math.Clamp((int)Math.Ceiling(box.Right / 1000 * width), left + 1, width);
            int bottom = Math.Clamp((int)Math.Ceiling(box.Bottom / 1000 * height), top + 1, height);

            FillAllowedArea(
                allowed,
                width,
                height,
                left,
                top,
                right,
                bottom,
                region.CleanupPolygon);
        }

        var resultPixels = new byte[originalPixels.Length];
        var resultMask = new byte[maskPixels.Length];
        Buffer.BlockCopy(originalPixels, 0, resultPixels, 0, originalPixels.Length);

        for (int pixel = 0; pixel < maskPixels.Length; pixel++)
        {
            if (allowed[pixel] == 0 || maskPixels[pixel] == 0)
            {
                continue;
            }

            int color = pixel * 4;
            resultPixels[color] = cleanedPixels[color];
            resultPixels[color + 1] = cleanedPixels[color + 1];
            resultPixels[color + 2] = cleanedPixels[color + 2];
            resultPixels[color + 3] = cleanedPixels[color + 3];
            resultMask[pixel] = maskPixels[pixel];
        }

        // LaMa puede tomar el negro exterior de un bocadillo como referencia y producir
        // una mancha oscura al borrar una palabra situada cerca del borde. Cuando el
        // entorno inmediato es realmente papel blanco/crema uniforme, reconstruimos
        // únicamente los píxeles de la máscara con el color local robusto. No se pinta
        // ninguna caja: el relleno sigue limitado a los glifos y al contorno de limpieza.
        foreach (ComicRegion region in kept)
        {
            if (TryEstimateFlatLightBackground(
                    region,
                    originalPixels,
                    maskPixels,
                    width,
                    height,
                    out BgraColor background))
            {
                RepairFlatLightBackground(
                    region,
                    background,
                    originalPixels,
                    resultPixels,
                    resultMask,
                    maskPixels,
                    width,
                    height);
            }
        }

        BitmapSource result = BitmapSource.Create(
            width,
            height,
            originalBgra.DpiX,
            originalBgra.DpiY,
            PixelFormats.Bgra32,
            null,
            resultPixels,
            colorStride);
        result.Freeze();

        BitmapSource filteredMask = BitmapSource.Create(
            width,
            height,
            maskGray.DpiX,
            maskGray.DpiY,
            PixelFormats.Gray8,
            null,
            resultMask,
            maskStride);
        filteredMask.Freeze();

        return new DialogueOnlyResult(result, filteredMask, kept);
    }

    private static bool TryEstimateFlatLightBackground(
        ComicRegion region,
        byte[] originalPixels,
        byte[] maskPixels,
        int width,
        int height,
        out BgraColor background)
    {
        background = default;
        NormalizedRect sampleBox = region.TextBox.Expand(0.62, 0.90);
        int left = Math.Clamp((int)Math.Floor(sampleBox.X / 1000 * width), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(sampleBox.Y / 1000 * height), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(sampleBox.Right / 1000 * width), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(sampleBox.Bottom / 1000 * height), top + 1, height);

        int step = Math.Max(1, Math.Min(right - left, bottom - top) / 80);
        var blues = new List<byte>();
        var greens = new List<byte>();
        var reds = new List<byte>();
        var luminances = new List<int>();
        int unmaskedSamples = 0;
        for (int y = top; y < bottom; y += step)
        {
            for (int x = left; x < right; x += step)
            {
                int pixel = y * width + x;
                if (maskPixels[pixel] >= 32)
                {
                    continue;
                }

                unmaskedSamples++;
                int offset = pixel * 4;
                byte blue = originalPixels[offset];
                byte green = originalPixels[offset + 1];
                byte red = originalPixels[offset + 2];
                int luminance = (red * 3 + green * 6 + blue) / 10;
                int chroma = Math.Max(red, Math.Max(green, blue))
                    - Math.Min(red, Math.Min(green, blue));
                if (luminance < 178 || chroma > 48)
                {
                    continue;
                }

                blues.Add(blue);
                greens.Add(green);
                reds.Add(red);
                luminances.Add(luminance);
            }
        }

        if (unmaskedSamples < 24
            || luminances.Count < 20
            || luminances.Count / (double)unmaskedSamples < 0.62)
        {
            return false;
        }

        blues.Sort();
        greens.Sort();
        reds.Sort();
        luminances.Sort();
        byte medianBlue = blues[blues.Count / 2];
        byte medianGreen = greens[greens.Count / 2];
        byte medianRed = reds[reds.Count / 2];
        int medianLuminance = luminances[luminances.Count / 2];
        int p10 = luminances[(int)Math.Floor((luminances.Count - 1) * 0.10)];
        int p90 = luminances[(int)Math.Ceiling((luminances.Count - 1) * 0.90)];
        if (medianLuminance < 190 || p90 - p10 > 26)
        {
            return false;
        }

        background = new BgraColor(medianBlue, medianGreen, medianRed, 255);
        return true;
    }

    private static void RepairFlatLightBackground(
        ComicRegion region,
        BgraColor background,
        byte[] originalPixels,
        byte[] resultPixels,
        byte[] resultMask,
        byte[] maskPixels,
        int width,
        int height)
    {
        NormalizedRect repairBox = region.TextBox.Expand(0.24, 0.34);
        int left = Math.Clamp((int)Math.Floor(repairBox.X / 1000 * width), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(repairBox.Y / 1000 * height), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(repairBox.Right / 1000 * width), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(repairBox.Bottom / 1000 * height), top + 1, height);
        int textHeight = Math.Max(
            1,
            (int)Math.Round(region.TextBox.Height / 1000 * height));
        int dilationRadius = Math.Clamp((int)Math.Round(textHeight / 13.0), 2, 7);

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int pixel = y * width + x;
                byte mask = GetDilatedMask(
                    maskPixels,
                    width,
                    height,
                    x,
                    y,
                    dilationRadius);
                if (mask == 0)
                {
                    continue;
                }

                // La dilatación se usa solo después de demostrar que el entorno es plano
                // y claro. Así elimina halos de antialias y letras que sobresalen uno o dos
                // píxeles del OCR sin acercarse al borde orgánico del bocadillo.
                double coverage = Math.Min(1, mask / 96.0);
                int offset = pixel * 4;
                resultPixels[offset] = Blend(originalPixels[offset], background.Blue, coverage);
                resultPixels[offset + 1] = Blend(originalPixels[offset + 1], background.Green, coverage);
                resultPixels[offset + 2] = Blend(originalPixels[offset + 2], background.Red, coverage);
                resultPixels[offset + 3] = 255;
                resultMask[pixel] = mask;
            }
        }
    }

    private static byte GetDilatedMask(
        byte[] maskPixels,
        int width,
        int height,
        int x,
        int y,
        int radius)
    {
        byte strongest = 0;
        int minY = Math.Max(0, y - radius);
        int maxY = Math.Min(height - 1, y + radius);
        int minX = Math.Max(0, x - radius);
        int maxX = Math.Min(width - 1, x + radius);
        int radiusSquared = radius * radius;
        for (int sampleY = minY; sampleY <= maxY; sampleY++)
        {
            int deltaY = sampleY - y;
            for (int sampleX = minX; sampleX <= maxX; sampleX++)
            {
                int deltaX = sampleX - x;
                if (deltaX * deltaX + deltaY * deltaY > radiusSquared)
                {
                    continue;
                }

                strongest = Math.Max(strongest, maskPixels[sampleY * width + sampleX]);
                if (strongest == byte.MaxValue)
                {
                    return strongest;
                }
            }
        }
        return strongest;
    }

    private static byte Blend(byte original, byte replacement, double coverage) =>
        (byte)Math.Clamp(
            (int)Math.Round(original + (replacement - original) * coverage),
            byte.MinValue,
            byte.MaxValue);

    private readonly record struct BgraColor(byte Blue, byte Green, byte Red, byte Alpha);

    private static void FillAllowedArea(
        byte[] allowed,
        int width,
        int height,
        int left,
        int top,
        int right,
        int bottom,
        IReadOnlyList<NormalizedPoint> cleanupPolygon)
    {
        if (cleanupPolygon.Count < 3)
        {
            for (int y = top; y < bottom; y++)
            {
                Array.Fill(allowed, (byte)1, y * width + left, right - left);
            }
            return;
        }

        (double X, double Y)[] polygon = cleanupPolygon
            .Select(point => (
                point.X / 1000 * width,
                point.Y / 1000 * height))
            .ToArray();

        // El motor erosiona previamente este contorno para separarlo del borde negro.
        // Admitimos solo dos píxeles de antialias alrededor de la forma: suficiente para
        // cubrir el halo de las letras sin atravesar la línea exterior del bocadillo.
        const double edgeTolerance = 2.0;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                if (ContainsOrTouches(polygon, x + 0.5, y + 0.5, edgeTolerance))
                {
                    allowed[y * width + x] = 1;
                }
            }
        }
    }

    private static bool ContainsOrTouches(
        IReadOnlyList<(double X, double Y)> polygon,
        double x,
        double y,
        double tolerance)
    {
        bool inside = false;
        int previous = polygon.Count - 1;
        double toleranceSquared = tolerance * tolerance;
        for (int current = 0; current < polygon.Count; current++)
        {
            (double X, double Y) first = polygon[previous];
            (double X, double Y) second = polygon[current];
            if (DistanceToSegmentSquared(x, y, first, second) <= toleranceSquared)
            {
                return true;
            }

            bool crosses = (second.Y > y) != (first.Y > y)
                && x < (first.X - second.X) * (y - second.Y)
                    / (first.Y - second.Y) + second.X;
            if (crosses)
            {
                inside = !inside;
            }
            previous = current;
        }
        return inside;
    }

    private static double DistanceToSegmentSquared(
        double x,
        double y,
        (double X, double Y) first,
        (double X, double Y) second)
    {
        double deltaX = second.X - first.X;
        double deltaY = second.Y - first.Y;
        double lengthSquared = deltaX * deltaX + deltaY * deltaY;
        if (lengthSquared <= double.Epsilon)
        {
            double pointX = x - first.X;
            double pointY = y - first.Y;
            return pointX * pointX + pointY * pointY;
        }

        double projection = Math.Clamp(
            ((x - first.X) * deltaX + (y - first.Y) * deltaY) / lengthSquared,
            0,
            1);
        double nearestX = first.X + projection * deltaX;
        double nearestY = first.Y + projection * deltaY;
        double distanceX = x - nearestX;
        double distanceY = y - nearestY;
        return distanceX * distanceX + distanceY * distanceY;
    }

    private static bool IsSpeechBubbleCandidate(
        ComicRegion region,
        byte[] originalPixels,
        int width,
        int height)
    {
        if (region.Type is "dialogue" or "thought")
        {
            return true;
        }

        if (region.Type != "sfx")
        {
            return false;
        }

        // Una exclamación corta como VICTORY! puede ser etiquetada como sfx aunque esté
        // dentro de un globo. El motor orgánico ya calcula bubbleConfidence; una señal baja
        // pero positiva es suficiente si la geometría también parece razonable.
        if (region.BubbleConfidence >= 0.035)
        {
            return true;
        }

        if (region.SafePolygon.Count >= 3
            && region.RenderBox.Area >= region.TextBox.Area * 1.025)
        {
            return true;
        }

        // Último rescate para el caso que antes quedaba vacío: si el OCR lo llamó SFX pero
        // el bloque está rodeado mayoritariamente por papel blanco/crema y neutro, es mucho
        // más probable que sea texto dentro de un bocadillo que una onomatopeya sobre dibujo.
        // El criterio es deliberadamente conservador para no empezar a traducir efectos
        // sonoros exteriores sobre fondos de color.
        return region.Confidence >= 0.30
            && LooksLikeLightBubbleInterior(region.TextBox, originalPixels, width, height);
    }

    private static bool LooksLikeLightBubbleInterior(
        NormalizedRect textBox,
        byte[] pixels,
        int width,
        int height)
    {
        NormalizedRect sampleBox = textBox.Expand(0.55, 0.70);
        int left = Math.Clamp((int)Math.Floor(sampleBox.X / 1000 * width), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(sampleBox.Y / 1000 * height), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(sampleBox.Right / 1000 * width), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(sampleBox.Bottom / 1000 * height), top + 1, height);

        int step = Math.Max(1, Math.Min(right - left, bottom - top) / 70);
        int total = 0;
        int bright = 0;
        int brightNeutral = 0;
        for (int y = top; y < bottom; y += step)
        {
            for (int x = left; x < right; x += step)
            {
                int offset = (y * width + x) * 4;
                int blue = pixels[offset];
                int green = pixels[offset + 1];
                int red = pixels[offset + 2];
                int luminance = (red * 3 + green * 6 + blue) / 10;
                int chroma = Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
                total++;
                if (luminance >= 178)
                {
                    bright++;
                    if (chroma <= 46)
                    {
                        brightNeutral++;
                    }
                }
            }
        }

        if (total == 0)
        {
            return false;
        }

        double brightRatio = bright / (double)total;
        double neutralRatio = brightNeutral / (double)total;
        return brightRatio >= 0.70 && neutralRatio >= 0.58;
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
}
