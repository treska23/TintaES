using System.Windows;
using System.Windows.Threading;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private static readonly bool CompactWorkspaceHeaderRegistered = RegisterCompactWorkspaceHeader();

    private static bool RegisterCompactWorkspaceHeader()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_CompactWorkspaceHeaderLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_CompactWorkspaceHeaderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.ApplyResponsiveTopBars,
                DispatcherPriority.ApplicationIdle);
        }
    }
}
