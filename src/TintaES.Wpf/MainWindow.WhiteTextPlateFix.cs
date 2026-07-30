using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// El fondo reconstruido nunca debe aparecer como una placa rectangular debajo de la rotulación.
/// Esta última barrera compara el original con el fondo limpio y, en interiores planos de bocadillo,
/// conserva únicamente la reconstrucción situada alrededor de los trazos reales de las letras.
/// Todo cambio rectangular ajeno a esos trazos se restaura desde la página original.
/// </summary>
public partial class MainWindow
{
    private static readonly bool WhiteTextPlateFixRegistered = RegisterWhiteTextPlateFix();

    private bool _whiteTextPlateFixInstalled;
    private bool _whiteTextPlateFixPending;
    private BitmapSource? _whiteTextPlateFixLastInput;
    private int _whiteTextPlateFixGeneration;

    private static bool RegisterWhiteTextPlateFix()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_WhiteTextPlateFixLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_WhiteTextPlateFixLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallWhiteTextPlateFix,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallWhiteTextPlateFix()
    {
        if (_whiteTextPlateFixInstalled)
        {
            QueueWhiteTextPlateFix();
            return;
        }

        _whiteTextPlateFixInstalled = true;
        BusyOverlay.IsVisibleChanged += (_, _) =>
        {
            if (!BusyOverlay.IsVisible)
            {
                QueueWhiteTextPlateFix();
            }
        };
        ResultPreviewButton.Click += (_, _) => QueueWhiteTextPlateFix();
        CleanPreviewButton.Click += (_, _) => QueueWhiteTextPlateFix();

        // También cubre páginas recuperadas de un proyecto o al navegar por un CBZ. LayoutUpdated
        // es frecuente, pero QueueWhiteTextPlateFix sale inmediatamente si el bitmap no ha cambiado.
        PageImage.LayoutUpdated += (_, _) => QueueWhiteTextPlateFix();
        QueueWhiteTextPlateFix();
    }

    private void QueueWhiteTextPlateFix()
    {
        if (_whiteTextPlateFixPending
            || BusyOverlay.IsVisible
            || _originalBitmap is null
            || _cleanedBaseBitmap is null
            || _maskBitmap is null
            || _regions.Count == 0
            || ReferenceEquals(_cleanedBaseBitmap, _whiteTextPlateFixLastInput))
        {
            return;
        }

        BitmapSource original = FreezeForBackground(_originalBitmap);
        BitmapSource cleaned = FreezeForBackground(_cleanedBaseBitmap);
        BitmapSource mask = FreezeForBackground(_maskBitmap);
        WhitePlateRegion[] regions = _regions
            .Where(region => region.IsEnabled && region.Type is "dialogue" or "thought")
            .Select(region => new WhitePlateRegion(region.TextBox))
            .ToArray();
        if (regions.Length == 0)
        {
            _whiteTextPlateFixLastInput = _cleanedBaseBitmap;
            return;
        }

        _whiteTextPlateFixPending = true;
        _whiteTextPlateFixLastInput = _cleanedBaseBitmap;
        int generation = ++_whiteTextPlateFixGeneration;
        _ = ApplyWhiteTextPlateFixAsync(original, cleaned, mask, regions, generation);
    }

    private async Task ApplyWhiteTextPlateFixAsync(
        BitmapSource original,
        BitmapSource cleaned,
        BitmapSource mask,
        IReadOnlyList<WhitePlateRegion> regions,
        int generation)
    {
        WhitePlateResult result;
        try
        {
            result = await Task.Run(() => RemoveWhiteTextPlates(original, cleaned, mask, regions));
        }
        catch
        {
            _whiteTextPlateFixPending = false;
            return;
        }

        if (generation != _whiteTextPlateFixGeneration
            || !ReferenceEquals(_originalBitmap, original)
            && (_originalBitmap?.PixelWidth != original.PixelWidth
                || _originalBitmap?.PixelHeight != original.PixelHeight))
        {
            _whiteTextPlateFixPending = false;
            return;
        }

        _cleanedBaseBitmap = result.Cleaned;
        _cleanedBitmap = result.Cleaned;
        _maskBitmap = result.Mask;
        _whiteTextPlateFixLastInput = result.Cleaned;
        _whiteTextPlateFixPending = false;

        if (_previewMode == "clean" || _previewMode == "result")
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
        if (pageIndex >= 0 && pageIndex < _comicPages.Count)
        {
            ComicBookPageState page = _comicPages[pageIndex];
            string? cleanedPath = page.CleanedPath;
            string? maskPath = page.MaskPath;
            if (!string.IsNullOrWhiteSpace(cleanedPath)
                && !string.IsNullOrWhiteSpace(maskPath))
            {
                await Task.WhenAll(
                    Task.Run(() => SaveBitmap(result.Cleaned, cleanedPath)),
                    Task.Run(() => SaveBitmap(result.Mask, maskPath)));
            }
        }
    }

