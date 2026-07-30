using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Impide que el fondo reconstruido aparezca como una placa rectangular detrás del texto.
/// LaMa puede trabajar sobre una caja rectangular interna, pero el resultado visible se conserva
/// únicamente dentro de la silueta orgánica del bocadillo o de la zona de limpieza detectada.
/// Fuera de esa silueta se restauran exactamente los píxeles de la página original.
/// </summary>
public partial class MainWindow
{
    private static readonly bool BubbleCleanupClipRegistered = RegisterBubbleCleanupClip();

    private bool _bubbleCleanupClipInstalled;
    private bool _bubbleCleanupClipPending;
    private BitmapSource? _bubbleCleanupClipLastInput;
    private int _bubbleCleanupClipGeneration;

    private static bool RegisterBubbleCleanupClip()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_BubbleCleanupClipLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_BubbleCleanupClipLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallBubbleCleanupClip,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallBubbleCleanupClip()
    {
        if (_bubbleCleanupClipInstalled)
        {
            QueueBubbleCleanupClip();
            return;
        }

        _bubbleCleanupClipInstalled = true;
        BusyOverlay.IsVisibleChanged += (_, _) =>
        {
            if (!BusyOverlay.IsVisible)
            {
                QueueBubbleCleanupClip();
            }
        };
        ResultPreviewButton.Click += (_, _) => QueueBubbleCleanupClip();
        CleanPreviewButton.Click += (_, _) => QueueBubbleCleanupClip();
        PageImage.LayoutUpdated += (_, _) => QueueBubbleCleanupClip();
        _regions.CollectionChanged += (_, _) => QueueBubbleCleanupClip();
        QueueBubbleCleanupClip();
    }

    private void QueueBubbleCleanupClip()
    {
        if (_bubbleCleanupClipPending
            || BusyOverlay.IsVisible
            || _originalBitmap is null
            || _cleanedBaseBitmap is null
            || _maskBitmap is null
            || _regions.Count == 0
            || ReferenceEquals(_cleanedBaseBitmap, _bubbleCleanupClipLastInput))
        {
            return;
        }

        BubbleClipRegion[] regions = _regions
            .Where(region =>
                region.IsEnabled
                && region.Type is "dialogue" or "thought")
            .Select(region => new BubbleClipRegion(
                region.TextBox,
                region.RenderBox,
                region.BubbleBox,
                (region.CleanupPolygon.Count >= 3
                    ? region.CleanupPolygon
                    : region.SafePolygon).ToArray()))
            .ToArray();

        if (regions.Length == 0)
        {
            _bubbleCleanupClipLastInput = _cleanedBaseBitmap;
            return;
        }

        BitmapSource original = FreezeBubbleClipBitmap(_originalBitmap);
        BitmapSource cleaned = FreezeBubbleClipBitmap(_cleanedBaseBitmap);
        BitmapSource mask = FreezeBubbleClipBitmap(_maskBitmap);

        _bubbleCleanupClipPending = true;
        _bubbleCleanupClipLastInput = _cleanedBaseBitmap;
        int generation = ++_bubbleCleanupClipGeneration;
        _ = ApplyBubbleCleanupClipAsync(original, cleaned, mask, regions, generation);
    }

    private async Task ApplyBubbleCleanupClipAsync(
        BitmapSource original,
        BitmapSource cleaned,
        BitmapSource mask,
        IReadOnlyList<BubbleClipRegion> regions,
        int generation)
    {
        BubbleClipResult result;
        try
        {
            result = await Task.Run(() => ClipCleanedBackgroundToBubbleShapes(original, cleaned, mask, regions));
        }
        catch
        {
            _bubbleCleanupClipPending = false;
            return;
        }

        if (generation != _bubbleCleanupClipGeneration
            || _originalBitmap is null
            || _originalBitmap.PixelWidth != original.PixelWidth
            || _originalBitmap.PixelHeight != original.PixelHeight)
        {
            _bubbleCleanupClipPending = false;
            return;
        }

        _bubbleCleanupClipPending = false;
        if (!result.Changed)
        {
            _bubbleCleanupClipLastInput = _cleanedBaseBitmap;
            return;
        }

        _cleanedBaseBitmap = result.Cleaned;
        _cleanedBitmap = result.Cleaned;
        _maskBitmap = result.Mask;
        _bubbleCleanupClipLastInput = result.Cleaned;

        if (_previewMode is "clean" or "result")
        {
            PageImage.Source = result.Cleaned;
        }
        if (_previewMode == "result")
        {
            RebuildOverlay();
        }

        int pageIndex = _visibleComicPageIndex >= 0
            ? _visibleComicPageIndex
            : _comicPageIndex;
        if (pageIndex < 0 || pageIndex >= _comicPages.Count)
        {
            return;
        }

        ComicBookPageState page = _comicPages[pageIndex];
        string? cleanedPath = page.CleanedPath;
        string? maskPath = page.MaskPath;
        if (string.IsNullOrWhiteSpace(cleanedPath) || string.IsNullOrWhiteSpace(maskPath))
        {
            return;
        }

        await Task.WhenAll(
            Task.Run(() => SaveBubbleClipBitmap(result.Cleaned, cleanedPath)),
            Task.Run(() => SaveBubbleClipBitmap(result.Mask, maskPath)));
    }

