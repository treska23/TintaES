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
            throw new InvalidOperationException("La imagen original, el fondo limpio y la máscara deben tener el mismo tamaño.");
        }

        ComicRegion[] kept = regions
            .Where(IsSpeechBubbleCandidate)
            .ToArray();
        for (int index = 0; index < kept.Length; index++)
        {
            kept[index].Order = index + 1;
        }

        int colorStride = width * 4;
        int maskStride = width;
        var originalPixels = new byte[colorStride * height];
        var cleanedPixels = new byte[colorStride * height];
        var maskPixels = new byte[maskStride * height];
        originalBgra.CopyPixels(originalPixels, colorStride, 0);
        cleanedBgra.CopyPixels(cleanedPixels, colorStride, 0);
        maskGray.CopyPixels(maskPixels, maskStride, 0);

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

    private static bool IsSpeechBubbleCandidate(ComicRegion region)
    {
        if (region.Type is "dialogue" or "thought")
        {
            return true;
        }

        // El motor a veces etiqueta una exclamación corta dentro de un bocadillo como sfx.
        // Si además detectó una silueta útil claramente mayor que las letras, la tratamos
        // como bocadillo. Una onomatopeya sobre el dibujo suele caer al rectángulo del OCR.
        return region.Type == "sfx"
            && region.SafePolygon.Count >= 3
            && region.RenderBox.Area >= region.TextBox.Area * 1.12;
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
