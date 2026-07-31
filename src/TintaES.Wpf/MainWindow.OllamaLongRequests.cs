using System.Net.Http;
using System.Reflection;
using System.Windows;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Configura Ollama para operaciones largas antes de que la ventana envíe su primera petición.
/// La cancelación sigue dependiendo exclusivamente del usuario y del token de la operación.
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
            // Los manejadores de clase se ejecutan antes que MainWindow_Loaded. Debe hacerse
            // aquí mismo, sin BeginInvoke: aplazarlo permitiría que RefreshModelsAsync enviara
            // la primera petición y bloqueara después cualquier cambio de HttpClient.Timeout.
            window.InstallOllamaLongRequests();
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

        try
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        }
        catch (InvalidOperationException)
        {
            // Respaldo por si en el futuro alguna inicialización llega a consultar Ollama antes
            // de Loaded. Se sustituye el cliente usado por otro sin límite temporal.
            var replacement = new HttpClient
            {
                BaseAddress = client.BaseAddress,
                Timeout = Timeout.InfiniteTimeSpan
            };
            foreach (var header in client.DefaultRequestHeaders)
            {
                replacement.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }

            field.SetValue(_ollama, replacement);
        }

        _ollamaLongRequestsInstalled = true;
    }
}
