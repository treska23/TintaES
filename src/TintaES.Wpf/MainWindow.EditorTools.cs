using System.ComponentModel;
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
/// Herramientas de edición destructiva controlada para la página visible. El usuario puede
/// eliminar una zona completa —texto, máscara y región—, dibujar zonas nuevas y deshacer o
/// rehacer tanto los cambios de rotulación como los cambios de fondo.
/// </summary>
public partial class MainWindow
{
    private const int EditorHistoryLimit = 30;
    private static readonly bool EditorToolsRegistered = RegisterEditorTools();

    private readonly Dictionary<int, EditorPageHistory> _editorPageHistories = [];
    private Button? _undoEditorButton;
    private Button? _redoEditorButton;
    private bool _editorToolsInstalled;
    private bool _applyingEditorSnapshot;
    private bool _drawingRegion;
    private bool _drawingPointerCaptured;
    private Point _drawingStart;
    private Rectangle? _drawingPreview;
    private EditorSnapshot? _textEditBaseline;
    private Guid? _textEditRegionId;
    private string? _editorHistorySessionKey;

    private static bool RegisterEditorTools()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_EditorToolsLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_EditorToolsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(window.InstallEditorTools, DispatcherPriority.ContextIdle);
        }
    }

    private void InstallEditorTools()
    {
        if (_editorToolsInstalled)
        {
            RefreshEditorToolAvailability();
            return;
        }

        _editorToolsInstalled = true;

        AddRegionButton.Click -= AddRegionButton_Click;
        AddRegionButton.Click += DrawRegionButton_Click;
        AddRegionButton.Content = "▭ Dibujar zona";
        AddRegionButton.ToolTip = "Arrastra sobre la página para crear una máscara y una zona de texto nuevas";

        DeleteRegionButton.Click -= DeleteRegionButton_Click;
        DeleteRegionButton.Click += DeleteSelectedRegionCompletely_Click;
        DeleteRegionButton.Content = "Eliminar";
        DeleteRegionButton.ToolTip = "Eliminar la traducción, la máscara y la caja seleccionadas";

        if (AddRegionButton.Parent is StackPanel toolbar)
        {
            Style? toolbarStyle = FindResource("ToolbarButton") as Style;
            _undoEditorButton = new Button
            {
                Content = "↶",
                Width = 38,
                Height = 34,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 5, 0),
                Style = toolbarStyle,
                ToolTip = "Deshacer (Ctrl+Z)"
            };
            _undoEditorButton.Click += (_, _) => UndoEditorChange();

            _redoEditorButton = new Button
            {
                Content = "↷",
                Width = 38,
                Height = 34,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 0),
                Style = toolbarStyle,
                ToolTip = "Rehacer (Ctrl+Y)"
            };
            _redoEditorButton.Click += (_, _) => RedoEditorChange();

            int index = toolbar.Children.IndexOf(AddRegionButton);
            toolbar.Children.Insert(Math.Max(0, index), _undoEditorButton);
            toolbar.Children.Insert(Math.Max(0, index + 1), _redoEditorButton);
        }

        OverlayCanvas.PreviewMouseLeftButtonDown += OverlayCanvas_DrawRegionMouseDown;
        OverlayCanvas.PreviewMouseMove += OverlayCanvas_DrawRegionMouseMove;
        OverlayCanvas.PreviewMouseLeftButtonUp += OverlayCanvas_DrawRegionMouseUp;
        OverlayCanvas.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(EditorThumb_DragStarted),
            handledEventsToo: true);
        OverlayCanvas.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(EditorThumb_DragCompleted),
            handledEventsToo: true);

        RegionListBox.SelectionChanged += (_, _) => RefreshEditorToolAvailability();
        PreviewKeyDown += MainWindow_EditorToolsPreviewKeyDown;

        TranslationTextBox.GotKeyboardFocus += TranslationTextBox_EditorGotFocus;
        TranslationTextBox.LostKeyboardFocus += TranslationTextBox_EditorLostFocus;

        foreach (UIElement control in new UIElement[]
                 {
                     RegionVisibleCheckBox,
                     TypeComboBox,
                     CleanupComboBox,
                     FontCategoryComboBox,
                     FontScaleSlider,
                     BoldCheckBox,
                     ItalicCheckBox,
                     UppercaseCheckBox
                 })
        {
            control.PreviewMouseLeftButtonDown += EditorVisualControl_PreviewMouseLeftButtonDown;
        }

        LayoutUpdated += (_, _) => RefreshEditorToolAvailability();
        RefreshEditorToolAvailability();
    }

    private void DrawRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_originalBitmap is null || _comicBatchBusy || _pageNavigationBusy)
        {
            return;
        }

        SetDrawingRegionMode(!_drawingRegion);
    }

    private void SetDrawingRegionMode(bool enabled)
    {
        _drawingRegion = enabled;
        _drawingPointerCaptured = false;
        RemoveDrawingPreview();

        AddRegionButton.Content = enabled ? "Cancelar dibujo" : "▭ Dibujar zona";
        AddRegionButton.ToolTip = enabled
            ? "Arrastra sobre la página. Esc cancela."
            : "Arrastra sobre la página para crear una máscara y una zona de texto nuevas";
        OverlayCanvas.Cursor = enabled ? Cursors.Cross : Cursors.Arrow;

        if (enabled)
        {
            ShowPreviewMode("result");
            SetFooterStatus("Dibuja el rectángulo que debe limpiarse y contener el texto nuevo. Esc cancela.", "#4CB2BB");
        }
        else if (_originalBitmap is not null)
        {
            SetFooterStatus("Edición de zonas preparada.", "#6C747A");
        }
    }

    private void OverlayCanvas_DrawRegionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_drawingRegion || _originalBitmap is null)
        {
            return;
        }

        e.Handled = true;
        _drawingStart = ClampCanvasPoint(e.GetPosition(OverlayCanvas));
        _drawingPointerCaptured = OverlayCanvas.CaptureMouse();

        _drawingPreview = new Rectangle
        {
            Stroke = FindResource("AccentBrush") as Brush ?? Brushes.IndianRed,
            StrokeThickness = Math.Max(2, 2 / CurrentZoom),
            StrokeDashArray = [6, 4],
            Fill = new SolidColorBrush(Color.FromArgb(36, 238, 89, 75)),
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_drawingPreview, 50_000);
        Canvas.SetLeft(_drawingPreview, _drawingStart.X);
        Canvas.SetTop(_drawingPreview, _drawingStart.Y);
        OverlayCanvas.Children.Add(_drawingPreview);
    }

    private void OverlayCanvas_DrawRegionMouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawingRegion || !_drawingPointerCaptured || _drawingPreview is null)
        {
            return;
        }

        e.Handled = true;
        Point current = ClampCanvasPoint(e.GetPosition(OverlayCanvas));
        double left = Math.Min(_drawingStart.X, current.X);
        double top = Math.Min(_drawingStart.Y, current.Y);
        double width = Math.Abs(current.X - _drawingStart.X);
        double height = Math.Abs(current.Y - _drawingStart.Y);

        Canvas.SetLeft(_drawingPreview, left);
        Canvas.SetTop(_drawingPreview, top);
        _drawingPreview.Width = width;
        _drawingPreview.Height = height;
    }

    private void OverlayCanvas_DrawRegionMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawingRegion || !_drawingPointerCaptured || _originalBitmap is null)
        {
            return;
        }

        e.Handled = true;
        Point end = ClampCanvasPoint(e.GetPosition(OverlayCanvas));
        OverlayCanvas.ReleaseMouseCapture();
        _drawingPointerCaptured = false;
        RemoveDrawingPreview();

        double left = Math.Min(_drawingStart.X, end.X);
        double top = Math.Min(_drawingStart.Y, end.Y);
        double width = Math.Abs(end.X - _drawingStart.X);
        double height = Math.Abs(end.Y - _drawingStart.Y);
        SetDrawingRegionMode(false);

        if (width < 8 || height < 8)
        {
            SetFooterStatus("La zona era demasiado pequeña y no se ha creado.", "#C99A35");
            return;
        }

        PushEditorUndoSnapshot();
        EnsureCurrentPageEditable();

        var box = new NormalizedRect(
            left / _originalBitmap.PixelWidth * 1000,
            top / _originalBitmap.PixelHeight * 1000,
            width / _originalBitmap.PixelWidth * 1000,
            height / _originalBitmap.PixelHeight * 1000).Clamp();

        var region = new ComicRegion
        {
            Order = _regions.Count == 0 ? 1 : _regions.Max(item => item.Order) + 1,
            Original = string.Empty,
            Translation = string.Empty,
            Type = "dialogue",
            Confidence = 1,
            BubbleConfidence = 1,
            TextBox = box,
            RenderBox = box,
            SafePolygon =
            [
                new NormalizedPoint(box.X, box.Y),
                new NormalizedPoint(box.Right, box.Y),
                new NormalizedPoint(box.Right, box.Bottom),
                new NormalizedPoint(box.X, box.Bottom)
            ],
            CleanupMode = "texture",
            IsManual = true,
            Style = new ComicTextStyle()
        };
        region.PropertyChanged += Region_PropertyChanged;
        _regions.Add(region);

        BitmapSource source = _cleanedBaseBitmap ?? _originalBitmap;
        _cleanedBaseBitmap = _processingService.CleanText(source, [region]);
        _cleanedBitmap = _cleanedBaseBitmap;
        _maskBitmap = PaintMaskArea(_maskBitmap, box, enabled: true);

        PersistCurrentEditorState(bitmapChanged: true);
        RebuildOverlay();
        UpdateRegionCount();
        RegionListBox.SelectedItem = region;
        RegionListBox.ScrollIntoView(region);
        TranslationTextBox.Focus();
        SetFooterStatus("Zona creada. Escribe la traducción o dibuja otra zona.", "#58A77D");
        RefreshEditorToolAvailability();
    }

    private void DeleteSelectedRegionCompletely_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedRegionCompletely();
    }

    private void DeleteSelectedRegionCompletely()
    {
        if (_selectedRegion is null || _originalBitmap is null)
        {
            return;
        }

        PushEditorUndoSnapshot();
        ComicRegion removed = _selectedRegion;
        int oldIndex = _regions.IndexOf(removed);
        NormalizedRect maskBounds = GetRegionMaskBounds(removed);

        BitmapSource source = _cleanedBaseBitmap ?? _originalBitmap;
        _cleanedBaseBitmap = RestoreOriginalArea(source, _originalBitmap, maskBounds);
        _cleanedBitmap = _cleanedBaseBitmap;
        _maskBitmap = PaintMaskArea(_maskBitmap, maskBounds, enabled: false);

        removed.PropertyChanged -= Region_PropertyChanged;
        _regions.Remove(removed);
        RenumberEditorRegions();
        _selectedRegion = null;

        PersistCurrentEditorState(bitmapChanged: true);
        RebuildOverlay();
        UpdateRegionCount();
        RegionListBox.SelectedIndex = _regions.Count == 0
            ? -1
            : Math.Clamp(oldIndex, 0, _regions.Count - 1);

        SetFooterStatus("Zona eliminada: se han quitado la traducción, la máscara y la caja.", "#58A77D");
        RefreshEditorToolAvailability();
    }

    private void MainWindow_EditorToolsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.Z)
        {
            UndoEditorChange();
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.Y)
        {
            RedoEditorChange();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _drawingRegion)
        {
            SetDrawingRegionMode(false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete
            && Keyboard.FocusedElement is not TextBoxBase
            && Keyboard.FocusedElement is not ComboBox)
        {
            DeleteSelectedRegionCompletely();
            e.Handled = true;
        }
    }

    private void UndoEditorChange()
    {
        if (Keyboard.FocusedElement is TextBoxBase textBox && textBox.CanUndo)
        {
            textBox.Undo();
            RefreshEditorToolAvailability();
            return;
        }

        EditorPageHistory? history = GetCurrentEditorHistory(create: false);
        if (history is null || history.Undo.Count == 0)
        {
            return;
        }

        history.Redo.Push(CaptureEditorSnapshot());
        EditorSnapshot target = history.Undo.Pop();
        ApplyEditorSnapshot(target);
        MarkActiveDocumentDirty();
        SetFooterStatus("Cambio deshecho.", "#4CB2BB");
    }

    private void RedoEditorChange()
    {
        if (Keyboard.FocusedElement is TextBoxBase textBox && textBox.CanRedo)
        {
            textBox.Redo();
            RefreshEditorToolAvailability();
            return;
        }

        EditorPageHistory? history = GetCurrentEditorHistory(create: false);
        if (history is null || history.Redo.Count == 0)
        {
            return;
        }

        history.Undo.Push(CaptureEditorSnapshot());
        EditorSnapshot target = history.Redo.Pop();
        ApplyEditorSnapshot(target);
        MarkActiveDocumentDirty();
        SetFooterStatus("Cambio rehecho.", "#4CB2BB");
    }

    private void PushEditorUndoSnapshot()
    {
        if (_applyingEditorSnapshot || _comicPageIndex < 0 || _comicPageIndex >= _comicPages.Count)
        {
            return;
        }

        EditorPageHistory history = GetCurrentEditorHistory(create: true)!;
        history.Undo.Push(CaptureEditorSnapshot());
        MarkActiveDocumentDirty();
        while (history.Undo.Count > EditorHistoryLimit)
        {
            EditorSnapshot[] ordered = history.Undo.Reverse().Skip(1).ToArray();
            history.Undo.Clear();
            foreach (EditorSnapshot snapshot in ordered)
            {
                history.Undo.Push(snapshot);
            }
        }
        history.Redo.Clear();
        RefreshEditorToolAvailability();
    }

    private EditorPageHistory? GetCurrentEditorHistory(bool create)
    {
        if (_comicPageIndex < 0 || _comicPageIndex >= _comicPages.Count)
        {
            return null;
        }

        string sessionKey = BuildActiveDocumentSessionKey();
        if (!string.Equals(sessionKey, _editorHistorySessionKey, StringComparison.OrdinalIgnoreCase))
        {
            _editorHistorySessionKey = sessionKey;
            _editorPageHistories.Clear();
            _textEditBaseline = null;
            _textEditRegionId = null;
        }

        if (_editorPageHistories.TryGetValue(_comicPageIndex, out EditorPageHistory? history))
        {
            return history;
        }
        if (!create)
        {
            return null;
        }

        history = new EditorPageHistory();
        _editorPageHistories[_comicPageIndex] = history;
        return history;
    }

    private EditorSnapshot CaptureEditorSnapshot()
    {
        return new EditorSnapshot(
            _regions.Select(CloneEditorRegion).ToList(),
            _cleanedBaseBitmap,
            _cleanedBitmap,
            _maskBitmap,
            _selectedRegion?.Id,
            _comicPageIndex >= 0 && _comicPageIndex < _comicPages.Count && _comicPages[_comicPageIndex].Processed);
    }

    private void ApplyEditorSnapshot(EditorSnapshot snapshot)
    {
        _applyingEditorSnapshot = true;
        try
        {
            foreach (ComicRegion region in _regions)
            {
                region.PropertyChanged -= Region_PropertyChanged;
            }
            _regions.Clear();
            foreach (ComicRegion stored in snapshot.Regions)
            {
                ComicRegion region = CloneEditorRegion(stored);
                region.PropertyChanged += Region_PropertyChanged;
                _regions.Add(region);
            }

            _cleanedBaseBitmap = snapshot.CleanedBaseBitmap ?? _originalBitmap;
            _cleanedBitmap = snapshot.CleanedBitmap ?? _cleanedBaseBitmap;
            _maskBitmap = snapshot.MaskBitmap;

            if (_comicPageIndex >= 0 && _comicPageIndex < _comicPages.Count)
            {
                _comicPages[_comicPageIndex].Processed = snapshot.Processed;
            }

            _selectedRegion = snapshot.SelectedRegionId is Guid selectedId
                ? _regions.FirstOrDefault(region => region.Id == selectedId)
                : null;

            PersistCurrentEditorState(bitmapChanged: true);
            PageImage.Source = _previewMode switch
            {
                "original" => _originalBitmap,
                "mask" when _maskBitmap is not null => _maskBitmap,
                _ => _cleanedBitmap ?? _cleanedBaseBitmap ?? _originalBitmap
            };
            MaskPreviewButton.IsEnabled = _maskBitmap is not null;
            CleanPreviewButton.IsEnabled = snapshot.Processed;
            ResultPreviewButton.IsEnabled = snapshot.Processed;
            RebuildOverlay();
            UpdateRegionCount();
            RegionListBox.SelectedItem = _selectedRegion;
            ShowRegionEditor(_selectedRegion);
        }
        finally
        {
            _applyingEditorSnapshot = false;
            RefreshEditorToolAvailability();
        }
    }

    private void TranslationTextBox_EditorGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_selectedRegion is null || _applyingEditorSnapshot)
        {
            return;
        }
        _textEditBaseline = CaptureEditorSnapshot();
        _textEditRegionId = _selectedRegion.Id;
    }

    private void TranslationTextBox_EditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_textEditBaseline is null || _textEditRegionId is not Guid regionId)
        {
            return;
        }

        ComicRegion? current = _regions.FirstOrDefault(region => region.Id == regionId);
        ComicRegion? baseline = _textEditBaseline.Regions.FirstOrDefault(region => region.Id == regionId);
        if (current is not null
            && baseline is not null
            && !string.Equals(current.Translation, baseline.Translation, StringComparison.Ordinal))
        {
            EditorPageHistory history = GetCurrentEditorHistory(create: true)!;
            history.Undo.Push(_textEditBaseline);
            history.Redo.Clear();
            MarkActiveDocumentDirty();
        }

        _textEditBaseline = null;
        _textEditRegionId = null;
        PersistVisibleComicPageRegions();
        RefreshEditorToolAvailability();
    }

    private void EditorVisualControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectedRegion is not null && !_applyingEditorSnapshot)
        {
            PushEditorUndoSnapshot();
        }
    }

    private void EditorThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (!_drawingRegion)
        {
            PushEditorUndoSnapshot();
        }
    }

    private void EditorThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        PersistVisibleComicPageRegions();
        RefreshEditorToolAvailability();
    }

    private void EnsureCurrentPageEditable()
    {
        if (_comicPageIndex < 0 || _comicPageIndex >= _comicPages.Count || _originalBitmap is null)
        {
            return;
        }

        ComicBookPageState page = _comicPages[_comicPageIndex];
        page.Processed = true;
        page.Error = null;
        _cleanedBaseBitmap ??= _originalBitmap;
        _cleanedBitmap ??= _cleanedBaseBitmap;
        CleanPreviewButton.IsEnabled = true;
        ResultPreviewButton.IsEnabled = true;
    }

    private void PersistCurrentEditorState(bool bitmapChanged)
    {
        if (_comicPageIndex < 0 || _comicPageIndex >= _comicPages.Count || _originalBitmap is null)
        {
            return;
        }

        EnsureCurrentPageEditable();
        ComicBookPageState page = _comicPages[_comicPageIndex];
        page.Regions.Clear();
        page.Regions.AddRange(_regions);

        if (bitmapChanged && _cleanedBaseBitmap is not null)
        {
            string processedDirectory = Path.Combine(
                _comicWorkspace ?? Path.Combine(Path.GetTempPath(), "TintaES", "manual"),
                "processed");
            Directory.CreateDirectory(processedDirectory);

            page.CleanedPath ??= Path.Combine(processedDirectory, $"{_comicPageIndex + 1:D4}-clean.png");
            SaveBitmap(_cleanedBaseBitmap, page.CleanedPath);

            if (_maskBitmap is not null)
            {
                page.MaskPath ??= Path.Combine(processedDirectory, $"{_comicPageIndex + 1:D4}-mask.png");
                SaveBitmap(_maskBitmap, page.MaskPath);
            }
            else if (!string.IsNullOrWhiteSpace(page.MaskPath))
            {
                try
                {
                    if (File.Exists(page.MaskPath))
                    {
                        File.Delete(page.MaskPath);
                    }
                }
                catch
                {
                }
                page.MaskPath = null;
            }

            ClearComicPageBitmapCache();
        }

        MaskPreviewButton.IsEnabled = _maskBitmap is not null;
        CleanPreviewButton.IsEnabled = page.Processed;
        ResultPreviewButton.IsEnabled = page.Processed;
        UpdateProjectCommandAvailability();
        UpdatePsdExportAvailability();
    }

    private void RefreshEditorToolAvailability()
    {
        bool hasPage = _originalBitmap is not null
            && _comicPageIndex >= 0
            && _comicPageIndex < _comicPages.Count;
        bool available = hasPage && !_comicBatchBusy && !_pageNavigationBusy;
        AddRegionButton.IsEnabled = available;
        DeleteRegionButton.IsEnabled = available && _selectedRegion is not null;

        EditorPageHistory? history = GetCurrentEditorHistory(create: false);
        if (_undoEditorButton is not null)
        {
            bool textUndo = Keyboard.FocusedElement is TextBoxBase textBox && textBox.CanUndo;
            _undoEditorButton.IsEnabled = available && (textUndo || history?.Undo.Count > 0);
        }
        if (_redoEditorButton is not null)
        {
            bool textRedo = Keyboard.FocusedElement is TextBoxBase textBox && textBox.CanRedo;
            _redoEditorButton.IsEnabled = available && (textRedo || history?.Redo.Count > 0);
        }
    }

    private void RenumberEditorRegions()
    {
        for (int index = 0; index < _regions.Count; index++)
        {
            _regions[index].Order = index + 1;
        }
        RegionListBox.Items.Refresh();
    }

    private Point ClampCanvasPoint(Point point)
    {
        double width = _originalBitmap?.PixelWidth ?? OverlayCanvas.ActualWidth;
        double height = _originalBitmap?.PixelHeight ?? OverlayCanvas.ActualHeight;
        return new Point(
            Math.Clamp(point.X, 0, Math.Max(0, width)),
            Math.Clamp(point.Y, 0, Math.Max(0, height)));
    }

    private void RemoveDrawingPreview()
    {
        if (_drawingPreview is not null)
        {
            OverlayCanvas.Children.Remove(_drawingPreview);
            _drawingPreview = null;
        }
        if (Mouse.Captured == OverlayCanvas)
        {
            OverlayCanvas.ReleaseMouseCapture();
        }
    }

    private NormalizedRect GetRegionMaskBounds(ComicRegion region)
    {
        double left = region.TextBox.X;
        double top = region.TextBox.Y;
        double right = region.TextBox.Right;
        double bottom = region.TextBox.Bottom;
        foreach (NormalizedPoint point in region.SafePolygon)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }
        return new NormalizedRect(left, top, right - left, bottom - top)
            .Expand(0.12, 0.18)
            .Clamp();
    }

    private static BitmapSource RestoreOriginalArea(
        BitmapSource cleaned,
        BitmapSource original,
        NormalizedRect area)
    {
        BitmapSource clean32 = ConvertToBgra32(cleaned);
        BitmapSource original32 = ConvertToBgra32(original);
        if (clean32.PixelWidth != original32.PixelWidth || clean32.PixelHeight != original32.PixelHeight)
        {
            return cleaned;
        }

        int width = clean32.PixelWidth;
        int height = clean32.PixelHeight;
        int stride = width * 4;
        byte[] cleanPixels = new byte[stride * height];
        byte[] originalPixels = new byte[stride * height];
        clean32.CopyPixels(cleanPixels, stride, 0);
        original32.CopyPixels(originalPixels, stride, 0);
        PixelArea rect = ToPixelArea(area, width, height);

        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            int offset = y * stride + rect.X * 4;
            Buffer.BlockCopy(originalPixels, offset, cleanPixels, offset, rect.Width * 4);
        }

        BitmapSource result = BitmapSource.Create(
            width,
            height,
            cleaned.DpiX > 0 ? cleaned.DpiX : 96,
            cleaned.DpiY > 0 ? cleaned.DpiY : 96,
            PixelFormats.Bgra32,
            null,
            cleanPixels,
            stride);
        result.Freeze();
        return result;
    }

    private BitmapSource? PaintMaskArea(BitmapSource? mask, NormalizedRect area, bool enabled)
    {
        if (_originalBitmap is null)
        {
            return mask;
        }

        int width = _originalBitmap.PixelWidth;
        int height = _originalBitmap.PixelHeight;
        int stride = width;
        byte[] pixels = new byte[stride * height];

        if (mask is not null)
        {
            BitmapSource gray = mask.Format == PixelFormats.Gray8
                ? mask
                : new FormatConvertedBitmap(mask, PixelFormats.Gray8, null, 0);
            if (gray.PixelWidth == width && gray.PixelHeight == height)
            {
                gray.CopyPixels(pixels, stride, 0);
            }
        }

        PixelArea rect = ToPixelArea(area, width, height);
        byte value = enabled ? (byte)255 : (byte)0;
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            Array.Fill(pixels, value, y * stride + rect.X, rect.Width);
        }

        if (!enabled && pixels.All(valueAtPixel => valueAtPixel == 0))
        {
            return null;
        }

        BitmapSource result = BitmapSource.Create(
            width,
            height,
            _originalBitmap.DpiX > 0 ? _originalBitmap.DpiX : 96,
            _originalBitmap.DpiY > 0 ? _originalBitmap.DpiY : 96,
            PixelFormats.Gray8,
            null,
            pixels,
            stride);
        result.Freeze();
        return result;
    }

    private static BitmapSource ConvertToBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
        {
            return source;
        }
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static PixelArea ToPixelArea(NormalizedRect area, int width, int height)
    {
        int x = Math.Clamp((int)Math.Floor(area.X / 1000 * width), 0, Math.Max(0, width - 1));
        int y = Math.Clamp((int)Math.Floor(area.Y / 1000 * height), 0, Math.Max(0, height - 1));
        int right = Math.Clamp((int)Math.Ceiling(area.Right / 1000 * width), x + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(area.Bottom / 1000 * height), y + 1, height);
        return new PixelArea(x, y, right - x, bottom - y);
    }

    private static ComicRegion CloneEditorRegion(ComicRegion source)
    {
        return new ComicRegion
        {
            Id = source.Id,
            Order = source.Order,
            Original = source.Original,
            OcrAlternatives = source.OcrAlternatives.ToArray(),
            Translation = source.Translation,
            Type = source.Type,
            Confidence = source.Confidence,
            BubbleConfidence = source.BubbleConfidence,
            TextBox = source.TextBox,
            RenderBox = source.RenderBox,
            SafePolygon = source.SafePolygon.ToArray(),
            Rotation = source.Rotation,
            Vertical = source.Vertical,
            Style = new ComicTextStyle
            {
                FontCategory = source.Style.FontCategory,
                FontFamily = source.Style.FontFamily,
                FontWeight = source.Style.FontWeight,
                FontSize = source.Style.FontSize,
                FontWidthRatio = source.Style.FontWidthRatio,
                LineHeightRatio = source.Style.LineHeightRatio,
                OriginalLineCount = source.Style.OriginalLineCount,
                Italic = source.Style.Italic,
                Uppercase = source.Style.Uppercase,
                TextColor = source.Style.TextColor,
                OutlineColor = source.Style.OutlineColor,
                OutlineWidth = source.Style.OutlineWidth,
                Alignment = source.Style.Alignment,
                BackgroundColor = source.Style.BackgroundColor,
                Shadow = source.Style.Shadow
            },
            IsEnabled = source.IsEnabled,
            CleanupMode = source.CleanupMode,
            FontScale = source.FontScale,
            ManualFontScale = source.ManualFontScale,
            TextOffsetX = source.TextOffsetX,
            TextOffsetY = source.TextOffsetY,
            IsManual = source.IsManual,
            ManualLayoutSeedText = source.ManualLayoutSeedText,
            ManualBaseFontSize = source.ManualBaseFontSize
        };
    }

    private sealed class EditorPageHistory
    {
        public Stack<EditorSnapshot> Undo { get; } = new();
        public Stack<EditorSnapshot> Redo { get; } = new();
    }

    private sealed record EditorSnapshot(
        IReadOnlyList<ComicRegion> Regions,
        BitmapSource? CleanedBaseBitmap,
        BitmapSource? CleanedBitmap,
        BitmapSource? MaskBitmap,
        Guid? SelectedRegionId,
        bool Processed);

    private sealed record PixelArea(int X, int Y, int Width, int Height)
    {
        public int Bottom => Y + Height;
    }
}
