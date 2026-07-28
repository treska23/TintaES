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
            // alrededor del bloque OCR, pero nunca usamos RenderBox para borrar el bocadillo.
            NormalizedRect box = region.TextBox.Expand(0.18, 0.24);
            int left = Math.Clamp((int)Math.Floor(box.X / 1000 * width), 0, width - 1);
            int top = Math.Clamp((int)Math.Floor(box.Y / 1000 * height), 0, height - 1);
            int right = Math.Clamp((int)Math.Ceiling(box.Right / 1000 * width), left + 1, width);
            int bottom = Math.Clamp((int)Math.Ceiling(box.Bottom / 1000 * height), top + 1, height);

            for (int y = top; y < bottom; y++)
            {
                Array.Fill(allowed, (byte)1, y * width + left, right - left);
            }
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
