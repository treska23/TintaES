using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

/// <summary>
/// Extrae el interior de cada bocadillo como una capa local enmascarada. El recorte se calcula
/// una vez por página y geometría: pintar, hacer scroll o seleccionar una zona no vuelve a
/// recorrer los píxeles de la página.
/// </summary>
public sealed class BalloonCropService
{
    private readonly ConditionalWeakTable<BitmapSource, PageCache> _cache = new();

    public BalloonCrop Create(BitmapSource cleanedPage, ComicRegion region)
    {
        PageCache cache = _cache.GetValue(
            cleanedPage,
            source => new PageCache(ReadPage(source)));
        CropKey key = CropKey.From(region);

        lock (cache.Gate)
        {
            if (cache.Crops.TryGetValue(key, out BalloonCrop? cached))
            {
                return cached;
            }
        }

        BalloonCrop created = CreateUncached(cache.Pixels, region);
        lock (cache.Gate)
        {
            if (cache.Crops.Count > 256)
            {
                cache.Crops.Clear();
            }
            cache.Crops[key] = created;
        }
        return created;
    }

    /// <summary>
    /// La etiqueta «dialogue» por sí sola no demuestra que exista un bocadillo. Se exige que
    /// una caja o un polígono rodee realmente el bloque OCR y que el detector aporte alguna
    /// confianza de contenedor. Así los rótulos del escenario no se convierten en diálogo.
    /// </summary>
    public static bool HasContainerEvidence(ComicRegion region)
    {
        if (region.IsManual)
        {
            return true;
        }
        if (region.Type is not ("dialogue" or "thought" or "narration" or "caption"))
        {
            return false;
        }

        double minimumConfidence = region.Type is "narration" or "caption" ? 0.05 : 0.10;
        if (region.BubbleConfidence < minimumConfidence)
        {
            return false;
        }

        if (region.BubbleBox is { } bubble && IsContainerAroundText(bubble, region.TextBox))
        {
            return true;
        }

        if (region.SafePolygon.Count >= 3)
        {
            NormalizedRect bounds = PolygonBounds(region.SafePolygon);
            return IsContainerAroundText(bounds, region.TextBox);
        }

        return false;
    }