    private static WhitePlateResult RemoveWhiteTextPlates(
        BitmapSource original,
        BitmapSource cleaned,
        BitmapSource mask,
        IReadOnlyList<WhitePlateRegion> regions)
    {
        BitmapSource originalBgra = ConvertWhitePlateBitmap(original, PixelFormats.Bgra32);
        BitmapSource cleanedBgra = ConvertWhitePlateBitmap(cleaned, PixelFormats.Bgra32);
        BitmapSource maskGray = ConvertWhitePlateBitmap(mask, PixelFormats.Gray8);

        int width = originalBgra.PixelWidth;
        int height = originalBgra.PixelHeight;
        if (cleanedBgra.PixelWidth != width
            || cleanedBgra.PixelHeight != height
            || maskGray.PixelWidth != width
            || maskGray.PixelHeight != height)
        {
            return new WhitePlateResult(cleaned, mask);
        }

        int colorStride = width * 4;
        var originalPixels = new byte[colorStride * height];
        var cleanedPixels = new byte[colorStride * height];
        var maskPixels = new byte[width * height];
        originalBgra.CopyPixels(originalPixels, colorStride, 0);
        cleanedBgra.CopyPixels(cleanedPixels, colorStride, 0);
        maskGray.CopyPixels(maskPixels, width, 0);

        bool changed = false;
        foreach (WhitePlateRegion region in regions)
        {
            PixelBox box = ToWhitePlatePixelBox(region.TextBox.Expand(0.48, 0.70), width, height);
            if (box.Width < 6 || box.Height < 6
                || !TryEstimateWhitePlateBackground(
                    originalPixels,
                    maskPixels,
                    width,
                    box,
                    out WhitePlateColor background,
                    out int backgroundLuminance,
                    out bool lightBackground))
            {
                continue;
            }

            int area = box.Width * box.Height;
            var ink = new byte[area];
            int inkCount = 0;
            int contrastThreshold = lightBackground ? 24 : 30;
            for (int y = box.Top; y < box.Bottom; y++)
            {
                for (int x = box.Left; x < box.Right; x++)
                {
                    int globalPixel = y * width + x;
                    if (maskPixels[globalPixel] == 0)
                    {
                        continue;
                    }

                    int offset = globalPixel * 4;
                    int luminance = Luminance(
                        originalPixels[offset + 2],
                        originalPixels[offset + 1],
                        originalPixels[offset]);
                    int contrast = lightBackground
                        ? backgroundLuminance - luminance
                        : luminance - backgroundLuminance;
                    if (contrast < contrastThreshold)
                    {
                        continue;
                    }

                    ink[(y - box.Top) * box.Width + x - box.Left] = 1;
                    inkCount++;
                }
            }

            double inkRatio = inkCount / (double)Math.Max(1, area);
            if (inkRatio < 0.0015 || inkRatio > 0.34)
            {
                continue;
            }

            int radius = Math.Clamp((int)Math.Round(region.TextBox.Height / 1000 * height / 11.0), 2, 7);
            byte[] support = DilateWhitePlateInk(ink, box.Width, box.Height, radius);

            for (int y = box.Top; y < box.Bottom; y++)
            {
                for (int x = box.Left; x < box.Right; x++)
                {
                    int globalPixel = y * width + x;
                    if (maskPixels[globalPixel] == 0)
                    {
                        continue;
                    }

                    int localPixel = (y - box.Top) * box.Width + x - box.Left;
                    int offset = globalPixel * 4;
                    if (support[localPixel] == 0)
                    {
                        cleanedPixels[offset] = originalPixels[offset];
                        cleanedPixels[offset + 1] = originalPixels[offset + 1];
                        cleanedPixels[offset + 2] = originalPixels[offset + 2];
                        cleanedPixels[offset + 3] = originalPixels[offset + 3];
                        maskPixels[globalPixel] = 0;
                        changed = true;
                        continue;
                    }

                    // En un interior demostrado como plano no dependemos del bloque que haya
                    // producido LaMa: rellenamos exclusivamente la huella de las letras.
                    cleanedPixels[offset] = background.Blue;
                    cleanedPixels[offset + 1] = background.Green;
                    cleanedPixels[offset + 2] = background.Red;
                    cleanedPixels[offset + 3] = 255;
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return new WhitePlateResult(cleaned, mask);
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
        return new WhitePlateResult(fixedCleaned, fixedMask);
    }

    private static bool TryEstimateWhitePlateBackground(
        byte[] pixels,
        byte[] mask,
        int width,
        PixelBox box,
        out WhitePlateColor background,
        out int backgroundLuminance,
        out bool lightBackground)
    {
        var blues = new List<byte>();
        var greens = new List<byte>();
        var reds = new List<byte>();
        var luminances = new List<int>();
        int border = Math.Clamp(Math.Min(box.Width, box.Height) / 9, 2, 12);
        int step = Math.Max(1, Math.Min(box.Width, box.Height) / 72);

        for (int y = box.Top; y < box.Bottom; y += step)
        {
            for (int x = box.Left; x < box.Right; x += step)
            {
                bool onRing = x < box.Left + border
                    || x >= box.Right - border
                    || y < box.Top + border
                    || y >= box.Bottom - border;
                if (!onRing || mask[y * width + x] >= 32)
                {
                    continue;
                }

                int offset = (y * width + x) * 4;
                byte blue = pixels[offset];
                byte green = pixels[offset + 1];
                byte red = pixels[offset + 2];
                blues.Add(blue);
                greens.Add(green);
                reds.Add(red);
                luminances.Add(Luminance(red, green, blue));
            }
        }

        background = default;
        backgroundLuminance = 0;
        lightBackground = true;
        if (luminances.Count < 18)
        {
            return false;
        }

        blues.Sort();
        greens.Sort();
        reds.Sort();
        luminances.Sort();
        int p10 = luminances[(int)Math.Floor((luminances.Count - 1) * 0.10)];
        int p90 = luminances[(int)Math.Ceiling((luminances.Count - 1) * 0.90)];
        if (p90 - p10 > 34)
        {
            return false;
        }

        byte medianBlue = blues[blues.Count / 2];
        byte medianGreen = greens[greens.Count / 2];
        byte medianRed = reds[reds.Count / 2];
        backgroundLuminance = luminances[luminances.Count / 2];
        lightBackground = backgroundLuminance >= 138;
        background = new WhitePlateColor(medianBlue, medianGreen, medianRed);
        return true;
    }

    private static byte[] DilateWhitePlateInk(byte[] ink, int width, int height, int radius)
    {
        var result = new byte[ink.Length];
        int radiusSquared = radius * radius;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (ink[y * width + x] == 0)
                {
                    continue;
                }

                int minY = Math.Max(0, y - radius);
                int maxY = Math.Min(height - 1, y + radius);
                int minX = Math.Max(0, x - radius);
                int maxX = Math.Min(width - 1, x + radius);
                for (int sampleY = minY; sampleY <= maxY; sampleY++)
                {
                    int deltaY = sampleY - y;
                    for (int sampleX = minX; sampleX <= maxX; sampleX++)
                    {
                        int deltaX = sampleX - x;
                        if (deltaX * deltaX + deltaY * deltaY <= radiusSquared)
                        {
                            result[sampleY * width + sampleX] = 1;
                        }
                    }
                }
            }
        }
        return result;
    }

    private static PixelBox ToWhitePlatePixelBox(NormalizedRect box, int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(box.X / 1000 * width), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(box.Y / 1000 * height), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(box.Right / 1000 * width), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(box.Bottom / 1000 * height), top + 1, height);
        return new PixelBox(left, top, right, bottom);
    }

    private static int Luminance(byte red, byte green, byte blue) =>
        (red * 3 + green * 6 + blue) / 10;

    private static BitmapSource ConvertWhitePlateBitmap(BitmapSource source, PixelFormat format)
    {
        if (source.Format == format)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(source, format, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource FreezeForBackground(BitmapSource source)
    {
        if (source.IsFrozen)
        {
            return source;
        }

        BitmapSource clone = source.CloneCurrentValue();
        clone.Freeze();
        return clone;
    }

    private sealed record WhitePlateRegion(NormalizedRect TextBox);
    private sealed record WhitePlateResult(BitmapSource Cleaned, BitmapSource Mask);
    private readonly record struct WhitePlateColor(byte Blue, byte Green, byte Red);
    private readonly record struct PixelBox(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}
