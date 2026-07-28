using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// El renderizador editorial preciso es demasiado costoso para ejecutarse dentro del dispatcher
/// de la ventana. En el lienzo interactivo se mantiene colapsado desde su creación y se sustituye
/// por InteractiveComicTextElement. La exportación no hereda este estilo y continúa usando
/// ComicTextElement en su hilo STA independiente.
/// </summary>
public partial class MainWindow
{
    private static readonly bool NonBlockingCanvasTextRegistered = RegisterNonBlockingCanvasText();
    private bool _nonBlockingCanvasTextInstalled;
    private bool _ensuringInteractiveCanvasText;

    private static bool RegisterNonBlockingCanvasText()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_NonBlockingCanvasTextLoaded),
            handledEventsToo: true);

        // Respaldo para elementos creados antes de que el estilo de la ventana haya quedado
        // instalado. El estilo es la protección principal; este evento solo repara casos tardíos.
        EventManager.RegisterClassHandler(
            typeof(ComicTextElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ComicTextElement_NonBlockingLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_NonBlockingCanvasTextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallNonBlockingCanvasText,
                DispatcherPriority.Loaded);
        }
    }

    private void InstallNonBlockingCanvasText()
    {
        if (_nonBlockingCanvasTextInstalled)
        {
            EnsureInteractiveCanvasTexts();
            return;
        }

        // El estilo está limitado a esta MainWindow. Los ComicTextElement creados por el servicio
        // de exportación viven en otro árbol visual y siguen siendo visibles y precisos.
        var hiddenAccurateRendererStyle = new Style(typeof(ComicTextElement));
        hiddenAccurateRendererStyle.Setters.Add(
            new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
        Resources[typeof(ComicTextElement)] = hiddenAccurateRendererStyle;

        _nonBlockingCanvasTextInstalled = true;
        OverlayCanvas.LayoutUpdated += (_, _) => EnsureInteractiveCanvasTexts();
        EnsureInteractiveCanvasTexts();
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

        e.Handled = true;
        window.InstallInteractiveCanvasText(layer, accurate, region);
    }

    private void EnsureInteractiveCanvasTexts()
    {
        if (_ensuringInteractiveCanvasText || OverlayCanvas is null)
        {
            return;
        }

        _ensuringInteractiveCanvasText = true;
        try
        {
            foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>().ToArray())
            {
                if (layer.Tag is not ComicRegion region)
                {
                    continue;
                }

                ComicTextElement? accurate = layer.Children
                    .OfType<ComicTextElement>()
                    .FirstOrDefault();
                if (accurate is not null)
                {
                    InstallInteractiveCanvasText(layer, accurate, region);
                }
            }
        }
        finally
        {
            _ensuringInteractiveCanvasText = false;
        }
    }

    private void InstallInteractiveCanvasText(
        Grid layer,
        ComicTextElement accurate,
        ComicRegion region)
    {
        // Se hace antes de crear o invalidar cualquier otro elemento: el OnRender preciso no debe
        // recibir ni un solo frame dentro de la ventana.
        accurate.Visibility = Visibility.Collapsed;
        accurate.Opacity = 0;
        accurate.IsEnabled = false;
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
            Panel.SetZIndex(interactive, 1);
            layer.Children.Insert(0, interactive);
        }

        bool nativeEditorVisible = layer.Children
            .OfType<TextBlock>()
            .Any(textBlock => Equals(textBlock.Tag, NativeTextBlockTag)
                && textBlock.Visibility == Visibility.Visible);
        interactive.Visibility = region.IsEnabled && !nativeEditorVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        interactive.InvalidateVisual();
    }
}