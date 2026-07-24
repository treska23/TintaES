using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Editor de cajas de texto basado en componentes nativos de WPF. Cada región conserva su propia
/// caja independiente; únicamente el marco de selección (Adorner) se reutiliza para la región activa.
/// Durante la edición se usa TextBlock con ajuste de línea y recorte nativos, sin geometrías ni
/// búsquedas tipográficas costosas.
/// </summary>
public partial class MainWindow
{
    private const string NativeTextBlockTag = "tinta-native-text-frame";
    private static readonly bool TextFrameEditingRegistered = RegisterTextFrameEditing();

    private readonly HashSet<Guid> _validatedNativeBaseSizes = [];
    private bool _textFrameEditingInstalled;
    private bool _nativeTextFrameRefreshPending;
    private AdornerDecorator? _textFrameAdornerDecorator;
    private AdornerLayer? _textFrameAdornerLayer;
    private NativeTextFrameAdorner? _textFrameAdorner;
    private Grid? _selectedTextFrameLayer;
    private TextBlock? _selectedNativeTextBlock;
    private ComicRegion? _observedTextFrameRegion;
    private TextFrameResizeState? _textFrameResizeState;

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
            QueueNativeTextFrameRefresh();
            return;
        }

        _textFrameEditingInstalled = true;
        EnsureNativeAdornerLayer();

        RegionListBox.SelectionChanged += RegionListBox_NativeTextFrameSelectionChanged;
        ZoomSlider.ValueChanged += ZoomSlider_NativeTextFrameValueChanged;
        BusyOverlay.IsVisibleChanged += BusyOverlay_NativeTextFrameVisibilityChanged;
        ResultPreviewButton.Click += ResultPreviewButton_NativeTextFrameClick;
        OverlayCanvas.MouseEnter += OverlayCanvas_NativeTextFrameMouseEnter;
        _regions.CollectionChanged += Regions_NativeTextFrameCollectionChanged;

        QueueNativeTextFrameRefresh();
    }

    private void EnsureNativeAdornerLayer()
    {
        if (_textFrameAdornerDecorator is not null)
        {
            return;
        }

        if (ImageScrollViewer.Content is not UIElement content || !ReferenceEquals(content, ImageStage))
        {
            return;
        }

        ImageScrollViewer.Content = null;
        _textFrameAdornerDecorator = new AdornerDecorator
        {
            Child = ImageStage,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        ImageScrollViewer.Content = _textFrameAdornerDecorator;
    }

    private void RegionListBox_NativeTextFrameSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        QueueNativeTextFrameRefresh();

    private void ZoomSlider_NativeTextFrameValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        _textFrameAdorner?.InvalidateMeasure();
        _textFrameAdorner?.InvalidateArrange();
        _textFrameAdorner?.InvalidateVisual();
    }

    private void BusyOverlay_NativeTextFrameVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!BusyOverlay.IsVisible)
        {
            QueueNativeTextFrameRefresh();
        }
    }

    private void ResultPreviewButton_NativeTextFrameClick(object sender, RoutedEventArgs e) =>
        QueueNativeTextFrameRefresh();

    private void OverlayCanvas_NativeTextFrameMouseEnter(object sender, MouseEventArgs e)
    {
        if (_selectedRegion is not null
            && (_selectedTextFrameLayer is null || !OverlayCanvas.Children.Contains(_selectedTextFrameLayer)))
        {
            QueueNativeTextFrameRefresh();
        }
    }

    private void Regions_NativeTextFrameCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueueNativeTextFrameRefresh();

    private void QueueNativeTextFrameRefresh()
    {
        if (_nativeTextFrameRefreshPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _nativeTextFrameRefreshPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _nativeTextFrameRefreshPending = false;
                RefreshSelectedTextFrame();
            },
            DispatcherPriority.Render);
    }

    /// <summary>
    /// Nombre conservado para los hooks de herramientas ya existentes.
    /// </summary>
    private void RefreshSelectedTextFrame()
    {
        EnsureNativeAdornerLayer();
        ObserveSelectedTextFrameRegion();

        bool shouldShow = _selectedRegion is not null
            && _originalBitmap is not null
            && string.Equals(_previewMode, "result", StringComparison.Ordinal)
            && _manualMaskTool == ManualMaskTool.None
            && !_drawingRegion
            && !BusyOverlay.IsVisible;

        if (!shouldShow)
        {
            ReleaseSelectedTextFrameLayer();
            return;
        }

        Grid? layer = OverlayCanvas.Children
            .OfType<Grid>()
            .FirstOrDefault(candidate => candidate.Tag is ComicRegion region
                && region.Id == _selectedRegion!.Id);

        if (layer is null)
        {
            ReleaseSelectedTextFrameLayer();
            return;
        }

        if (!ReferenceEquals(layer, _selectedTextFrameLayer))
        {
            ReleaseSelectedTextFrameLayer();
            _selectedTextFrameLayer = layer;
        }

        _selectedNativeTextBlock = EnsureNativeTextBlock(layer, _selectedRegion!);
        UpdateSelectedTextLayerGeometry();
        EnsureNativeTextFrameAdorner(layer);
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

        // Es una actualización local: solo cambia el TextBlock y la caja seleccionados.
        UpdateSelectedTextLayerGeometry();
    }

    private TextBlock EnsureNativeTextBlock(Grid layer, ComicRegion region)
    {
        TextBlock? textBlock = layer.Children
            .OfType<TextBlock>()
            .FirstOrDefault(candidate => Equals(candidate.Tag, NativeTextBlockTag));

        if (textBlock is null)
        {
            textBlock = new TextBlock
            {
                Tag = NativeTextBlockTag,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                ClipToBounds = true
            };
            TextOptions.SetTextFormattingMode(textBlock, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(textBlock, TextRenderingMode.Grayscale);
            Panel.SetZIndex(textBlock, 50_000);
            layer.Children.Add(textBlock);
        }

        layer.ClipToBounds = true;

        foreach (FastComicTextPreviewElement preview in layer.Children.OfType<FastComicTextPreviewElement>())
        {
            preview.Visibility = Visibility.Collapsed;
        }
        foreach (ComicTextElement automatic in layer.Children.OfType<ComicTextElement>())
        {
            automatic.Visibility = Visibility.Collapsed;
        }
        foreach (ManualComicTextElement manual in layer.Children.OfType<ManualComicTextElement>())
        {
            manual.Visibility = Visibility.Collapsed;
        }

        textBlock.Visibility = region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        return textBlock;
    }

    private void ReleaseSelectedTextFrameLayer()
    {
        RemoveNativeTextFrameAdorner();

        if (_selectedTextFrameLayer is not null)
        {
            foreach (TextBlock native in _selectedTextFrameLayer.Children
                         .OfType<TextBlock>()
                         .Where(candidate => Equals(candidate.Tag, NativeTextBlockTag)))
            {
                native.Visibility = Visibility.Collapsed;
            }

            if (_selectedTextFrameLayer.Tag is ComicRegion region)
            {
                foreach (FastComicTextPreviewElement preview in _selectedTextFrameLayer.Children
                             .OfType<FastComicTextPreviewElement>())
                {
                    preview.Visibility = region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            // Toda caja, seleccionada o no, recorta su contenido.
            _selectedTextFrameLayer.ClipToBounds = true;
        }

        _selectedTextFrameLayer = null;
        _selectedNativeTextBlock = null;
    }

    private void EnsureNativeTextFrameAdorner(Grid layer)
    {
        AdornerLayer? adornerLayer = AdornerLayer.GetAdornerLayer(layer);
        if (adornerLayer is null)
        {
            return;
        }

        if (_textFrameAdorner is not null
            && _textFrameAdornerLayer is not null
            && ReferenceEquals(_textFrameAdorner.AdornedElement, layer))
        {
            _textFrameAdorner.InvalidateArrange();
            _textFrameAdorner.InvalidateVisual();
            return;
        }

        RemoveNativeTextFrameAdorner();
        _textFrameAdornerLayer = adornerLayer;
        _textFrameAdorner = new NativeTextFrameAdorner(
            layer,
            () => Math.Max(0.05, ZoomSlider.Value / 100),
            TextFrameThumb_DragStarted,
            TextFrameThumb_DragDelta,
            TextFrameThumb_DragCompleted,
            FindResource("AccentBrush") as Brush ?? Brushes.OrangeRed);
        _textFrameAdornerLayer.Add(_textFrameAdorner);
    }

    private void RemoveNativeTextFrameAdorner()
    {
        if (_textFrameAdorner is not null && _textFrameAdornerLayer is not null)
        {
            _textFrameAdornerLayer.Remove(_textFrameAdorner);
        }
        _textFrameAdorner = null;
        _textFrameAdornerLayer = null;
    }

    private void UpdateSelectedTextLayerGeometry()
    {
        ComicRegion? region = _selectedRegion;
        Grid? layer = _selectedTextFrameLayer;
        TextBlock? textBlock = _selectedNativeTextBlock;
        if (region is null || layer is null || textBlock is null || _originalBitmap is null)
        {
            return;
        }

        NormalizedRect box = region.RenderBox;
        double width = Math.Max(8, box.Width / 1000 * _originalBitmap.PixelWidth);
        double height = Math.Max(8, box.Height / 1000 * _originalBitmap.PixelHeight);
        double left = (box.X + region.TextOffsetX) / 1000 * _originalBitmap.PixelWidth;
        double top = (box.Y + region.TextOffsetY) / 1000 * _originalBitmap.PixelHeight;

        layer.Width = width;
        layer.Height = height;
        layer.ClipToBounds = true;
        Canvas.SetLeft(layer, left);
        Canvas.SetTop(layer, top);

        UpdateNativeTextBlock(textBlock, region, width, height);
        _textFrameAdornerLayer?.Update(layer);
    }

    private void UpdateNativeTextBlock(TextBlock textBlock, ComicRegion region, double width, double height)
    {
        string text = region.DisplayText;
        if (region.Style.Uppercase)
        {
            text = text.ToUpper(CultureInfo.GetCultureInfo("es-ES"));
        }

        double padding = Math.Max(2, Math.Min(width, height) * 0.035);
        double fontSize = region.IsManual && region.Type != "sfx"
            ? ResolveNativeManualBaseSize(region, height) * Math.Clamp(region.ManualFontScale, 0.25, 2.5)
            : ResolveNativeAutomaticFontSize(region, width, height, text);

        textBlock.Text = text;
        textBlock.FontFamily = new FontFamily(ResolveNativeFontFamily(region));
        textBlock.FontWeight = FontWeight.FromOpenTypeWeight(Math.Clamp(region.Style.FontWeight, 100, 999));
        textBlock.FontStyle = region.Style.Italic ? FontStyles.Italic : FontStyles.Normal;
        textBlock.Foreground = ParseNativeTextBrush(region.Style.TextColor, Brushes.Black);
        textBlock.TextAlignment = region.Style.Alignment switch
        {
            "left" => TextAlignment.Left,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Center
        };
        textBlock.FontSize = Math.Max(1.2, fontSize);
        textBlock.LineHeight = Math.Max(
            textBlock.FontSize * 0.9,
            textBlock.FontSize * Math.Clamp(region.Style.LineHeightRatio, 0.82, 1.8));
        textBlock.Margin = new Thickness(padding);
        textBlock.MaxHeight = Math.Max(2, height - padding * 2);
        textBlock.Width = Math.Max(2, width - padding * 2);
        textBlock.ClipToBounds = true;
        textBlock.Visibility = region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private double ResolveNativeManualBaseSize(ComicRegion region, double boxHeight)
    {
        double detected = region.Style.FontSize > 0 && _originalBitmap is not null
            ? region.Style.FontSize / 1000 * _originalBitmap.PixelHeight
            : 0;
        double stored = region.ManualBaseFontSize;

        if (_validatedNativeBaseSizes.Add(region.Id))
        {
            bool storedValid = double.IsFinite(stored) && stored >= 1.2;
            if (detected >= 1.2 && double.IsFinite(detected))
            {
                double ratio = storedValid ? stored / detected : 0;
                if (!storedValid || ratio < 0.45 || ratio > 2.2)
                {
                    stored = detected;
                }
            }
            else
            {
                double maximumReasonableBase = Math.Max(8, boxHeight * 0.62);
                if (!storedValid || stored > maximumReasonableBase)
                {
                    int originalLines = region.Style.OriginalLineCount > 0
                        ? region.Style.OriginalLineCount
                        : 3;
                    double lineRatio = Math.Clamp(region.Style.LineHeightRatio, 0.82, 1.8);
                    stored = Math.Max(1.2, boxHeight * 0.72 / (originalLines * lineRatio));
                }
            }

            region.ManualBaseFontSize = Math.Max(1.2, stored);
        }

        return Math.Max(1.2, region.ManualBaseFontSize);
    }

    private double ResolveNativeAutomaticFontSize(
        ComicRegion region,
        double width,
        double height,
        string text)
    {
        double detected = region.Style.FontSize > 0 && _originalBitmap is not null
            ? region.Style.FontSize / 1000 * _originalBitmap.PixelHeight
            : Math.Sqrt(Math.Max(1, width * height) / Math.Max(4, text.Length)) * 1.2;
        double maximum = Math.Max(5, Math.Min(height * 0.48, width * 0.30));
        return Math.Clamp(detected * Math.Clamp(region.FontScale, 0.35, 1.6), 2.5, maximum);
    }

    private void EnsureRegionUsesNativeTextFrame(ComicRegion region)
    {
        if (region.Type == "sfx")
        {
            return;
        }

        if (!region.IsManual)
        {
            double currentSize = _selectedNativeTextBlock?.FontSize
                ?? ResolveNativeAutomaticFontSize(
                    region,
                    Math.Max(8, _selectedTextFrameLayer?.Width ?? 100),
                    Math.Max(8, _selectedTextFrameLayer?.Height ?? 60),
                    region.DisplayText);

            region.ManualLayoutSeedText = region.Translation;
            region.ManualBaseFontSize = Math.Max(1.2, currentSize);
            region.ManualFontScale = 1;
            region.FontScale = 1;
            region.IsManual = true;
            region.Vertical = false;
            _validatedNativeBaseSizes.Add(region.Id);
            region.NotifyVisualChange();
        }
        else
        {
            ResolveNativeManualBaseSize(
                region,
                Math.Max(8, _selectedTextFrameLayer?.Height ?? 60));
        }
    }

    private void TextFrameThumb_DragStarted(TextFrameCorner corner)
    {
        if (_selectedRegion is null || _originalBitmap is null)
        {
            return;
        }

        PushEditorUndoSnapshot();
        EnsureRegionUsesNativeTextFrame(_selectedRegion);

        // Consolidamos el desplazamiento en la caja. La posición visual no cambia y desde aquí
        // el redimensionado queda totalmente independiente de la máscara.
        NormalizedRect box = _selectedRegion.RenderBox;
        var absoluteBox = new NormalizedRect(
            box.X + _selectedRegion.TextOffsetX,
            box.Y + _selectedRegion.TextOffsetY,
            box.Width,
            box.Height).Clamp();
        _selectedRegion.RenderBox = absoluteBox;
        _selectedRegion.TextOffsetX = 0;
        _selectedRegion.TextOffsetY = 0;

        _textFrameResizeState = new TextFrameResizeState(
            _selectedRegion,
            corner,
            Mouse.GetPosition(OverlayCanvas),
            absoluteBox);
        UpdateSelectedTextLayerGeometry();
    }

    private void TextFrameThumb_DragDelta(TextFrameCorner corner)
    {
        TextFrameResizeState? state = _textFrameResizeState;
        if (state is null
            || _originalBitmap is null
            || !ReferenceEquals(state.Region, _selectedRegion)
            || state.Corner != corner)
        {
            return;
        }

        Point pointer = Mouse.GetPosition(OverlayCanvas);
        double dx = (pointer.X - state.Pointer.X) / _originalBitmap.PixelWidth * 1000;
        double dy = (pointer.Y - state.Pointer.Y) / _originalBitmap.PixelHeight * 1000;

        double left = state.Box.X;
        double top = state.Box.Y;
        double right = state.Box.Right;
        double bottom = state.Box.Bottom;
        double minimumWidth = Math.Max(5, 28d / _originalBitmap.PixelWidth * 1000);
        double minimumHeight = Math.Max(5, 24d / _originalBitmap.PixelHeight * 1000);

        switch (corner)
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

        state.Region.RenderBox = new NormalizedRect(left, top, right - left, bottom - top);
        state.Region.NotifyVisualChange();
        UpdateSelectedTextLayerGeometry();
    }

    private void TextFrameThumb_DragCompleted(TextFrameCorner corner)
    {
        if (_textFrameResizeState is null)
        {
            return;
        }

        _textFrameResizeState = null;
        PersistVisibleComicPageRegions();
        UpdateSelectedTextLayerGeometry();
        SetFooterStatus(
            "Caja redimensionada. El texto queda envuelto y recortado dentro de esta caja.",
            "#58A77D");
    }

    private static string ResolveNativeFontFamily(ComicRegion region) =>
        !string.IsNullOrWhiteSpace(region.Style.FontFamily)
            ? region.Style.FontFamily
            : region.Style.FontCategory switch
            {
                "comic" => "Comic Sans MS",
                "handwritten" => "Segoe Print",
                "condensed" => "Arial Narrow",
                "serif" => "Georgia",
                "display" => "Impact",
                "monospace" => "Consolas",
                _ => "Arial"
            };

    private static Brush ParseNativeTextBrush(string? value, Brush fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
        catch
        {
            return fallback;
        }
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
        NormalizedRect Box);

    /// <summary>
    /// Adorner nativo: WPF lo mantiene ligado al elemento seleccionado y lo dibuja siempre encima.
    /// Solo sus cuatro Thumb participan en el hit testing.
    /// </summary>
    private sealed class NativeTextFrameAdorner : Adorner
    {
        private readonly VisualCollection _visuals;
        private readonly Thumb[] _thumbs;
        private readonly Func<double> _zoomProvider;
        private readonly Brush _accent;

        public NativeTextFrameAdorner(
            UIElement adornedElement,
            Func<double> zoomProvider,
            Action<TextFrameCorner> dragStarted,
            Action<TextFrameCorner> dragDelta,
            Action<TextFrameCorner> dragCompleted,
            Brush accent)
            : base(adornedElement)
        {
            _zoomProvider = zoomProvider;
            _accent = accent;
            _visuals = new VisualCollection(this);
            _thumbs = Enum.GetValues<TextFrameCorner>()
                .Select(corner => CreateThumb(corner, dragStarted, dragDelta, dragCompleted))
                .ToArray();
            foreach (Thumb thumb in _thumbs)
            {
                _visuals.Add(thumb);
            }
        }

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index) => _visuals[index];

        protected override Size ArrangeOverride(Size finalSize)
        {
            double zoom = Math.Max(0.05, _zoomProvider());
            double size = Math.Clamp(12 / zoom, 8, 52);
            Point[] centres =
            [
                new Point(0, 0),
                new Point(finalSize.Width, 0),
                new Point(finalSize.Width, finalSize.Height),
                new Point(0, finalSize.Height)
            ];

            for (int index = 0; index < _thumbs.Length; index++)
            {
                Thumb thumb = _thumbs[index];
                thumb.Measure(new Size(size, size));
                thumb.Arrange(new Rect(
                    centres[index].X - size / 2,
                    centres[index].Y - size / 2,
                    size,
                    size));
            }
            return finalSize;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            double zoom = Math.Max(0.05, _zoomProvider());
            double thickness = Math.Clamp(1 / zoom, 0.75, 4);
            var pen = new Pen(_accent, thickness);
            drawingContext.DrawRectangle(
                null,
                pen,
                new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)));
        }

        private Thumb CreateThumb(
            TextFrameCorner corner,
            Action<TextFrameCorner> dragStarted,
            Action<TextFrameCorner> dragDelta,
            Action<TextFrameCorner> dragCompleted)
        {
            var thumb = new Thumb
            {
                Background = _accent,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Cursor = corner is TextFrameCorner.TopLeft or TextFrameCorner.BottomRight
                    ? Cursors.SizeNWSE
                    : Cursors.SizeNESW,
                Focusable = false
            };
            thumb.DragStarted += (_, e) =>
            {
                dragStarted(corner);
                e.Handled = true;
            };
            thumb.DragDelta += (_, e) =>
            {
                dragDelta(corner);
                e.Handled = true;
            };
            thumb.DragCompleted += (_, e) =>
            {
                dragCompleted(corner);
                e.Handled = true;
            };
            return thumb;
        }
    }
}
