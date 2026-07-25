using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// La paleta sobre el lienzo nunca muestra rótulos. Algunos manejadores antiguos todavía cambian
/// Content a "Pincel" o "Borrador" al activar una herramienta; este módulo sustituye inmediatamente
/// esos valores por iconos vectoriales y mantiene el estado activo únicamente en el borde.
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
            ApplyIconOnlyFloatingPalette();
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
                CanvasPaletteIcon.Select,
                "Seleccionar y mover textos (Esc)",
                !_drawingRegion && _manualMaskTool == ManualMaskTool.None);
            SetIconOnlyCanvasButton(
                AddRegionButton,
                CanvasPaletteIcon.Region,
                _drawingRegion
                    ? "Dibujar zona activo · pulsa de nuevo o Esc para cancelar"
                    : "Dibujar una zona de texto",
                _drawingRegion);
            SetIconOnlyCanvasButton(
                _maskPaintButton,
                CanvasPaletteIcon.Brush,
                "Pincel: borrar el texto original",
                _manualMaskTool == ManualMaskTool.Paint);
            SetIconOnlyCanvasButton(
                _maskEraseButton,
                CanvasPaletteIcon.Eraser,
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
        CanvasPaletteIcon icon,
        string toolTip,
        bool active)
    {
        button.Content = CreateCanvasPaletteIcon(icon);
        button.ToolTip = toolTip;
        button.Width = 34;
        button.Height = 32;
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(2, 0, 0, 0);
        button.BorderBrush = active
            ? FindResource("AccentBrush") as Brush
            : FindResource("LineBrush") as Brush;
    }

    private FrameworkElement CreateCanvasPaletteIcon(CanvasPaletteIcon icon)
    {
        Brush ink = FindResource("InkBrush") as Brush ?? Brushes.White;
        var canvas = new Canvas
        {
            Width = 24,
            Height = 24,
            IsHitTestVisible = false
        };

        switch (icon)
        {
            case CanvasPaletteIcon.Select:
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M3,2 L3,20 L8,15 L12,23 L16,21 L12,13 L20,13 Z"),
                    Fill = ink,
                    Stretch = Stretch.Uniform
                });
                break;

            case CanvasPaletteIcon.Region:
                canvas.Children.Add(new Rectangle
                {
                    Width = 16,
                    Height = 12,
                    Stroke = ink,
                    StrokeThickness = 1.8,
                    RadiusX = 1,
                    RadiusY = 1
                });
                Canvas.SetLeft(canvas.Children[^1], 4);
                Canvas.SetTop(canvas.Children[^1], 6);
                break;

            case CanvasPaletteIcon.Brush:
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M4,16 C4,19 2,21 2,21 C7,22 10,20 10,16 L21,5 L17,1 L6,13 C5,14 4,15 4,16 Z"),
                    Fill = ink,
                    Stretch = Stretch.Uniform
                });
                break;

            case CanvasPaletteIcon.Eraser:
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M3,16 L13,6 C14,5 15,5 16,6 L21,11 C22,12 22,13 21,14 L13,22 L7,22 L3,18 C2,17 2,17 3,16 Z M8,21 L13,21 L18,16 L13,11 L6,18 Z"),
                    Fill = ink,
                    Stretch = Stretch.Uniform
                });
                break;
        }

        return new Viewbox
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            Child = canvas
        };
    }

    private enum CanvasPaletteIcon
    {
        Select,
        Region,
        Brush,
        Eraser
    }
}
