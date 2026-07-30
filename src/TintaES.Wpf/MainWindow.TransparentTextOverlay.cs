using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// La capa de rotulación solo contiene glifos. El fondo pertenece exclusivamente a PageImage
/// (clean.png); ninguna caja o TextBlock del overlay puede aportar un rectángulo opaco.
/// </summary>
public partial class MainWindow
{
    private static readonly bool TransparentTextOverlayRegistered = RegisterTransparentTextOverlay();

    private bool _transparentTextOverlayInstalled;
    private bool _transparentTextOverlayPending;

    private static bool RegisterTransparentTextOverlay()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_TransparentTextOverlayLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_TransparentTextOverlayLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallTransparentTextOverlay,
                DispatcherPriority.Loaded);
        }
    }

    private void InstallTransparentTextOverlay()
    {
        if (_transparentTextOverlayInstalled)
        {
            QueueTransparentTextOverlayRefresh();
            return;
        }

        _transparentTextOverlayInstalled = true;
        OverlayCanvas.Background = Brushes.Transparent;
        OverlayCanvas.LayoutUpdated += (_, _) => QueueTransparentTextOverlayRefresh();
        QueueTransparentTextOverlayRefresh();
    }

    private void QueueTransparentTextOverlayRefresh()
    {
        if (_transparentTextOverlayPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _transparentTextOverlayPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _transparentTextOverlayPending = false;
                EnforceTransparentTextOverlay();
            },
            DispatcherPriority.Render);
    }

    private void EnforceTransparentTextOverlay()
    {
        OverlayCanvas.Background = Brushes.Transparent;

        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (layer.Tag is not ComicRegion)
            {
                continue;
            }

            // Grid.Background es el único fondo posible de la caja contenedora.
            layer.Background = Brushes.Transparent;

            // El editor nativo usa TextBlock para la caja seleccionada. WPF lo deja transparente
            // por defecto, pero se fija expresamente para impedir que estilos heredados lo cambien.
            foreach (TextBlock textBlock in layer.Children.OfType<TextBlock>())
            {
                textBlock.Background = Brushes.Transparent;
            }
        }
    }
}
