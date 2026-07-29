using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Las regiones automáticas se ajustan a la silueta detectada del bocadillo. El marco rectangular
/// con tiradores queda reservado a las cajas creadas manualmente por el usuario.
/// </summary>
public partial class MainWindow
{
    private static readonly bool AutomaticTextFrameSafetyRegistered = RegisterAutomaticTextFrameSafety();
    private bool _automaticTextFrameSafetyInstalled;
    private bool _automaticTextFrameSafetyPending;

    private static bool RegisterAutomaticTextFrameSafety()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_AutomaticTextFrameSafetyLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_AutomaticTextFrameSafetyLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallAutomaticTextFrameSafety,
                DispatcherPriority.ContextIdle);
        }
    }

    private void InstallAutomaticTextFrameSafety()
    {
        if (_automaticTextFrameSafetyInstalled)
        {
            QueueAutomaticTextFrameSafety();
            return;
        }

        _automaticTextFrameSafetyInstalled = true;
        RegionListBox.SelectionChanged += (_, _) => QueueAutomaticTextFrameSafety();
        BusyOverlay.IsVisibleChanged += (_, _) => QueueAutomaticTextFrameSafety();
        ResultPreviewButton.Click += (_, _) => QueueAutomaticTextFrameSafety();

        // RefreshSelectedTextFrame pertenece al editor de cajas manuales y puede ejecutarse después
        // de SelectionChanged. Esta comprobación es deliberadamente mínima: solo actúa cuando existe
        // un adorner improcedente sobre una región automática.
        OverlayCanvas.LayoutUpdated += (_, _) =>
        {
            if (_textFrameAdorner is not null && _selectedRegion is { IsManual: false })
            {
                RemoveNativeTextFrameAdorner();
            }
        };

        QueueAutomaticTextFrameSafety();
    }

    private void QueueAutomaticTextFrameSafety()
    {
        if (_automaticTextFrameSafetyPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _automaticTextFrameSafetyPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _automaticTextFrameSafetyPending = false;
                ApplyAutomaticTextFrameSafety();
            },
            DispatcherPriority.Background);
    }

    private void ApplyAutomaticTextFrameSafety()
    {
        if (_selectedRegion is { IsManual: false })
        {
            RemoveNativeTextFrameAdorner();
        }

        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>().ToArray())
        {
            if (layer.Tag is not ComicRegion region || region.IsManual)
            {
                continue;
            }

            foreach (TextBlock native in layer.Children
                         .OfType<TextBlock>()
                         .Where(text => Equals(text.Tag, NativeTextBlockTag)))
            {
                native.Visibility = Visibility.Collapsed;
            }

            ComicTextElement? precise = layer.Children
                .OfType<ComicTextElement>()
                .FirstOrDefault();
            if (precise is not null)
            {
                InstallInteractiveCanvasText(layer, precise, region);
            }

            foreach (ComicTextElement renderer in layer.Children.OfType<ComicTextElement>())
            {
                renderer.Visibility = Visibility.Collapsed;
                renderer.Opacity = 0;
                renderer.IsEnabled = false;
            }

            InteractiveComicTextElement? interactive = layer.Children
                .OfType<InteractiveComicTextElement>()
                .FirstOrDefault();
            if (interactive is not null)
            {
                interactive.Visibility = region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
                interactive.InvalidateVisual();
            }
        }
    }
}
