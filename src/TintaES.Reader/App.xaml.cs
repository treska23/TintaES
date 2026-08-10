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

        // El Reader independiente instala antes de mostrarse las dos funciones que definen
        // su experiencia actual: biblioteca local .tinta y traducción por hover con ratón.
        // Así ninguna de ellas depende del orden de eventos Loaded.
        reader.EnsureStandaloneLibraryInstalled();
        reader.EnsureReaderHoverInstalled();

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
