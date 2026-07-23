using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Edición fina de la composición y navegación del lienzo.
/// - El editor muestra los saltos de línea calculados por el rotulador automático.
/// - Al editar esos saltos, la zona pasa a composición manual y se respetan los Enter.
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
    }

    private void RegionListBox_SelectionChanged_LineLayout(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedRegion is null || _originalBitmap is null)
        {
            return;
        }

        // Una composición manual ya contiene exactamente los saltos elegidos por el usuario.
        if (_selectedRegion.Vertical || HasExplicitLineBreaks(_selectedRegion.Translation))
        {
            return;
        }

        string formatted = _editorLineBreakService.FormatForEditor(
            _selectedRegion,
            _originalBitmap.PixelWidth,
            _originalBitmap.PixelHeight);

        if (string.IsNullOrWhiteSpace(formatted)
            || string.Equals(formatted, TranslationTextBox.Text, StringComparison.Ordinal))
        {
            return;
        }

        // Solo cambiamos la representación del cuadro de edición. Mientras el usuario no
        // toque el texto, Translation conserva la cadena limpia y el rotulador sigue en modo
        // automático. En cuanto el usuario edita el cuadro, los saltos pasan a ser manuales.
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

        bool manualLines = HasExplicitLineBreaks(TranslationTextBox.Text);
        if (_selectedRegion.Vertical != manualLines)
        {
            // Para diálogo Vertical se usa aquí únicamente como indicador de composición
            // rectangular/manual. En SFX mantiene su significado original.
            if (_selectedRegion.Type != "sfx")
            {
                _selectedRegion.Vertical = manualLines;
                InvalidateRegionVisual(_selectedRegion);
            }
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
