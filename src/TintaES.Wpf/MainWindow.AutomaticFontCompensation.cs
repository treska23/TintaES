using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// La vista interactiva no aplicaba la compensación visual que sí usa el render editorial.
/// Como resultado, una estimación OCR algo baja (por ejemplo 29 px) se mostraba literalmente
/// y obligaba a ampliar cada bocadillo a mano. Las regiones automáticas usan una compensación
/// estable e idempotente; las regiones que el usuario convierte en manual no se tocan.
/// </summary>
public partial class MainWindow
{
    private const double AutomaticFontCompensation = 1.40;

    private static readonly bool AutomaticFontCompensationRegistered =
        RegisterAutomaticFontCompensation();

    private bool _automaticFontCompensationInstalled;

    private static bool RegisterAutomaticFontCompensation()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_AutomaticFontCompensationLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_AutomaticFontCompensationLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallAutomaticFontCompensation,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallAutomaticFontCompensation()
    {
        if (_automaticFontCompensationInstalled)
        {
            ApplyAutomaticFontCompensationToAllPages();
            return;
        }

        _automaticFontCompensationInstalled = true;
        _regions.CollectionChanged += Regions_AutomaticFontCompensationCollectionChanged;
        BusyOverlay.IsVisibleChanged += BusyOverlay_AutomaticFontCompensationVisibilityChanged;
        ApplyAutomaticFontCompensationToAllPages();
    }

    private void Regions_AutomaticFontCompensationCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (object item in e.NewItems)
            {
                if (item is ComicRegion region)
                {
                    ApplyAutomaticFontCompensation(region, notify: false);
                }
            }
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                QueueFastCanvasTextRefresh(forceLayout: false);
                RefreshFontSizeNumber();
            },
            DispatcherPriority.DataBind);
    }

    private void BusyOverlay_AutomaticFontCompensationVisibilityChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (!BusyOverlay.IsVisible)
        {
            ApplyAutomaticFontCompensationToAllPages();
        }
    }

    private void ApplyAutomaticFontCompensationToAllPages()
    {
        bool currentChanged = false;
        foreach (ComicRegion region in _regions)
        {
            currentChanged |= ApplyAutomaticFontCompensation(region, notify: false);
        }

        foreach (ComicBookPageState page in _comicPages)
        {
            foreach (ComicRegion region in page.Regions)
            {
                ApplyAutomaticFontCompensation(region, notify: false);
            }
        }

        if (currentChanged)
        {
            foreach (ComicRegion region in _regions)
            {
                region.NotifyVisualChange();
            }
            QueueFastCanvasTextRefresh(forceLayout: false);
            RefreshFontSizeNumber();
        }
    }

    private static bool ApplyAutomaticFontCompensation(ComicRegion region, bool notify)
    {
        if (region.IsManual || region.Type == "sfx")
        {
            return false;
        }

        if (Math.Abs(region.FontScale - AutomaticFontCompensation) < 0.001)
        {
            return false;
        }

        region.FontScale = AutomaticFontCompensation;
        if (notify)
        {
            region.NotifyVisualChange();
        }
        return true;
    }
}
