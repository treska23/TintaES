using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// La edición manual de bocadillos es deliberadamente simple: el pincel pinta blanco puro y el
/// borrador recupera los píxeles originales. No se muestrean colores, no se reconstruye el fondo y
/// la pintura no se desplaza cuando se mueve una caja de texto.
/// </summary>
public partial class MainWindow
{
    private bool _simpleWhiteMaskPaintingInstalled;
    private readonly HashSet<int> _whiteMaskNormalizedPages = [];
    private readonly System.Threading.SemaphoreSlim _whiteMaskSaveGate = new(1, 1);
    private int _whiteMaskSaveVersion;

    private void InstallSimpleWhiteMaskPainting()
    {
        if (_simpleWhiteMaskPaintingInstalled)
        {
            return;
        }

        InstallManualMaskEditing();
        if (!_manualMaskEditingInstalled)
        {
            Dispatcher.BeginInvoke(InstallSimpleWhiteMaskPainting, DispatcherPriority.ContextIdle);
            return;
        }

        _simpleWhiteMaskPaintingInstalled = true;

        // Sustituye la ruta antigua de máscara, que calculaba un color medio del entorno.
        OverlayCanvas.PreviewMouseLeftButtonDown -= OverlayCanvas_ManualMaskMouseDown;
        OverlayCanvas.PreviewMouseMove -= OverlayCanvas_ManualMaskMouseMove;
        OverlayCanvas.PreviewMouseLeftButtonUp -= OverlayCanvas_ManualMaskMouseUp;
        OverlayCanvas.PreviewMouseLeftButtonDown += OverlayCanvas_WhiteMaskMouseDown;
        OverlayCanvas.PreviewMouseMove += OverlayCanvas_WhiteMaskMouseMove;
        OverlayCanvas.PreviewMouseLeftButtonUp += OverlayCanvas_WhiteMaskMouseUp;

        // La pintura pertenece a la página, no a la caja de texto. Mover una caja no mueve el blanco.
        OverlayCanvas.RemoveHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(ManualMaskThumb_DragStarted));
        OverlayCanvas.RemoveHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(ManualMaskThumb_DragCompleted));

        _maskPaintButton?.SetCurrentValue(
            ToolTipService.ToolTipProperty,
            "Pincel blanco: cubrir el texto original del bocadillo");
        _maskEraseButton?.SetCurrentValue(
            ToolTipService.ToolTipProperty,
            "Borrador: recuperar la imagen original");
    }

    private void OverlayCanvas_WhiteMaskMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_manualMaskTool == ManualMaskTool.None
            || _maskEditorBusy
            || _originalBitmap is null
            || (_manualMaskTool == ManualMaskTool.Erase && _maskBitmap is null))
        {
            return;
        }

        e.Handled = true;
        PushEditorUndoSnapshot();
        _maskStrokeActive = true;
        _maskStrokePoints.Clear();

        Point point = ClampCanvasPoint(e.GetPosition(OverlayCanvas));
        _maskStrokePoints.Add(point);
        _maskStrokePreview = new Polyline
        {
            Stroke = _manualMaskTool == ManualMaskTool.Paint
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(210, 150, 150, 150)),
            StrokeThickness = CurrentMaskBrushSize,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = 0.88,
            IsHitTestVisible = false
        };
        _maskStrokePreview.Points.Add(point);
        _maskStrokePreview.Points.Add(point);
        Panel.SetZIndex(_maskStrokePreview, 60_000);
        OverlayCanvas.Children.Add(_maskStrokePreview);
        OverlayCanvas.CaptureMouse();
    }

    private void OverlayCanvas_WhiteMaskMouseMove(object sender, MouseEventArgs e)
    {
        if (!_maskStrokeActive
            || _maskStrokePreview is null
            || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        e.Handled = true;
        Point point = ClampCanvasPoint(e.GetPosition(OverlayCanvas));
        Point previous = _maskStrokePoints[^1];
        double minimumDistance = Math.Max(1, CurrentMaskBrushSize * 0.08);
        if ((point - previous).Length < minimumDistance)
        {
            return;
        }

        _maskStrokePoints.Add(point);
        _maskStrokePreview.Points.Add(point);
    }

    private void OverlayCanvas_WhiteMaskMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_maskStrokeActive)
        {
            return;
        }

        e.Handled = true;
        Point point = ClampCanvasPoint(e.GetPosition(OverlayCanvas));
        if ((_maskStrokePoints[^1] - point).Length > 0.5)
        {
            _maskStrokePoints.Add(point);
            _maskStrokePreview?.Points.Add(point);
        }

        if (Mouse.Captured == OverlayCanvas)
        {
            OverlayCanvas.ReleaseMouseCapture();
        }
        _maskStrokeActive = false;

        Point[] points = _maskStrokePoints.ToArray();
        ManualMaskTool tool = _manualMaskTool;
        double brushSize = CurrentMaskBrushSize;
        _ = ApplySimpleWhiteMaskStrokeAsync(points, tool, brushSize);
    }

    private async Task ApplySimpleWhiteMaskStrokeAsync(
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
        BitmapSource current = _cleanedBitmap ?? _cleanedBaseBitmap ?? original;
        BitmapSource? mask = _maskBitmap;
        bool normalizeExistingMask = _whiteMaskNormalizedPages.Add(pageIndex);

        try
        {
            MaskEditResult result = await Task.Run(() => ApplySimpleWhiteMaskStrokeCore(
                original,
                current,
                mask,
                points,
                brushSize,
                tool == ManualMaskTool.Paint,
                normalizeExistingMask));

            if (_comicPageIndex != pageIndex)
            {
                return;
            }

            _cleanedBaseBitmap = result.Cleaned;
            _cleanedBitmap = result.Cleaned;
            _maskBitmap = result.Mask;

            ComicBookPageState page = _comicPages[pageIndex];
            page.Processed = true;
            page.Error = null;
            PrepareFastDeletionPaths(pageIndex, page, hasMask: true);
            UpdateFastDeletionBitmapCache(pageIndex, page, original, result.Cleaned, result.Mask);

            _previewMode = "result";
            PageImage.Source = result.Cleaned;
            MaskPreviewButton.IsEnabled = true;
            CleanPreviewButton.IsEnabled = true;
            ResultPreviewButton.IsEnabled = true;
            SetFooterStatus(
                tool == ManualMaskTool.Paint
                    ? "Blanco aplicado sobre el bocadillo."
                    : "Imagen original recuperada.",
                "#58A77D");

            int saveVersion = System.Threading.Interlocked.Increment(ref _whiteMaskSaveVersion);
            _ = PersistSimpleWhiteMaskAsync(
                saveVersion,
                pageIndex,
                page,
                result.Cleaned,
                result.Mask);
        }
        catch (Exception exception)
        {
            SetFooterStatus("No se pudo aplicar el pincel blanco.", "#EE594B");
            MessageBox.Show(
                this,
                $"No se pudo editar el bocadillo.\n\n{exception.Message}",
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
            UpdateComicControls();
        }
    }

    private async Task PersistSimpleWhiteMaskAsync(
        int version,
        int pageIndex,
        ComicBookPageState page,
        BitmapSource cleaned,
        BitmapSource mask)
    {
        await _whiteMaskSaveGate.WaitAsync();
        try
        {
            if (version != _whiteMaskSaveVersion)
            {
                return;
            }

            await SaveFastDeletionBitmapsAsync(page, cleaned, mask);
            if (version == _whiteMaskSaveVersion && pageIndex == _comicPageIndex)
            {
                SetFooterStatus("Página guardada.", "#58A77D");
            }
        }
        catch
        {
            if (version == _whiteMaskSaveVersion && pageIndex == _comicPageIndex)
            {
                SetFooterStatus("El blanco se aplicó, pero no se pudo guardar la página.", "#EE594B");
            }
        }
        finally
        {
            _whiteMaskSaveGate.Release();
        }
    }

    private static MaskEditResult ApplySimpleWhiteMaskStrokeCore(
        BitmapSource original,
        BitmapSource current,
        BitmapSource? mask,
        IReadOnlyList<Point> points,
        double brushSize,
        bool paint,
        bool normalizeExistingMask)
    {
        BitmapSource original32 = ConvertMaskEditFormat(original, PixelFormats.Bgra32);
        BitmapSource current32 = ConvertMaskEditFormat(current, PixelFormats.Bgra32);
        int width = original32.PixelWidth;
        int height = original32.PixelHeight;
        if (current32.PixelWidth != width || current32.PixelHeight != height)
        {
            current32 = original32;
        }

        BitmapSource grayMask = mask is not null
            && mask.PixelWidth == width
            && mask.PixelHeight == height
                ? ConvertMaskEditFormat(mask, PixelFormats.Gray8)
                : CreateEmptyGrayMask(width, height, original32.DpiX, original32.DpiY);

        Int32Rect strokeRect = CalculateMaskStrokeRect(points, brushSize, width, height);
        Int32Rect workRect = normalizeExistingMask
            ? new Int32Rect(0, 0, width, height)
            : strokeRect;

        int colorStride = workRect.Width * 4;
        int maskStride = workRect.Width;
        byte[] originalArea = new byte[colorStride * workRect.Height];
        byte[] currentArea = new byte[colorStride * workRect.Height];
        byte[] maskArea = new byte[maskStride * workRect.Height];
        original32.CopyPixels(workRect, originalArea, colorStride, 0);
        current32.CopyPixels(workRect, currentArea, colorStride, 0);
        grayMask.CopyPixels(workRect, maskArea, maskStride, 0);

        var affected = new bool[workRect.Width * workRect.Height];
        RasterizeMaskStroke(points, brushSize / 2, workRect, affected);

        for (int index = 0; index < maskArea.Length; index++)
        {
            if (affected[index])
            {
                maskArea[index] = paint ? (byte)255 : (byte)0;
            }

            int colorIndex = index * 4;
            if (maskArea[index] > 0)
            {
                currentArea[colorIndex] = 255;
                currentArea[colorIndex + 1] = 255;
                currentArea[colorIndex + 2] = 255;
                currentArea[colorIndex + 3] = 255;
            }
            else if (affected[index] && !paint)
            {
                currentArea[colorIndex] = originalArea[colorIndex];
                currentArea[colorIndex + 1] = originalArea[colorIndex + 1];
                currentArea[colorIndex + 2] = originalArea[colorIndex + 2];
                currentArea[colorIndex + 3] = originalArea[colorIndex + 3];
            }
        }

        var cleanedResult = new WriteableBitmap(current32);
        cleanedResult.WritePixels(workRect, currentArea, colorStride, 0);
        cleanedResult.Freeze();

        var maskResult = new WriteableBitmap(grayMask);
        maskResult.WritePixels(workRect, maskArea, maskStride, 0);
        maskResult.Freeze();
        return new MaskEditResult(cleanedResult, maskResult);
    }

    private static BitmapSource CreateEmptyGrayMask(int width, int height, double dpiX, double dpiY)
    {
        int stride = width;
        byte[] pixels = new byte[stride * height];
        BitmapSource result = BitmapSource.Create(
            width,
            height,
            dpiX > 0 ? dpiX : 96,
            dpiY > 0 ? dpiY : 96,
            PixelFormats.Gray8,
            null,
            pixels,
            stride);
        result.Freeze();
        return result;
    }
}
