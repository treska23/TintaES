using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Las capas creadas al deserializar un proyecto necesitan pasar por un ciclo de medida y
/// disposición real. Invalidar el dibujo sin organizar los controles dejaba ComicTextElement
/// con ActualWidth/ActualHeight igual a cero y por eso se veían las zonas, pero no las letras.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ProjectLetteringRecoveryRegistered = RegisterProjectLetteringRecovery();

    private bool _projectLetteringRecoveryInstalled;
    private bool _restoringProjectLettering;
    private bool _projectLetteringRefreshPending;

    private static bool RegisterProjectLetteringRecovery()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ProjectLetteringRecoveryLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ProjectLetteringRecoveryLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.Dispatcher.BeginInvoke(
            window.InstallProjectLetteringRecovery,
            DispatcherPriority.ApplicationIdle);
    }

    private void InstallProjectLetteringRecovery()
    {
        if (_projectLetteringRecoveryInstalled)
        {
            return;
        }

        _projectLetteringRecoveryInstalled = true;
        BusyOverlay.IsVisibleChanged += BusyOverlay_ProjectLetteringIsVisibleChanged;
        OverlayCanvas.LayoutUpdated += OverlayCanvas_ProjectLetteringLayoutUpdated;
    }

    private void BusyOverlay_ProjectLetteringIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (BusyOverlay.IsVisible || _comicBatchBusy || _pageNavigationBusy)
        {
            return;
        }

        QueueProjectLetteringRestore(rebuildOverlay: true);
    }

    private void OverlayCanvas_ProjectLetteringLayoutUpdated(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentProjectPath)
            || _comicBatchBusy
            || _pageNavigationBusy
            || _regions.Count == 0
            || !string.Equals(_previewMode, "result", StringComparison.Ordinal))
        {
            return;
        }

        QueueProjectLetteringRestore(rebuildOverlay: false);
    }

    private void QueueProjectLetteringRestore(bool rebuildOverlay)
    {
        if (_projectLetteringRefreshPending)
        {
            return;
        }

        _projectLetteringRefreshPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _projectLetteringRefreshPending = false;
                RestoreVisibleProjectLettering(rebuildOverlay);
            },
            DispatcherPriority.Render);
    }

    private void RestoreVisibleProjectLettering(bool rebuildOverlay = true)
    {
        if (_restoringProjectLettering
            || string.IsNullOrWhiteSpace(_currentProjectPath)
            || _originalBitmap is null
            || _regions.Count == 0
            || !string.Equals(_previewMode, "result", StringComparison.Ordinal))
        {
            return;
        }

        _restoringProjectLettering = true;
        try
        {
            foreach (ComicRegion region in _regions)
            {
                NormalizeLoadedProjectRegion(region);
                region.PropertyChanged -= Region_PropertyChanged;
                region.PropertyChanged += Region_PropertyChanged;
            }

            if (rebuildOverlay || OverlayCanvas.Children.Count == 0)
            {
                RebuildOverlay();
            }

            OverlayCanvas.Visibility = Visibility.Visible;
            OverlayCanvas.Width = _originalBitmap.PixelWidth;
            OverlayCanvas.Height = _originalBitmap.PixelHeight;

            // Ejecutamos la misma preparación que usa la vista normal: oculta los marcos de
            // diagnóstico, conecta el arrastre rápido y crea el renderizador manual si procede.
            OverlayCanvas_PresentationLayoutUpdated(OverlayCanvas, EventArgs.Empty);

            foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
            {
                if (layer.Tag is not ComicRegion region)
                {
                    continue;
                }

                NormalizedRect box = region.RenderBox;
                double width = Math.Max(2, box.Width / 1000 * _originalBitmap.PixelWidth);
                double height = Math.Max(2, box.Height / 1000 * _originalBitmap.PixelHeight);
                layer.Width = width;
                layer.Height = height;
                Canvas.SetLeft(layer, (box.X + region.TextOffsetX) / 1000 * _originalBitmap.PixelWidth);
                Canvas.SetTop(layer, (box.Y + region.TextOffsetY) / 1000 * _originalBitmap.PixelHeight);

                EnsureManualLineVisual(layer, region, invalidate: true);

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
                foreach (Thumb thumb in layer.Children.OfType<Thumb>().Skip(1))
                {
                    thumb.Visibility = Visibility.Collapsed;
                }

                layer.Measure(new Size(width, height));
                layer.Arrange(new Rect(0, 0, width, height));
                renderer?.Measure(new Size(width, height));
                renderer?.Arrange(new Rect(0, 0, width, height));
                renderer?.InvalidateMeasure();
                renderer?.InvalidateArrange();
                renderer?.InvalidateVisual();
            }

            OverlayCanvas.Measure(new Size(_originalBitmap.PixelWidth, _originalBitmap.PixelHeight));
            OverlayCanvas.Arrange(new Rect(0, 0, _originalBitmap.PixelWidth, _originalBitmap.PixelHeight));
            OverlayCanvas.UpdateLayout();
            OverlayCanvas.InvalidateVisual();
        }
        finally
        {
            _restoringProjectLettering = false;
        }
    }

    private static void NormalizeLoadedProjectRegion(ComicRegion region)
    {
        region.Style ??= new ComicTextStyle();
        region.TextBox = (region.TextBox ?? new NormalizedRect(100, 100, 200, 80)).Clamp();
        region.RenderBox = (region.RenderBox ?? region.TextBox.Expand(0.1, 0.2)).Clamp();
        region.SafePolygon ??= [];

        if (!double.IsFinite(region.FontScale) || region.FontScale <= 0)
        {
            region.FontScale = 1;
        }
        if (!double.IsFinite(region.ManualFontScale) || region.ManualFontScale <= 0)
        {
            region.ManualFontScale = 1;
        }
        if (!double.IsFinite(region.ManualBaseFontSize) || region.ManualBaseFontSize < 0)
        {
            region.ManualBaseFontSize = 0;
        }
        if (!double.IsFinite(region.TextOffsetX))
        {
            region.TextOffsetX = 0;
        }
        if (!double.IsFinite(region.TextOffsetY))
        {
            region.TextOffsetY = 0;
        }
    }
}
