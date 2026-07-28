using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Sustituye en la ventana el render tipográfico preciso por una vista interactiva ligera.
/// ComicTextElement se conserva oculto porque la exportación lo sigue utilizando fuera del
/// dispatcher principal para obtener el resultado editorial completo.
/// </summary>
public partial class MainWindow
{
    private static readonly bool NonBlockingCanvasTextRegistered = RegisterNonBlockingCanvasText();

    private static bool RegisterNonBlockingCanvasText()
    {
        EventManager.RegisterClassHandler(
            typeof(ComicTextElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ComicTextElement_NonBlockingLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void ComicTextElement_NonBlockingLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComicTextElement accurate
            || Window.GetWindow(accurate) is not MainWindow window
            || accurate.Parent is not Grid layer
            || layer.Tag is not ComicRegion region)
        {
            return;
        }

        // Evita que el manejador Loaded del renderizador preciso vuelva a activarlo y dispare
        // su búsqueda tipográfica exhaustiva dentro del dispatcher de la ventana.
        e.Handled = true;
        window.InstallInteractiveCanvasText(layer, accurate, region);
    }

    private void InstallInteractiveCanvasText(
        Grid layer,
        ComicTextElement accurate,
        ComicRegion region)
    {
        accurate.Visibility = Visibility.Collapsed;
        accurate.IsHitTestVisible = false;

        if (!Equals(accurate.Tag, "tinta-precise-export-only"))
        {
            accurate.Tag = "tinta-precise-export-only";
            accurate.IsVisibleChanged += (_, _) =>
            {
                if (Window.GetWindow(accurate) is MainWindow
                    && accurate.Visibility != Visibility.Collapsed)
                {
                    accurate.Visibility = Visibility.Collapsed;
                }
            };
        }

        InteractiveComicTextElement? interactive = layer.Children
            .OfType<InteractiveComicTextElement>()
            .FirstOrDefault();
        if (interactive is null)
        {
            interactive = new InteractiveComicTextElement
            {
                Region = region,
                PageWidth = accurate.PageWidth,
                PageHeight = accurate.PageHeight,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(interactive, -5);
            layer.Children.Insert(0, interactive);
        }

        interactive.Visibility = region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        interactive.InvalidateVisual();

        // Permite que WPF pinte la caja ligera antes de continuar con la siguiente región.
        _ = Dispatcher.BeginInvoke(
            interactive.InvalidateVisual,
            DispatcherPriority.Background);
    }
}