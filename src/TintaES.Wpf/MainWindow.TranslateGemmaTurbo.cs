using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Acelera únicamente la orquestación de TranslateGemma 12B en páginas densas.
/// Reutiliza los métodos de traducción, validación, reparación y pulido ya probados de
/// OllamaClient; no cambia el modelo, el prompt de traducción ni los criterios de calidad.
///
/// El cuello anterior estaba en GetTranslateGemmaChunkSize: al superar 6000 caracteres de
/// contexto se traducían solo seis zonas por petición aunque cada petición volvía a incluir
/// TODA la página. Una página de 51 zonas podía repetir el mismo contexto nueve veces antes
/// de los reintentos. Aquí se agrupan más objetivos por inferencia manteniendo el contexto
/// completo, de modo que una página densa normal cae a dos o tres inferencias iniciales.
/// </summary>
public partial class MainWindow
{
    private static readonly MethodInfo TranslateGemmaChunkMethod = RequireOllamaMethod("TranslateGemmaChunkAsync");
    private static readonly MethodInfo IsAcceptableTranslationMethod = RequireOllamaMethod("IsAcceptableTranslation");
    private static readonly MethodInfo ApplyKnownSfxLocalizationsMethod = RequireOllamaMethod("ApplyKnownSfxLocalizations");
    private static readonly MethodInfo ApplySemanticGuardsMethod = RequireOllamaMethod("ApplySemanticGuards");
    private static readonly MethodInfo NormalizeSignTranslationsMethod = RequireOllamaMethod("NormalizeSignTranslations");
    private static readonly MethodInfo RepairSplitFragmentSequencesMethod = RequireOllamaMethod("RepairSplitFragmentSequencesAsync");
    private static readonly MethodInfo RefineTranslateGemmaBatchMethod = RequireOllamaMethod("RefineTranslateGemmaBatchAsync");

    private async Task TranslatePageRegionsFastAsync(
        IReadOnlyList<ComicRegion> targets,
        IReadOnlyList<ComicRegion> fullContext,
        string model,
        CancellationToken cancellationToken,
        IProgress<AnalysisProgress>? progress)
    {
        if (!model.StartsWith("translategemma:12b", StringComparison.OrdinalIgnoreCase)
            || targets.Count <= 24)
        {
            await _ollama.TranslateRegionsAsync(targets, model, cancellationToken, progress);
            return;
        }

        // Aunque el camino de preparación normal ya intenta hacerlo, aquí se garantiza otra
        // vez que el VLM de Paddle y su llama-server no compitan con TranslateGemma por VRAM.
        await PaddleOcrResidentControl.ReleaseAsync();
        cancellationToken.ThrowIfCancellationRequested();

        foreach (ComicRegion region in targets)
        {
            region.Translation = string.Empty;
        }
        InvokeStatic(ApplyKnownSfxLocalizationsMethod, targets);

        ComicRegion[] translatable = targets
            .Where(region => !IsAcceptableTranslation(region))
            .ToArray();
        if (translatable.Length == 0)
        {
            return;
        }

        int contextCharacters = EstimateTranslationContextCharacters(fullContext);
        int chunkSize = ChooseDenseTranslateGemmaChunkSize(contextCharacters);
        int initialCalls = (int)Math.Ceiling(translatable.Length / (double)chunkSize);
        int callNumber = 0;
        var total = Stopwatch.StartNew();
        var callDurations = new List<double>();

        for (int start = 0; start < translatable.Length; start += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComicRegion[] chunk = translatable.Skip(start).Take(chunkSize).ToArray();
            callNumber++;
            progress?.Report(new AnalysisProgress(
                960,
                1000,
                $"Traduciendo la escena con contexto · bloque {callNumber}/{initialCalls} · {chunk.Length} textos…"));

            var call = Stopwatch.StartNew();
            await InvokeOllamaTaskAsync(
                TranslateGemmaChunkMethod,
                chunk,
                fullContext,
                model,
                cancellationToken);
            call.Stop();
            callDurations.Add(call.Elapsed.TotalMilliseconds);
            ReportDenseTranslationProgress(
                progress,
                targets,
                $"Contexto traducido · bloque {callNumber}/{initialCalls} · {call.Elapsed.TotalSeconds:0.#} s");
        }

        // Un único segundo pase por lotes para lo realmente dudoso. Se conserva exactamente
        // el mismo traductor y validador; solo evitamos repetir de nuevo toda la página en
        // minilotes de seis.
        ComicRegion[] unresolved = translatable
            .Where(region => !IsAcceptableTranslation(region))
            .ToArray();
        if (unresolved.Length > 0)
        {
            int retrySize = Math.Max(chunkSize, Math.Min(32, unresolved.Length));
            int retryCalls = (int)Math.Ceiling(unresolved.Length / (double)retrySize);
            int retryNumber = 0;
            for (int start = 0; start < unresolved.Length; start += retrySize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ComicRegion[] retry = unresolved.Skip(start).Take(retrySize).ToArray();
                foreach (ComicRegion region in retry)
                {
                    region.Translation = string.Empty;
                }

                retryNumber++;
                var call = Stopwatch.StartNew();
                await InvokeOllamaTaskAsync(
                    TranslateGemmaChunkMethod,
                    retry,
                    fullContext,
                    model,
                    cancellationToken);
                call.Stop();
                callDurations.Add(call.Elapsed.TotalMilliseconds);
                ReportDenseTranslationProgress(
                    progress,
                    targets,
                    $"Repitiendo líneas dudosas · bloque {retryNumber}/{retryCalls} · {call.Elapsed.TotalSeconds:0.#} s");
            }
        }

        // La recuperación individual se mantiene únicamente para las zonas que sigan sin
        // superar el mismo filtro de calidad después de los dos pases agrupados.
        ComicRegion[] individuallyUnresolved = translatable
            .Where(region => !IsAcceptableTranslation(region))
            .ToArray();
        foreach (ComicRegion region in individuallyUnresolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            region.Translation = string.Empty;
            var call = Stopwatch.StartNew();
            await InvokeOllamaTaskAsync(
                TranslateGemmaChunkMethod,
                new[] { region },
                fullContext,
                model,
                cancellationToken);
            call.Stop();
            callDurations.Add(call.Elapsed.TotalMilliseconds);
            ReportDenseTranslationProgress(
                progress,
                targets,
                $"Recuperando un bocadillo aislado · {call.Elapsed.TotalSeconds:0.#} s");
        }

        // Solo la primera traducción completa de la página necesita estas fases. Si el llamador
        // llega aquí con un subconjunto pendiente, repetir el pulido de textos ya válidos sería
        // trabajo duplicado.
        if (targets.Count == fullContext.Count)
        {
            await InvokeOllamaTaskAsync(
                RepairSplitFragmentSequencesMethod,
                fullContext,
                model,
                cancellationToken,
                progress);
            await InvokeOllamaTaskAsync(
                RefineTranslateGemmaBatchMethod,
                fullContext,
                model,
                cancellationToken,
                progress);
        }

        InvokeStatic(ApplyKnownSfxLocalizationsMethod, targets);
        InvokeStatic(ApplySemanticGuardsMethod, targets);
        InvokeStatic(NormalizeSignTranslationsMethod, targets);

        ComicRegion[] finalUnresolved = targets
            .Where(region => !IsAcceptableTranslation(region))
            .ToArray();
        total.Stop();
        AppendTranslateGemmaTiming(
            model,
            targets.Count,
            fullContext.Count,
            contextCharacters,
            chunkSize,
            callDurations,
            total.Elapsed.TotalMilliseconds,
            finalUnresolved.Length);

        if (finalUnresolved.Length > 0)
        {
            foreach (ComicRegion region in finalUnresolved)
            {
                region.Translation = string.Empty;
            }

            string examples = string.Join(
                "; ",
                finalUnresolved.Take(3).Select(region => $"«{CompactSource(region.Original)}»"));
            throw new IncompleteTranslationException(
                $"Ollama no devolvió una traducción española válida para {finalUnresolved.Length} de " +
                $"{targets.Count} zonas ({examples}).");
        }

        ReportDenseTranslationProgress(
            progress,
            targets,
            $"Traducción contextual terminada · {total.Elapsed.TotalSeconds:0.#} s");
    }

