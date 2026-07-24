using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Caja de texto editable al estilo de un procesador de textos. Solo existe una caja visual para
/// la región seleccionada; redimensionarla actualiza esa capa sin recorrer ni reconstruir el resto.
/// </summary>
public partial class MainWindow
{
    private static readonly bool TextFrameEditingRegistered = RegisterTextFrameEditing();

    private readonly List<Thumb> _textFrameHandles = [];
    private Border? _textFrameBorder;
    private Grid? _selectedTextFrameLayer;
    private FastComicTextPreviewElement? _selectedTextFramePreview;
    private ComicRegion? _observedTextFrameRegion;
    private TextFrameResizeState? _textFrameResizeState;
    private bool _textFrameEditingInstalled;

    private static bool RegisterTextFrameEditing()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_TextFrameEditingLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_TextFrameEditingLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallTextFrameEditing,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallTextFrameEditing()
    {
        if (_textFrameEditingInstalled)
        {
            RefreshSelectedTextFrame();
            return;
        }

        _textFrameEditingInstalled = true;
        RegionListBox.SelectionChanged += RegionListBox_TextFrameSelectionChanged;
        ZoomSlider.ValueChanged += ZoomSlider_TextFrameValueChanged;
        OverlayCanvas.MouseEnter += OverlayCanvas_TextFrameMouseEnter;
        BusyOverlay.IsVisibleChanged += BusyOverlay_TextFrameVisibilityChanged;
        ResultPreviewButton.Click += ResultPreviewButton_TextFrameClick;
        RefreshSelectedTextFrame();
    }

    private void RegionListBox_TextFrameSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshSelectedTextFrame();

    private void ZoomSlider_TextFrameValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateTextFrameChrome();

    private void OverlayCanvas_TextFrameMouseEnter(object sender, MouseEventArgs e)
    {
        if (_textFrameBorder is null || !OverlayCanvas.Children.Contains(_textFrameBorder))
        {
            RefreshSelectedTextFrame();
        }
    }

