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

        // El Reader tiene una ventana propia de lectura. No reutiliza MainWindow ni carga
        // instaladores, paneles o herramientas del editor.
        var reader = new ComicReaderWindow();
        MainWindow = reader;
        reader.Show();

        string? startupPath = e.Args
            .Select(Path.GetFullPath)
            .FirstOrDefault(path => File.Exists(path)
                && (string.Equals(Path.GetExtension(path), ".tinta", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(path), ".cbz", StringComparison.OrdinalIgnoreCase)));
        if (startupPath is null)
        {
            return;
        }

        reader.Dispatcher.BeginInvoke(
            async () =>
            {
                try
                {
                    await reader.OpenReaderPathAsync(startupPath);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(
                        reader,
                        $"No se pudo abrir el proyecto.\n\n{exception.Message}",
                        "Tinta ES Reader",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            },
            DispatcherPriority.ContextIdle);
    }
}
