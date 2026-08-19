using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TintaES.Wpf;

/// <summary>
/// Desplazamiento directo de la página: el botón izquierdo arrastra sin teclas modificadoras.
/// Ctrl+clic sobre una zona fija o libera su tarjeta en el inspector para poder editarla sin
/// que el hover cambie la selección. Escape libera cualquier selección fijada.
/// </summary>
public partial class MainWindow
{
    private bool _isSpacePanning;
    private Point _panStartPointer;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (!ImageScrollViewer.IsMouseOver || BusyOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        // Ctrl+clic pertenece exclusivamente al bloqueo de tarjeta. No inicia pan y por tanto
        // no compite con el gesto normal de arrastrar la página.
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (ToggleMainTranslationSelectionLockAt(e.GetPosition(ImageStage)))
            {
                e.Handled = true;
            }
            return;
        }

        HideMainTranslation();
        _isSpacePanning = true;
        _panStartPointer = e.GetPosition(ImageScrollViewer);
        _panStartHorizontalOffset = ImageScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = ImageScrollViewer.VerticalOffset;
        ImageScrollViewer.Cursor = Cursors.Hand;
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
        ImageScrollViewer.Cursor = Cursors.Hand;
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape && ReleaseMainTranslationSelectionLock())
        {
            e.Handled = true;
        }
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
}