    private static int ChooseDenseTranslateGemmaChunkSize(int contextCharacters)
    {
        // TranslateGemmaChunkAsync mantiene num_ctx=4096. Estos tamaños dejan margen para la
        // salida JSON sin volver a la penalización extrema de seis objetivos por petición.
        if (contextCharacters <= 6_500)
        {
            return 30;
        }
        if (contextCharacters <= 9_000)
        {
            return 24;
        }
        if (contextCharacters <= 12_500)
        {
            return 18;
        }
        if (contextCharacters <= 17_000)
        {
            return 12;
        }
        return 8;
    }

    private static int EstimateTranslationContextCharacters(IReadOnlyList<ComicRegion> regions) =>
        regions.Sum(region =>
            CompactSource(region.Original).Length
            + region.OcrAlternatives.Take(3).Sum(value => CompactSource(value).Length)
            + 32);

    private static string CompactSource(string? value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void ReportDenseTranslationProgress(
        IProgress<AnalysisProgress>? progress,
        IReadOnlyList<ComicRegion> regions,
        string message)
    {
        if (progress is null)
        {
            return;
        }

        int completed = regions.Count(region => region.HasRenderableTranslation);
        double fraction = completed / (double)Math.Max(1, regions.Count);
        progress.Report(new AnalysisProgress(
            (int)Math.Round(960 + fraction * 40),
            1000,
            $"{message} · {completed}/{regions.Count}"));
    }

    private bool IsAcceptableTranslation(ComicRegion region)
    {
        try
        {
            return IsAcceptableTranslationMethod.Invoke(
                       null,
                       new object?[] { region, region.Translation }) is true;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private async Task InvokeOllamaTaskAsync(MethodInfo method, params object?[] arguments)
    {
        object? result;
        try
        {
            result = method.Invoke(_ollama, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }

        if (result is not Task task)
        {
            throw new InvalidOperationException($"{method.Name} no devolvió una tarea.");
        }

        try
        {
            await task;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static void InvokeStatic(MethodInfo method, object argument)
    {
        try
        {
            method.Invoke(null, new[] { argument });
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static MethodInfo RequireOllamaMethod(string name) =>
        typeof(OllamaClient).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        ?? throw new MissingMethodException(typeof(OllamaClient).FullName, name);

    private static void AppendTranslateGemmaTiming(
        string model,
        int targetCount,
        int contextCount,
        int contextCharacters,
        int chunkSize,
        IReadOnlyList<double> calls,
        double totalMs,
        int unresolved)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "tintaes-translategemma-timing.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
            {
                File.Delete(path);
            }

            string line = System.Text.Json.JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow,
                model,
                target_count = targetCount,
                context_count = contextCount,
                context_characters = contextCharacters,
                chunk_size = chunkSize,
                inference_calls = calls.Count,
                call_ms = calls.Select(value => Math.Round(value, 1)).ToArray(),
                total_ms = Math.Round(totalMs, 1),
                unresolved
            });
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // La telemetría nunca debe interferir con una traducción válida.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
