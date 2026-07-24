using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private static readonly bool EditorMenuCommandsRegistered = RegisterEditorMenuCommands();
    private bool _editorMenuCommandsInstalled;

    private static bool RegisterEditorMenuCommands()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_EditorMenuCommandsLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_EditorMenuCommandsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.LayoutUpdated -= window.MainWindow_TryInstallEditorMenuCommands;
        window.LayoutUpdated += window.MainWindow_TryInstallEditorMenuCommands;
        window.Dispatcher.BeginInvoke(window.TryInstallEditorMenuCommands, DispatcherPriority.ContextIdle);
    }

    private void MainWindow_TryInstallEditorMenuCommands(object? sender, EventArgs e) =>
        TryInstallEditorMenuCommands();

    private void TryInstallEditorMenuCommands()
    {
        if (_editorMenuCommandsInstalled || _classicMenu is null)
        {
            return;
        }

        MenuItem? editMenu = _classicMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => item.Header?.ToString()?.Contains("Edición", StringComparison.OrdinalIgnoreCase) == true);
        if (editMenu is null)
        {
            return;
        }

        _editorMenuCommandsInstalled = true;
        LayoutUpdated -= MainWindow_TryInstallEditorMenuCommands;
        editMenu.Items.Clear();
        editMenu.Items.Add(CreateMenuItem("_Deshacer", "Ctrl+Z", (_, _) => UndoEditorChange()));
        editMenu.Items.Add(CreateMenuItem("_Rehacer", "Ctrl+Y", (_, _) => RedoEditorChange()));
        editMenu.Items.Add(new Separator());
        editMenu.Items.Add(CreateMenuItem("_Dibujar zona", null, DrawRegionButton_Click));
        editMenu.Items.Add(CreateMenuItem("_Eliminar zona seleccionada", "Supr", DeleteSelectedRegionCompletely_Click));
        editMenu.Items.Add(new Separator());
        editMenu.Items.Add(CreateMenuItem("Analizar y _traducir", null, AnalyzeComicButton_Click));
        editMenu.SubmenuOpened += (_, _) => RefreshEditorToolAvailability();
    }
}
