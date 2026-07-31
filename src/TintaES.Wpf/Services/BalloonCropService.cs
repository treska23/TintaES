using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

/// <summary>
/// Extrae el interior de cada bocadillo como una capa local enmascarada. BubbleBox solo delimita
/// dónde buscar el borde; nunca se utiliza directamente como superficie de escritura.
/// </summary>
public sealed class BalloonCropService
{
    private readonly ConditionalWeakTable<BitmapSource, PagePixels> _cache = new();

    public BalloonCrop Create(BitmapSource cleanedPage, ComicRegion region)
    {
        PagePixels page = _cache.GetValue(cleanedPage, ReadPage);
        if (!region.IsManual && TryFlood(page, region, out BalloonCrop? crop))
        {
            return crop!;
        }
        if (region.SafePolygon.Count >= 3 && TryPolygon(page.Width, page.Height, region.SafePolygon, out crop))
        {
            return crop!;
        }
        return Fallback(page.Width, page.Height, region);
    }

    private static PagePixels ReadPage(BitmapSource source)
    {
        BitmapSource image = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = image.PixelWidth * 4;
        byte[] pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return new PagePixels(image.PixelWidth, image.PixelHeight, stride, pixels);
    }

    private static bool TryFlood(PagePixels page, ComicRegion region, out BalloonCrop? crop)
    {
        crop = null;
        PixelRect text = Pixels(region.TextBox, page.Width, page.Height);
        PixelRect search = SearchRect(region, text, page.Width, page.Height);
        if (search.Width < 12 || search.Height < 12)
        {
            return false;
        }

        ColorSample reference = MedianBackground(page, text);
        int tolerance = Tolerance(page, text, reference);
        bool[] mask = new bool[search.Width * search.Height];
        Queue<int> queue = new();
        foreach ((int x, int y) in Seeds(text))
        {
            int lx = x - search.X;
            int ly = y - search.Y;
            if (lx < 0 || ly < 0 || lx >= search.Width || ly >= search.Height)
            {
                continue;
            }
            int index = ly * search.Width + lx;
            if (!mask[index] && Similar(page.At(x, y), reference, tolerance))
            {
                mask[index] = true;
                queue.Enqueue(index);
            }
        }
        if (queue.Count == 0)
        {
            return false;
        }

        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            int x = index % search.Width;
            int y = index / search.Width;
            Visit(x - 1, y);
            Visit(x + 1, y);
            Visit(x, y - 1);
            Visit(x, y + 1);

            void Visit(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= search.Width || ny >= search.Height)
                {
                    return;
                }
                int next = ny * search.Width + nx;
                if (mask[next] || !Similar(page.At(search.X + nx, search.Y + ny), reference, tolerance))
                {
                    return;
                }
                mask[next] = true;
                queue.Enqueue(next);
            }
        }

        int area = mask.Count(value => value);
        int searchArea = search.Width * search.Height;
        int boundary = BoundaryTouches(mask, search.Width, search.Height);
        if (area < Math.Max(40, text.Width * text.Height * 0.65)
            || area > searchArea * 0.93
            || boundary > Math.Max(8, (search.Width + search.Height) / 16))
        {
            return false;
        }

        FillHoles(mask, search.Width, search.Height);
        Erode(mask, search.Width, search.Height, Math.Clamp(Math.Min(search.Width, search.Height) / 90, 2, 7));
        if (!MaskBounds(mask, search.Width, search.Height, out PixelRect local))
        {
            return false;
        }

        PixelRect pageBounds = new PixelRect(
            search.X + local.X,
            search.Y + local.Y,
            local.Width,
            local.Height).Expand(3, page.Width, page.Height);
        crop = Build(mask, search, pageBounds, "lazo automático");
        return crop.LayoutPolygon.Count >= 3;
    }

    private static PixelRect SearchRect(ComicRegion region, PixelRect text, int pageWidth, int pageHeight)
    {
        NormalizedRect hint = region.TextBox.Expand(2.0, 2.2);
        if (region.BubbleBox is { } bubble
            && ContainsCenter(bubble, region.TextBox)
            && bubble.Area >= region.TextBox.Area * 1.05
            && bubble.Area <= region.TextBox.Area * 36
            && bubble.Width <= region.TextBox.Width * 7
            && bubble.Height <= region.TextBox.Height * 7)
        {
            hint = bubble.Expand(0.18, 0.20);
        }
        else if (region.SafePolygon.Count >= 3)
        {
            NormalizedRect polygon = PolygonBounds(region.SafePolygon);
            if (polygon.Area >= region.TextBox.Area * 0.8 && polygon.Area <= region.TextBox.Area * 32)
            {
                hint = polygon.Expand(0.22, 0.24);
            }
        }

        PixelRect result = Pixels(hint, pageWidth, pageHeight);
        int maximumWidth = Math.Min(pageWidth, Math.Max(text.Width * 8, (int)(pageWidth * 0.48)));
        int maximumHeight = Math.Min(pageHeight, Math.Max(text.Height * 8, (int)(pageHeight * 0.42)));
        if (result.Width > maximumWidth || result.Height > maximumHeight)
        {
            result = PixelRect.Centered(
                text.X + text.Width / 2,
                text.Y + text.Height / 2,
                Math.Min(result.Width, maximumWidth),
                Math.Min(result.Height, maximumHeight),
                pageWidth,
                pageHeight);
        }
        return result.Expand(Math.Max(4, text.Width / 10), pageWidth, pageHeight);
    }

    private static bool ContainsCenter(NormalizedRect outer, NormalizedRect inner)
    {
        double x = inner.X + inner.Width / 2;
        double y = inner.Y + inner.Height / 2;
        return x >= outer.X && x <= outer.Right && y >= outer.Y && y <= outer.Bottom;
    }

    private static IEnumerable<(int X, int Y)> Seeds(PixelRect text)
    {
        foreach (double y in new[] { 0.30, 0.50, 0.70 })
        {
            foreach (double x in new[] { 0.25, 0.50, 0.75 })
            {
                yield return (
                    text.X + (int)Math.Round(text.Width * x),
                    text.Y + (int)Math.Round(text.Height * y));
            }
        }
    }

    private static ColorSample MedianBackground(PagePixels page, PixelRect text)
    {
        List<byte> blue = [];
        List<byte> green = [];
        List<byte> red = [];
        int stepX = Math.Max(1, text.Width / 18);
        int stepY = Math.Max(1, text.Height / 18);
        for (int y = text.Y + text.Height / 10; y < text.Bottom - text.Height / 10; y += stepY)
        {
            for (int x = text.X + text.Width / 10; x < text.Right - text.Width / 10; x += stepX)
            {
                ColorSample sample = page.At(x, y);
                blue.Add(sample.Blue);
                green.Add(sample.Green);
                red.Add(sample.Red);
            }
        }
        if (red.Count == 0)
        {
            return page.At(text.X + text.Width / 2, text.Y + text.Height / 2);
        }
        blue.Sort();
        green.Sort();
        red.Sort();
        return new ColorSample(blue[blue.Count / 2], green[green.Count / 2], red[red.Count / 2]);
    }

    private static int Tolerance(PagePixels page, PixelRect text, ColorSample reference)
    {
        List<int> deviations = [];
        int stepX = Math.Max(1, text.Width / 14);
        int stepY = Math.Max(1, text.Height / 14);
        for (int y = text.Y; y < text.Bottom; y += stepY)
        {
            for (int x = text.X; x < text.Right; x += stepX)
            {
                ColorSample sample = page.At(x, y);
                deviations.Add(Math.Max(
                    Math.Abs(sample.Red - reference.Red),
                    Math.Max(Math.Abs(sample.Green - reference.Green), Math.Abs(sample.Blue - reference.Blue))));
            }
        }
        deviations.Sort();
        int median = deviations.Count == 0 ? 9 : deviations[deviations.Count / 2];
        return Math.Clamp(median * 2 + 24, 34, 72);
    }

    private static bool Similar(ColorSample sample, ColorSample reference, int tolerance)
    {
        int r = sample.Red - reference.Red;
        int g = sample.Green - reference.Green;
        int b = sample.Blue - reference.Blue;
        return r * r + g * g + b * b <= tolerance * tolerance * 3
            && Math.Abs(sample.Luminance - reference.Luminance) <= tolerance;
    }

    private static int BoundaryTouches(bool[] mask, int width, int height)
    {
        int count = 0;
        for (int x = 0; x < width; x++)
        {
            if (mask[x]) count++;
            if (mask[(height - 1) * width + x]) count++;
        }
        for (int y = 1; y < height - 1; y++)
        {
            if (mask[y * width]) count++;
            if (mask[y * width + width - 1]) count++;
        }
        return count;
    }

    private static void FillHoles(bool[] mask, int width, int height)
    {
        bool[] outside = new bool[mask.Length];
        Queue<int> queue = new();
        for (int x = 0; x < width; x++)
        {
            Add(x, 0);
            Add(x, height - 1);
        }
        for (int y = 1; y < height - 1; y++)
        {
            Add(0, y);
            Add(width - 1, y);
        }
        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            int x = index % width;
            int y = index / width;
            Add(x - 1, y); Add(x + 1, y); Add(x, y - 1); Add(x, y + 1);
        }
        for (int index = 0; index < mask.Length; index++)
        {
            mask[index] |= !outside[index];
        }

        void Add(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }
            int index = y * width + x;
            if (mask[index] || outside[index])
            {
                return;
            }
            outside[index] = true;
            queue.Enqueue(index);
        }
    }

    private static void Erode(bool[] mask, int width, int height, int iterations)
    {
        bool[] next = new bool[mask.Length];
        for (int pass = 0; pass < iterations; pass++)
        {
            Array.Clear(next, 0, next.Length);
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    int i = y * width + x;
                    next[i] = mask[i] && mask[i - 1] && mask[i + 1] && mask[i - width] && mask[i + width];
                }
            }
            Array.Copy(next, mask, mask.Length);
        }
    }

    private static bool MaskBounds(bool[] mask, int width, int height, out PixelRect bounds)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!mask[y * width + x]) continue;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        }
        if (right < left || bottom < top)
        {
            bounds = default;
            return false;
        }
        bounds = new PixelRect(left, top, right - left + 1, bottom - top + 1);
        return true;
    }

    private static BalloonCrop Build(bool[] source, PixelRect sourceBounds, PixelRect pageBounds, string method)
    {
        int width = pageBounds.Width;
        int height = pageBounds.Height;
        byte[] alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int sy = pageBounds.Y + y - sourceBounds.Y;
            if (sy < 0 || sy >= sourceBounds.Height) continue;
            for (int x = 0; x < width; x++)
            {
                int sx = pageBounds.X + x - sourceBounds.X;
                if (sx >= 0 && sx < sourceBounds.Width && source[sy * sourceBounds.Width + sx])
                {
                    alpha[y * width + x] = 255;
                }
            }
        }

        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < alpha.Length; i++)
        {
            int offset = i * 4;
            pixels[offset] = 255;
            pixels[offset + 1] = 255;
            pixels[offset + 2] = 255;
            pixels[offset + 3] = alpha[i];
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return new BalloonCrop(
            new Rect(pageBounds.X, pageBounds.Y, width, height),
            bitmap,
            LayoutPolygon(alpha, width, height),
            method);
    }

    private static IReadOnlyList<Point> LayoutPolygon(byte[] mask, int width, int height)
    {
        List<(double Y, double Left, double Right)> rows = [];
        List<(int Y, int Left, int Right)> raw = [];
        int step = Math.Max(1, height / 72);
        int maximum = 0;
        for (int y = 0; y < height; y += step)
        {
            if (WidestRun(mask, width, y, out int left, out int right))
            {
                maximum = Math.Max(maximum, right - left + 1);
                raw.Add((y, left, right));
            }
        }
        foreach ((int y, int left, int right) in raw)
        {
            if (right - left + 1 >= maximum * 0.28)
            {
                rows.Add((y + 0.5, left + 0.5, right + 0.5));
            }
        }
        if (rows.Count < 3) return [];
        List<Point> points = rows.Select(row => new Point(row.Left, row.Y)).ToList();
        points.AddRange(rows.AsEnumerable().Reverse().Select(row => new Point(row.Right, row.Y)));
        return points;
    }

    private static bool WidestRun(byte[] mask, int width, int y, out int left, out int right)
    {
        left = 0; right = -1;
        int best = 0, start = -1, row = y * width;
        for (int x = 0; x <= width; x++)
        {
            bool inside = x < width && mask[row + x] != 0;
            if (inside && start < 0) start = x;
            if (!inside && start >= 0)
            {
                if (x - start > best)
                {
                    best = x - start; left = start; right = x - 1;
                }
                start = -1;
            }
        }
        return best > 0;
    }

    private static bool TryPolygon(int pageWidth, int pageHeight, IReadOnlyList<NormalizedPoint> polygon, out BalloonCrop? crop)
    {
        PixelRect bounds = Pixels(PolygonBounds(polygon).Expand(0.03, 0.04), pageWidth, pageHeight);
        bool[] mask = new bool[bounds.Width * bounds.Height];
        Point[] local = polygon.Select(point => new Point(
            point.X / 1000 * pageWidth - bounds.X,
            point.Y / 1000 * pageHeight - bounds.Y)).ToArray();
        for (int y = 0; y < bounds.Height; y++)
        {
            for (int x = 0; x < bounds.Width; x++)
            {
                mask[y * bounds.Width + x] = Inside(local, x + 0.5, y + 0.5);
            }
        }
        Erode(mask, bounds.Width, bounds.Height, 2);
        crop = Build(mask, bounds, bounds, "polígono detectado");
        return crop.LayoutPolygon.Count >= 3;
    }

    private static BalloonCrop Fallback(int pageWidth, int pageHeight, ComicRegion region)
    {
        bool rectangular = region.Type is "narration" or "caption";
        NormalizedRect normalized = region.IsManual
            ? region.RenderBox
            : region.TextBox.Expand(rectangular ? 0.20 : 0.28, rectangular ? 0.34 : 0.46);
        PixelRect bounds = Pixels(normalized, pageWidth, pageHeight);
        bool[] mask = new bool[bounds.Width * bounds.Height];
        double cx = (bounds.Width - 1) / 2d, cy = (bounds.Height - 1) / 2d;
        double rx = Math.Max(1, bounds.Width * 0.46), ry = Math.Max(1, bounds.Height * 0.43);
        for (int y = 0; y < bounds.Height; y++)
        {
            for (int x = 0; x < bounds.Width; x++)
            {
                mask[y * bounds.Width + x] = rectangular
                    ? x >= 2 && y >= 2 && x < bounds.Width - 2 && y < bounds.Height - 2
                    : Math.Pow((x - cx) / rx, 2) + Math.Pow((y - cy) / ry, 2) <= 1;
            }
        }
        return Build(mask, bounds, bounds, "respaldo conservador");
    }

    private static bool Inside(IReadOnlyList<Point> polygon, double x, double y)
    {
        bool inside = false;
        int previous = polygon.Count - 1;
        for (int current = 0; current < polygon.Count; current++)
        {
            Point a = polygon[previous], b = polygon[current];
            if ((b.Y > y) != (a.Y > y)
                && x < (a.X - b.X) * (y - b.Y) / (a.Y - b.Y) + b.X)
            {
                inside = !inside;
            }
            previous = current;
        }
        return inside;
    }

    private static NormalizedRect PolygonBounds(IReadOnlyList<NormalizedPoint> points)
    {
        double left = points.Min(p => p.X), top = points.Min(p => p.Y);
        double right = points.Max(p => p.X), bottom = points.Max(p => p.Y);
        return new NormalizedRect(left, top, Math.Max(5, right - left), Math.Max(5, bottom - top)).Clamp();
    }

    private static PixelRect Pixels(NormalizedRect rect, int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(rect.X / 1000 * width), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(rect.Y / 1000 * height), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(rect.Right / 1000 * width), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom / 1000 * height), top + 1, height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private sealed record PagePixels(int Width, int Height, int Stride, byte[] Pixels)
    {
        public ColorSample At(int x, int y)
        {
            int offset = Math.Clamp(y, 0, Height - 1) * Stride + Math.Clamp(x, 0, Width - 1) * 4;
            return new ColorSample(Pixels[offset], Pixels[offset + 1], Pixels[offset + 2]);
        }
    }

    private readonly record struct ColorSample(byte Blue, byte Green, byte Red)
    {
        public int Luminance => (Red * 3 + Green * 6 + Blue) / 10;
    }

    private readonly record struct PixelRect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;

        public PixelRect Expand(int amount, int pageWidth, int pageHeight)
        {
            int left = Math.Max(0, X - amount), top = Math.Max(0, Y - amount);
            int right = Math.Min(pageWidth, Right + amount), bottom = Math.Min(pageHeight, Bottom + amount);
            return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        }

        public static PixelRect Centered(int cx, int cy, int width, int height, int pageWidth, int pageHeight)
        {
            width = Math.Min(width, pageWidth);
            height = Math.Min(height, pageHeight);
            int left = Math.Clamp(cx - width / 2, 0, pageWidth - width);
            int top = Math.Clamp(cy - height / 2, 0, pageHeight - height);
            return new PixelRect(left, top, width, height);
        }
    }
}

public sealed record BalloonCrop(
    Rect PageBounds,
    BitmapSource InteriorMask,
    IReadOnlyList<Point> LayoutPolygon,
    string DetectionMethod)
{
    public NormalizedRect ToNormalized(int pageWidth, int pageHeight) =>
        new NormalizedRect(
            PageBounds.X / pageWidth * 1000,
            PageBounds.Y / pageHeight * 1000,
            PageBounds.Width / pageWidth * 1000,
            PageBounds.Height / pageHeight * 1000).Clamp();
}
