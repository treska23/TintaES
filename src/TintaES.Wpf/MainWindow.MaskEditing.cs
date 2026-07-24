using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Edición manual de la máscara. Permite pintar o borrar máscara con un pincel y mantiene el
/// parche limpiado unido a la caja cuando el usuario mueve una traducción.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ManualMaskEditingRegistered = RegisterManualMaskEditing();

    private Button? _maskPaintButton;
    private Button? _maskEraseButton;
    private Slider? _maskBrushSizeSlider;
    private TextBlock? _maskBrushSizeText;
    private ManualMaskTool _manualMaskTool;
    private bool _manualMaskEditingInstalled;
    private bool _maskStrokeActive;
    private bool _maskEditorBusy;
    private readonly List<Point> _maskStrokePoints = [];
    private Polyline? _maskStrokePreview;
    private MaskMoveState? _maskMoveState;

    private static bool RegisterManualMaskEditing()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ManualMaskEditingLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ManualMaskEditingLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallManualMaskEditing,
                DispatcherPriority.SystemIdle);
        }
    }

    private void InstallManualMaskEditing()
    {
        if (_manualMaskEditingInstalled)
        {
            RefreshManualMaskAvailability();
            return;
        }

        _manualMaskEditingInstalled = true;
        InstallManualMaskPanel();

        OverlayCanvas.PreviewMouseLeftButtonDown += OverlayCanvas_ManualMaskMouseDown;
        OverlayCanvas.PreviewMouseMove += OverlayCanvas_ManualMaskMouseMove;
        OverlayCanvas.PreviewMouseLeftButtonUp += OverlayCanvas_ManualMaskMouseUp;
        OverlayCanvas.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(ManualMaskThumb_DragStarted),
            handledEventsToo: true);
        OverlayCanvas.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(ManualMaskThumb_DragCompleted),
            handledEventsToo: true);

        PreviewKeyDown += MainWindow_ManualMaskPreviewKeyDown;
        LayoutUpdated += (_, _) => RefreshManualMaskAvailability();
        RefreshManualMaskAvailability();
    }

    private void InstallManualMaskPanel()
    {
        if (DeleteRegionButton.Parent is not StackPanel editorPanel)
        {
            return;
        }

        Style? toolbarStyle = FindResource("ToolbarButton") as Style;
        _maskPaintButton = new Button
        {
            Content = "Pincel",
            Style = toolbarStyle,
            Margin = new Thickness(0, 0, 7, 0),
            ToolTip = "Añadir máscara sobre la página"
        };
        _maskPaintButton.Click += (_, _) => ToggleManualMaskTool(ManualMaskTool.Paint);

        _maskEraseButton = new Button
        {
            Content = "Borrador",
            Style = toolbarStyle,
            Margin = new Thickness(0),
            ToolTip = "Quitar máscara y recuperar la imagen original"
        };
        _maskEraseButton.Click += (_, _) => ToggleManualMaskTool(ManualMaskTool.Erase);

        _maskBrushSizeText = new TextBlock
        {
            Text = "64 px",
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = FindResource("MutedBrush") as Brush,
            FontSize = 10
        };
        _maskBrushSizeSlider = new Slider
        {
            Minimum = 12,
            Maximum = 240,
            Value = 64,
            TickFrequency = 4,
            IsSnapToTickEnabled = false,
            Margin = new Thickness(0, 4, 0, 0)
        };
        _maskBrushSizeSlider.ValueChanged += (_, _) =>
        {
            if (_maskBrushSizeText is not null)
            {
                _maskBrushSizeText.Text = $"{Math.Round(CurrentMaskBrushSize)} px";
            }
        };

        var titleGrid = new Grid();
        titleGrid.Children.Add(new TextBlock
        {
            Text = "MÁSCARA MANUAL",
            Style = FindResource("LabelText") as Style
        });
        titleGrid.Children.Add(_maskBrushSizeText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 9, 0, 0)
        };
        buttons.Children.Add(_maskPaintButton);
        buttons.Children.Add(_maskEraseButton);

        var panel = new StackPanel();
        panel.Children.Add(titleGrid);
        panel.Children.Add(new TextBlock
        {
            Text = "Pinta para borrar contenido; usa el borrador para recuperarlo.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = FindResource("MutedBrush") as Brush,
            FontSize = 10,
            Margin = new Thickness(0, 5, 0, 0)
        });
        panel.Children.Add(buttons);
        panel.Children.Add(_maskBrushSizeSlider);

        var border = new Border
        {
            BorderBrush = FindResource("LineBrush") as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(11),
            Margin = new Thickness(0, 18, 0, 0),
            Child = panel
        };

        int deleteIndex = editorPanel.Children.IndexOf(DeleteRegionButton);
        editorPanel.Children.Insert(Math.Max(0, deleteIndex), border);
    }

    private double CurrentMaskBrushSize => _maskBrushSizeSlider?.Value ?? 64;

    private void ToggleManualMaskTool(ManualMaskTool tool)
    {
        SetManualMaskTool(_manualMaskTool == tool ? ManualMaskTool.None : tool);
    }

    private void SetManualMaskTool(ManualMaskTool tool)
    {
        if (_maskEditorBusy || _originalBitmap is null)
        {
            return;
        }

        if (_drawingRegion)
        {
            SetDrawingRegionMode(false);
        }

        CancelManualMaskStroke();
        _manualMaskTool = tool;
        if (tool != ManualMaskTool.None)
        {
            ShowPreviewMode("result");
            OverlayCanvas.Cursor = Cursors.Cross;
            SetFooterStatus(
                tool == ManualMaskTool.Paint
                    ? "Pincel de máscara activo. Arrastra sobre lo que quieras ocultar."
                    : "Borrador de máscara activo. Arrastra para recuperar la imagen original.",
                "#4CB2BB");
        }
        else
        {
            OverlayCanvas.Cursor = _drawingRegion ? Cursors.Cross : Cursors.Arrow;
            SetFooterStatus("Edición de máscara finalizada.", "#6C747A");
        }

        UpdateManualMaskButtonState();
    }

    private void UpdateManualMaskButtonState()
    {
        if (_maskPaintButton is not null)
        {
            bool active = _manualMaskTool == ManualMaskTool.Paint;
            _maskPaintButton.Content = active ? "✓ Pincel" : "Pincel";
            _maskPaintButton.BorderBrush = active ? FindResource("AccentBrush") as Brush : FindResource("LineBrush") as Brush;
        }
        if (_maskEraseButton is not null)
        {
            bool active = _manualMaskTool == ManualMaskTool.Erase;
            _maskEraseButton.Content = active ? "✓ Borrador" : "Borrador";
            _maskEraseButton.BorderBrush = active ? FindResource("AccentBrush") as Brush : FindResource("LineBrush") as Brush;
        }
    }

    private void OverlayCanvas_ManualMaskMouseDown(object sender, MouseButtonEventArgs e)
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
                ? new SolidColorBrush(Color.FromArgb(180, 238, 89, 75))
                : new SolidColorBrush(Color.FromArgb(210, 242, 238, 229)),
            StrokeThickness = CurrentMaskBrushSize,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = 0.72,
            IsHitTestVisible = false
        };
        _maskStrokePreview.Points.Add(point);
        _maskStrokePreview.Points.Add(point);
        Panel.SetZIndex(_maskStrokePreview, 60_000);
        OverlayCanvas.Children.Add(_maskStrokePreview);
        OverlayCanvas.CaptureMouse();
    }

    private void OverlayCanvas_ManualMaskMouseMove(object sender, MouseEventArgs e)
    {
        if (!_maskStrokeActive || _maskStrokePreview is null || e.LeftButton != MouseButtonState.Pressed)
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

    private void OverlayCanvas_ManualMaskMouseUp(object sender, MouseButtonEventArgs e)
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
        _ = ApplyManualMaskStrokeAsync(points, tool, brushSize);
    }

    private async Task ApplyManualMaskStrokeAsync(
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
        _pageNavigationBusy = true;
        int pageIndex = _comicPageIndex;
        BitmapSource original = _originalBitmap;
        BitmapSource cleaned = _cleanedBaseBitmap ?? original;
        BitmapSource? mask = _maskBitmap;

        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = true;
        FooterStatusText.Text = tool == ManualMaskTool.Paint
            ? "Aplicando el pincel de máscara…"
            : "Borrando máscara y recuperando el original…";
        RefreshManualMaskAvailability();
        RefreshPageSaveAvailability();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            MaskEditResult result = await Task.Run(() => ApplyMaskStrokeCore(
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
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            FooterStatusText.Text = "Cambio visible. Guardando la página en segundo plano…";
            await SaveFastDeletionBitmapsAsync(page, result.Cleaned, result.Mask);
            SetFooterStatus(
                tool == ManualMaskTool.Paint
                    ? "Máscara añadida y fondo actualizado."
                    : "Máscara borrada y contenido original recuperado.",
                "#58A77D");
        }
        catch (Exception exception)
        {
            SetFooterStatus("No se pudo editar la máscara.", "#EE594B");
            MessageBox.Show(
                this,
                $"No se pudo editar la máscara.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            RemoveManualMaskPreview();
            _maskEditorBusy = false;
            _pageNavigationBusy = false;
            FooterProgressBar.IsIndeterminate = false;
            FooterProgressBar.Visibility = Visibility.Collapsed;
            RefreshManualMaskAvailability();
            RefreshPageSaveAvailability();
            UpdateComicControls();
        }
    }

    private void ManualMaskThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (_manualMaskTool != ManualMaskTool.None
            || _maskEditorBusy
            || _maskBitmap is null
            || _originalBitmap is null
            || e.OriginalSource is not Thumb { Tag: RegionVisual visual } thumb
            || thumb.Cursor != Cursors.SizeAll)
        {
            _maskMoveState = null;
            return;
        }

        _maskMoveState = new MaskMoveState(
            _comicPageIndex,
            visual.Region.Id,
            visual.Region.RenderBox,
            GetRegionMaskBounds(visual.Region));
    }

    private void ManualMaskThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        MaskMoveState? state = _maskMoveState;
        _maskMoveState = null;
        if (state is null
            || _maskEditorBusy
            || _maskBitmap is null
            || _originalBitmap is null
            || e.OriginalSource is not Thumb { Tag: RegionVisual visual } thumb
            || thumb.Cursor != Cursors.SizeAll
            || visual.Region.Id != state.RegionId
            || state.PageIndex != _comicPageIndex)
        {
            return;
        }

        double deltaX = visual.Region.RenderBox.X - state.OriginalRenderBox.X;
        double deltaY = visual.Region.RenderBox.Y - state.OriginalRenderBox.Y;
        if (Math.Abs(deltaX) < 0.01 && Math.Abs(deltaY) < 0.01)
        {
            return;
        }

        _ = MoveRegionMaskAsync(state, deltaX, deltaY);
    }

    private async Task MoveRegionMaskAsync(MaskMoveState state, double deltaX, double deltaY)
    {
        if (_maskEditorBusy
            || _originalBitmap is null
            || _maskBitmap is null
            || _comicPageIndex != state.PageIndex)
        {
            return;
        }

        _maskEditorBusy = true;
        _pageNavigationBusy = true;
        BitmapSource original = _originalBitmap;
        BitmapSource cleaned = _cleanedBaseBitmap ?? original;
        BitmapSource mask = _maskBitmap;

        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = true;
        FooterStatusText.Text = "Moviendo la máscara junto con el texto…";
        RefreshManualMaskAvailability();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            MaskEditResult result = await Task.Run(() => MoveMaskPatchCore(
                original,
                cleaned,
                mask,
                state.OriginalMaskBounds,
                deltaX,
                deltaY));

            _cleanedBaseBitmap = result.Cleaned;
            _cleanedBitmap = result.Cleaned;
            _maskBitmap = result.Mask;

            ComicBookPageState page = _comicPages[state.PageIndex];
            page.Processed = true;
            page.Error = null;
            page.Regions.Clear();
            page.Regions.AddRange(_regions);
            PrepareFastDeletionPaths(state.PageIndex, page, hasMask: true);
            UpdateFastDeletionBitmapCache(state.PageIndex, page, original, result.Cleaned, result.Mask);

            PageImage.Source = result.Cleaned;
            await SaveFastDeletionBitmapsAsync(page, result.Cleaned, result.Mask);
            SetFooterStatus("Texto y máscara desplazados juntos.", "#58A77D");
        }
        catch (Exception exception)
        {
            SetFooterStatus("El texto se movió, pero no se pudo trasladar su máscara.", "#EE594B");
            MessageBox.Show(
                this,
                $"No se pudo mover la máscara de la zona.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _maskEditorBusy = false;
            _pageNavigationBusy = false;
            FooterProgressBar.IsIndeterminate = false;
            FooterProgressBar.Visibility = Visibility.Collapsed;
            RefreshManualMaskAvailability();
            RefreshPageSaveAvailability();
            UpdateComicControls();
        }
    }

    private void MainWindow_ManualMaskPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_manualMaskTool != ManualMaskTool.None || _maskStrokeActive))
        {
            CancelManualMaskStroke();
            SetManualMaskTool(ManualMaskTool.None);
            e.Handled = true;
        }
    }

    private void CancelManualMaskStroke()
    {
        _maskStrokeActive = false;
        _maskStrokePoints.Clear();
        if (Mouse.Captured == OverlayCanvas)
        {
            OverlayCanvas.ReleaseMouseCapture();
        }
        RemoveManualMaskPreview();
    }

    private void RemoveManualMaskPreview()
    {
        if (_maskStrokePreview is not null)
        {
            OverlayCanvas.Children.Remove(_maskStrokePreview);
            _maskStrokePreview = null;
        }
    }

    private void RefreshManualMaskAvailability()
    {
        bool available = _originalBitmap is not null
            && _comicPageIndex >= 0
            && _comicPageIndex < _comicPages.Count
            && !_comicBatchBusy
            && !_pageNavigationBusy
            && !_maskEditorBusy;

        if (_maskPaintButton is not null)
        {
            _maskPaintButton.IsEnabled = available;
        }
        if (_maskEraseButton is not null)
        {
            _maskEraseButton.IsEnabled = available && _maskBitmap is not null;
        }
        if (_maskBrushSizeSlider is not null)
        {
            _maskBrushSizeSlider.IsEnabled = available;
        }
        UpdateManualMaskButtonState();
    }

    private static MaskEditResult ApplyMaskStrokeCore(
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
            throw new InvalidOperationException("El fondo limpio no tiene el mismo tamaño que la página original.");
        }

        BitmapSource? grayMask = mask is null ? null : ConvertMaskEditFormat(mask, PixelFormats.Gray8);
        if (grayMask is not null && (grayMask.PixelWidth != width || grayMask.PixelHeight != height))
        {
            grayMask = null;
        }

        Int32Rect dirty = CalculateMaskStrokeRect(points, brushSize, width, height);
        int pixelCount = dirty.Width * dirty.Height;
        int cleanStride = dirty.Width * 4;
        int maskStride = dirty.Width;
        byte[] originalArea = new byte[cleanStride * dirty.Height];
        byte[] cleanedArea = new byte[cleanStride * dirty.Height];
        byte[] maskArea = new byte[maskStride * dirty.Height];
        original32.CopyPixels(dirty, originalArea, cleanStride, 0);
        cleaned32.CopyPixels(dirty, cleanedArea, cleanStride, 0);
        grayMask?.CopyPixels(dirty, maskArea, maskStride, 0);

        bool[] affected = new bool[pixelCount];
        RasterizeMaskStroke(points, brushSize / 2, dirty, affected);
        (byte B, byte G, byte R) fill = SampleMaskFillColor(cleanedArea, maskArea, dirty.Width, dirty.Height);

        for (int index = 0; index < affected.Length; index++)
        {
            if (!affected[index])
            {
                continue;
            }

            int colorIndex = index * 4;
            if (paint)
            {
                bool newlyMasked = maskArea[index] == 0;
                maskArea[index] = 255;
                if (newlyMasked)
                {
                    cleanedArea[colorIndex] = fill.B;
                    cleanedArea[colorIndex + 1] = fill.G;
                    cleanedArea[colorIndex + 2] = fill.R;
                    cleanedArea[colorIndex + 3] = 255;
                }
            }
            else
            {
                maskArea[index] = 0;
                cleanedArea[colorIndex] = originalArea[colorIndex];
                cleanedArea[colorIndex + 1] = originalArea[colorIndex + 1];
                cleanedArea[colorIndex + 2] = originalArea[colorIndex + 2];
                cleanedArea[colorIndex + 3] = originalArea[colorIndex + 3];
            }
        }

        var cleanedOutput = new WriteableBitmap(cleaned32);
        cleanedOutput.WritePixels(dirty, cleanedArea, cleanStride, 0);
        cleanedOutput.Freeze();

        WriteableBitmap maskOutput = grayMask is null
            ? new WriteableBitmap(
                width,
                height,
                original.DpiX > 0 ? original.DpiX : 96,
                original.DpiY > 0 ? original.DpiY : 96,
                PixelFormats.Gray8,
                null)
            : new WriteableBitmap(grayMask);
        maskOutput.WritePixels(dirty, maskArea, maskStride, 0);
        maskOutput.Freeze();

        return new MaskEditResult(cleanedOutput, maskOutput);
    }

    private static MaskEditResult MoveMaskPatchCore(
        BitmapSource original,
        BitmapSource cleaned,
        BitmapSource mask,
        NormalizedRect originalMaskBounds,
        double deltaX,
        double deltaY)
    {
        BitmapSource original32 = ConvertMaskEditFormat(original, PixelFormats.Bgra32);
        BitmapSource cleaned32 = ConvertMaskEditFormat(cleaned, PixelFormats.Bgra32);
        BitmapSource mask8 = ConvertMaskEditFormat(mask, PixelFormats.Gray8);
        int width = original32.PixelWidth;
        int height = original32.PixelHeight;
        if (cleaned32.PixelWidth != width
            || cleaned32.PixelHeight != height
            || mask8.PixelWidth != width
            || mask8.PixelHeight != height)
        {
            throw new InvalidOperationException("La máscara y la página no tienen el mismo tamaño.");
        }

        Int32Rect source = NormalizedToMaskRect(originalMaskBounds, width, height);
        int offsetX = (int)Math.Round(deltaX / 1000 * width);
        int offsetY = (int)Math.Round(deltaY / 1000 * height);
        if (offsetX == 0 && offsetY == 0)
        {
            return new MaskEditResult(cleaned32, mask8);
        }

        Int32Rect destination = ClipMaskRect(
            new Int32Rect(source.X + offsetX, source.Y + offsetY, source.Width, source.Height),
            width,
            height);
        Int32Rect dirty = UnionMaskRects(source, destination, width, height);
        int dirtyColorStride = dirty.Width * 4;
        int dirtyMaskStride = dirty.Width;
        byte[] dirtyOriginal = new byte[dirtyColorStride * dirty.Height];
        byte[] dirtyCleaned = new byte[dirtyColorStride * dirty.Height];
        byte[] dirtyMask = new byte[dirtyMaskStride * dirty.Height];
        original32.CopyPixels(dirty, dirtyOriginal, dirtyColorStride, 0);
        cleaned32.CopyPixels(dirty, dirtyCleaned, dirtyColorStride, 0);
        mask8.CopyPixels(dirty, dirtyMask, dirtyMaskStride, 0);

        int sourceColorStride = source.Width * 4;
        int sourceMaskStride = source.Width;
        byte[] sourceCleaned = new byte[sourceColorStride * source.Height];
        byte[] sourceMask = new byte[sourceMaskStride * source.Height];
        cleaned32.CopyPixels(source, sourceCleaned, sourceColorStride, 0);
        mask8.CopyPixels(source, sourceMask, sourceMaskStride, 0);

        // Primero restauramos el hueco antiguo. Se usa una copia separada del origen para que
        // los desplazamientos que se solapan no destruyan el parche antes de recolocarlo.
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int sourceMaskIndex = y * sourceMaskStride + x;
                if (sourceMask[sourceMaskIndex] == 0)
                {
                    continue;
                }

                int globalX = source.X + x;
                int globalY = source.Y + y;
                int dirtyX = globalX - dirty.X;
                int dirtyY = globalY - dirty.Y;
                int dirtyMaskIndex = dirtyY * dirtyMaskStride + dirtyX;
                int dirtyColorIndex = dirtyY * dirtyColorStride + dirtyX * 4;
                dirtyMask[dirtyMaskIndex] = 0;
                dirtyCleaned[dirtyColorIndex] = dirtyOriginal[dirtyColorIndex];
                dirtyCleaned[dirtyColorIndex + 1] = dirtyOriginal[dirtyColorIndex + 1];
                dirtyCleaned[dirtyColorIndex + 2] = dirtyOriginal[dirtyColorIndex + 2];
                dirtyCleaned[dirtyColorIndex + 3] = dirtyOriginal[dirtyColorIndex + 3];
            }
        }

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int sourceMaskIndex = y * sourceMaskStride + x;
                byte maskValue = sourceMask[sourceMaskIndex];
                if (maskValue == 0)
                {
                    continue;
                }

                int destinationX = source.X + x + offsetX;
                int destinationY = source.Y + y + offsetY;
                if (destinationX < 0 || destinationX >= width || destinationY < 0 || destinationY >= height)
                {
                    continue;
                }

                int dirtyX = destinationX - dirty.X;
                int dirtyY = destinationY - dirty.Y;
                int dirtyMaskIndex = dirtyY * dirtyMaskStride + dirtyX;
                int dirtyColorIndex = dirtyY * dirtyColorStride + dirtyX * 4;
                int sourceColorIndex = y * sourceColorStride + x * 4;
                dirtyMask[dirtyMaskIndex] = maskValue;
                dirtyCleaned[dirtyColorIndex] = sourceCleaned[sourceColorIndex];
                dirtyCleaned[dirtyColorIndex + 1] = sourceCleaned[sourceColorIndex + 1];
                dirtyCleaned[dirtyColorIndex + 2] = sourceCleaned[sourceColorIndex + 2];
                dirtyCleaned[dirtyColorIndex + 3] = sourceCleaned[sourceColorIndex + 3];
            }
        }

        var cleanedOutput = new WriteableBitmap(cleaned32);
        cleanedOutput.WritePixels(dirty, dirtyCleaned, dirtyColorStride, 0);
        cleanedOutput.Freeze();
        var maskOutput = new WriteableBitmap(mask8);
        maskOutput.WritePixels(dirty, dirtyMask, dirtyMaskStride, 0);
        maskOutput.Freeze();
        return new MaskEditResult(cleanedOutput, maskOutput);
    }

    private static void RasterizeMaskStroke(
        IReadOnlyList<Point> points,
        double radius,
        Int32Rect dirty,
        bool[] affected)
    {
        double safeRadius = Math.Max(1, radius);
        if (points.Count == 1)
        {
            MarkMaskCircle(points[0], safeRadius, dirty, affected);
            return;
        }

        for (int segment = 1; segment < points.Count; segment++)
        {
            Point start = points[segment - 1];
            Point end = points[segment];
            Vector vector = end - start;
            double distance = vector.Length;
            int steps = Math.Max(1, (int)Math.Ceiling(distance / Math.Max(1, safeRadius * 0.35)));
            for (int step = 0; step <= steps; step++)
            {
                double factor = step / (double)steps;
                MarkMaskCircle(start + vector * factor, safeRadius, dirty, affected);
            }
        }
    }

    private static void MarkMaskCircle(Point center, double radius, Int32Rect dirty, bool[] affected)
    {
        int left = Math.Max(dirty.X, (int)Math.Floor(center.X - radius));
        int top = Math.Max(dirty.Y, (int)Math.Floor(center.Y - radius));
        int right = Math.Min(dirty.X + dirty.Width - 1, (int)Math.Ceiling(center.X + radius));
        int bottom = Math.Min(dirty.Y + dirty.Height - 1, (int)Math.Ceiling(center.Y + radius));
        double squaredRadius = radius * radius;

        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                double dx = x + 0.5 - center.X;
                double dy = y + 0.5 - center.Y;
                if (dx * dx + dy * dy <= squaredRadius)
                {
                    affected[(y - dirty.Y) * dirty.Width + (x - dirty.X)] = true;
                }
            }
        }
    }

    private static (byte B, byte G, byte R) SampleMaskFillColor(
        byte[] cleaned,
        byte[] mask,
        int width,
        int height)
    {
        long blue = 0;
        long green = 0;
        long red = 0;
        long count = 0;
        int step = Math.Max(1, Math.Min(width, height) / 128);

        void AddSample(int x, int y)
        {
            int maskIndex = y * width + x;
            if (mask[maskIndex] != 0)
            {
                return;
            }
            int colorIndex = maskIndex * 4;
            blue += cleaned[colorIndex];
            green += cleaned[colorIndex + 1];
            red += cleaned[colorIndex + 2];
            count++;
        }

        for (int x = 0; x < width; x += step)
        {
            AddSample(x, 0);
            AddSample(x, height - 1);
        }
        for (int y = 0; y < height; y += step)
        {
            AddSample(0, y);
            AddSample(width - 1, y);
        }

        return count == 0
            ? ((byte)255, (byte)255, (byte)255)
            : ((byte)(blue / count), (byte)(green / count), (byte)(red / count));
    }

    private static Int32Rect CalculateMaskStrokeRect(
        IReadOnlyList<Point> points,
        double brushSize,
        int width,
        int height)
    {
        double radius = Math.Max(1, brushSize / 2) + 2;
        double left = points.Min(point => point.X) - radius;
        double top = points.Min(point => point.Y) - radius;
        double right = points.Max(point => point.X) + radius;
        double bottom = points.Max(point => point.Y) + radius;
        return ClipMaskRect(
            new Int32Rect(
                (int)Math.Floor(left),
                (int)Math.Floor(top),
                Math.Max(1, (int)Math.Ceiling(right - left)),
                Math.Max(1, (int)Math.Ceiling(bottom - top))),
            width,
            height);
    }

    private static Int32Rect NormalizedToMaskRect(NormalizedRect rect, int width, int height)
    {
        int x = Math.Clamp((int)Math.Floor(rect.X / 1000 * width), 0, Math.Max(0, width - 1));
        int y = Math.Clamp((int)Math.Floor(rect.Y / 1000 * height), 0, Math.Max(0, height - 1));
        int right = Math.Clamp((int)Math.Ceiling(rect.Right / 1000 * width), x + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom / 1000 * height), y + 1, height);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private static Int32Rect ClipMaskRect(Int32Rect rect, int width, int height)
    {
        int left = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        int top = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        int right = Math.Clamp(rect.X + rect.Width, left + 1, width);
        int bottom = Math.Clamp(rect.Y + rect.Height, top + 1, height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    private static Int32Rect UnionMaskRects(Int32Rect first, Int32Rect second, int width, int height)
    {
        int left = Math.Min(first.X, second.X);
        int top = Math.Min(first.Y, second.Y);
        int right = Math.Max(first.X + first.Width, second.X + second.Width);
        int bottom = Math.Max(first.Y + first.Height, second.Y + second.Height);
        return ClipMaskRect(new Int32Rect(left, top, right - left, bottom - top), width, height);
    }

    private static BitmapSource ConvertMaskEditFormat(BitmapSource source, PixelFormat format)
    {
        if (source.Format == format)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(source, format, null, 0);
        converted.Freeze();
        return converted;
    }

    private enum ManualMaskTool
    {
        None,
        Paint,
        Erase
    }

    private sealed record MaskMoveState(
        int PageIndex,
        Guid RegionId,
        NormalizedRect OriginalRenderBox,
        NormalizedRect OriginalMaskBounds);

    private sealed record MaskEditResult(BitmapSource Cleaned, BitmapSource Mask);
}
