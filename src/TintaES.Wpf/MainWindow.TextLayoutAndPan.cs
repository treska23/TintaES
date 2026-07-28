using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene el desplazamiento con Espacio y la compatibilidad de los antiguos hooks tipográficos.
/// Las zonas automáticas usan siempre ComicTextElement, tanto seleccionadas como sin seleccionar.
/// FastComicTextPreviewElement queda reservado para las cajas manuales durante la edición.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<Guid, string> _automaticLinePreviews = new();
    private bool _textLayoutHooksInstalled;
    private bool _spacePanHeld;
    private bool _isSpacePanning;
    private Point _panStartPointer;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        InstallTextLayoutHooks();
    }

    private void InstallTextLayoutHooks()
    {
        if (_textLayoutHooksInstalled)
        {
            return;
        }

        _textLayoutHooksInstalled = true;
        _regions.CollectionChanged += Regions_CollectionChanged_ForLineLayout;

        // No añadimos handlers a selección, escritura o escala. MainWindow.ManualTextRegressionFix
        // instala una sola ruta local para cada gesto.
    }

    private void Regions_CollectionChanged_ForLineLayout(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _automaticLinePreviews.Clear();
        }
    }

    // Se conservan las firmas porque versiones anteriores las desconectan al instalar el editor
    // rápido. Ya no realizan trabajo.
    private void RegionListBox_SelectionChanged_LineLayout(object sender, SelectionChangedEventArgs e)
    {
    }

    private void TranslationTextBox_TextChanged_LineLayout(object sender, TextChangedEventArgs e)
    {
    }

    private void FontScaleSlider_ValueChanged_ManualLineLayout(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
    }

    private void RefreshManualLineVisual(ComicRegion region, bool invalidate = true)
    {
        Grid? layer = OverlayCanvas.Children
            .OfType<Grid>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Tag, region));
        if (layer is not null)
        {
            EnsureManualLineVisual(layer, region, invalidate);
        }
    }

    private void EnsureManualLineVisual(Grid layer, ComicRegion region, bool invalidate = true)
    {
        if (_originalBitmap is null)
        {
            return;
        }

        bool usesManualPreview = region.IsManual && region.Type != "sfx";
        foreach (ComicTextElement renderer in layer.Children.OfType<ComicTextElement>())
        {
            renderer.Visibility = region.IsEnabled && !usesManualPreview
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!usesManualPreview && invalidate)
            {
                renderer.InvalidateVisual();
            }
        }
        foreach (ManualComicTextElement renderer in layer.Children.OfType<ManualComicTextElement>())
        {
            renderer.Visibility = Visibility.Collapsed;
        }

        FastComicTextPreviewElement? preview = layer.Children
            .OfType<FastComicTextPreviewElement>()
            .FirstOrDefault();
        if (!usesManualPreview)
        {
            if (preview is not null)
            {
                preview.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (preview is null)
        {
            preview = new FastComicTextPreviewElement
            {
                Region = region,
                PageWidth = _originalBitmap.PixelWidth,
                PageHeight = _originalBitmap.PixelHeight,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(preview, 12);
            layer.Children.Add(preview);
        }

        preview.Width = Math.Max(2, layer.Width);
        preview.Height = Math.Max(2, layer.Height);
        preview.Visibility = region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (invalidate)
        {
            preview.InvalidateVisual();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key != Key.Space || IsTextEntryFocused())
        {
            return;
        }

        _spacePanHeld = true;
        ImageScrollViewer.Cursor = Cursors.Hand;
        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);

        if (e.Key != Key.Space)
        {
            return;
        }

        _spacePanHeld = false;
        EndSpacePan();
        ImageScrollViewer.Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (!_spacePanHeld || !ImageScrollViewer.IsMouseOver)
        {
            return;
        }

        _isSpacePanning = true;
        _panStartPointer = e.GetPosition(ImageScrollViewer);
        _panStartHorizontalOffset = ImageScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = ImageScrollViewer.VerticalOffset;
        ImageScrollViewer.Cursor = Cursors.SizeAll;
        Mouse.Capture(ImageScrollViewer, CaptureMode.Element);
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);

        if (!_isSpacePanning || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point pointer = e.GetPosition(ImageScrollViewer);
        ImageScrollViewer.ScrollToHorizontalOffset(
            _panStartHorizontalOffset - (pointer.X - _panStartPointer.X));
        ImageScrollViewer.ScrollToVerticalOffset(
            _panStartVerticalOffset - (pointer.Y - _panStartPointer.Y));
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);

        if (!_isSpacePanning)
        {
            return;
        }

        EndSpacePan();
        ImageScrollViewer.Cursor = _spacePanHeld ? Cursors.Hand : Cursors.Arrow;
        e.Handled = true;
    }

    private void EndSpacePan()
    {
        if (!_isSpacePanning)
        {
            return;
        }

        _isSpacePanning = false;
        if (Mouse.Captured == ImageScrollViewer)
        {
            Mouse.Capture(null);
        }
    }

    private static bool IsTextEntryFocused() =>
        Keyboard.FocusedElement is TextBoxBase or ComboBox;
}
