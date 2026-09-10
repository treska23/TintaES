using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TintaES.Core;
using TintaES.Wpf;

internal static class TranslateGemmaTurboRegression
{
    internal static async Task<int> RunAsync()
    {
        foreach (string model in new[] { "translategemma:12b", "translategemma:4b" })
        {
            await VerifySmallSubsetRetainsPageContextAsync(model);
        }
        await VerifyCancellationDoesNotTouchValidTargetsAsync();
        Console.WriteLine("TRADUCCION_WPF_SUBCONJUNTO_LOCAL=OK");
        return 0;
    }

    private static async Task VerifySmallSubsetRetainsPageContextAsync(string model)
    {
        ComicRegion[] page = CreatePage();
        // Include one already valid target as well as the three pending regions. Its
        // alternative wording passes the normal validator but a repeated semantic guard
        // would replace it. Recovery must preserve it exactly, like every valid neighbour.
        page[19].Original = "HOW CAN THIS BE?";
        page[19].Translation = "¿Cómo puede pasar esto?";
        int[] pendingPositions = [20, 21, 22];
        foreach (int position in pendingPositions)
        {
            page[position].Translation = string.Empty;
        }
        string[] originalTranslations = page.Select(region => region.Translation).ToArray();
        Guid[] originalIds = page.Select(region => region.Id).ToArray();
        string[] originalSources = page.Select(region => region.Original).ToArray();

        using var handler = new LocalRecoveryHandler();
        using var http = new HttpClient(handler);
        using var ollama = new OllamaClient(httpClient: http);
        MainWindow window = CreateOrchestrationOnlyWindow(ollama);
        await InvokeTranslationAsync(window, page.Skip(19).Take(4).ToArray(), page, model,
            CancellationToken.None);

        Require(handler.Prompts.Count == 1, "Tres pendientes contiguos deben recuperarse juntos.");
        string prompt = handler.Prompts[0];
        Require(prompt.Contains("LOCAL SCENE CONTEXT:", StringComparison.Ordinal)
                && prompt.Contains("The message number 18 is ready.", StringComparison.Ordinal)
                && prompt.Contains("The message number 24 is ready.", StringComparison.Ordinal)
                && prompt.Contains("ESPAÑOL_VECINO: ¿Cómo puede pasar esto?", StringComparison.Ordinal),
            "El camino WPF para menos de 24 objetivos debe conservar los vecinos de la página completa.");
        Require(!prompt.Contains("The message number 0 is ready.", StringComparison.Ordinal)
                && !prompt.Contains("The message number 50 is ready.", StringComparison.Ordinal),
            "El reintento no debe volver a enviar las 51 zonas.");
        Require(handler.TargetCounts.SequenceEqual(new[] { 3 }),
            "La traducción ya válida incluida en targets debe quedar fuera de los objetivos del modelo.");

        for (int index = 0; index < page.Length; index++)
        {
            Require(page[index].Id == originalIds[index] && page[index].Original == originalSources[index],
                "El camino WPF no debe cambiar identidades ni lecturas OCR.");
            if (pendingPositions.Contains(index))
            {
                Require(page[index].Translation == $"El mensaje número {index} está listo.",
                    "Cada resultado recuperado debe conservar la asociación con su bocadillo.");
            }
            else
            {
                Require(page[index].Translation == originalTranslations[index],
                    $"La recuperación modificó la traducción válida de la zona {index}.");
            }
        }

        int callsBefore = handler.Prompts.Count;
        await InvokeTranslationAsync(window, page.Skip(19).Take(4).ToArray(), page, model,
            CancellationToken.None);
        Require(handler.Prompts.Count == callsBefore,
            "Reintentar un subconjunto ya válido no debe hacer peticiones nuevas.");
        Require(page[19].Translation == originalTranslations[19],
            "Un subconjunto ya válido debe conservar también su redacción original.");
    }

    private static async Task VerifyCancellationDoesNotTouchValidTargetsAsync()
    {
        ComicRegion[] page = CreatePage();
        string[] translations = page.Select(region => region.Translation).ToArray();
        using var handler = new LocalRecoveryHandler();
        using var http = new HttpClient(handler);
        using var ollama = new OllamaClient(httpClient: http);
        MainWindow window = CreateOrchestrationOnlyWindow(ollama);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await InvokeTranslationAsync(window, page.Skip(20).Take(3).ToArray(), page,
                "translategemma:12b", cancellation.Token);
            throw new InvalidOperationException("La cancelación WPF no se propagó.");
        }
        catch (OperationCanceledException)
        {
            Require(handler.Prompts.Count == 0 && page.Select(region => region.Translation).SequenceEqual(translations),
                "Cancelar antes de recuperar no debe generar peticiones ni alterar traducciones.");
        }
    }

    private static ComicRegion[] CreatePage() => Enumerable.Range(0, 51).Select(index => new ComicRegion
    {
        Order = index + 1,
        Original = $"The message number {index} is ready.",
        Translation = $"El mensaje número {index} está listo.",
        TextBox = new NormalizedRect(100, index * 15, 120, 12)
    }).ToArray();

    private static MainWindow CreateOrchestrationOnlyWindow(OllamaClient ollama)
    {
        // The subset path only needs _ollama. Do not construct or show a WPF window:
        // Loaded/bootstrap handlers would start OCR or inspect the user's local models.
        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        FieldInfo field = typeof(MainWindow).GetField("_ollama", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(MainWindow).FullName, "_ollama");
        field.SetValue(window, ollama);
        return window;
    }

    private static async Task InvokeTranslationAsync(
        MainWindow window,
        IReadOnlyList<ComicRegion> targets,
        IReadOnlyList<ComicRegion> fullContext,
        string model,
        CancellationToken cancellationToken)
    {
        MethodInfo method = typeof(MainWindow).GetMethod("TranslatePageRegionsFastAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "TranslatePageRegionsFastAsync");
        try
        {
            await (Task)(method.Invoke(window, [targets, fullContext, model, cancellationToken, null])
                ?? throw new InvalidOperationException("El flujo de traducción WPF no devolvió una tarea."));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Regresión de recuperación WPF: " + message);
        }
    }

    private sealed class LocalRecoveryHandler : HttpMessageHandler
    {
        internal List<string> Prompts { get; } = [];
        internal List<int> TargetCounts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/chat",
                "La prueba aislada solo permite peticiones chat al servidor simulado.");
            using JsonDocument document = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            string prompt = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Prompts.Add(prompt);
            MatchCollection matches = Regex.Matches(prompt,
                @"\[\[(R[A-F0-9]+)\]\]\s*The message number (\d+) is ready\.\s*\[\[/\1\]\]",
                RegexOptions.CultureInvariant);
            TargetCounts.Add(matches.Count);
            var translations = matches.ToDictionary(match => match.Groups[1].Value,
                match => $"El mensaje número {match.Groups[2].Value} está listo.");
            string content = JsonSerializer.Serialize(new { translations });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { message = new { content } }),
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
