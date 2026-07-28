using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene el lienzo interactivo ligero y evita cualquier Measure, Arrange o UpdateLayout
/// síncrono. El render editorial preciso se utiliza únicamente al exportar.
/// </summary>
public partial class MainWindow
{
    private static readonly bool FastCanvasTextRegistered = RegisterFastCanvasText();
    private bool _fastCanvasTextInstalled;
    private bool _applyingFastCanvasText;
    private bool _fastCanvasTextRefreshPending;

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
                DispatcherPriority.Loaded);
        }
    }

    private void InstallFastCanvasText()
    {
        if (_fastCanvasTextInstalled)
        {
            return;
        }

        // Instala primero el estilo que mantiene colapsado ComicTextElement. Antes se hacía al
        // revés y este archivo volvía a mostrarlo para las zonas automáticas.
        InstallNonBlockingCanvasText();
        _fastCanvasTextInstalled = true;
        _regions.CollectionChanged += Regions_FastCanvasTextCollectionChanged;
        QueueFastCanvasTextRefresh(forceLayout: false);
    }

    private void Regions_FastCanvasTextCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueueFastCanvasTextRefresh(forceLayout: false);

    private void QueueFastCanvasTextRefresh(bool forceLayout)
    {
        if (_fastCanvasTextRefreshPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _fastCanvasTextRefreshPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _fastCanvasTextRefreshPending = false;
                EnsureFastCanvasTextPreviews(forceLayout);
            },
            DispatcherPriority.Background);
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

        EnsureFastCanvasTextPreviews(forceLayout: false);
        OverlayCanvas.InvalidateMeasure();
        OverlayCanvas.InvalidateArrange();
        OverlayCanvas.InvalidateVisual();

        // Nunca forzamos UpdateLayout en el hilo de interfaz. Incluso el pase final se programa
        // para después de atender entrada, movimiento de ventana y repintado del marco.
        if (finalPass)
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    OverlayCanvas.InvalidateMeasure();
                    OverlayCanvas.InvalidateArrange();
                    OverlayCanvas.InvalidateVisual();
                },
                DispatcherPriority.Background);
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
                layer.ClipToBounds = true;
                Canvas.SetLeft(layer, (box.X + region.TextOffsetX) / 1000 * _originalBitmap.PixelWidth);
                Canvas.SetTop(layer, (box.Y + region.TextOffsetY) / 1000 * _originalBitmap.PixelHeight);

                // Esta era la regresión: automatic.Visibility se volvía a poner en Visible y WPF
                // ejecutaba el ajuste exhaustivo dentro de OnRender. Ahora permanece siempre fuera
                // del lienzo interactivo.
                foreach (ComicTextElement accurate in layer.Children.OfType<ComicTextElement>())
                {
                    accurate.Visibility = Visibility.Collapsed;
                    accurate.Opacity = 0;
                    accurate.IsEnabled = false;
                    accurate.IsHitTestVisible = false;
                }
                foreach (ManualComicTextElement manual in layer.Children.OfType<ManualComicTextElement>())
                {
                    manual.Visibility = Visibility.Collapsed;
                }

                bool nativeEditorVisible = ReferenceEquals(region, _selectedRegion)
                    && layer.Children
                        .OfType<TextBlock>()
                        .Any(text => Equals(text.Tag, NativeTextBlockTag)
                            && text.Visibility == Visibility.Visible);

                bool usesInteractivePreview = !region.IsManual || region.Type == "sfx";
                InteractiveComicTextElement? interactive = layer.Children
                    .OfType<InteractiveComicTextElement>()
                    .FirstOrDefault();
                if (usesInteractivePreview && interactive is null)
                {
                    interactive = new InteractiveComicTextElement
                    {
                        Region = region,
                        PageWidth = _originalBitmap.PixelWidth,
                        PageHeight = _originalBitmap.PixelHeight,
                        IsHitTestVisible = false
                    };
                    Panel.SetZIndex(interactive, 1);
                    layer.Children.Insert(0, interactive);
                }

                if (interactive is not null)
                {
                    interactive.Width = width;
                    interactive.Height = height;
                    interactive.RenderTransform = Transform.Identity;
                    interactive.Visibility = region.IsEnabled
                        && usesInteractivePreview
                        && !nativeEditorVisible
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    interactive.InvalidateVisual();
                }

                bool usesManualPreview = region.IsManual && region.Type != "sfx";
                FastComicTextPreviewElement? preview = layer.Children
                    .OfType<FastComicTextPreviewElement>()
                    .FirstOrDefault();
                if (usesManualPreview && preview is null)
                {
                    preview = new FastComicTextPreviewElement
                    {
                        Region = region,
                        PageWidth = _originalBitmap.PixelWidth,
                        PageHeight = _originalBitmap.PixelHeight,
                        IsHitTestVisible = false
                    };
                    Panel.SetZIndex(preview, 1);
                    layer.Children.Insert(0, preview);
                }

                if (preview is not null)
                {
                    preview.Width = width;
                    preview.Height = height;
                    preview.RenderTransform = Transform.Identity;
                    preview.Visibility = region.IsEnabled
                        && usesManualPreview
                        && !nativeEditorVisible
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    preview.InvalidateVisual();
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

                if (forceLayout)
                {
                    // Solo invalidamos; WPF hará el layout cuando el dispatcher esté libre.
                    layer.InvalidateMeasure();
                    layer.InvalidateArrange();
                    layer.InvalidateVisual();
                }
            }
        }
        finally
        {
            _applyingFastCanvasText = false;
        }
    }
}