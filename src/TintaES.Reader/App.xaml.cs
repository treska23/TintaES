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

        // El Reader independiente conserva la biblioteca, pero la lectura replica la experiencia
        // del programa madre: hover con ratón y presión táctil muestran la traducción centrada.
        reader.EnsureStandaloneLibraryInstalled();
        reader.EnsureReaderHoverInstalled();
        reader.EnsureMotherTranslationInteractionInstalled();
        reader.EnsureStandaloneResponsiveLayoutInstalled();

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