    private static BubbleClipResult ClipCleanedBackgroundToBubbleShapes(
        BitmapSource original,
        BitmapSource cleaned,
        BitmapSource mask,
        IReadOnlyList<BubbleClipRegion> regions)
    {
        BitmapSource originalBgra = ConvertBubbleClipBitmap(original, PixelFormats.Bgra32);
        BitmapSource cleanedBgra = ConvertBubbleClipBitmap(cleaned, PixelFormats.Bgra32);
        BitmapSource maskGray = ConvertBubbleClipBitmap(mask, PixelFormats.Gray8);

        int width = originalBgra.PixelWidth;
        int height = originalBgra.PixelHeight;
        if (cleanedBgra.PixelWidth != width
            || cleanedBgra.PixelHeight != height
            || maskGray.PixelWidth != width
            || maskGray.PixelHeight != height)
        {
            return new BubbleClipResult(cleaned, mask, false);
        }

        int colorStride = width * 4;
        var originalPixels = new byte[colorStride * height];
        var cleanedPixels = new byte[colorStride * height];
        var maskPixels = new byte[width * height];
        originalBgra.CopyPixels(originalPixels, colorStride, 0);
        cleanedBgra.CopyPixels(cleanedPixels, colorStride, 0);
        maskGray.CopyPixels(maskPixels, width, 0);

        var scope = new byte[width * height];
        var allowed = new byte[width * height];

        foreach (BubbleClipRegion region in regions)
        {
            BubblePixelPoint[] shape = BuildBubbleClipShape(region, width, height);
            if (shape.Length < 3)
            {
                continue;
            }

            BubblePixelBox textBox = ToBubblePixelBox(region.TextBox, width, height);
            BubblePixelBox scopeBox = ToBubblePixelBox(
                UnionBubbleRects(
                    region.RenderBox.Expand(0.48, 0.72),
                    region.TextBox.Expand(0.85, 1.20)),
                width,
                height);

            double margin = Math.Clamp(Math.Min(textBox.Width, textBox.Height) * 0.075, 2.0, 12.0);
            for (int y = scopeBox.Top; y < scopeBox.Bottom; y++)
            {
                double sampleY = y + 0.5;
                int row = y * width;
                for (int x = scopeBox.Left; x < scopeBox.Right; x++)
                {
                    int pixel = row + x;
                    scope[pixel] = 1;
                    double sampleX = x + 0.5;
                    if (PointInsideBubblePolygon(sampleX, sampleY, shape)
                        || DistanceToBubblePolygon(sampleX, sampleY, shape) <= margin)
                    {
                        allowed[pixel] = 1;
                    }
                }
            }
        }

        bool changed = false;
        for (int pixel = 0; pixel < scope.Length; pixel++)
        {
            if (scope[pixel] == 0 || allowed[pixel] != 0)
            {
                continue;
            }

            int offset = pixel * 4;
            int difference = Math.Max(
                Math.Abs(cleanedPixels[offset] - originalPixels[offset]),
                Math.Max(
                    Math.Abs(cleanedPixels[offset + 1] - originalPixels[offset + 1]),
                    Math.Abs(cleanedPixels[offset + 2] - originalPixels[offset + 2])));

            if (difference > 1)
            {
                cleanedPixels[offset] = originalPixels[offset];
                cleanedPixels[offset + 1] = originalPixels[offset + 1];
                cleanedPixels[offset + 2] = originalPixels[offset + 2];
                cleanedPixels[offset + 3] = originalPixels[offset + 3];
                changed = true;
            }

            if (maskPixels[pixel] != 0)
            {
                maskPixels[pixel] = 0;
                changed = true;
            }
        }

        if (!changed)
        {
            return new BubbleClipResult(cleaned, mask, false);
        }

        BitmapSource fixedCleaned = BitmapSource.Create(
            width,
            height,
            originalBgra.DpiX,
            originalBgra.DpiY,
            PixelFormats.Bgra32,
            null,
            cleanedPixels,
            colorStride);
        fixedCleaned.Freeze();

        BitmapSource fixedMask = BitmapSource.Create(
            width,
            height,
            maskGray.DpiX,
            maskGray.DpiY,
            PixelFormats.Gray8,
            null,
            maskPixels,
            width);
        fixedMask.Freeze();

        return new BubbleClipResult(fixedCleaned, fixedMask, true);
    }

