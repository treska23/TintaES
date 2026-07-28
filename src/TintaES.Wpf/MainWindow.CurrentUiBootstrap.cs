using System.Windows;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Instala de forma explícita la interfaz actual. La ventana XAML conserva la composición base y
/// muchas funciones se añaden desde archivos parciales; este punto único evita que la aplicación
/// pueda arrancar con la carcasa antigua si cambia el orden de los eventos de WPF.
/// </summary>
public partial class MainWindow
{
    private const string CurrentUiBuildStamp = "UI 2026.07.28-r8";
    private const int CurrentUiBootstrapMaxAttempts = 3;

    private static readonly bool CurrentUiBootstrapRegistered = RegisterCurrentUiBootstrap();

    private bool _currentUiBootstrapInstalled;
    private bool _currentUiBootstrapQueued;
    private int _currentUiBootstrapAttempt;

    private static bool RegisterCurrentUiBootstrap()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_CurrentUiBootstrapClassLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_CurrentUiBootstrapClassLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.QueueCurrentUiBootstrap(DispatcherPriority.Loaded);
        }
    }

    private void QueueCurrentUiBootstrap(DispatcherPriority priority)
    {
        if (_currentUiBootstrapInstalled || _currentUiBootstrapQueued)
        {
            return;
        }

        _currentUiBootstrapQueued = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _currentUiBootstrapQueued = false;
                InstallCurrentUiBootstrap();
            },
            priority);
    }

    private void InstallCurrentUiBootstrap()
    {
        if (_currentUiBootstrapInstalled)
        {
            return;
        }

        _currentUiBootstrapAttempt++;
        Title = $"Tinta ES · Traductor local de cómics · {CurrentUiBuildStamp}";

        var failures = new List<string>();

        // Primero se crean los comandos y controles externos. Después se construyen los menús y,
        // por último, las herramientas que dependen de todos ellos.
        RunCurrentUiInstaller(InstallComicBookHandlers, "sesión de cómic", failures);
        RunCurrentUiInstaller(InstallProjectCommands, "comandos de proyecto", failures);
        RunCurrentUiInstaller(InstallPsdExportCommand, "exportación PSD", failures);
        RunCurrentUiInstaller(InstallComicReaderCommand, "lector de cómic", failures);
        RunCurrentUiInstaller(InstallPageSelectionPanel, "selector de páginas", failures);
        RunCurrentUiInstaller(InstallEditorTools, "herramientas de edición", failures);
        RunCurrentUiInstaller(InstallManualMaskEditing, "edición de máscara", failures);
        RunCurrentUiInstaller(InstallSimpleWhiteMaskPainting, "pincel blanco simple", failures);
        RunCurrentUiInstaller(InstallPageSaveAndShortcuts, "guardado de página", failures);
        RunCurrentUiInstaller(InstallClassicMenu, "menú superior", failures);
        RunCurrentUiInstaller(InstallAddImagePagesCommand, "agregar páginas", failures);
        RunCurrentUiInstaller(InstallPageSelectionDefaults, "selección inicial de páginas", failures);
        RunCurrentUiInstaller(InstallResponsiveInspector, "inspector rápido", failures);
        RunCurrentUiInstaller(TryInstallEditorMenuCommands, "menú de edición", failures);
        RunCurrentUiInstaller(InstallContextualBrushOptions, "opciones del pincel", failures);
        RunCurrentUiInstaller(TryInstallFloatingEditorPalette, "paleta flotante", failures);
        RunCurrentUiInstaller(InstallIconOnlyFloatingPalette, "iconos de la paleta", failures);
        RunCurrentUiInstaller(InstallResponsiveTopBars, "barras superiores adaptables", failures);
        RunCurrentUiInstaller(UpdateClassicMenuAvailability, "estado del menú", failures);

        if (failures.Count == 0)
        {
            _currentUiBootstrapInstalled = true;
            SetFooterStatus($"Interfaz actual cargada · {CurrentUiBuildStamp}", "#58A77D");
            return;
        }

        if (_currentUiBootstrapAttempt < CurrentUiBootstrapMaxAttempts)
        {
            SetFooterStatus(
                $"Terminando de cargar la interfaz actual ({_currentUiBootstrapAttempt}/{CurrentUiBootstrapMaxAttempts})…",
                "#C99A35");
            QueueCurrentUiBootstrap(DispatcherPriority.ContextIdle);
            return;
        }

        // Se detiene tras tres intentos para no crear un bucle de Dispatcher. El sello del título
        // permite distinguir inmediatamente esta versión de cualquier ejecutable antiguo.
        _currentUiBootstrapInstalled = true;
        SetFooterStatus(
            $"La interfaz actual no pudo cargar: {string.Join(", ", failures)}",
            "#EE594B");
    }

    private static void RunCurrentUiInstaller(Action installer, string name, ICollection<string> failures)
    {
        try
        {
            installer();
        }
        catch (Exception exception)
        {
            failures.Add($"{name} ({exception.GetType().Name})");
        }
    }
}
