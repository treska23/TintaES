using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TintaES.Core;

public sealed partial class OllamaClient
{
    /// <summary>
    /// Ruta de traducción estrictamente anclada al OCR. El contexto de página puede aclarar
    /// referencias, tono y acepciones, pero nunca aportar palabras ausentes del TARGET.
    /// </summary>
    public async Task TranslateGroundedRegionsAsync(
        IReadOnlyList<ComicRegion> targets,
        IReadOnlyList<ComicRegion> fullContext,
        string model,
        CancellationToken cancellationToken = default,
        IProgress<AnalysisProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Sanea también el contexto: una traducción antigua nacida de OCR basura no debe
        // reaparecer como ESPAÑOL_VECINO ni ayudar a inventar otra línea.
        foreach (ComicRegion region in fullContext.Concat(targets).Distinct())
        {
            if (!region.IsManual)
            {
                TranslationSourceGuard.NormalizeEvidence(region);
            }
        }

        ComicRegion[] groundedContext = fullContext
            .Where(IsGroundedSource)
            .Distinct()
            .ToArray();
        ComicRegion[] activeTargets = targets
            .Where(IsGroundedSource)
            .Distinct()
            .ToArray();

        if (activeTargets.Length == 0)
        {
            return;
        }

        bool recoveringSubset = targets.Count < fullContext.Count;
        if (!recoveringSubset)
        {
            foreach (ComicRegion region in activeTargets)
            {
                region.Translation = string.Empty;
            }
        }

        ApplyKnownSfxLocalizations(activeTargets);
        ComicRegion[] translatable = activeTargets
            .Where(region => !IsGroundedAcceptableTranslation(region, region.Translation))
            .ToArray();
        if (translatable.Length == 0)
        {
            return;
        }

        var total = Stopwatch.StartNew();
        int contextCharacters = groundedContext.Sum(region =>
            NormalizeSourceText(region.Original).Length
            + region.StoredOcrAlternatives.Take(3).Sum(value => NormalizeSourceText(value).Length)
            + 32);
        int chunkSize = ChooseGroundedChunkSize(contextCharacters);
        int initialCalls = (int)Math.Ceiling(translatable.Length / (double)chunkSize);

        if (!recoveringSubset)
        {
            int callNumber = 0;
            for (int start = 0; start < translatable.Length; start += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ComicRegion[] chunk = translatable.Skip(start).Take(chunkSize).ToArray();
                callNumber++;
                progress?.Report(new AnalysisProgress(
                    960,
                    1000,
                    $"Traduciendo OCR verificado · bloque {callNumber}/{initialCalls} · {chunk.Length} textos…"));

                await TranslateGroundedChunkAsync(
                    chunk,
                    groundedContext,
                    model,
                    cancellationToken,
                    "initial_grounded",
                    localContext: false);

                ReportGroundedProgress(
                    progress,
                    groundedContext,
                    $"OCR verificado traducido · bloque {callNumber}/{initialCalls}");
            }
        }

        ComicRegion[] unresolved = translatable
            .Where(region => !IsGroundedAcceptableTranslation(region, region.Translation))
            .ToArray();
        if (unresolved.Length > 0)
        {
            await RecoverGroundedRegionsAsync(
                unresolved,
                groundedContext,
                model,
                cancellationToken,
                progress);
        }

        ApplyKnownSfxLocalizations(activeTargets);
        ApplySemanticGuards(activeTargets);
        NormalizeSignTranslations(activeTargets);

        ComicRegion[] finalUnresolved = activeTargets
            .Where(region => !IsGroundedAcceptableTranslation(region, region.Translation))
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
                finalUnresolved.Take(3).Select(region => $"«{NormalizeSourceText(region.Original)}»"));
            throw new IncompleteTranslationException(
                $"TranslateGemma no pudo traducir con seguridad {finalUnresolved.Length} de " +
                $"{activeTargets.Length} zonas ({examples}). Se dejan pendientes en vez de inventar diálogo.");
        }

        ReportGroundedProgress(
            progress,
            groundedContext,
            $"Traducción anclada al OCR terminada · {total.Elapsed.TotalSeconds:0.#} s");
    }

    private async Task RecoverGroundedRegionsAsync(
        IReadOnlyList<ComicRegion> unresolved,
        IReadOnlyList<ComicRegion> fullContext,
        string model,
        CancellationToken cancellationToken,
        IProgress<AnalysisProgress>? progress)
    {
        IReadOnlyList<LocalTranslationGroup> groups = GroupLocalTranslationTargets(unresolved, fullContext);
        for (int index = 0; index < groups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalTranslationGroup group = groups[index];
            ComicRegion[] pending = group.Targets
                .Where(region => !IsGroundedAcceptableTranslation(region, region.Translation))
                .ToArray();
            if (pending.Length == 0)
            {
                continue;
            }

            foreach (ComicRegion region in pending)
            {
                region.Translation = string.Empty;
            }

            progress?.Report(new AnalysisProgress(
                980,
                1000,
                $"Reintentando {pending.Length} texto(s) con su OCR y vecinos · bloque {index + 1}/{groups.Count}"));
            await TranslateGroundedChunkAsync(
                pending,
                group.Context,
                model,
                cancellationToken,
                "recovery_grounded",
                localContext: true);
        }

        // Un único rescate individual. Sigue viendo solo su propio OCR y, como máximo, un vecino
        // fiable a cada lado. No existe un segundo pase creativo de reconstrucción.
        foreach (ComicRegion region in groups.SelectMany(group => group.Targets).Distinct().Where(region =>
                     !IsGroundedAcceptableTranslation(region, region.Translation)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            region.Translation = string.Empty;
            ComicRegion[] context = SelectLocalTranslationContext([region], fullContext, radius: 1);
            progress?.Report(new AnalysisProgress(
                990,
                1000,
                "Último intento de una línea · OCR propio, sin completar por contexto"));
            await TranslateGroundedChunkAsync(
                [region],
                context,
                model,
                cancellationToken,
                "recovery_grounded_individual",
                localContext: true);
        }
    }

    private async Task TranslateGroundedChunkAsync(
        IReadOnlyList<ComicRegion> requestedTargets,
        IReadOnlyList<ComicRegion> requestedContext,
        string model,
        CancellationToken cancellationToken,
        string phase,
        bool localContext)
    {
        ComicRegion[] targets = requestedTargets
            .Where(IsGroundedSource)
            .Distinct()
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        ComicRegion[] contextRegions = requestedContext
            .Where(IsGroundedSource)
            .Distinct()
            .ToArray();
        string context = localContext
            ? FormatLocalTranslationContext(targets, contextRegions)
            : string.Join(
                "\n",
                contextRegions.Select((region, index) =>
                    $"C{index:000} ({region.Type}): {FormatSourceForModel(region)}"));

        var tokens = targets.ToDictionary(region => region, CreateTranslationToken);
        string targetText = string.Join(
            "\n",
            targets.Select(region =>
                $"[[{tokens[region]}]] {NormalizeOcrForTranslation(region.Original)} [[/{tokens[region]}]]"));

        string prompt =
            """
            You are a professional English-to-Spanish comic translator. Translate into natural Spanish from Spain.

            GROUNDING RULES — THESE OVERRIDE EVERY OTHER INSTRUCTION:
            - Translate only lexical information explicitly supported by EACH TARGET'S own OCR text or its OCR ALTERNATIVES.
            - CONTEXT may clarify pronouns, register, who is speaking, or the sense of a word that is already present in the TARGET. It must NEVER supply missing words, a missing sentence, or a plausible reply.
            - Never infer dialogue from a character, an image, plot knowledge, documented comic context, or neighbouring balloons.
            - OCR ALTERNATIVES are different readings of the SAME printed lettering. You may choose the best supported reading and correct an unmistakable character-level OCR typo, but never reconstruct absent words from scene logic.
            - If a TARGET is not intelligible enough from its own OCR evidence to translate faithfully, return exactly [[UNREADABLE]] for that TARGET. Do not guess.
            - A short reaction must stay a short reaction. Do not expand fragments into something a character might plausibly say.

            Preserve meaning, speaker intent, jokes, register, names, actions, punctuation and negation. Keep the result concise enough for the original balloon. Consecutive TARGETS may form one printed sentence; you may distribute a translation across them only when the grammatical continuation is explicitly present in those TARGET strings themselves.

            Return one JSON object whose "translations" object contains every exact RANDOMTAG from TARGETS as a key and only its Spanish translation or [[UNREADABLE]] as the value. Never omit, rename or add keys. Output no explanations and no English source text. The JSON shape is enforced separately.

            OCR-VERIFIED PAGE CONTEXT:
            """ + "\n" + context + "\n\nTARGETS:\n" + targetText;

        object payload = new
        {
            model,
            stream = false,
            think = false,
            keep_alive = "1m",
            format = CreateTranslateGemmaFormat(tokens),
            messages = new[] { new { role = "user", content = prompt } },
            options = new
            {
                temperature = 0,
                seed = 73,
                num_ctx = 4096,
                num_predict = GetTranslateGemmaPredictionBudget(targets)
            }
        };

        string content = await SendChatAsync(payload, cancellationToken,
            new TranslateGemmaRequestMetrics(
                phase,
                model,
                targets.Length,
                contextRegions.Length,
                context.Length,
                prompt.Length));
        IReadOnlyDictionary<string, string> structured = ParseStructuredTranslations(content);

        foreach (ComicRegion region in targets)
        {
            string token = tokens[region];
            string candidate = structured.TryGetValue(token, out string? structuredValue)
                ? CleanTranslationCandidate(structuredValue)
                : string.Empty;

            // Compatibilidad únicamente para un objetivo individual y una respuesta etiquetada
            // antigua. Una respuesta libre sin asociación nunca se acepta en lotes.
            if (string.IsNullOrWhiteSpace(candidate))
            {
                Match match = Regex.Match(
                    content,
                    $@"\[\[{Regex.Escape(token)}\]\]\s*(.*?)\s*\[\[/{Regex.Escape(token)}\]\]",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant);
                candidate = match.Success
                    ? CleanTranslationCandidate(match.Groups[1].Value)
                    : string.Empty;
            }

            if (IsGroundedAcceptableTranslation(region, candidate))
            {
                region.Translation = candidate;
            }
            else
            {
                region.Translation = string.Empty;
            }
        }
    }

    private static bool IsGroundedSource(ComicRegion region) =>
        region.IsManual || TranslationSourceGuard.IsReliable(region);

    private static bool IsGroundedAcceptableTranslation(ComicRegion region, string? translation)
    {
        if (!IsGroundedSource(region)
            || string.IsNullOrWhiteSpace(translation)
            || string.Equals(
                translation.Trim(),
                "[[UNREADABLE]]",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsAcceptableTranslation(region, translation);
    }

    private static int ChooseGroundedChunkSize(int contextCharacters)
    {
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

    private static void ReportGroundedProgress(
        IProgress<AnalysisProgress>? progress,
        IReadOnlyList<ComicRegion> regions,
        string message)
    {
        if (progress is null)
        {
            return;
        }

        int completed = regions.Count(region =>
            IsGroundedAcceptableTranslation(region, region.Translation));
        double fraction = completed / (double)Math.Max(1, regions.Count);
        progress.Report(new AnalysisProgress(
            (int)Math.Round(960 + fraction * 40),
            1000,
            $"{message} · {completed}/{regions.Count}"));
    }
}
