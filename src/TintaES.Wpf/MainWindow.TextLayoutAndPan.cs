using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TintaES.Core;
using TintaES.Wpf.Controls;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene una sola composición de líneas para editor y bocadillo y la navegación
/// Espacio + arrastrar. El cálculo automático se hace una sola vez al incorporar la región,
/// nunca al hacer clic sobre ella.
/// </summary>
public partial class MainWindow
{
    private readonly ComicTextLineBreakService _editorLineBreakService = new();
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
        RegionListBox.SelectionChanged += RegionListBox_SelectionChanged_LineLayout;
        TranslationTextBox.TextChanged += TranslationTextBox_TextChanged_LineLayout;
        FontScaleSlider.ValueChanged += FontScaleSlider_ValueChanged_ManualLineLayout;

        foreach (ComicRegion region in _regions)
        {
            PrepareRegionLineLayout(region);
        }
    }

    private void Regions_CollectionChanged_ForLineLayout(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
        {
            return;
        }

        foreach (ComicRegion region in e.NewItems.OfType<ComicRegion>())
        {
            // El Refresh global de la lista en cada carácter era innecesario: los bindings ya
            // reciben INotifyPropertyChanged. Lo quitamos para evitar pausas durante la edición.
            region.PropertyChanged -= Region_PropertyChanged;
            PrepareRegionLineLayout(region);
        }
    }

    private void PrepareRegionLineLayout(ComicRegion region)
    {
        if (_originalBitmap is null
            || region.Type == "sfx"
            || string.IsNullOrWhiteSpace(region.Translation)
            || region.IsManual)
        {
            return;
        }

        string formatted = _editorLineBreakService.FormatForEditor(
            region,
            _originalBitmap.PixelWidth,
            _originalBitmap.PixelHeight);

        if (!string.IsNullOrWhiteSpace(formatted))
        {
            region.Translation = formatted;
        }

        // A partir de aquí Translation es la única fuente de verdad para la composición.
        // Las líneas del cuadro lateral son exactamente las que dibuja el bocadillo.
        region.IsManual = true;
        region.Vertical = false;
    }

    private void RegionListBox_SelectionChanged_LineLayout(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedRegion is not null)
        {
            RefreshManualLineVisual(_selectedRegion, invalidate: false);
        }
    }

    private void TranslationTextBox_TextChanged_LineLayout(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null || _selectedRegion.Type == "sfx")
        {
            return;
        }

        _selectedRegion.IsManual = true;
        _selectedRegion.Vertical = false;
        RefreshManualLineVisual(_selectedRegion);
    }

    private void FontScaleSlider_ValueChanged_ManualLineLayout(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingEditor || _selectedRegion is null || !_selectedRegion.IsManual)
        {
            return;
        }

        RefreshManualLineVisual(_selectedRegion);
    }

    private void RefreshManualLineVisual(ComicRegion region, bool invalidate = true)
    {
        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (ReferenceEquals(layer.Tag, region))
            {
                EnsureManualLineVisual(layer, region, invalidate);
                return;
            }
        }
    }

    private void EnsureManualLineVisual(Grid layer, ComicRegion region, bool invalidate = true)
    {
        if (_originalBitmap is null)
        {
            return;
        }

        ComicTextElement? automatic = layer.Children.OfType<ComicTextElement>().FirstOrDefault();
        ManualComicTextElement? manual = layer.Children.OfType<ManualComicTextElement>().FirstOrDefault();
        bool useManual = region.Type != "sfx" && region.IsManual;

        if (!useManual)
        {
            if (automatic is not null)
            {
                automatic.Visibility = Visibility.Visible;
            }
            if (manual is not null)
            {
                manual.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (manual is null)
        {
            manual = new ManualComicTextElement
            {
                Region = region,
                PageWidth = _originalBitmap.PixelWidth,
                PageHeight = _originalBitmap.PixelHeight,
                Width = layer.Width,
                Height = layer.Height,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(manual, 11);
            layer.Children.Add(manual);
            invalidate = true;
        }
        else
        {
            manual.Width = layer.Width;
            manual.Height = layer.Height;
        }

        if (automatic is not null)
        {
            automatic.Visibility = Visibility.Collapsed;
        }
        manual.Visibility = Visibility.Visible;

        if (invalidate)
        {
            manual.InvalidateVisual();
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
