using System.Windows;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Instala de forma explícita la interfaz actual. La rotulación nace desde AddRegionVisual y
/// aquí solo se inicializan funciones independientes de la aplicación.
/// </summary>
public partial class MainWindow
{
    internal const string CurrentUiBuildStamp = "UI 2026.08.18-r62-save-dirty-state";
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
        FontCategoryComboBox.SelectedValue = "comic";
        FontCategoryComboBox.IsEnabled = false;
        FontCategoryComboBox.ToolTip = "TintaES utiliza una única tipografía de cómic gruesa para todos los bocadillos";

        var failures = new List<string>();
        RunCurrentUiInstaller(InstallOllamaLongRequests, "peticiones largas de Ollama", failures);
        RunCurrentUiInstaller(InstallComicBookHandlers, "sesión de cómic", failures);
        RunCurrentUiInstaller(InstallComicArchiveOpening, "apertura CBZ/CBR/RAR", failures);
        RunCurrentUiInstaller(InstallProjectCommands, "comandos de proyecto", failures);
        RunCurrentUiInstaller(InstallAutoBackupRecovery, "copias automáticas de recuperación", failures);
        RunCurrentUiInstaller(InstallPsdExportCommand, "exportación PSD", failures);
        RunCurrentUiInstaller(InstallComicReaderCommand, "lector de cómic", failures);
        RunCurrentUiInstaller(InstallPageSelectionPanel, "selector de páginas", failures);
        RunCurrentUiInstaller(InstallStandaloneImageTabs, "pestañas de páginas sueltas", failures);
        RunCurrentUiInstaller(InstallDirectPageSelector, "selector unificado TintaES/CBZ/CBR/RAR", failures);
        RunCurrentUiInstaller(InstallComicResearch, "contexto web del cómic", failures);
        RunCurrentUiInstaller(InstallSelectedPageProcessing, "procesamiento fiable de páginas", failures);
        RunCurrentUiInstaller(InstallEditorTools, "herramientas de edición", failures);
        RunCurrentUiInstaller(InstallManualMaskEditing, "edición de máscara", failures);
        RunCurrentUiInstaller(InstallSimpleWhiteMaskPainting, "pincel blanco simple", failures);
        RunCurrentUiInstaller(InstallPageSaveAndShortcuts, "guardado de página", failures);
        RunCurrentUiInstaller(InstallClassicMenu, "menú superior", failures);
        RunCurrentUiInstaller(InstallSaveDirtyTracking, "estado de guardado", failures);
        RunCurrentUiInstaller(InstallAddImagePagesCommand, "agregar páginas", failures);
        RunCurrentUiInstaller(InstallResponsiveInspector, "inspector rápido", failures);
        RunCurrentUiInstaller(TryInstallEditorMenuCommands, "menú de edición", failures);
        RunCurrentUiInstaller(InstallContextualBrushOptions, "opciones del pincel", failures);
        RunCurrentUiInstaller(TryInstallFloatingEditorPalette, "paleta flotante", failures);
        RunCurrentUiInstaller(InstallIconOnlyFloatingPalette, "iconos de la paleta", failures);
        RunCurrentUiInstaller(InstallResponsiveTopBars, "barra superior única", failures);
        RunCurrentUiInstaller(InstallOrRefreshResizableSidePanels, "paneles laterales ajustables", failures);
        RunCurrentUiInstaller(InstallReaderFirstMode, "modo lector y traductor", failures);
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
