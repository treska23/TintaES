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

        // r12: el Reader ya no mantiene un visor paralelo. Es la MainWindow real de TintaES con
        // herramientas de autoría retiradas por ReaderOnlyMode.
        var reader = new MainWindow(readerOnly: true);
        MainWindow = reader;
        reader.Show();

        string? startupPath = e.Args
            .Select(Path.GetFullPath)
            .FirstOrDefault(path => File.Exists(path)
                && string.Equals(Path.GetExtension(path), ".tinta", StringComparison.OrdinalIgnoreCase));
        if (startupPath is null)
        {
            return;
        }

        reader.Dispatcher.BeginInvoke(
            async () =>
            {
                try
                {
                    await reader.OpenReaderProjectAsync(startupPath);
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
