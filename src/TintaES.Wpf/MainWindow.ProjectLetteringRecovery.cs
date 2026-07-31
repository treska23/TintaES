using System.Windows;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Restaura regiones guardadas y reconstruye el único overlay canónico. No crea previews,
/// TextBlocks ni controles alternativos.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ProjectLetteringRecoveryRegistered = RegisterProjectLetteringRecovery();
    private bool _projectLetteringRecoveryInstalled;
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
            window.InstallProjectLetteringRecovery();
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

    private void BusyOverlay_ProjectLetteringIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (!BusyOverlay.IsVisible
            && !_comicBatchBusy
            && !_pageNavigationBusy
            && _regions.Count > 0)
        {
            QueueProjectLetteringRestore();
        }
    }

    private void QueueProjectLetteringRestore()
    {
        if (_projectLetteringRefreshPending || Dispatcher.HasShutdownStarted)
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
            DispatcherPriority.Background);
    }

    private void RestoreVisibleProjectLettering()
    {
        if (_originalBitmap is null
            || _regions.Count == 0
            || !string.Equals(_previewMode, "result", StringComparison.Ordinal))
        {
            return;
        }

        foreach (ComicRegion region in _regions)
        {
            NormalizeLoadedProjectRegion(region);
            region.PropertyChanged -= Region_PropertyChanged;
            region.PropertyChanged += Region_PropertyChanged;
        }

        RebuildOverlay();
        OverlayCanvas.Visibility = Visibility.Visible;
        FinalizeProgressiveOverlayTextLayout(finalPass: false);
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
