using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

/// <summary>
/// Extrae cada bocadillo como una capa local independiente. El rectángulo solo es el soporte
/// técnico; InteriorMask conserva la selección irregular del interior, como una selección con
/// lazo en una capa transparente.
/// </summary>
public sealed class BalloonCropService
{
    private readonly ConditionalWeakTable<BitmapSource, PagePixels> _pixelCache = new();

    public BalloonCrop Create(BitmapSource cleanedPage, ComicRegion region)
    {
        ArgumentNullException.ThrowIfNull(cleanedPage);
        ArgumentNullException.ThrowIfNull(region);

        PagePixels page = _pixelCache.GetValue(cleanedPage, CreatePagePixels);
        if (!region.IsManual
            && TryCreateFloodCrop(page, region, out BalloonCrop? detected))
        {
            return detected;
        }

        if (region.SafePolygon.Count >= 3
            && TryCreatePolygonCrop(page.Width, page.Height, region.SafePolygon, out BalloonCrop? polygonCrop))
        {
            return polygonCrop;
        }

        return CreateConservativeFallback(page.Width, page.Height, region);
    }

    private static PagePixels CreatePagePixels(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return new PagePixels(bgra.PixelWidth, bgra.PixelHeight, stride, pixels);
    }

    private static bool TryCreateFloodCrop(
        PagePixels page,
        ComicRegion region,
        out BalloonCrop? crop)
    {
        crop = null;
        PixelRect text = ToPixelRect(region.TextBox, page.Width, page.Height);
        PixelRect search = CreateSearchRect(region, text, page.Width, page.Height);
        if (search.Width < 12 || search.Height < 12)
        {
            return false;
        }

        BgraColor reference = EstimateBackground(page, text);
        int tolerance = EstimateTolerance(page, text, reference);
        var mask = new bool[search.Width * search.Height];
        var queue = new Queue<int>();

        foreach ((int X, int Y) seed in CreateSeeds(text, search))
        {
            int localX = seed.X - search.X;
            int localY = seed.Y - search.Y;
            if (localX < 0 || localY < 0 || localX >= search.Width || localY >= search.Height)
            {
                continue;
            }

            int localIndex = localY * search.Width + localX;
            if (mask[localIndex]
                || !IsCompatible(page.Get(seed.X, seed.Y), reference, tolerance))
            {
                continue;
            }

            mask[localIndex] = true;
            queue.Enqueue(localIndex);
        }

        if (queue.Count == 0)
        {
            return false;
        }

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            int x = current % search.Width;
            int y = current / search.Width;
            Visit(x - 1, y);
            Visit(x + 1, y);
            Visit(x, y - 1);
            Visit(x, y + 1);

            void Visit(int nextX, int nextY)
            {
                if (nextX < 0 || nextY < 0 || nextX >= search.Width || nextY >= search.Height)
                {
                    return;
                }

                int index = nextY * search.Width + nextX;
                if (mask[index])
                {
                    return;
                }

                BgraColor color = page.Get(search.X + nextX, search.Y + nextY);
                if (!IsCompatible(color, reference, tolerance))
                {
                    return;
                }

                mask[index] = true;
                queue.Enqueue(index);
            }
        }

        int area = mask.Count(value => value);
        int textArea = Math.Max(1, text.Width * text.Height);
        int searchArea = search.Width * search.Height;
        int boundaryTouches = CountBoundaryTouches(mask, search.Width, search.Height);
        int boundaryLimit = Math.Max(8, (search.Width + search.Height) / 16);
        if (area < textArea * 0.70
            || area > searchArea * 0.93
            || boundaryTouches > boundaryLimit)
        {
            return false;
        }

        FillInternalHoles(mask, search.Width, search.Height);
        int erosion = Math.Clamp(Math.Min(search.Width, search.Height) / 90, 2, 7);
        Erode(mask, search.Width, search.Height, erosion);

        if (!TryFindBounds(mask, search.Width, search.Height, out PixelRect localBounds))
        {
            return false;
        }

