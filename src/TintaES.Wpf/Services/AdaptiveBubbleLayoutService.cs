using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

/// <summary>
/// Detecta el interior útil de los bocadillos sobre la página ya limpia.
/// La máscara usada para borrar las letras originales y el área disponible
/// para la nueva rotulación son conceptos independientes.
/// </summary>
public sealed class AdaptiveBubbleLayoutService
{
    public bool Refine(BitmapSource cleanPage, IEnumerable<ComicRegion> regions)
    {
        BitmapSource converted = cleanPage.Format == PixelFormats.Bgra32
            ? cleanPage
            : new FormatConvertedBitmap(cleanPage, PixelFormats.Bgra32, null, 0);

        int pageWidth = converted.PixelWidth;
        int pageHeight = converted.PixelHeight;
        int stride = pageWidth * 4;
        byte[] pixels = new byte[stride * pageHeight];
        converted.CopyPixels(pixels, stride, 0);

        bool changed = false;
        foreach (ComicRegion region in regions)
        {
            if (!region.IsEnabled || region.Type is not ("dialogue" or "thought"))
            {
                continue;
            }

            if (TryDetectBubble(pixels, stride, pageWidth, pageHeight, region, out BubbleShape? shape))
            {
                region.SafePolygon = shape!.Polygon;
                region.RenderBox = shape.Bounds;
                changed = true;
                continue;
            }

            // Si no se detecta un bocadillo fiable, nunca dejamos una forma gigantesca
            // heredada de un análisis anterior. Se usa una zona conservadora alrededor
            // del texto original y el renderizador reducirá la fuente hasta que quepa.
            if (IsSuspicious(region.SafePolygon, region.TextBox))
            {
                NormalizedRect fallback = region.TextBox.Expand(0.70, 0.60);
                region.RenderBox = fallback;
                region.SafePolygon = CreateEllipse(fallback);
                changed = true;
            }
        }

        return changed;
    }

