using System.Windows;
using System.Windows.Threading;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private static readonly bool UiRevisionR13Registered = RegisterUiRevisionR13();

    private static bool RegisterUiRevisionR13()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_UiRevisionR13Loaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_UiRevisionR13Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                () => window.Title = "Tinta ES · Traductor local de cómics · UI 2026.07.31-r25",
                DispatcherPriority.ApplicationIdle);
        }
    }
}