    private static BubblePixelPoint[] BuildBubbleClipShape(
        BubbleClipRegion region,
        int width,
        int height)
    {
        if (region.Shape.Length >= 3)
        {
            return region.Shape
                .Select(point => new BubblePixelPoint(
                    Math.Clamp(point.X / 1000 * width, 0, width - 1),
                    Math.Clamp(point.Y / 1000 * height, 0, height - 1)))
                .Distinct()
                .ToArray();
        }

        NormalizedRect fallback = region.BubbleBox
            ?? region.TextBox.Expand(0.34, 0.58);
        double centerX = (fallback.X + fallback.Width / 2) / 1000 * width;
        double centerY = (fallback.Y + fallback.Height / 2) / 1000 * height;
        double radiusX = fallback.Width / 2000 * width;
        double radiusY = fallback.Height / 2000 * height;

        var points = new BubblePixelPoint[48];
        for (int index = 0; index < points.Length; index++)
        {
            double angle = Math.PI * 2 * index / points.Length;
            points[index] = new BubblePixelPoint(
                centerX + Math.Cos(angle) * radiusX,
                centerY + Math.Sin(angle) * radiusY);
        }
        return points;
    }

    private static bool PointInsideBubblePolygon(
        double x,
        double y,
        IReadOnlyList<BubblePixelPoint> polygon)
    {
        bool inside = false;
        for (int current = 0, previous = polygon.Count - 1;
             current < polygon.Count;
             previous = current++)
        {
            BubblePixelPoint a = polygon[current];
            BubblePixelPoint b = polygon[previous];
            bool crosses = (a.Y > y) != (b.Y > y)
                && x < (b.X - a.X) * (y - a.Y) / Math.Max(0.000001, b.Y - a.Y) + a.X;
            if (crosses)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static double DistanceToBubblePolygon(
        double x,
        double y,
        IReadOnlyList<BubblePixelPoint> polygon)
    {
        double best = double.PositiveInfinity;
        for (int index = 0; index < polygon.Count; index++)
        {
            BubblePixelPoint first = polygon[index];
            BubblePixelPoint second = polygon[(index + 1) % polygon.Count];
            best = Math.Min(best, DistanceToBubbleSegment(x, y, first, second));
        }
        return best;
    }

    private static double DistanceToBubbleSegment(
        double x,
        double y,
        BubblePixelPoint first,
        BubblePixelPoint second)
    {
        double deltaX = second.X - first.X;
        double deltaY = second.Y - first.Y;
        double lengthSquared = deltaX * deltaX + deltaY * deltaY;
        if (lengthSquared <= 0.000001)
        {
            return Math.Sqrt(Math.Pow(x - first.X, 2) + Math.Pow(y - first.Y, 2));
        }

        double position = Math.Clamp(
            ((x - first.X) * deltaX + (y - first.Y) * deltaY) / lengthSquared,
            0,
            1);
        double nearestX = first.X + position * deltaX;
        double nearestY = first.Y + position * deltaY;
        return Math.Sqrt(Math.Pow(x - nearestX, 2) + Math.Pow(y - nearestY, 2));
    }

    private static NormalizedRect UnionBubbleRects(NormalizedRect first, NormalizedRect second)
    {
        double left = Math.Min(first.X, second.X);
        double top = Math.Min(first.Y, second.Y);
        double right = Math.Max(first.Right, second.Right);
        double bottom = Math.Max(first.Bottom, second.Bottom);
        return new NormalizedRect(left, top, right - left, bottom - top).Clamp();
    }

    private static BubblePixelBox ToBubblePixelBox(NormalizedRect box, int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(box.X / 1000 * width), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(box.Y / 1000 * height), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(box.Right / 1000 * width), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(box.Bottom / 1000 * height), top + 1, height);
        return new BubblePixelBox(left, top, right, bottom);
    }

    private static BitmapSource ConvertBubbleClipBitmap(BitmapSource source, PixelFormat format)
    {
        if (source.Format == format)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(source, format, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource FreezeBubbleClipBitmap(BitmapSource source)
    {
        if (source.IsFrozen)
        {
            return source;
        }

        BitmapSource clone = source.CloneCurrentValue();
        clone.Freeze();
        return clone;
    }

    private static void SaveBubbleClipBitmap(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed record BubbleClipRegion(
        NormalizedRect TextBox,
        NormalizedRect RenderBox,
        NormalizedRect? BubbleBox,
        NormalizedPoint[] Shape);

    private sealed record BubbleClipResult(BitmapSource Cleaned, BitmapSource Mask, bool Changed);
    private readonly record struct BubblePixelPoint(double X, double Y);
    private readonly record struct BubblePixelBox(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}
