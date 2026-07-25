using System.Windows;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Instala de forma explícita la interfaz actual. La ventana XAML conserva la composición base y
/// muchas funciones se añaden desde archivos parciales; este punto único evita que la aplicación
/// pueda arrancar con la carcasa antigua si cambia el orden de los eventos Loaded/ApplicationIdle.
/// </summary>
public partial class MainWindow
{
    private const string CurrentUiBuildStamp = "UI 2026.07.25-r1";
    private bool _currentUiBootstrapInstalled;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded -= MainWindow_CurrentUiBootstrapLoaded;
        Loaded += MainWindow_CurrentUiBootstrapLoaded;
    }

    private void MainWindow_CurrentUiBootstrapLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_CurrentUiBootstrapLoaded;
        Dispatcher.BeginInvoke(InstallCurrentUiBootstrap, DispatcherPriority.Loaded);
    }

    private void InstallCurrentUiBootstrap()
    {
        if (_currentUiBootstrapInstalled)
        {
            return;
        }

        var failures = new List<string>();

        RunCurrentUiInstaller(InstallComicBookHandlers, "sesión de cómic", failures);
        RunCurrentUiInstaller(InstallClassicMenu, "menú superior", failures);
        RunCurrentUiInstaller(InstallProjectCommands, "comandos de proyecto", failures);
        RunCurrentUiInstaller(InstallPsdExportCommand, "exportación PSD", failures);
        RunCurrentUiInstaller(InstallComicReaderCommand, "lector de cómic", failures);
        RunCurrentUiInstaller(InstallPageSelectionPanel, "selector de páginas", failures);
        RunCurrentUiInstaller(InstallEditorTools, "herramientas de edición", failures);
        RunCurrentUiInstaller(InstallManualMaskEditing, "edición de máscara", failures);
        RunCurrentUiInstaller(InstallPageSaveAndShortcuts, "guardado de página", failures);
        RunCurrentUiInstaller(InstallAddImagePagesCommand, "agregar páginas", failures);
        RunCurrentUiInstaller(InstallPageSelectionDefaults, "selección inicial de páginas", failures);
        RunCurrentUiInstaller(InstallResponsiveInspector, "inspector rápido", failures);
        RunCurrentUiInstaller(TryInstallEditorMenuCommands, "menú de edición", failures);
        RunCurrentUiInstaller(TryInstallFloatingEditorPalette, "paleta flotante", failures);
        RunCurrentUiInstaller(InstallContextualBrushOptions, "opciones del pincel", failures);

        _currentUiBootstrapInstalled = true;
        Title = $"Tinta ES · Traductor local de cómics · {CurrentUiBuildStamp}";

        if (failures.Count == 0)
        {
            SetFooterStatus($"Interfaz actual cargada · {CurrentUiBuildStamp}", "#58A77D");
        }
        else
        {
            SetFooterStatus(
                $"La interfaz actual cargó con {failures.Count} módulo(s) pendiente(s): {string.Join(", ", failures)}",
                "#C99A35");
        }
    }

    private static void RunCurrentUiInstaller(Action installer, string name, ICollection<string> failures)
    {
        try
        {
            installer();
        }
        catch
        {
            failures.Add(name);
        }
    }
}
