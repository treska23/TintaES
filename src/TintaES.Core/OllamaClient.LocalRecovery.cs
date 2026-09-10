using System.Diagnostics;

namespace TintaES.Core;

public sealed partial class OllamaClient
{
    /// <summary>
    /// Recovers only invalid targets after the contextual pass. Neighbours supply evidence,
    /// never translation targets. Unreadable OCR is never sent to the generative recovery path.
    /// </summary>
    public async Task RecoverTranslateGemmaRegionsAsync(
        IReadOnlyList<ComicRegion> targets,
        IReadOnlyList<ComicRegion> fullContext,
        string model,
        CancellationToken cancellationToken = default,
        IProgress<AnalysisProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ComicRegion[] unresolved = targets
            .Where(region => TranslationSourceGuard.IsReliable(region)
                             && !IsAcceptableTranslation(region, region.Translation))
            .Distinct()
            .ToArray();
        if (unresolved.Length == 0)
        {
            return;
        }

        IReadOnlyList<LocalTranslationGroup> groups = GroupLocalTranslationTargets(unresolved, fullContext);
        var timer = Stopwatch.StartNew();
        for (int index = 0; index < groups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalTranslationGroup group = groups[index];
            ComicRegion[] pending = group.Targets
                .Where(region => TranslationSourceGuard.IsReliable(region)
                                 && !IsAcceptableTranslation(region, region.Translation))
                .ToArray();
            if (pending.Length == 0)
            {
                continue;
            }
            ReportTranslationProgress(progress, fullContext,
                $"Recuperando {pending.Length} líneas dudosas · contexto local · bloque {index + 1}/{groups.Count}");
            foreach (ComicRegion region in pending)
            {
                region.Translation = string.Empty;
            }
            await TranslateGemmaChunkWithContextAsync(
                pending, group.Context, model, cancellationToken, "recovery_local", localContext: true);
        }

        // Un servidor que omite las claves de un lote aún puede devolver una frase inequívoca
        // para un único objetivo. Se mantiene ese rescate con los vecinos inmediatos.
        foreach (ComicRegion region in groups.SelectMany(group => group.Targets).Where(region =>
                     TranslationSourceGuard.IsReliable(region)
                     && !IsAcceptableTranslation(region, region.Translation)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportTranslationProgress(progress, fullContext,
                "Recuperando 1 línea aislada · contexto local");
            region.Translation = string.Empty;
            ComicRegion[] context = SelectLocalTranslationContext([region], fullContext, radius: 1);
            await TranslateGemmaChunkWithContextAsync(
                [region], context, model, cancellationToken, "recovery_individual", localContext: true);
        }

        int recovered = unresolved.Count(region => IsAcceptableTranslation(region, region.Translation));
        ReportTranslationProgress(progress, fullContext,
            $"{recovered} líneas recuperadas · {timer.Elapsed.TotalSeconds:0.#} s");
    }

    private static IReadOnlyList<LocalTranslationGroup> GroupLocalTranslationTargets(
        IReadOnlyList<ComicRegion> targets,
        IReadOnlyList<ComicRegion> fullContext)
    {
        // The supplied page sequence is the same reading order used by the initial prompt.
        var orderedContext = fullContext
            .Where(region => targets.Contains(region) || TranslationSourceGuard.IsReliable(region))
            .ToList();
        foreach (ComicRegion target in targets)
        {
            if (!orderedContext.Contains(target))
            {
                orderedContext.Add(target);
            }
        }
        ComicRegion[] orderedTargets = targets.Distinct().OrderBy(orderedContext.IndexOf).ToArray();
        var groups = new List<LocalTranslationGroup>();
        var current = new List<ComicRegion>();
        int firstPosition = -1;
        int previousPosition = -1;
        foreach (ComicRegion target in orderedTargets)
        {
            int position = orderedContext.IndexOf(target);
            // Bound both the number of targets and the span: a chain of nearby failures
            // must not accidentally grow back into a complete dense-page prompt.
            if (current.Count > 0 && (position - previousPosition > 4
                                      || position - firstPosition > 7
                                      || current.Count >= 6))
            {
                AddGroup();
            }
            if (current.Count == 0)
            {
                firstPosition = position;
            }
            current.Add(target);
            previousPosition = position;
        }
        if (current.Count > 0)
        {
            AddGroup();
        }
        return groups;

        void AddGroup()
        {
            ComicRegion[] group = current.ToArray();
            groups.Add(new LocalTranslationGroup(
                group, SelectLocalTranslationContext(group, orderedContext, radius: 2)));
            current.Clear();
        }
    }

    private static ComicRegion[] SelectLocalTranslationContext(
        IReadOnlyList<ComicRegion> targets,
        IReadOnlyList<ComicRegion> fullContext,
        int radius)
    {
        var included = new SortedSet<int>();
        foreach (ComicRegion target in targets)
        {
            for (int index = 0; index < fullContext.Count; index++)
            {
                if (!ReferenceEquals(fullContext[index], target))
                {
                    continue;
                }
                for (int neighbour = Math.Max(0, index - radius);
                     neighbour <= Math.Min(fullContext.Count - 1, index + radius);
                     neighbour++)
                {
                    ComicRegion candidate = fullContext[neighbour];
                    if (targets.Contains(candidate) || TranslationSourceGuard.IsReliable(candidate))
                    {
                        included.Add(neighbour);
                    }
                }
                break;
            }
        }
        return included.Select(index => fullContext[index])
            .Concat(targets.Where(target => !fullContext.Contains(target)))
            .Distinct()
            .ToArray();
    }

    private static string FormatLocalTranslationContext(
        IReadOnlyList<ComicRegion> targets,
        IReadOnlyList<ComicRegion> context)
    {
        var targetSet = targets.ToHashSet();
        return string.Join("\n", context
            .Where(region => targetSet.Contains(region) || TranslationSourceGuard.IsReliable(region))
            .Select((region, index) =>
            {
                string source = $"C{index:000} ({region.Type}): {FormatSourceForModel(region)}";
                return !targetSet.Contains(region) && IsAcceptableTranslation(region, region.Translation)
                    ? source + $"\n    ESPAÑOL_VECINO: {NormalizeSourceText(region.Translation)}"
                    : source;
            }));
    }

    private sealed record LocalTranslationGroup(ComicRegion[] Targets, ComicRegion[] Context);
}
