using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Finaliza los trazos manuales mediante bitmaps completos y strides fijos. Evita las escrituras
/// parciales que podían producir franjas verticales y mantiene el guardado fuera del gesto.
/// </summary>
public partial class MainWindow
{
    private static readonly bool SafeMaskBrushRegistered = RegisterSafeMaskBrush();

    private static bool RegisterSafeMaskBrush()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(MainWindow_SafeMaskBrushPreviewMouseUp),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_SafeMaskBrushPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not MainWindow window
            || e.ChangedButton != MouseButton.Left
            || !window._maskStrokeActive
            || window._manualMaskTool == ManualMaskTool.None
            || Mouse.Captured != window.OverlayCanvas)
        {
            return;
        }

        e.Handled = true;
        Point point = window.ClampCanvasPoint(e.GetPosition(window.OverlayCanvas));
        if (window._maskStrokePoints.Count == 0
            || (window._maskStrokePoints[^1] - point).Length > 0.5)
        {
            window._maskStrokePoints.Add(point);
            window._maskStrokePreview?.Points.Add(point);
        }

        window.OverlayCanvas.ReleaseMouseCapture();
        window._maskStrokeActive = false;

        Point[] points = window._maskStrokePoints.ToArray();
        ManualMaskTool tool = window._manualMaskTool;
        double brushSize = window.CurrentMaskBrushSize;
        _ = window.ApplySafeManualMaskStrokeAsync(points, tool, brushSize);
    }

    private async Task ApplySafeManualMaskStrokeAsync(
        IReadOnlyList<Point> points,
        ManualMaskTool tool,
        double brushSize)
    {
        if (_maskEditorBusy
            || _originalBitmap is null
            || points.Count == 0
            || _comicPageIndex < 0
            || _comicPageIndex >= _comicPages.Count)
        {
            RemoveManualMaskPreview();
            return;
        }

        _maskEditorBusy = true;
        int pageIndex = _comicPageIndex;
        BitmapSource original = _originalBitmap;
        BitmapSource cleaned = _cleanedBaseBitmap ?? original;
        BitmapSource? mask = _maskBitmap;

        FooterStatusText.Text = tool == ManualMaskTool.Paint
            ? "Borrando el texto original…"
            : "Recuperando la imagen original…";
        RefreshManualMaskAvailability();
        await Dispatcher.Yield(DispatcherPriority.Render);

        try
        {
            MaskEditResult result = await Task.Run(() => ApplySafeMaskStrokeCore(
                original,
                cleaned,
                mask,
                points,
                brushSize,
                tool == ManualMaskTool.Paint));

            _cleanedBaseBitmap = result.Cleaned;
            _cleanedBitmap = result.Cleaned;
            _maskBitmap = result.Mask;

            ComicBookPageState page = _comicPages[pageIndex];
            page.Processed = true;
            page.Error = null;
            page.Regions.Clear();
            page.Regions.AddRange(_regions);
            PrepareFastDeletionPaths(pageIndex, page, hasMask: true);
            UpdateFastDeletionBitmapCache(pageIndex, page, original, result.Cleaned, result.Mask);

            PageImage.Source = result.Cleaned;
            MaskPreviewButton.IsEnabled = true;
            CleanPreviewButton.IsEnabled = true;
            ResultPreviewButton.IsEnabled = true;
            RemoveManualMaskPreview();

            SetFooterStatus(
                tool == ManualMaskTool.Paint
                    ? "Texto original borrado. Guarda la página cuando termines."
                    : "Zona original recuperada. Guarda la página cuando termines.",
                "#58A77D");
        }
        catch (Exception exception)
        {
            SetFooterStatus("No se pudo aplicar el trazo.", "#EE594B");
            MessageBox.Show(
                this,
                $"No se pudo aplicar el pincel.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            RemoveManualMaskPreview();
            _maskEditorBusy = false;
            RefreshManualMaskAvailability();
            RefreshPageSaveAvailability();
            RefreshEditorToolAvailability();
            ApplyCompactCanvasToolIcons();
        }
    }

    private static MaskEditResult ApplySafeMaskStrokeCore(
        BitmapSource original,
        BitmapSource cleaned,
        BitmapSource? mask,
        IReadOnlyList<Point> points,
        double brushSize,
        bool paint)
    {
        BitmapSource original32 = ConvertMaskEditFormat(original, PixelFormats.Bgra32);
        BitmapSource cleaned32 = ConvertMaskEditFormat(cleaned, PixelFormats.Bgra32);
        int width = original32.PixelWidth;
        int height = original32.PixelHeight;
        if (cleaned32.PixelWidth != width || cleaned32.PixelHeight != height)
        {
            throw new InvalidOperationException("La página original y el fondo no tienen el mismo tamaño.");
        }

        int colorStride = checked(width * 4);
        int maskStride = width;
        byte[] originalPixels = new byte[checked(colorStride * height)];
        byte[] cleanedPixels = new byte[checked(colorStride * height)];
        byte[] maskPixels = new byte[checked(maskStride * height)];
        original32.CopyPixels(originalPixels, colorStride, 0);
        cleaned32.CopyPixels(cleanedPixels, colorStride, 0);

        if (mask is not null)
        {
            BitmapSource mask8 = ConvertMaskEditFormat(mask, PixelFormats.Gray8);
            if (mask8.PixelWidth == width && mask8.PixelHeight == height)
            {
                mask8.CopyPixels(maskPixels, maskStride, 0);
            }
        }

        Int32Rect dirty = CalculateMaskStrokeRect(points, brushSize, width, height);
        bool[] affected = new bool[checked(dirty.Width * dirty.Height)];
        RasterizeMaskStroke(points, brushSize / 2, dirty, affected);
        (byte B, byte G, byte R) fill = SampleSafeBrushFill(
            cleanedPixels,
            maskPixels,
            colorStride,
            maskStride,
            width,
            height,
            dirty);

        for (int localY = 0; localY < dirty.Height; localY++)
        {
            int globalY = dirty.Y + localY;
            for (int localX = 0; localX < dirty.Width; localX++)
            {
                int affectedIndex = localY * dirty.Width + localX;
                if (!affected[affectedIndex])
                {
                    continue;
                }

                int globalX = dirty.X + localX;
                int maskIndex = globalY * maskStride + globalX;
                int colorIndex = globalY * colorStride + globalX * 4;
                if (paint)
                {
                    maskPixels[maskIndex] = 255;
                    cleanedPixels[colorIndex] = fill.B;
                    cleanedPixels[colorIndex + 1] = fill.G;
                    cleanedPixels[colorIndex + 2] = fill.R;
                    cleanedPixels[colorIndex + 3] = 255;
                }
                else
                {
                    maskPixels[maskIndex] = 0;
                    cleanedPixels[colorIndex] = originalPixels[colorIndex];
                    cleanedPixels[colorIndex + 1] = originalPixels[colorIndex + 1];
                    cleanedPixels[colorIndex + 2] = originalPixels[colorIndex + 2];
                    cleanedPixels[colorIndex + 3] = originalPixels[colorIndex + 3];
                }
            }
        }

        BitmapSource cleanedOutput = BitmapSource.Create(
            width,
            height,
            cleaned.DpiX > 0 ? cleaned.DpiX : 96,
            cleaned.DpiY > 0 ? cleaned.DpiY : 96,
            PixelFormats.Bgra32,
            null,
            cleanedPixels,
            colorStride);
        cleanedOutput.Freeze();

        BitmapSource maskOutput = BitmapSource.Create(
            width,
            height,
            original.DpiX > 0 ? original.DpiX : 96,
            original.DpiY > 0 ? original.DpiY : 96,
            PixelFormats.Gray8,
            null,
            maskPixels,
            maskStride);
        maskOutput.Freeze();
        return new MaskEditResult(cleanedOutput, maskOutput);
    }

    private static (byte B, byte G, byte R) SampleSafeBrushFill(
        byte[] cleaned,
        byte[] mask,
        int colorStride,
        int maskStride,
        int width,
        int height,
        Int32Rect dirty)
    {
        int margin = Math.Max(3, Math.Min(dirty.Width, dirty.Height) / 10);
        int left = Math.Max(0, dirty.X - margin);
        int top = Math.Max(0, dirty.Y - margin);
        int right = Math.Min(width - 1, dirty.X + dirty.Width - 1 + margin);
        int bottom = Math.Min(height - 1, dirty.Y + dirty.Height - 1 + margin);
        int step = Math.Max(1, Math.Min(right - left + 1, bottom - top + 1) / 96);
        var blue = new List<byte>();
        var green = new List<byte>();
        var red = new List<byte>();

        void Add(int x, int y)
        {
            int maskIndex = y * maskStride + x;
            if (mask[maskIndex] != 0)
            {
                return;
            }
            int colorIndex = y * colorStride + x * 4;
            blue.Add(cleaned[colorIndex]);
            green.Add(cleaned[colorIndex + 1]);
            red.Add(cleaned[colorIndex + 2]);
        }

        for (int x = left; x <= right; x += step)
        {
            Add(x, top);
            Add(x, bottom);
        }
        for (int y = top; y <= bottom; y += step)
        {
            Add(left, y);
            Add(right, y);
        }

        if (blue.Count == 0)
        {
            return (255, 255, 255);
        }

        blue.Sort();
        green.Sort();
        red.Sort();
        int middle = blue.Count / 2;
        return (blue[middle], green[middle], red[middle]);
    }
}
