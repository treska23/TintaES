using System.Net.Http;
using System.Reflection;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Configura Ollama para operaciones largas antes de que la ventana envíe su primera petición.
/// La cancelación sigue dependiendo exclusivamente del usuario y del token de la operación.
/// </summary>
public partial class MainWindow
{
    private bool _ollamaLongRequestsInstalled;

    protected override void OnInitialized(EventArgs e)
    {
        // Debe ejecutarse antes de base.OnInitialized: otros manejadores de inicialización o
        // Loaded pueden consultar los modelos, y HttpClient.Timeout queda bloqueado después de
        // la primera petición.
        InstallOllamaLongRequests();
        base.OnInitialized(e);
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
            // Respaldo para una posible petición extremadamente temprana: se sustituye el
            // HttpClient ya usado por otro configurado antes de su primera solicitud.
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
