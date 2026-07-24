using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Guardar y abrir un proyecto puede provocar un nuevo ciclo completo de layout. Esta capa
/// reconstruye e invalida expresamente la rotulación visible cuando termina la operación para
/// que las traducciones conservadas en las regiones vuelvan a dibujarse inmediatamente.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ProjectLetteringRecoveryRegistered = RegisterProjectLetteringRecovery();

    private bool _projectLetteringRecoveryInstalled;
    private bool _restoringProjectLettering;

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
    }

    private void BusyOverlay_ProjectLetteringIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (BusyOverlay.IsVisible || _comicBatchBusy || _pageNavigationBusy)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            RestoreVisibleProjectLettering,
            DispatcherPriority.Loaded);
    }

    private void RestoreVisibleProjectLettering()
    {
        if (_restoringProjectLettering
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
            }

            RebuildOverlay();
            OverlayCanvas.UpdateLayout();

            foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
            {
                if (layer.Tag is not ComicRegion region)
                {
                    continue;
                }

                ComicTextElement? automatic = layer.Children.OfType<ComicTextElement>().FirstOrDefault();
                if (automatic is not null)
                {
                    Panel.SetZIndex(automatic, 10);
                    ApplyRegionPlacement(layer, automatic, region);
                    automatic.InvalidateMeasure();
                    automatic.InvalidateArrange();
                    automatic.InvalidateVisual();
                }

                EnsureManualLineVisual(layer, region, invalidate: true);
            }

            OverlayCanvas.Visibility = Visibility.Visible;
            OverlayCanvas.InvalidateMeasure();
            OverlayCanvas.InvalidateArrange();
            OverlayCanvas.InvalidateVisual();
        }
        finally
        {
            _restoringProjectLettering = false;
        }
    }
}