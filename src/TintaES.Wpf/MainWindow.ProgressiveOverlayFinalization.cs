using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene en el lienzo un renderer ligero. ComicTextElement y ManualComicTextElement siguen
/// presentes para reutilizar la edición existente, pero permanecen colapsados: la composición
/// tipográfica precisa se reserva para las exportaciones.
/// </summary>
public partial class MainWindow
{
    private static readonly bool FastCanvasTextRegistered = RegisterFastCanvasText();
    private bool _fastCanvasTextInstalled;
    private bool _applyingFastCanvasText;

    private static bool RegisterFastCanvasText()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_FastCanvasTextLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_FastCanvasTextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallFastCanvasText,
                DispatcherPriority.SystemIdle);
        }
    }

    private void InstallFastCanvasText()
    {
        if (_fastCanvasTextInstalled)
        {
            return;
        }

        _fastCanvasTextInstalled = true;
        OverlayCanvas.LayoutUpdated += OverlayCanvas_FastCanvasTextLayoutUpdated;
        EnsureFastCanvasTextPreviews(forceLayout: false);
    }

    private void OverlayCanvas_FastCanvasTextLayoutUpdated(object? sender, EventArgs e)
    {
        EnsureFastCanvasTextPreviews(forceLayout: false);
    }

    private void FinalizeProgressiveOverlayTextLayout(bool finalPass)
    {
        if (_originalBitmap is null || !string.Equals(_previewMode, "result", StringComparison.Ordinal))
        {
            return;
        }

        OverlayCanvas.Visibility = Visibility.Visible;
        OverlayCanvas.Width = _originalBitmap.PixelWidth;
        OverlayCanvas.Height = _originalBitmap.PixelHeight;

        // Conservamos la preparación de los thumbs y la selección, pero sustituimos los dos
        // renderizadores costosos antes del siguiente ciclo de pintura.
        OverlayCanvas_PresentationLayoutUpdated(OverlayCanvas, EventArgs.Empty);
        EnsureFastCanvasTextPreviews(forceLayout: true);

        OverlayCanvas.InvalidateMeasure();
        OverlayCanvas.InvalidateArrange();
        OverlayCanvas.InvalidateVisual();

        if (finalPass)
        {
            // Una sola actualización final. Los renderizadores precisos están Collapsed y por
            // tanto no participan en Measure ni ejecutan su algoritmo de ajuste tipográfico.
            OverlayCanvas.Measure(new Size(_originalBitmap.PixelWidth, _originalBitmap.PixelHeight));
            OverlayCanvas.Arrange(new Rect(0, 0, _originalBitmap.PixelWidth, _originalBitmap.PixelHeight));
            OverlayCanvas.UpdateLayout();
        }
    }

    private void EnsureFastCanvasTextPreviews(bool forceLayout)
    {
        if (_applyingFastCanvasText
            || _originalBitmap is null
            || !string.Equals(_previewMode, "result", StringComparison.Ordinal))
        {
            return;
        }

        _applyingFastCanvasText = true;
        try
        {
            foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>().ToArray())
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

                foreach (ComicTextElement automatic in layer.Children.OfType<ComicTextElement>())
                {
                    automatic.Visibility = Visibility.Collapsed;
                }
                foreach (ManualComicTextElement manual in layer.Children.OfType<ManualComicTextElement>())
                {
                    manual.Visibility = Visibility.Collapsed;
                }

                FastComicTextPreviewElement? preview = layer.Children
                    .OfType<FastComicTextPreviewElement>()
                    .FirstOrDefault();
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

                preview.Width = width;
                preview.Height = height;
                preview.Visibility = region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
                preview.RenderTransformOrigin = new Point(0.5, 0.5);
                double scale = Math.Clamp(region.ManualFontScale, 0.25, 2.5);
                preview.RenderTransform = new ScaleTransform(scale, scale);
                preview.InvalidateVisual();

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

                if (forceLayout)
                {
                    layer.Measure(new Size(width, height));
                    layer.Arrange(new Rect(0, 0, width, height));
                    preview.Measure(new Size(width, height));
                    preview.Arrange(new Rect(0, 0, width, height));
                }
            }
        }
        finally
        {
            _applyingFastCanvasText = false;
        }
    }
}
