using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Organiza una página restaurada usando exclusivamente las previsualizaciones ligeras. Los
/// renderizadores tipográficos precisos quedan reservados para exportar y no se miden al navegar.
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
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallProjectLetteringRecovery,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallProjectLetteringRecovery()
    {
        if (_projectLetteringRecoveryInstalled)
        {
            return;
        }

        _projectLetteringRecoveryInstalled = true;
        BusyOverlay.IsVisibleChanged += BusyOverlay_ProjectLetteringIsVisibleChanged;
    }

    private void BusyOverlay_ProjectLetteringIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (BusyOverlay.IsVisible
            || _comicBatchBusy
            || _pageNavigationBusy
            || string.IsNullOrWhiteSpace(_currentProjectPath)
            || _regions.Count == 0)
        {
            return;
        }

        QueueProjectLetteringRestore();
    }

    private void QueueProjectLetteringRestore()
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
                RestoreVisibleProjectLettering();
            },
            DispatcherPriority.Render);
    }

    private void RestoreVisibleProjectLettering()
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

            RebuildOverlay();
            OverlayCanvas.Visibility = Visibility.Visible;
            OverlayCanvas.Width = _originalBitmap.PixelWidth;
            OverlayCanvas.Height = _originalBitmap.PixelHeight;

            // Prepara los controles de arrastre una sola vez. EnsureManualLineVisual ya no crea ni
            // mide los renderizadores caros: únicamente instala FastComicTextPreviewElement.
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
                layer.ClipToBounds = true;
                Canvas.SetLeft(layer, (box.X + region.TextOffsetX) / 1000 * _originalBitmap.PixelWidth);
                Canvas.SetTop(layer, (box.Y + region.TextOffsetY) / 1000 * _originalBitmap.PixelHeight);

                EnsureManualLineVisual(layer, region, invalidate: false);
                FastComicTextPreviewElement? preview = layer.Children
                    .OfType<FastComicTextPreviewElement>()
                    .FirstOrDefault();

                bool usesManualPreview = region.IsManual && region.Type != "sfx";
                foreach (ComicTextElement renderer in layer.Children.OfType<ComicTextElement>())
                {
                    renderer.Width = width;
                    renderer.Height = height;
                    renderer.RenderTransform = System.Windows.Media.Transform.Identity;
                    renderer.Visibility = region.IsEnabled && !usesManualPreview
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    if (!usesManualPreview)
                    {
                        renderer.InvalidateVisual();
                    }
                }
                foreach (ManualComicTextElement renderer in layer.Children.OfType<ManualComicTextElement>())
                {
                    renderer.Visibility = Visibility.Collapsed;
                }
                foreach (Border border in layer.Children.OfType<Border>())
                {
                    border.Visibility = Visibility.Collapsed;
                }
                foreach (Thumb thumb in layer.Children.OfType<Thumb>().Skip(1))
                {
                    thumb.Visibility = Visibility.Collapsed;
                    thumb.Opacity = 0;
                }

                if (preview is not null)
                {
                    preview.Width = width;
                    preview.Height = height;
                    preview.Visibility = region.IsEnabled && usesManualPreview
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    preview.Measure(new Size(width, height));
                    preview.Arrange(new Rect(0, 0, width, height));
                    preview.InvalidateVisual();
                }

                layer.Measure(new Size(width, height));
                layer.Arrange(new Rect(0, 0, width, height));
            }

            OverlayCanvas.Measure(new Size(_originalBitmap.PixelWidth, _originalBitmap.PixelHeight));
            OverlayCanvas.Arrange(new Rect(0, 0, _originalBitmap.PixelWidth, _originalBitmap.PixelHeight));
            OverlayCanvas.InvalidateVisual();
            RefreshSelectedTextFrame();
        }
        finally
        {
            _restoringProjectLettering = false;
        }
    }

    private static void NormalizeLoadedProjectRegion(ComicRegion region)
    {
        region.Style ??= new ComicTextStyle();
        if (string.Equals(
                region.Translation?.Trim(),
                ComicRegion.PendingTranslationMarker,
                StringComparison.OrdinalIgnoreCase))
        {
            region.Translation = string.Empty;
        }
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
