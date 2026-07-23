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
/// Mantiene el rotulado automático como estado inicial y solo activa la composición manual
/// cuando el usuario modifica realmente el texto o sus saltos de línea. También gestiona
/// Espacio + arrastrar para navegar por la página.
/// </summary>
public partial class MainWindow
{
    private readonly ComicTextLineBreakService _editorLineBreakService = new();
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
        RegionListBox.SelectionChanged += RegionListBox_SelectionChanged_LineLayout;
        TranslationTextBox.TextChanged += TranslationTextBox_TextChanged_LineLayout;
        FontScaleSlider.ValueChanged += FontScaleSlider_ValueChanged_ManualLineLayout;

        foreach (ComicRegion region in _regions)
        {
            PrepareRegionLinePreview(region);
        }
    }

    private void Regions_CollectionChanged_ForLineLayout(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _automaticLinePreviews.Clear();
            return;
        }

        if (e.NewItems is null)
        {
            return;
        }

        foreach (ComicRegion region in e.NewItems.OfType<ComicRegion>())
        {
            // El binding de cada región ya escucha INotifyPropertyChanged. Evitamos el
            // Items.Refresh global que provocaba pausas al escribir cada carácter.
            region.PropertyChanged -= Region_PropertyChanged;
            PrepareRegionLinePreview(region);
        }
    }

    private void PrepareRegionLinePreview(ComicRegion region)
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
            _automaticLinePreviews[region.Id] = formatted;
        }
    }

    private void RegionListBox_SelectionChanged_LineLayout(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedRegion is null)
        {
            return;
        }

        if (_selectedRegion.IsManual)
        {
            RefreshManualLineVisual(_selectedRegion, invalidate: false);
            return;
        }

        // El cuadro lateral muestra una copia de la composición automática, pero NO cambia
        // Translation ni activa el render manual. Por eso la página sigue usando el ajuste
        // automático hasta que el usuario toque realmente el texto.
        if (!_automaticLinePreviews.TryGetValue(_selectedRegion.Id, out string? formatted)
            || string.IsNullOrWhiteSpace(formatted)
            || string.Equals(formatted, TranslationTextBox.Text, StringComparison.Ordinal))
        {
            return;
        }

        _syncingEditor = true;
        try
        {
            int caret = Math.Min(TranslationTextBox.CaretIndex, formatted.Length);
            TranslationTextBox.Text = formatted;
            TranslationTextBox.CaretIndex = caret;
        }
        finally
        {
            _syncingEditor = false;
        }
    }

    private void TranslationTextBox_TextChanged_LineLayout(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null || _selectedRegion.Type == "sfx")
        {
            return;
        }

        if (!_selectedRegion.IsManual)
        {
            // Congelamos como semilla la composición que el usuario estaba viendo antes de
            // editar. El render manual calculará una sola vez su tamaño de partida y después
            // cambiar los Enter no volverá a encoger ni agrandar la fuente.
            _selectedRegion.ManualLayoutSeedText = _automaticLinePreviews.TryGetValue(_selectedRegion.Id, out string? preview)
                ? preview
                : _selectedRegion.Translation;
            _selectedRegion.ManualBaseFontSize = 0;
            _selectedRegion.ManualFontScale = 1;
            _selectedRegion.IsManual = true;
        }

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