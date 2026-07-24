using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Termina de preparar las capas añadidas durante la carga progresiva. Crear el Grid y el
/// ComicTextElement no basta: los renderizadores personalizados necesitan un tamaño real,
/// Measure/Arrange y la selección correcta entre el motor automático y el manual.
/// </summary>
public partial class MainWindow
{
    private void FinalizeProgressiveOverlayTextLayout(bool finalPass)
    {
        if (_originalBitmap is null || !string.Equals(_previewMode, "result", StringComparison.Ordinal))
        {
            return;
        }

        OverlayCanvas.Visibility = Visibility.Visible;
        OverlayCanvas.Width = _originalBitmap.PixelWidth;
        OverlayCanvas.Height = _originalBitmap.PixelHeight;

        // Prepara los controladores de arrastre, oculta los marcos de diagnóstico y crea el
        // renderizador manual cuando la traducción fue retocada por el usuario.
        OverlayCanvas_PresentationLayoutUpdated(OverlayCanvas, EventArgs.Empty);

        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (layer.Tag is not ComicRegion region)
            {
                continue;
            }

            NormalizeLoadedProjectRegion(region);
            NormalizedRect box = region.RenderBox;
            double width = Math.Max(2, box.Width / 1000 * _originalBitmap.PixelWidth);
            double height = Math.Max(2, box.Height / 1000 * _originalBitmap.PixelHeight);

            layer.Width = width;
            layer.Height = height;
            Canvas.SetLeft(layer, (box.X + region.TextOffsetX) / 1000 * _originalBitmap.PixelWidth);
            Canvas.SetTop(layer, (box.Y + region.TextOffsetY) / 1000 * _originalBitmap.PixelHeight);

            EnsureManualLineVisual(layer, region, invalidate: false);

            ComicTextElement? automatic = layer.Children.OfType<ComicTextElement>().FirstOrDefault();
            ManualComicTextElement? manual = layer.Children.OfType<ManualComicTextElement>().FirstOrDefault();
            FrameworkElement? renderer = region.Type != "sfx" && region.IsManual
                ? manual
                : automatic;

            if (automatic is not null)
            {
                automatic.Width = width;
                automatic.Height = height;
                automatic.Visibility = renderer == automatic ? Visibility.Visible : Visibility.Collapsed;
                if (renderer == automatic)
                {
                    ApplyTextTransform(automatic, region);
                }
            }

            if (manual is not null)
            {
                manual.Width = width;
                manual.Height = height;
                manual.Visibility = renderer == manual ? Visibility.Visible : Visibility.Collapsed;
            }

            foreach (Border border in layer.Children.OfType<Border>())
            {
                border.Visibility = Visibility.Collapsed;
            }

            Thumb[] thumbs = layer.Children.OfType<Thumb>().ToArray();
            foreach (Thumb thumb in thumbs.Skip(1))
            {
                thumb.Visibility = Visibility.Collapsed;
                thumb.Opacity = 0;
            }

            // El fallo anterior dejaba ActualWidth/ActualHeight en cero. Organizar cada capa
            // explícitamente permite que las letras aparezcan en el mismo lote que su caja.
            layer.Measure(new Size(width, height));
            layer.Arrange(new Rect(0, 0, width, height));
            renderer?.Measure(new Size(width, height));
            renderer?.Arrange(new Rect(0, 0, width, height));
            renderer?.InvalidateVisual();
        }

        OverlayCanvas.InvalidateMeasure();
        OverlayCanvas.InvalidateArrange();
        OverlayCanvas.InvalidateVisual();

        if (finalPass)
        {
            OverlayCanvas.Measure(new Size(_originalBitmap.PixelWidth, _originalBitmap.PixelHeight));
            OverlayCanvas.Arrange(new Rect(0, 0, _originalBitmap.PixelWidth, _originalBitmap.PixelHeight));
            OverlayCanvas.UpdateLayout();
        }
    }
}
