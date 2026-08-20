using System.IO;
using System.Windows;
using System.Windows.Threading;
using TintaES.Wpf;

namespace TintaES.Reader;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var reader = new ComicReaderWindow();

        // El Reader independiente conserva la biblioteca como herramienta opcional, mientras que
        // la lectura replica la experiencia del programa madre y añade navegación táctil inmersiva.
        reader.EnsureStandaloneLibraryInstalled();
        reader.EnsureReaderHoverInstalled();
        reader.EnsureStandaloneResponsiveLayoutInstalled();
        reader.EnsureStandaloneImmersiveNavigationInstalled();
        reader.EnsureDirectTouchNavigationInstalled();
        reader.EnsureStandaloneDirectTranslationInputInstalled();
        // La capa física de regiones se instala la última. Es la autoridad final para hover/toque
        // porque usa el hit-test real de WPF sobre elementos que se escalan junto con la página.
        reader.EnsureStandaloneTranslationHitLayerInstalled();

        MainWindow = reader;
        reader.Show();

        string? startupPath = e.Args
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
        if (startupPath is null)
        {
            return;
        }

        reader.Dispatcher.BeginInvoke(
            async () => await reader.OpenReaderPathAsync(startupPath),
            DispatcherPriority.Loaded);
    }
}
