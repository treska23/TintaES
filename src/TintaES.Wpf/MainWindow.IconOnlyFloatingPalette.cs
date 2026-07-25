using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// La paleta sobre el lienzo nunca muestra rótulos. Algunos manejadores antiguos todavía cambian
/// Content a "Pincel" o "Borrador" al activar una herramienta; este módulo sustituye inmediatamente
/// esos valores por símbolos de interfaz nativos y mantiene el estado activo únicamente en el borde.
/// </summary>
public partial class MainWindow
{
    private static readonly bool IconOnlyFloatingPaletteRegistered = RegisterIconOnlyFloatingPalette();
    private static readonly DependencyPropertyDescriptor? FloatingToolContentDescriptor =
        DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(Button));

    private bool _iconOnlyFloatingPaletteInstalled;
    private bool _applyingIconOnlyFloatingPalette;

    private static bool RegisterIconOnlyFloatingPalette()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_IconOnlyFloatingPaletteLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_IconOnlyFloatingPaletteLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallIconOnlyFloatingPalette,
                DispatcherPriority.ContextIdle);
        }
    }

    private void InstallIconOnlyFloatingPalette()
    {
        TryInstallFloatingEditorPalette();
        if (!_floatingEditorPaletteInstalled
            || _selectCanvasToolButton is null
            || _maskPaintButton is null
            || _maskEraseButton is null)
        {
            Dispatcher.BeginInvoke(InstallIconOnlyFloatingPalette, DispatcherPriority.ContextIdle);
            return;
        }

        if (!_iconOnlyFloatingPaletteInstalled)
        {
            _iconOnlyFloatingPaletteInstalled = true;
            if (FloatingToolContentDescriptor is not null)
            {
                foreach (Button button in new[]
                {
                    _selectCanvasToolButton,
                    AddRegionButton,
                    _maskPaintButton,
                    _maskEraseButton
                })
                {
                    FloatingToolContentDescriptor.AddValueChanged(
                        button,
                        FloatingCanvasToolContentChanged);
                }
            }
        }

        ApplyIconOnlyFloatingPalette();
    }

    private void FloatingCanvasToolContentChanged(object? sender, EventArgs e)
    {
        if (!_applyingIconOnlyFloatingPalette)
        {
            Dispatcher.BeginInvoke(ApplyIconOnlyFloatingPalette, DispatcherPriority.Input);
        }
    }

    private void ApplyIconOnlyFloatingPalette()
    {
        if (!_iconOnlyFloatingPaletteInstalled
            || _applyingIconOnlyFloatingPalette
            || _selectCanvasToolButton is null
            || _maskPaintButton is null
            || _maskEraseButton is null)
        {
            return;
        }

        _applyingIconOnlyFloatingPalette = true;
        try
        {
            SetIconOnlyCanvasButton(
                _selectCanvasToolButton,
                "↖",
                "Seleccionar y mover textos (Esc)",
                !_drawingRegion && _manualMaskTool == ManualMaskTool.None);
            SetIconOnlyCanvasButton(
                AddRegionButton,
                "▭",
                _drawingRegion
                    ? "Dibujar zona activo · pulsa de nuevo o Esc para cancelar"
                    : "Dibujar una zona de texto",
                _drawingRegion);
            SetIconOnlyCanvasButton(
                _maskPaintButton,
                "✎",
                "Pincel: borrar el texto original",
                _manualMaskTool == ManualMaskTool.Paint);
            SetIconOnlyCanvasButton(
                _maskEraseButton,
                "⌫",
                "Borrador: recuperar la imagen original",
                _manualMaskTool == ManualMaskTool.Erase);
        }
        finally
        {
            _applyingIconOnlyFloatingPalette = false;
        }
    }

    private void SetIconOnlyCanvasButton(
        Button button,
        string glyph,
        string toolTip,
        bool active)
    {
        button.Content = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = glyph == "✎" ? 19 : 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindResource("InkBrush") as Brush ?? Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };
        button.ToolTip = toolTip;
        button.Width = 34;
        button.Height = 32;
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(2, 0, 0, 0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.BorderBrush = active
            ? FindResource("AccentBrush") as Brush
            : FindResource("LineBrush") as Brush;
    }
}