    private static BalloonCrop CreateUncached(PagePixels page, ComicRegion region)
    {
        if (!region.IsManual && TryFlood(page, region, out BalloonCrop? crop))
        {
            return crop!;
        }
        if (region.SafePolygon.Count >= 3
            && TryPolygon(page, region, out crop))
        {
            return crop!;
        }
        return Fallback(page, region);
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
            int localX = x - search.X;
            int localY = y - search.Y;
            if (localX < 0 || localY < 0 || localX >= search.Width || localY >= search.Height)
            {
                continue;
            }

            int index = localY * search.Width + localX;
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

            void Visit(int nextX, int nextY)
            {
                if (nextX < 0 || nextY < 0 || nextX >= search.Width || nextY >= search.Height)
                {
                    return;
                }

                int next = nextY * search.Width + nextX;
                if (mask[next]
                    || !Similar(page.At(search.X + nextX, search.Y + nextY), reference, tolerance))
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
        Erode(
            mask,
            search.Width,
            search.Height,
            Math.Clamp(Math.Min(search.Width, search.Height) / 75, 3, 9));
        if (!MaskBounds(mask, search.Width, search.Height, out PixelRect local))
        {
            return false;
        }

        PixelRect pageBounds = new PixelRect(
            search.X + local.X,
            search.Y + local.Y,
            local.Width,
            local.Height).Expand(3, page.Width, page.Height);
        double variation = SurfaceVariation(page, mask, search, reference);
        bool reliable = HasContainerEvidence(region)
                        && variation <= (region.Type is "narration" or "caption" ? 64 : 48);
        crop = Build(
            mask,
            search,
            pageBounds,
            reference,
            reliable,
            variation,
            "lazo automático");
        return crop.LayoutPolygon.Count >= 3;
    }

    private static PixelRect SearchRect(
        ComicRegion region,
        PixelRect text,
        int pageWidth,
        int pageHeight)
    {
        NormalizedRect hint = region.TextBox.Expand(1.7, 1.9);
        if (region.BubbleBox is { } bubble && IsContainerAroundText(bubble, region.TextBox))
        {
            hint = bubble.Expand(0.14, 0.16);
        }
        else if (region.SafePolygon.Count >= 3)
        {
            NormalizedRect polygon = PolygonBounds(region.SafePolygon);
            if (IsContainerAroundText(polygon, region.TextBox))
            {
                hint = polygon.Expand(0.16, 0.18);
            }
        }

        PixelRect result = Pixels(hint, pageWidth, pageHeight);
        int maximumWidth = Math.Min(
            pageWidth,
            Math.Max(text.Width * 6, (int)(pageWidth * 0.36)));
        int maximumHeight = Math.Min(
            pageHeight,
            Math.Max(text.Height * 6, (int)(pageHeight * 0.28)));
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
        return result.Expand(Math.Max(4, text.Width / 12), pageWidth, pageHeight);
    }

    private static bool IsContainerAroundText(NormalizedRect outer, NormalizedRect text)
    {
        double centerX = text.X + text.Width / 2;
        double centerY = text.Y + text.Height / 2;
        double areaRatio = outer.Area / Math.Max(1, text.Area);
        return centerX >= outer.X
               && centerX <= outer.Right
               && centerY >= outer.Y
               && centerY <= outer.Bottom
               && areaRatio >= 1.12
               && areaRatio <= 24
               && outer.Width <= text.Width * 6.5
               && outer.Height <= text.Height * 6.5
               && outer.Width >= text.Width * 0.92
               && outer.Height >= text.Height * 0.92;
    }

    private static IEnumerable<(int X, int Y)> Seeds(PixelRect text)
    {
        foreach (double y in new[] { 0.28, 0.50, 0.72 })
        {
            foreach (double x in new[] { 0.22, 0.50, 0.78 })
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
        return new ColorSample(
            blue[blue.Count / 2],
            green[green.Count / 2],
            red[red.Count / 2]);
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
                    Math.Max(
                        Math.Abs(sample.Green - reference.Green),
                        Math.Abs(sample.Blue - reference.Blue))));
            }
        }
        deviations.Sort();
        int median = deviations.Count == 0 ? 9 : deviations[deviations.Count / 2];
        return Math.Clamp(median * 2 + 22, 32, 68);
    }

    private static bool Similar(ColorSample sample, ColorSample reference, int tolerance)
    {
        int red = sample.Red - reference.Red;
        int green = sample.Green - reference.Green;
        int blue = sample.Blue - reference.Blue;
        return red * red + green * green + blue * blue <= tolerance * tolerance * 3
               && Math.Abs(sample.Luminance - reference.Luminance) <= tolerance;
    }

    private static double SurfaceVariation(
        PagePixels page,
        bool[] mask,
        PixelRect bounds,
        ColorSample reference)
    {
        long total = 0;
        int count = 0;
        int step = Math.Max(1, Math.Min(bounds.Width, bounds.Height) / 120);
        for (int y = 0; y < bounds.Height; y += step)
        {
            for (int x = 0; x < bounds.Width; x += step)
            {
                if (!mask[y * bounds.Width + x])
                {
                    continue;
                }
                ColorSample sample = page.At(bounds.X + x, bounds.Y + y);
                total += Math.Abs(sample.Red - reference.Red)
                         + Math.Abs(sample.Green - reference.Green)
                         + Math.Abs(sample.Blue - reference.Blue);
                count += 3;
            }
        }
        return count == 0 ? double.MaxValue : total / (double)count;
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
            Add(x - 1, y);
            Add(x + 1, y);
            Add(x, y - 1);
            Add(x, y + 1);
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
                    int index = y * width + x;
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

    private static bool MaskBounds(bool[] mask, int width, int height, out PixelRect bounds)
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

    private static BalloonCrop Build(
        bool[] source,
        PixelRect sourceBounds,
        PixelRect pageBounds,
        ColorSample background,
        bool reliable,
        double variation,
        string method)
    {
        int width = pageBounds.Width;
        int height = pageBounds.Height;
        byte[] alpha = new byte[width * height];
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
                if (sourceX >= 0
                    && sourceX < sourceBounds.Width
                    && source[sourceY * sourceBounds.Width + sourceX])
                {
                    alpha[y * width + x] = 255;
                }
            }
        }

        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < alpha.Length; index++)
        {
            int offset = index * 4;
            pixels[offset] = 255;
            pixels[offset + 1] = 255;
            pixels[offset + 2] = 255;
            pixels[offset + 3] = alpha[index];
        }
        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();

        IReadOnlyList<Point> polygon = LayoutPolygon(alpha, width, height);
        if (polygon.Count < 3)
        {
            polygon = ConservativePolygon(alpha, width, height);
        }

        return new BalloonCrop(
            new Rect(pageBounds.X, pageBounds.Y, width, height),
            bitmap,
            polygon,
            Color.FromRgb(background.Red, background.Green, background.Blue),
            reliable,
            variation,
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
            if (right - left + 1 >= maximum * 0.34)
            {
                rows.Add((y + 0.5, left + 0.5, right + 0.5));
            }
        }
        if (rows.Count < 3)
        {
            return [];
        }
        List<Point> points = rows.Select(row => new Point(row.Left, row.Y)).ToList();
        points.AddRange(rows.AsEnumerable().Reverse().Select(row => new Point(row.Right, row.Y)));
        return points;
    }

    private static IReadOnlyList<Point> ConservativePolygon(byte[] mask, int width, int height)
    {
        int left = width;
        int top = height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (mask[y * width + x] == 0)
                {
                    continue;
                }
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        if (right <= left || bottom <= top)
        {
            return [];
        }

        double insetX = Math.Max(2, (right - left) * 0.12);
        double insetY = Math.Max(2, (bottom - top) * 0.12);
        return
        [
            new Point(left + insetX, top + insetY),
            new Point(right - insetX, top + insetY),
            new Point(right - insetX, bottom - insetY),
            new Point(left + insetX, bottom - insetY)
        ];
    }

    private static bool WidestRun(byte[] mask, int width, int y, out int left, out int right)
    {
        left = 0;
        right = -1;
        int best = 0;
        int start = -1;
        int row = y * width;
        for (int x = 0; x <= width; x++)
        {
            bool inside = x < width && mask[row + x] != 0;
            if (inside && start < 0)
            {
                start = x;
            }
            if (!inside && start >= 0)
            {
                if (x - start > best)
                {
                    best = x - start;
                    left = start;
                    right = x - 1;
                }
                start = -1;
            }
        }
        return best > 0;
    }

    private static bool TryPolygon(
        PagePixels page,
        ComicRegion region,
        out BalloonCrop? crop)
    {
        IReadOnlyList<NormalizedPoint> polygon = region.SafePolygon;
        PixelRect bounds = Pixels(
            PolygonBounds(polygon).Expand(0.03, 0.04),
            page.Width,
            page.Height);
        bool[] mask = new bool[bounds.Width * bounds.Height];
        Point[] local = polygon.Select(point => new Point(
            point.X / 1000 * page.Width - bounds.X,
            point.Y / 1000 * page.Height - bounds.Y)).ToArray();
        for (int y = 0; y < bounds.Height; y++)
        {
            for (int x = 0; x < bounds.Width; x++)
            {
                mask[y * bounds.Width + x] = Inside(local, x + 0.5, y + 0.5);
            }
        }
        Erode(mask, bounds.Width, bounds.Height, 3);
        ColorSample background = MedianBackground(
            page,
            Pixels(region.TextBox, page.Width, page.Height));
        double variation = SurfaceVariation(page, mask, bounds, background);
        bool reliable = HasContainerEvidence(region)
                        && variation <= (region.Type is "narration" or "caption" ? 68 : 52);
        crop = Build(
            mask,
            bounds,
            bounds,
            background,
            reliable,
            variation,
            "polígono detectado");
        return crop.LayoutPolygon.Count >= 3;
    }

    private static BalloonCrop Fallback(PagePixels page, ComicRegion region)
    {
        bool rectangular = region.Type is "narration" or "caption";
        NormalizedRect normalized = region.IsManual
            ? region.RenderBox
            : region.TextBox.Expand(
                rectangular ? 0.18 : 0.24,
                rectangular ? 0.30 : 0.38);
        PixelRect bounds = Pixels(normalized, page.Width, page.Height);
        bool[] mask = new bool[bounds.Width * bounds.Height];
        double centerX = (bounds.Width - 1) / 2d;
        double centerY = (bounds.Height - 1) / 2d;
        double radiusX = Math.Max(1, bounds.Width * 0.44);
        double radiusY = Math.Max(1, bounds.Height * 0.41);
        for (int y = 0; y < bounds.Height; y++)
        {
            for (int x = 0; x < bounds.Width; x++)
            {
                mask[y * bounds.Width + x] = rectangular
                    ? x >= 3 && y >= 3 && x < bounds.Width - 3 && y < bounds.Height - 3
                    : Math.Pow((x - centerX) / radiusX, 2)
                      + Math.Pow((y - centerY) / radiusY, 2) <= 1;
            }
        }
        ColorSample background = MedianBackground(
            page,
            Pixels(region.TextBox, page.Width, page.Height));
        return Build(
            mask,
            bounds,
            bounds,
            background,
            region.IsManual,
            0,
            "respaldo manual");
    }

    private static bool Inside(IReadOnlyList<Point> polygon, double x, double y)
    {
        bool inside = false;
        int previous = polygon.Count - 1;
        for (int current = 0; current < polygon.Count; current++)
        {
            Point first = polygon[previous];
            Point second = polygon[current];
            if ((second.Y > y) != (first.Y > y)
                && x < (first.X - second.X) * (y - second.Y)
                    / (first.Y - second.Y) + second.X)
            {
                inside = !inside;
            }
            previous = current;
        }
        return inside;
    }

    private static NormalizedRect PolygonBounds(IReadOnlyList<NormalizedPoint> points)
    {
        double left = points.Min(point => point.X);
        double top = points.Min(point => point.Y);
        double right = points.Max(point => point.X);
        double bottom = points.Max(point => point.Y);
        return new NormalizedRect(
            left,
            top,
            Math.Max(5, right - left),
            Math.Max(5, bottom - top)).Clamp();
    }

    private static PixelRect Pixels(NormalizedRect rectangle, int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(rectangle.X / 1000 * width), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(rectangle.Y / 1000 * height), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(rectangle.Right / 1000 * width), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(rectangle.Bottom / 1000 * height), top + 1, height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private sealed class PageCache(PagePixels pixels)
    {
        public PagePixels Pixels { get; } = pixels;
        public object Gate { get; } = new();
        public Dictionary<CropKey, BalloonCrop> Crops { get; } = [];
    }

    private sealed record PagePixels(int Width, int Height, int Stride, byte[] Pixels)
    {
        public ColorSample At(int x, int y)
        {
            int offset = Math.Clamp(y, 0, Height - 1) * Stride
                         + Math.Clamp(x, 0, Width - 1) * 4;
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
            int left = Math.Max(0, X - amount);
            int top = Math.Max(0, Y - amount);
            int right = Math.Min(pageWidth, Right + amount);
            int bottom = Math.Min(pageHeight, Bottom + amount);
            return new PixelRect(
                left,
                top,
                Math.Max(1, right - left),
                Math.Max(1, bottom - top));
        }

        public static PixelRect Centered(
            int centerX,
            int centerY,
            int width,
            int height,
            int pageWidth,
            int pageHeight)
        {
            width = Math.Min(width, pageWidth);
            height = Math.Min(height, pageHeight);
            int left = Math.Clamp(centerX - width / 2, 0, pageWidth - width);
            int top = Math.Clamp(centerY - height / 2, 0, pageHeight - height);
            return new PixelRect(left, top, width, height);
        }
    }

    private readonly record struct CropKey(
        Guid RegionId,
        string Type,
        bool IsManual,
        NormalizedRect TextBox,
        NormalizedRect? BubbleBox,
        NormalizedRect RenderBox,
        int GeometryHash,
        int BubbleConfidenceBucket)
    {
        public static CropKey From(ComicRegion region)
        {
            var hash = new HashCode();
            foreach (NormalizedPoint point in region.SafePolygon.Take(160))
            {
                hash.Add(Math.Round(point.X, 2));
                hash.Add(Math.Round(point.Y, 2));
            }
            return new CropKey(
                region.Id,
                region.Type,
                region.IsManual,
                region.TextBox,
                region.BubbleBox,
                region.RenderBox,
                hash.ToHashCode(),
                (int)Math.Round(region.BubbleConfidence * 100));
        }
    }
}

public sealed record BalloonCrop(
    Rect PageBounds,
    BitmapSource InteriorMask,
    IReadOnlyList<Point> LayoutPolygon,
    Color InteriorColor,
    bool IsReliableContainer,
    double SurfaceVariation,
    string DetectionMethod)
{
    public NormalizedRect ToNormalized(int pageWidth, int pageHeight) =>
        new NormalizedRect(
            PageBounds.X / pageWidth * 1000,
            PageBounds.Y / pageHeight * 1000,
            PageBounds.Width / pageWidth * 1000,
            PageBounds.Height / pageHeight * 1000).Clamp();
}