    private void BusyOverlay_TextFrameVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!BusyOverlay.IsVisible)
        {
            Dispatcher.BeginInvoke(RefreshSelectedTextFrame, DispatcherPriority.ContextIdle);
        }
    }

    private void ResultPreviewButton_TextFrameClick(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(RefreshSelectedTextFrame, DispatcherPriority.Render);

    private void EnsureTextFrameChrome()
    {
        if (_textFrameBorder is null)
        {
            _textFrameBorder = new Border
            {
                BorderBrush = FindResource("AccentBrush") as Brush ?? Brushes.OrangeRed,
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(_textFrameBorder, 70_000);
        }

        if (!OverlayCanvas.Children.Contains(_textFrameBorder))
        {
            OverlayCanvas.Children.Add(_textFrameBorder);
        }

        if (_textFrameHandles.Count == 0)
        {
            foreach (TextFrameCorner corner in Enum.GetValues<TextFrameCorner>())
            {
                var thumb = new Thumb
                {
                    Tag = corner,
                    Background = FindResource("AccentBrush") as Brush ?? Brushes.OrangeRed,
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(1),
                    Cursor = corner is TextFrameCorner.TopLeft or TextFrameCorner.BottomRight
                        ? Cursors.SizeNWSE
                        : Cursors.SizeNESW,
                    Visibility = Visibility.Collapsed,
                    Focusable = false
                };
                thumb.DragStarted += TextFrameThumb_DragStarted;
                thumb.DragDelta += TextFrameThumb_DragDelta;
                thumb.DragCompleted += TextFrameThumb_DragCompleted;
                Panel.SetZIndex(thumb, 70_001);
                _textFrameHandles.Add(thumb);
            }
        }

        foreach (Thumb thumb in _textFrameHandles)
        {
            if (!OverlayCanvas.Children.Contains(thumb))
            {
                OverlayCanvas.Children.Add(thumb);
            }
        }
    }

    private void RefreshSelectedTextFrame()
    {
        ObserveSelectedTextFrameRegion();
        EnsureTextFrameChrome();

        bool visible = _selectedRegion is not null
            && _originalBitmap is not null
            && string.Equals(_previewMode, "result", StringComparison.Ordinal)
            && _manualMaskTool == ManualMaskTool.None
            && !_drawingRegion;

        if (!visible)
        {
            HideSelectedTextFrame();
            return;
        }

        _selectedTextFrameLayer = OverlayCanvas.Children
            .OfType<Grid>()
            .FirstOrDefault(layer => layer.Tag is ComicRegion region && region.Id == _selectedRegion!.Id);
        _selectedTextFramePreview = _selectedTextFrameLayer?.Children
            .OfType<FastComicTextPreviewElement>()
            .FirstOrDefault();

        UpdateSelectedTextLayerGeometry();
    }

    private void ObserveSelectedTextFrameRegion()
    {
        if (ReferenceEquals(_observedTextFrameRegion, _selectedRegion))
        {
            return;
        }

        if (_observedTextFrameRegion is not null)
        {
            _observedTextFrameRegion.PropertyChanged -= SelectedTextFrameRegion_PropertyChanged;
        }

        _observedTextFrameRegion = _selectedRegion;
        if (_observedTextFrameRegion is not null)
        {
            _observedTextFrameRegion.PropertyChanged += SelectedTextFrameRegion_PropertyChanged;
        }
    }

    private void SelectedTextFrameRegion_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ComicRegion region || !ReferenceEquals(region, _selectedRegion))
        {
            return;
        }

        if (e.PropertyName is nameof(ComicRegion.TextOffsetX) or nameof(ComicRegion.TextOffsetY))
        {
            UpdateSelectedTextLayerGeometry();
        }
    }

    private void HideSelectedTextFrame()
    {
        if (_textFrameBorder is not null)
        {
            _textFrameBorder.Visibility = Visibility.Collapsed;
        }
        foreach (Thumb thumb in _textFrameHandles)
        {
            thumb.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSelectedTextLayerGeometry()
    {
        ComicRegion? region = _selectedRegion;
        if (region is null || _originalBitmap is null)
        {
            HideSelectedTextFrame();
            return;
        }

        double width = Math.Max(8, region.RenderBox.Width / 1000 * _originalBitmap.PixelWidth);
        double height = Math.Max(8, region.RenderBox.Height / 1000 * _originalBitmap.PixelHeight);
        double left = (region.RenderBox.X + region.TextOffsetX) / 1000 * _originalBitmap.PixelWidth;
        double top = (region.RenderBox.Y + region.TextOffsetY) / 1000 * _originalBitmap.PixelHeight;

        if (_selectedTextFrameLayer is not null)
        {
            _selectedTextFrameLayer.Width = width;
            _selectedTextFrameLayer.Height = height;
            _selectedTextFrameLayer.ClipToBounds = false;
            Canvas.SetLeft(_selectedTextFrameLayer, left);
            Canvas.SetTop(_selectedTextFrameLayer, top);
        }

        if (_selectedTextFramePreview is not null)
        {
            _selectedTextFramePreview.Width = width;
            _selectedTextFramePreview.Height = height;
            _selectedTextFramePreview.InvalidateMeasure();
            _selectedTextFramePreview.InvalidateVisual();
        }

        if (_textFrameBorder is not null)
        {
            _textFrameBorder.Width = width;
            _textFrameBorder.Height = height;
            _textFrameBorder.Visibility = Visibility.Visible;
            Canvas.SetLeft(_textFrameBorder, left);
            Canvas.SetTop(_textFrameBorder, top);
        }

        UpdateTextFrameChrome();
    }

    private void UpdateTextFrameChrome()
    {
        if (_textFrameBorder is null
            || _textFrameBorder.Visibility != Visibility.Visible
            || _textFrameHandles.Count != 4)
        {
            return;
        }

        double left = Canvas.GetLeft(_textFrameBorder);
        double top = Canvas.GetTop(_textFrameBorder);
        double width = _textFrameBorder.Width;
        double height = _textFrameBorder.Height;
        double zoom = Math.Max(0.05, ZoomSlider.Value / 100);
        double handleSize = Math.Clamp(12 / zoom, 8, 52);
        double borderWidth = Math.Clamp(1 / zoom, 0.75, 4);
        _textFrameBorder.BorderThickness = new Thickness(borderWidth);

        Point[] centres =
        [
            new Point(left, top),
            new Point(left + width, top),
            new Point(left + width, top + height),
            new Point(left, top + height)
        ];

        for (int index = 0; index < _textFrameHandles.Count; index++)
        {
            Thumb thumb = _textFrameHandles[index];
            thumb.Width = handleSize;
            thumb.Height = handleSize;
            thumb.Visibility = Visibility.Visible;
            Canvas.SetLeft(thumb, centres[index].X - handleSize / 2);
            Canvas.SetTop(thumb, centres[index].Y - handleSize / 2);
        }
    }

    private void TextFrameThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { Tag: TextFrameCorner corner }
            || _selectedRegion is null
            || _originalBitmap is null)
        {
            return;
        }

        EnsureRegionUsesTextFrame(_selectedRegion);
        PushEditorUndoSnapshot();

        NormalizedRect box = _selectedRegion.RenderBox;
        _textFrameResizeState = new TextFrameResizeState(
            _selectedRegion,
            corner,
            Mouse.GetPosition(OverlayCanvas),
            box,
            _selectedRegion.TextOffsetX,
            _selectedRegion.TextOffsetY);
        e.Handled = true;
    }

    private void TextFrameThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        TextFrameResizeState? state = _textFrameResizeState;
        if (state is null
            || _originalBitmap is null
            || !ReferenceEquals(state.Region, _selectedRegion))
        {
            return;
        }

        Point pointer = Mouse.GetPosition(OverlayCanvas);
        double dx = (pointer.X - state.Pointer.X) / _originalBitmap.PixelWidth * 1000;
        double dy = (pointer.Y - state.Pointer.Y) / _originalBitmap.PixelHeight * 1000;

        double left = state.Box.X + state.OffsetX;
        double top = state.Box.Y + state.OffsetY;
        double right = left + state.Box.Width;
        double bottom = top + state.Box.Height;
        double minimumWidth = Math.Max(5, 28d / _originalBitmap.PixelWidth * 1000);
        double minimumHeight = Math.Max(5, 24d / _originalBitmap.PixelHeight * 1000);

        switch (state.Corner)
        {
            case TextFrameCorner.TopLeft:
                left = Math.Min(right - minimumWidth, left + dx);
                top = Math.Min(bottom - minimumHeight, top + dy);
                break;
            case TextFrameCorner.TopRight:
                right = Math.Max(left + minimumWidth, right + dx);
                top = Math.Min(bottom - minimumHeight, top + dy);
                break;
            case TextFrameCorner.BottomRight:
                right = Math.Max(left + minimumWidth, right + dx);
                bottom = Math.Max(top + minimumHeight, bottom + dy);
                break;
            case TextFrameCorner.BottomLeft:
                left = Math.Min(right - minimumWidth, left + dx);
                bottom = Math.Max(top + minimumHeight, bottom + dy);
                break;
        }

        left = Math.Clamp(left, 0, 1000 - minimumWidth);
        top = Math.Clamp(top, 0, 1000 - minimumHeight);
        right = Math.Clamp(right, left + minimumWidth, 1000);
        bottom = Math.Clamp(bottom, top + minimumHeight, 1000);

        state.Region.RenderBox = new NormalizedRect(
            left - state.OffsetX,
            top - state.OffsetY,
            right - left,
            bottom - top);
        state.Region.NotifyVisualChange();
        UpdateSelectedTextLayerGeometry();
        e.Handled = true;
    }

    private void TextFrameThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_textFrameResizeState is null)
        {
            return;
        }

        _textFrameResizeState = null;
        PersistVisibleComicPageRegions();
        RefreshSelectedTextFrame();
        SetFooterStatus("Caja de texto redimensionada. La fuente no se ha reajustado automáticamente.", "#58A77D");
        e.Handled = true;
    }

    private enum TextFrameCorner
    {
        TopLeft,
        TopRight,
        BottomRight,
        BottomLeft
    }

    private sealed record TextFrameResizeState(
        ComicRegion Region,
        TextFrameCorner Corner,
        Point Pointer,
        NormalizedRect Box,
        double OffsetX,
        double OffsetY);
}