    private static bool TryDetectBubble(
        byte[] pixels,
        int stride,
        int pageWidth,
        int pageHeight,
        ComicRegion region,
        out BubbleShape? result)
    {
        result = null;
        PixelRect text = ToPixelRect(region.TextBox, pageWidth, pageHeight);
        if (text.Width < 3 || text.Height < 3)
        {
            return false;
        }

        int padX = Math.Max(48, (int)Math.Round(text.Width * 2.4));
        int padY = Math.Max(40, (int)Math.Round(text.Height * 2.1));
        PixelRect crop = text.Expand(padX, padY, pageWidth, pageHeight);
        if (crop.Width < 8 || crop.Height < 8)
        {
            return false;
        }

        Rgb target = SampleInteriorColor(pixels, stride, text);
        int seedX = text.X + text.Width / 2;
        int seedY = text.Y + text.Height / 2;
        if (!FindSeed(pixels, stride, crop, target, seedX, seedY, out seedX, out seedY))
        {
            return false;
        }

        int localWidth = crop.Width;
        int localHeight = crop.Height;
        bool[] visited = new bool[localWidth * localHeight];
        bool[] component = new bool[localWidth * localHeight];
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((seedX, seedY));

        int count = 0;
        int minX = seedX;
        int maxX = seedX;
        int minY = seedY;
        int maxY = seedY;
        int boundaryHits = 0;

        while (queue.Count > 0)
        {
            (int x, int y) = queue.Dequeue();
            int lx = x - crop.X;
            int ly = y - crop.Y;
            if (lx < 0 || ly < 0 || lx >= localWidth || ly >= localHeight)
            {
                continue;
            }

            int localIndex = ly * localWidth + lx;
            if (visited[localIndex])
            {
                continue;
            }
            visited[localIndex] = true;

            if (!IsSimilar(ReadPixel(pixels, stride, x, y), target))
            {
                continue;
            }

            component[localIndex] = true;
            count++;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
            if (lx == 0 || ly == 0 || lx == localWidth - 1 || ly == localHeight - 1)
            {
                boundaryHits++;
            }

            queue.Enqueue((x - 1, y));
            queue.Enqueue((x + 1, y));
            queue.Enqueue((x, y - 1));
            queue.Enqueue((x, y + 1));
        }

        int textArea = Math.Max(1, text.Width * text.Height);
        int componentWidth = maxX - minX + 1;
        int componentHeight = maxY - minY + 1;
        if (count < textArea * 0.75
            || count > textArea * 24
            || componentWidth > Math.Max(text.Width * 5.2, 520)
            || componentHeight > Math.Max(text.Height * 5.0, 420)
            || boundaryHits > Math.Max(8, count / 80))
        {
            return false;
        }

        int[] left = Enumerable.Repeat(int.MaxValue, localHeight).ToArray();
        int[] right = Enumerable.Repeat(int.MinValue, localHeight).ToArray();
        var widths = new List<int>();
        for (int ly = 0; ly < localHeight; ly++)
        {
            for (int lx = 0; lx < localWidth; lx++)
            {
                if (!component[ly * localWidth + lx])
                {
                    continue;
                }
                left[ly] = Math.Min(left[ly], lx);
                right[ly] = Math.Max(right[ly], lx);
            }
            if (right[ly] >= left[ly])
            {
                widths.Add(right[ly] - left[ly] + 1);
            }
        }

        if (widths.Count < 3)
        {
            return false;
        }

        widths.Sort();
        int medianWidth = widths[widths.Count / 2];
        int minimumBodyWidth = Math.Max((int)Math.Round(text.Width * 0.78), (int)Math.Round(medianWidth * 0.42));
        int centerRow = Math.Clamp(seedY - crop.Y, 0, localHeight - 1);

        // Elimina la cola del bocadillo y cualquier pasillo estrecho: conservamos el
        // cuerpo principal, es decir, la secuencia de filas anchas que contiene el texto.
        int top = centerRow;
        int bottom = centerRow;
        while (top > 0 && RowIsBody(left, right, top - 1, minimumBodyWidth))
        {
            top--;
        }
        while (bottom + 1 < localHeight && RowIsBody(left, right, bottom + 1, minimumBodyWidth))
        {
            bottom++;
        }

        int marginX = Math.Max(3, (int)Math.Round(Math.Min(text.Width, text.Height) * 0.07));
        int marginY = Math.Max(2, (int)Math.Round(Math.Min(text.Width, text.Height) * 0.045));
        top += marginY;
        bottom -= marginY;
        if (bottom - top < Math.Max(4, text.Height * 0.65))
        {
            return false;
        }

        var leftEdge = new List<NormalizedPoint>();
        var rightEdge = new List<NormalizedPoint>();
        int step = Math.Max(1, (bottom - top + 1) / 24);
        for (int ly = top; ly <= bottom; ly += step)
        {
            if (!RowIsBody(left, right, ly, minimumBodyWidth))
            {
                continue;
            }

            int xLeft = crop.X + left[ly] + marginX;
            int xRight = crop.X + right[ly] - marginX;
            int y = crop.Y + ly;
            if (xRight - xLeft < Math.Max(4, text.Width * 0.55))
            {
                continue;
            }

            leftEdge.Add(ToNormalizedPoint(xLeft, y, pageWidth, pageHeight));
            rightEdge.Add(ToNormalizedPoint(xRight, y, pageWidth, pageHeight));
        }

        if (leftEdge.Count < 3 || rightEdge.Count < 3)
        {
            return false;
        }

        var polygon = leftEdge.Concat(rightEdge.AsEnumerable().Reverse()).ToArray();
        NormalizedRect bounds = BoundsFromPolygon(polygon).Clamp();
        double areaRatio = bounds.Area / Math.Max(1, region.TextBox.Area);
        if (areaRatio < 0.65 || areaRatio > 16
            || bounds.Width > region.TextBox.Width * 4.5
            || bounds.Height > region.TextBox.Height * 4.2)
        {
            return false;
        }

        result = new BubbleShape(bounds, polygon);
        return true;
    }

    private static bool RowIsBody(int[] left, int[] right, int row, int minimumWidth) =>
        row >= 0
        && row < left.Length
        && right[row] >= left[row]
        && right[row] - left[row] + 1 >= minimumWidth;

    private static bool FindSeed(
        byte[] pixels,
        int stride,
        PixelRect crop,
        Rgb target,
        int preferredX,
        int preferredY,
        out int seedX,
        out int seedY)
    {
        if (crop.Contains(preferredX, preferredY)
            && IsSimilar(ReadPixel(pixels, stride, preferredX, preferredY), target))
        {
            seedX = preferredX;
            seedY = preferredY;
            return true;
        }

        for (int radius = 1; radius <= Math.Min(30, Math.Max(crop.Width, crop.Height)); radius++)
        {
            for (int y = Math.Max(crop.Y, preferredY - radius); y <= Math.Min(crop.Bottom - 1, preferredY + radius); y++)
            {
                for (int x = Math.Max(crop.X, preferredX - radius); x <= Math.Min(crop.Right - 1, preferredX + radius); x++)
                {
                    if ((Math.Abs(x - preferredX) != radius && Math.Abs(y - preferredY) != radius)
                        || !IsSimilar(ReadPixel(pixels, stride, x, y), target))
                    {
                        continue;
                    }
                    seedX = x;
                    seedY = y;
                    return true;
                }
            }
        }

        seedX = seedY = 0;
        return false;
    }

