using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// La capa de rotulación automática contiene únicamente los glifos. El elemento que cubre la
/// región para permitir arrastrarla conserva el hit-test, pero usa una plantilla visual vacía:
/// no puede pintar una placa, un borde ni un fondo debajo del texto.
/// </summary>
public partial class MainWindow
{
    private static readonly bool TransparentTextOverlayRegistered = RegisterTransparentTextOverlay();

    private bool _transparentTextOverlayInstalled;
    private bool _transparentTextOverlayPending;
    private ControlTemplate? _transparentAutomaticHitTargetTemplate;

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
        OverlayCanvas.Background = null;
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
        OverlayCanvas.Background = null;

        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (layer.Tag is not ComicRegion region)
            {
                continue;
            }

            layer.Background = null;

            foreach (TextBlock textBlock in layer.Children.OfType<TextBlock>())
            {
                textBlock.Background = null;
            }

            if (region.IsManual)
            {
                continue;
            }

            // Las zonas automáticas deben mostrar solo el renderizador de glifos. El primer Thumb
            // es el área invisible de arrastre. Background=Transparent no bastaba porque la
            // plantilla nativa podía seguir dibujando su chrome; una plantilla vacía lo impide.
            Thumb[] thumbs = layer.Children.OfType<Thumb>().ToArray();
            for (int index = 0; index < thumbs.Length; index++)
            {
                Thumb thumb = thumbs[index];
                thumb.Background = null;
                thumb.BorderBrush = null;
                thumb.BorderThickness = new Thickness(0);

                if (index == 0)
                {
                    thumb.Template = GetTransparentAutomaticHitTargetTemplate();
                    thumb.Opacity = 0;
                    thumb.Visibility = Visibility.Visible;
                    thumb.IsHitTestVisible = true;
                    Panel.SetZIndex(thumb, 0);
                }
                else
                {
                    thumb.Visibility = Visibility.Collapsed;
                    thumb.Opacity = 0;
                    thumb.IsHitTestVisible = false;
                }
            }

            foreach (Border border in layer.Children.OfType<Border>())
            {
                border.Background = null;
                border.BorderBrush = null;
                border.Visibility = Visibility.Collapsed;
            }

            foreach (UIElement child in layer.Children)
            {
                switch (child)
                {
                    case InteractiveComicTextElement interactive:
                        interactive.Opacity = 1;
                        Panel.SetZIndex(interactive, 10);
                        break;
                    case ComicTextElement precise:
                        precise.Visibility = Visibility.Collapsed;
                        precise.Opacity = 0;
                        break;
                    case Thumb:
                    case Border:
                        break;
                    default:
                        // Una región automática no necesita ninguna superficie visual adicional.
                        child.Visibility = Visibility.Collapsed;
                        child.Opacity = 0;
                        break;
                }
            }
        }
    }

    private ControlTemplate GetTransparentAutomaticHitTargetTemplate()
    {
        if (_transparentAutomaticHitTargetTemplate is not null)
        {
            return _transparentAutomaticHitTargetTemplate;
        }

        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        root.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        root.SetValue(Border.BorderThicknessProperty, new Thickness(0));

        _transparentAutomaticHitTargetTemplate = new ControlTemplate(typeof(Thumb))
        {
            VisualTree = root
        };
        return _transparentAutomaticHitTargetTemplate;
    }
}
