using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// La paleta sobre el lienzo muestra exclusivamente iconos. Los botones de máscara conservan
/// manejadores antiguos que modifican Content durante LayoutUpdated; por eso cada botón recibe una
/// DataTemplate fija que ignora por completo ese contenido y dibuja siempre el glifo correspondiente.
/// </summary>
public partial class MainWindow
{
    private static readonly bool IconOnlyFloatingPaletteRegistered = RegisterIconOnlyFloatingPalette();

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

    private void SetIconOnlyCanvasButton(
        Button button,
        string glyph,
        string toolTip,
        bool active)
    {
        // Content puede seguir cambiando internamente a "Pincel" o "Borrador". La plantilla no
        // enlaza ese valor, así que esos textos nunca vuelven a dibujarse ni fuerzan otro refresco.
        button.ContentTemplate = CreateFixedGlyphTemplate(glyph);
        button.Content = string.Empty;
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

    private DataTemplate CreateFixedGlyphTemplate(string glyph)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, glyph);
        text.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI Symbol"));
        text.SetValue(TextBlock.FontSizeProperty, glyph == "✎" ? 19d : 18d);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(TextBlock.ForegroundProperty, FindResource("InkBrush") as Brush ?? Brushes.White);
        text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        text.SetValue(TextBlock.IsHitTestVisibleProperty, false);

        return new DataTemplate
        {
            VisualTree = text
        };
    }
}