    private static Rgb SampleInteriorColor(byte[] pixels, int stride, PixelRect text)
    {
        var reds = new List<byte>();
        var greens = new List<byte>();
        var blues = new List<byte>();
        int insetX = Math.Max(1, text.Width / 5);
        int insetY = Math.Max(1, text.Height / 5);
        int stepX = Math.Max(1, text.Width / 12);
        int stepY = Math.Max(1, text.Height / 12);
        for (int y = text.Y + insetY; y < text.Bottom - insetY; y += stepY)
        {
            for (int x = text.X + insetX; x < text.Right - insetX; x += stepX)
            {
                Rgb pixel = ReadPixel(pixels, stride, x, y);
                reds.Add(pixel.R);
                greens.Add(pixel.G);
                blues.Add(pixel.B);
            }
        }

        if (reds.Count == 0)
        {
            return ReadPixel(pixels, stride, text.X + text.Width / 2, text.Y + text.Height / 2);
        }

        reds.Sort();
        greens.Sort();
        blues.Sort();
        int middle = reds.Count / 2;
        return new Rgb(reds[middle], greens[middle], blues[middle]);
    }

    private static bool IsSimilar(Rgb pixel, Rgb target)
    {
        int dr = pixel.R - target.R;
        int dg = pixel.G - target.G;
        int db = pixel.B - target.B;
        return dr * dr + dg * dg + db * db <= 46 * 46 * 3;
    }

    private static bool IsSuspicious(IReadOnlyList<NormalizedPoint> polygon, NormalizedRect textBox)
    {
        if (polygon.Count < 3)
        {
            return true;
        }

        NormalizedRect bounds = BoundsFromPolygon(polygon);
        return bounds.Area > textBox.Area * 12
            || bounds.Width > textBox.Width * 4.2
            || bounds.Height > textBox.Height * 4.0;
    }

    private static IReadOnlyList<NormalizedPoint> CreateEllipse(NormalizedRect box)
    {
        const int points = 40;
        double cx = box.X + box.Width / 2;
        double cy = box.Y + box.Height / 2;
        double rx = box.Width / 2;
        double ry = box.Height / 2;
        return Enumerable.Range(0, points)
            .Select(index =>
            {
                double angle = Math.PI * 2 * index / points;
                return new NormalizedPoint(cx + Math.Cos(angle) * rx, cy + Math.Sin(angle) * ry);
            })
            .ToArray();
    }

    private static NormalizedRect BoundsFromPolygon(IReadOnlyList<NormalizedPoint> polygon)
    {
        double left = polygon.Min(point => point.X);
        double top = polygon.Min(point => point.Y);
        double right = polygon.Max(point => point.X);
        double bottom = polygon.Max(point => point.Y);
        return new NormalizedRect(left, top, Math.Max(5, right - left), Math.Max(5, bottom - top));
    }

    private static PixelRect ToPixelRect(NormalizedRect box, int width, int height)
    {
        int x = Math.Clamp((int)Math.Floor(box.X / 1000 * width), 0, width - 1);
        int y = Math.Clamp((int)Math.Floor(box.Y / 1000 * height), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(box.Right / 1000 * width), x + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(box.Bottom / 1000 * height), y + 1, height);
        return new PixelRect(x, y, right - x, bottom - y);
    }

    private static NormalizedPoint ToNormalizedPoint(int x, int y, int width, int height) =>
        new(
            Math.Clamp(x / (double)width * 1000, 0, 1000),
            Math.Clamp(y / (double)height * 1000, 0, 1000));

    private static Rgb ReadPixel(byte[] pixels, int stride, int x, int y)
    {
        int index = y * stride + x * 4;
        return new Rgb(pixels[index + 2], pixels[index + 1], pixels[index]);
    }

    private sealed record BubbleShape(NormalizedRect Bounds, IReadOnlyList<NormalizedPoint> Polygon);
    private readonly record struct Rgb(byte R, byte G, byte B);

    private readonly record struct PixelRect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;

        public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

        public PixelRect Expand(int padX, int padY, int imageWidth, int imageHeight)
        {
            int left = Math.Max(0, X - padX);
            int top = Math.Max(0, Y - padY);
            int right = Math.Min(imageWidth, Right + padX);
            int bottom = Math.Min(imageHeight, Bottom + padY);
            return new PixelRect(left, top, right - left, bottom - top);
        }
    }
}
