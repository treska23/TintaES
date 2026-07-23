using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TintaES.Core;
using TintaES.Wpf.Controls;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Edición fina de la composición y navegación del lienzo.
/// - El editor muestra los saltos de línea calculados por el rotulador automático.
/// - Al editar esos saltos, la zona pasa a composición manual: cada Enter es una línea real.
/// - Espacio + arrastrar desplaza la página como en Photoshop.
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
        RegionListBox.SelectionChanged += RegionListBox_SelectionChanged_LineLayout;
        TranslationTextBox.TextChanged += TranslationTextBox_TextChanged_LineLayout;
        FontScaleSlider.ValueChanged += FontScaleSlider_ValueChanged_ManualLineLayout;
        OverlayCanvas.LayoutUpdated += OverlayCanvas_ManualLineLayoutUpdated;
    }

    private void RegionListBox_SelectionChanged_LineLayout(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedRegion is null || _originalBitmap is null)
        {
            return;
        }

        // Una composición manual ya contiene exactamente los saltos elegidos por el usuario.
        if (_selectedRegion.Type != "sfx" && HasExplicitLineBreaks(_selectedRegion.Translation))
        {
            _selectedRegion.Vertical = true;
            RefreshManualLineVisual(_selectedRegion);
            return;
        }

        RefreshManualLineVisual(_selectedRegion);

        string formatted = _editorLineBreakService.FormatForEditor(
            _selectedRegion,
            _originalBitmap.PixelWidth,
            _originalBitmap.PixelHeight);

        if (string.IsNullOrWhiteSpace(formatted)
            || string.Equals(formatted, TranslationTextBox.Text, StringComparison.Ordinal))
        {
            return;
        }

        // El cuadro lateral enseña la propuesta automática con sus líneas. Translation sigue
        // limpia hasta que el usuario toque el contenido. En ese momento esos saltos pasan a
        // ser la composición manual real que se dibuja en la página.
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
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        bool manualLines = _selectedRegion.Type != "sfx"
            && HasExplicitLineBreaks(TranslationTextBox.Text);

        // Vertical se mantiene como indicador compatible de composición manual para diálogo,
        // pero el dibujo ya no usa el render rectangular que añadía saltos por su cuenta.
        if (_selectedRegion.Type != "sfx")
        {
            _selectedRegion.Vertical = manualLines;
        }

        RefreshManualLineVisual(_selectedRegion);
    }

    private void FontScaleSlider_ValueChanged_ManualLineLayout(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        RefreshManualLineVisual(_selectedRegion);
    }

    private void OverlayCanvas_ManualLineLayoutUpdated(object? sender, EventArgs e)
    {
        if (_selectedRegion is not null
            && _selectedRegion.Type != "sfx"
            && HasExplicitLineBreaks(_selectedRegion.Translation))
        {
            // RebuildOverlay puede recrear la capa por cambios de estilo. Reponemos el
            // render manual solo si falta; no invalidamos continuamente durante LayoutUpdated.
            RefreshManualLineVisual(_selectedRegion, invalidate: false);
        }
    }

    private void RefreshManualLineVisual(ComicRegion region, bool invalidate = true)
    {
        if (_originalBitmap is null)
        {
            return;
        }

        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (!ReferenceEquals(layer.Tag, region))
            {
                continue;
            }

            ComicTextElement? automatic = layer.Children.OfType<ComicTextElement>().FirstOrDefault();
            ManualComicTextElement? manual = layer.Children.OfType<ManualComicTextElement>().FirstOrDefault();
            bool useManual = region.Type != "sfx" && HasExplicitLineBreaks(region.Translation);

            if (!useManual)
            {
                if (automatic is not null)
                {
                    automatic.Visibility = Visibility.Visible;
                    automatic.InvalidateVisual();
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
                if (Math.Abs(manual.Width - layer.Width) > 0.1)
                {
                    manual.Width = layer.Width;
                    invalidate = true;
                }
                if (Math.Abs(manual.Height - layer.Height) > 0.1)
                {
                    manual.Height = layer.Height;
                    invalidate = true;
                }
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
            return;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key != Key.Space || IsTextEntryFocused())
        {
            return;
        }

        if (!_spacePanHeld)
        {
            _spacePanHeld = true;
            ImageScrollViewer.Cursor = Cursors.Hand;
        }

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
        double deltaX = pointer.X - _panStartPointer.X;
        double deltaY = pointer.Y - _panStartPointer.Y;
        ImageScrollViewer.ScrollToHorizontalOffset(_panStartHorizontalOffset - deltaX);
        ImageScrollViewer.ScrollToVerticalOffset(_panStartVerticalOffset - deltaY);
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

    private static bool HasExplicitLineBreaks(string text) =>
        text.Contains('\n') || text.Contains('\r');
}
