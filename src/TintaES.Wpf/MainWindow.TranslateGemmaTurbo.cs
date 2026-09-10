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
        bool recoveringSubset = model.StartsWith("translategemma", StringComparison.OrdinalIgnoreCase)
                                && targets.Count < fullContext.Count;
        if (!recoveringSubset && (!model.StartsWith("translategemma:12b", StringComparison.OrdinalIgnoreCase)
            || targets.Count <= 24))
        {
            await _ollama.TranslateRegionsAsync(targets, model, cancellationToken, progress);
            return;
        }

        // Aunque el camino de preparación normal ya intenta hacerlo, aquí se garantiza otra
        // vez que el VLM de Paddle y su llama-server no compitan con TranslateGemma por VRAM.
        if (!recoveringSubset)
        {
            await PaddleOcrResidentControl.ReleaseAsync();
        }
        cancellationToken.ThrowIfCancellationRequested();

        ComicRegion[] activeTargets = recoveringSubset
            ? targets.Where(region => !IsAcceptableTranslation(region)).ToArray()
            : targets.ToArray();
        foreach (ComicRegion region in activeTargets)
        {
            if (!recoveringSubset)
            {
                region.Translation = string.Empty;
            }
        }
        InvokeStatic(ApplyKnownSfxLocalizationsMethod, activeTargets);

        ComicRegion[] translatable = activeTargets
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


        for (int start = 0; !recoveringSubset && start < translatable.Length; start += chunkSize)
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

            ReportDenseTranslationProgress(
                progress,
                targets,
                $"Contexto traducido · bloque {callNumber}/{initialCalls} · {call.Elapsed.TotalSeconds:0.#} s");
        }

        await _ollama.RecoverTranslateGemmaRegionsAsync(
            translatable, fullContext, model, cancellationToken, progress);

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

        InvokeStatic(ApplyKnownSfxLocalizationsMethod, activeTargets);
        InvokeStatic(ApplySemanticGuardsMethod, activeTargets);
        InvokeStatic(NormalizeSignTranslationsMethod, activeTargets);

        ComicRegion[] finalUnresolved = activeTargets
            .Where(region => !IsAcceptableTranslation(region))
            .ToArray();
        total.Stop();
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

}
