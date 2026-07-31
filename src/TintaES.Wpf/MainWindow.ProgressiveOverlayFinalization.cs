using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene actualizado el único árbol visual creado por AddRegionVisual. No crea renderizadores
/// alternativos ni cambia visibilidades: solo sincroniza geometría e invalida los glifos.
/// </summary>
public partial class MainWindow
{
    private static readonly bool OrganicCanvasRefreshRegistered = RegisterOrganicCanvasRefresh();
    private bool _organicCanvasRefreshInstalled;
    private bool _organicCanvasRefreshPending;

    private static bool RegisterOrganicCanvasRefresh()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_OrganicCanvasRefreshLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_OrganicCanvasRefreshLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.InstallFastCanvasText();
        }
    }

    private void InstallFastCanvasText()
    {
        if (_organicCanvasRefreshInstalled)
        {
            QueueFastCanvasTextRefresh(forceLayout: false);
            return;
        }

        _organicCanvasRefreshInstalled = true;
        _regions.CollectionChanged += Regions_OrganicCanvasRefreshCollectionChanged;
        QueueFastCanvasTextRefresh(forceLayout: false);
    }

    private void Regions_OrganicCanvasRefreshCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e) =>
        QueueFastCanvasTextRefresh(forceLayout: false);

    private void QueueFastCanvasTextRefresh(bool forceLayout)
    {
        if (_organicCanvasRefreshPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _organicCanvasRefreshPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _organicCanvasRefreshPending = false;
                RefreshOrganicCanvas(forceLayout);
            },
            DispatcherPriority.Background);
    }

    private void FinalizeProgressiveOverlayTextLayout(bool finalPass)
    {
        RefreshOrganicCanvas(forceLayout: finalPass);
    }

    private void RefreshOrganicCanvas(bool forceLayout)
    {
        if (_originalBitmap is null
            || !string.Equals(_previewMode, "result", StringComparison.Ordinal))
        {
            return;
        }

        OverlayCanvas.Visibility = Visibility.Visible;
        OverlayCanvas.Background = null;
        OverlayCanvas.Width = _originalBitmap.PixelWidth;
        OverlayCanvas.Height = _originalBitmap.PixelHeight;

        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (layer.Tag is not ComicRegion region)
            {
                continue;
            }

            InteractiveComicTextElement? text = layer.Children
                .OfType<InteractiveComicTextElement>()
                .FirstOrDefault();
            if (text is null)
            {
                continue;
            }

            PositionLayer(layer, text, region);
            text.Visibility = region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            text.InvalidateVisual();

            if (forceLayout)
            {
                layer.InvalidateMeasure();
                layer.InvalidateArrange();
                layer.InvalidateVisual();
            }
        }

        RefreshRegionSelectionChrome();
        OverlayCanvas.InvalidateVisual();
    }
}
