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

        // La biblioteca es una parte obligatoria del ejecutable Reader, no una extensión que se
        // instala después del Loaded. Se crea antes de mostrar la ventana para que siempre esté
        // visible y empiece a localizar proyectos .tinta desde el primer arranque.
        reader.EnsureStandaloneLibraryInstalled();

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
