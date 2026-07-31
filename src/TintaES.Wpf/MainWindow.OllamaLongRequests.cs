using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Ollama puede tardar más de 75 segundos al traducir una página completa. El cliente base
/// conserva la cancelación explícita del usuario, pero deja de abortar la petición por tiempo.
/// </summary>
public partial class MainWindow
{
    private static readonly bool OllamaLongRequestsRegistered = RegisterOllamaLongRequests();
    private bool _ollamaLongRequestsInstalled;

    private static bool RegisterOllamaLongRequests()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_OllamaLongRequestsLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_OllamaLongRequestsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallOllamaLongRequests,
                DispatcherPriority.Loaded);
        }
    }

    private void InstallOllamaLongRequests()
    {
        if (_ollamaLongRequestsInstalled)
        {
            return;
        }

        FieldInfo? field = typeof(OllamaClient).GetField(
            "_httpClient",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(_ollama) is not HttpClient client)
        {
            throw new InvalidOperationException(
                "No se pudo configurar el cliente local de Ollama para tareas largas.");
        }

        client.Timeout = Timeout.InfiniteTimeSpan;
        _ollamaLongRequestsInstalled = true;
    }
}