        PixelRect pageBounds = new(
            search.X + localBounds.X,
            search.Y + localBounds.Y,
            localBounds.Width,
            localBounds.Height).Expand(3, page.Width, page.Height);
        crop = BuildCropFromMask(mask, search, pageBounds, "flood");
        return crop.LayoutPolygon.Count >= 3;
    }

    private static PixelRect CreateSearchRect(
        ComicRegion region,
        PixelRect text,
        int pageWidth,
        int pageHeight)
    {
        NormalizedRect hint = region.TextBox.Expand(2.0, 2.2);
        if (region.BubbleBox is { } bubble
            && bubble.X <= region.TextBox.X + region.TextBox.Width / 2
            && bubble.Y <= region.TextBox.Y + region.TextBox.Height / 2
            && bubble.Right >= region.TextBox.X + region.TextBox.Width / 2
            && bubble.Bottom >= region.TextBox.Y + region.TextBox.Height / 2
            && bubble.Area >= region.TextBox.Area * 1.05
            && bubble.Area <= region.TextBox.Area * 36
            && bubble.Width <= region.TextBox.Width * 7
            && bubble.Height <= region.TextBox.Height * 7)
        {
            hint = bubble.Expand(0.18, 0.20);
        }
        else if (region.SafePolygon.Count >= 3)
        {
            NormalizedRect polygonBounds = Bounds(region.SafePolygon);
            if (polygonBounds.Area >= region.TextBox.Area * 0.8
                && polygonBounds.Area <= region.TextBox.Area * 32)
            {
                hint = polygonBounds.Expand(0.22, 0.24);
            }
        }

        PixelRect search = ToPixelRect(hint, pageWidth, pageHeight);
        int maxWidth = Math.Max(text.Width * 8, (int)Math.Round(pageWidth * 0.48));
        int maxHeight = Math.Max(text.Height * 8, (int)Math.Round(pageHeight * 0.42));
        if (search.Width > maxWidth || search.Height > maxHeight)
        {
            int centerX = text.X + text.Width / 2;
            int centerY = text.Y + text.Height / 2;
            search = PixelRect.Centered(
                centerX,
                centerY,
                Math.Min(search.Width, maxWidth),
                Math.Min(search.Height, maxHeight),
                pageWidth,
                pageHeight);
        }

        return search.Expand(Math.Max(4, text.Width / 10), pageWidth, pageHeight);
    }

    private static IEnumerable<(int X, int Y)> CreateSeeds(PixelRect text, PixelRect search)
    {
        int centerX = text.X + text.Width / 2;
        int centerY = text.Y + text.Height / 2;
        yield return (centerX, centerY);

        foreach (double yRatio in new[] { 0.30, 0.50, 0.70 })
        {
            foreach (double xRatio in new[] { 0.25, 0.50, 0.75 })
            {
                int x = text.X + (int)Math.Round(text.Width * xRatio);
                int y = text.Y + (int)Math.Round(text.Height * yRatio);
                if (x >= search.X && x < search.Right && y >= search.Y && y < search.Bottom)
                {
                    yield return (x, y);
                }
            }
        }
    }

    private static BgraColor EstimateBackground(PagePixels page, PixelRect text)
    {
        var blues = new List<byte>();
        var greens = new List<byte>();
        var reds = new List<byte>();
        int stepX = Math.Max(1, text.Width / 18);
        int stepY = Math.Max(1, text.Height / 18);
        int insetX = Math.Max(1, text.Width / 10);
        int insetY = Math.Max(1, text.Height / 10);

        for (int y = text.Y + insetY; y < text.Bottom - insetY; y += stepY)
        {
            for (int x = text.X + insetX; x < text.Right - insetX; x += stepX)
            {
                BgraColor color = page.Get(x, y);
                blues.Add(color.Blue);
                greens.Add(color.Green);
                reds.Add(color.Red);
            }
        }

        if (reds.Count == 0)
        {
            return page.Get(
                Math.Clamp(text.X + text.Width / 2, 0, page.Width - 1),
                Math.Clamp(text.Y + text.Height / 2, 0, page.Height - 1));
        }

        blues.Sort();
        greens.Sort();
        reds.Sort();
        return new BgraColor(
            blues[blues.Count / 2],
            greens[greens.Count / 2],
            reds[reds.Count / 2],
            255);
    }

    private static int EstimateTolerance(PagePixels page, PixelRect text, BgraColor reference)
    {
        var deviations = new List<int>();
        int stepX = Math.Max(1, text.Width / 14);
        int stepY = Math.Max(1, text.Height / 14);
        for (int y = text.Y; y < text.Bottom; y += stepY)
        {
            for (int x = text.X; x < text.Right; x += stepX)
            {
                BgraColor color = page.Get(x, y);
                deviations.Add(Math.Max(
                    Math.Abs(color.Red - reference.Red),
                    Math.Max(
                        Math.Abs(color.Green - reference.Green),
                        Math.Abs(color.Blue - reference.Blue))));
            }
        }

        if (deviations.Count == 0)
        {
            return 42;
        }

        deviations.Sort();
        int median = deviations[deviations.Count / 2];
        return Math.Clamp(median * 2 + 24, 34, 72);
    }

    private static bool IsCompatible(BgraColor color, BgraColor reference, int tolerance)
    {
        int red = color.Red - reference.Red;
        int green = color.Green - reference.Green;
        int blue = color.Blue - reference.Blue;
        int distance = red * red + green * green + blue * blue;
        int luminanceDifference = Math.Abs(color.Luminance - reference.Luminance);
        return distance <= tolerance * tolerance * 3
            && luminanceDifference <= tolerance;
    }

    private static int CountBoundaryTouches(bool[] mask, int width, int height)
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

    private static void FillInternalHoles(bool[] mask, int width, int height)
    {
        var exterior = new bool[mask.Length];
        var queue = new Queue<int>();
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
            int current = queue.Dequeue();
            int x = current % width;
            int y = current / width;
            Add(x - 1, y);
            Add(x + 1, y);
            Add(x, y - 1);
            Add(x, y + 1);
        }

        for (int index = 0; index < mask.Length; index++)
        {
            if (!mask[index] && !exterior[index])
            {
                mask[index] = true;
            }
        }

        void Add(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }
            int index = y * width + x;
            if (mask[index] || exterior[index])
            {
                return;
            }
            exterior[index] = true;
            queue.Enqueue(index);
        }
    }

    private static void Erode(bool[] mask, int width, int height, int iterations)
    {
        var next = new bool[mask.Length];
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Array.Clear(next, 0, next.Length);
            for (int y = 1; y < height - 1; y++)
            {
                int row = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    int index = row + x;
                    next[index] = mask[index]
                        && mask[index - 1]
                        && mask[index + 1]
                        && mask[index - width]
                        && mask[index + width];
                }
            }
            Array.Copy(next, mask, mask.Length);
        }
    }

    private static BalloonCrop BuildCropFromMask(
        bool[] sourceMask,
        PixelRect sourceBounds,
        PixelRect pageBounds,
        string detectionMethod)
    {
        int width = pageBounds.Width;
        int height = pageBounds.Height;
        var maskBytes = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int sourceY = pageBounds.Y + y - sourceBounds.Y;
            if (sourceY < 0 || sourceY >= sourceBounds.Height)
            {
                continue;
            }
            for (int x = 0; x < width; x++)
            {
                int sourceX = pageBounds.X + x - sourceBounds.X;
                if (sourceX < 0 || sourceX >= sourceBounds.Width)
                {
                    continue;
                }
                if (sourceMask[sourceY * sourceBounds.Width + sourceX])
                {
                    maskBytes[y * width + x] = 255;
                }
            }
        }

        var alphaPixels = new byte[width * height * 4];
        for (int index = 0; index < maskBytes.Length; index++)
        {
            int offset = index * 4;
            alphaPixels[offset] = 255;
            alphaPixels[offset + 1] = 255;
            alphaPixels[offset + 2] = 255;
            alphaPixels[offset + 3] = maskBytes[index];
        }

        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            alphaPixels,
            width * 4);
        bitmap.Freeze();
        IReadOnlyList<Point> polygon = BuildLayoutPolygon(maskBytes, width, height);
        return new BalloonCrop(
            new Rect(pageBounds.X, pageBounds.Y, width, height),
            bitmap,
            polygon,
            detectionMethod);
    }

    private static IReadOnlyList<Point> BuildLayoutPolygon(byte[] mask, int width, int height)
    {
        var rows = new List<(double Y, double Left, double Right)>();
        int step = Math.Max(1, height / 72);
        int maximumWidth = 0;
        var raw = new List<(int Y, int Left, int Right)>();
        for (int y = 0; y < height; y += step)
        {
            if (TryFindWidestRun(mask, width, height, y, out int left, out int right))
            {
                maximumWidth = Math.Max(maximumWidth, right - left + 1);
                raw.Add((y, left, right));
            }
        }

        foreach ((int y, int left, int right) in raw)
        {
            if (right - left + 1 >= maximumWidth * 0.28)
            {
                rows.Add((y + 0.5, left + 0.5, right + 0.5));
            }
        }

        if (rows.Count < 3)
        {
            return [];
        }

        var points = new List<Point>(rows.Count * 2);
        points.AddRange(rows.Select(row => new Point(row.Left, row.Y)));
        points.AddRange(rows.AsEnumerable().Reverse().Select(row => new Point(row.Right, row.Y)));
        return points;
    }

    private static bool TryFindWidestRun(
        byte[] mask,
        int width,
        int height,
        int y,
        out int left,
        out int right)
    {
        left = 0;
        right = -1;
        int bestWidth = 0;
        int currentStart = -1;
        int row = Math.Clamp(y, 0, height - 1) * width;
        for (int x = 0; x <= width; x++)
        {
            bool inside = x < width && mask[row + x] != 0;
            if (inside && currentStart < 0)
            {
                currentStart = x;
            }
            else if (!inside && currentStart >= 0)
            {
                int runWidth = x - currentStart;
                if (runWidth > bestWidth)
                {
                    bestWidth = runWidth;
                    left = currentStart;
                    right = x - 1;
                }
                currentStart = -1;
            }
        }
        return bestWidth > 0;
    }

    private static bool TryCreatePolygonCrop(
        int pageWidth,
        int pageHeight,
        IReadOnlyList<NormalizedPoint> polygon,
        out BalloonCrop? crop)
    {
        crop = null;
        NormalizedRect normalizedBounds = Bounds(polygon).Expand(0.03, 0.04);
        PixelRect pageBounds = ToPixelRect(normalizedBounds, pageWidth, pageHeight);
        if (pageBounds.Width < 6 || pageBounds.Height < 6)
        {
            return false;
        }

        var mask = new bool[pageBounds.Width * pageBounds.Height];
        Point[] local = polygon.Select(point => new Point(
            point.X / 1000 * pageWidth - pageBounds.X,
            point.Y / 1000 * pageHeight - pageBounds.Y)).ToArray();
        for (int y = 0; y < pageBounds.Height; y++)
        {
            for (int x = 0; x < pageBounds.Width; x++)
            {
                mask[y * pageBounds.Width + x] = Contains(local, x + 0.5, y + 0.5);
            }
        }

        Erode(mask, pageBounds.Width, pageBounds.Height, 2);
        crop = BuildCropFromMask(mask, pageBounds, pageBounds, "polygon");
        return crop.LayoutPolygon.Count >= 3;
    }

    private static BalloonCrop CreateConservativeFallback(
        int pageWidth,
        int pageHeight,
        ComicRegion region)
    {
        bool rectangular = region.Type is "narration" or "caption";
        NormalizedRect normalized = region.IsManual
            ? region.RenderBox
            : region.TextBox.Expand(rectangular ? 0.20 : 0.28, rectangular ? 0.34 : 0.46);
        PixelRect pageBounds = ToPixelRect(normalized, pageWidth, pageHeight);
        var mask = new bool[pageBounds.Width * pageBounds.Height];
        double centerX = (pageBounds.Width - 1) / 2d;
        double centerY = (pageBounds.Height - 1) / 2d;
        double radiusX = Math.Max(1, pageBounds.Width * 0.46);
        double radiusY = Math.Max(1, pageBounds.Height * 0.43);

        for (int y = 0; y < pageBounds.Height; y++)
        {
            for (int x = 0; x < pageBounds.Width; x++)
            {
                bool inside = rectangular
                    ? x >= 2 && y >= 2 && x < pageBounds.Width - 2 && y < pageBounds.Height - 2
                    : Math.Pow((x - centerX) / radiusX, 2)
                      + Math.Pow((y - centerY) / radiusY, 2) <= 1;
                mask[y * pageBounds.Width + x] = inside;
            }
        }

        return BuildCropFromMask(
            mask,
            pageBounds,
            pageBounds,
            "fallback");
    }

    private static bool TryFindBounds(bool[] mask, int width, int height, out PixelRect bounds)
    {
        int left = width;
        int top = height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!mask[y * width + x])
                {
                    continue;
                }
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
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

    private static bool Contains(IReadOnlyList<Point> polygon, double x, double y)
    {
        bool inside = false;
        int previous = polygon.Count - 1;
        for (int current = 0; current < polygon.Count; current++)
        {
            Point first = polygon[previous];
            Point second = polygon[current];
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

    private static NormalizedRect Bounds(IReadOnlyList<NormalizedPoint> polygon)
    {
        double left = polygon.Min(point => point.X);
        double top = polygon.Min(point => point.Y);
        double right = polygon.Max(point => point.X);
        double bottom = polygon.Max(point => point.Y);
        return new NormalizedRect(left, top, Math.Max(5, right - left), Math.Max(5, bottom - top)).Clamp();
    }

    private static PixelRect ToPixelRect(NormalizedRect rect, int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(rect.X / 1000 * width), 0, Math.Max(0, width - 1));
        int top = Math.Clamp((int)Math.Floor(rect.Y / 1000 * height), 0, Math.Max(0, height - 1));
        int right = Math.Clamp((int)Math.Ceiling(rect.Right / 1000 * width), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom / 1000 * height), top + 1, height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private sealed record PagePixels(int Width, int Height, int Stride, byte[] Pixels)
    {
        public BgraColor Get(int x, int y)
        {
            int safeX = Math.Clamp(x, 0, Width - 1);
            int safeY = Math.Clamp(y, 0, Height - 1);
            int offset = safeY * Stride + safeX * 4;
            return new BgraColor(
                Pixels[offset],
                Pixels[offset + 1],
                Pixels[offset + 2],
                Pixels[offset + 3]);
        }
    }

    private readonly record struct BgraColor(byte Blue, byte Green, byte Red, byte Alpha)
    {
        public int Luminance => (Red * 3 + Green * 6 + Blue) / 10;
    }

    private readonly record struct PixelRect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;

        public PixelRect Expand(int pixels, int pageWidth, int pageHeight)
        {
            int left = Math.Max(0, X - pixels);
            int top = Math.Max(0, Y - pixels);
            int right = Math.Min(pageWidth, Right + pixels);
            int bottom = Math.Min(pageHeight, Bottom + pixels);
            return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        }

        public static PixelRect Centered(
            int centerX,
            int centerY,
            int width,
            int height,
            int pageWidth,
            int pageHeight)
        {
            int left = Math.Clamp(centerX - width / 2, 0, Math.Max(0, pageWidth - width));
            int top = Math.Clamp(centerY - height / 2, 0, Math.Max(0, pageHeight - height));
            return new PixelRect(left, top, Math.Min(width, pageWidth), Math.Min(height, pageHeight));
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
        new(
            PageBounds.X / pageWidth * 1000,
            PageBounds.Y / pageHeight * 1000,
            PageBounds.Width / pageWidth * 1000,
            PageBounds.Height / pageHeight * 1000).Clamp();
}
