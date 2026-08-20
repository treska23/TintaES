using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TintaES.Core;

namespace TintaES.Wpf.Services;

/// <summary>
/// Segunda pasada lingüística sobre zonas ya detectadas. No abre imágenes, no ejecuta OCR y no
/// modifica geometría ni estilo: compara el original con la traducción guardada y solo sustituye
/// el borrador cuando el modelo devuelve una revisión española válida.
/// </summary>
public sealed class TranslationReviewService
{
    private const int ReviewChunkSize = 18;

    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:11434/"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async Task<TranslationReviewResult> ReviewPageAsync(
        IReadOnlyList<ComicRegion> regions,
        string model,
        CancellationToken cancellationToken,
        IProgress<AnalysisProgress>? progress = null)
    {
        // El proyecto puede haber guardado como principal una lectura parcial y conservar la
        // frase completa entre las alternativas OCR. Se corrige antes de construir el prompt:
        // así Repasar traducción también puede arreglar media frase sin repetir la detección.
        OcrReadingCompletion.PromoteCompleteAlternatives(regions);

        ComicRegion[] targets = regions
            .Where(region => region.IsEnabled
                             && !string.IsNullOrWhiteSpace(region.Original))
            .OrderBy(region => region.Order)
            .ToArray();
        if (targets.Length == 0)
        {
            return new TranslationReviewResult(0, 0, 0);
        }

        // Una frase española puede ser formalmente válida y, sin embargo, pertenecer a otro
        // bocadillo. Es preferible dejarla pendiente que enseñar un texto claramente cruzado.
        foreach (ComicRegion region in targets.Where(region =>
                     IsCrossAssignedTranslation(region, region.Translation, targets)))
        {
            region.Translation = string.Empty;
        }

        int changed = 0;
        for (int start = 0; start < targets.Length; start += ReviewChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComicRegion[] chunk = targets.Skip(start).Take(ReviewChunkSize).ToArray();
            TranslationReviewChunkResult chunkResult = await ReviewChunkAsync(
                chunk,
                targets,
                model,
                cancellationToken);
            changed += chunkResult.Changed;

            int completed = Math.Min(targets.Length, start + chunk.Length);
            progress?.Report(new AnalysisProgress(
                completed,
                targets.Length,
                $"Revisando traducciones · {completed}/{targets.Length}"));
        }

        // Si el modelo omitió una etiqueta o intentó volver a copiar el texto de otra zona,
        // se reintenta solo ese bocadillo. En una respuesta individual ya no puede desplazar
        // las traducciones de sus vecinos.
        ComicRegion[] unresolved = targets
            .Where(region => !region.HasRenderableTranslation)
            .ToArray();
        foreach (ComicRegion region in unresolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranslationReviewChunkResult recovery = await ReviewChunkAsync(
                [region],
                targets,
                model,
                cancellationToken);
            changed += recovery.Changed;
        }

        return new TranslationReviewResult(
            targets.Length,
            changed,
            targets.Count(region => !region.HasRenderableTranslation));
    }

    private static async Task<TranslationReviewChunkResult> ReviewChunkAsync(
        IReadOnlyList<ComicRegion> targets,
        IReadOnlyList<ComicRegion> fullPage,
        string model,
        CancellationToken cancellationToken)
    {
        string pageContext = string.Join(
            "\n",
            fullPage.Select((region, index) =>
                $"C{index:000} ORIGINAL: {Compact(region.Original)} | " +
                $"TRADUCCIÓN ACTUAL: {FormatDraft(region.Translation)}"));

        var tokens = targets.ToDictionary(
            region => region,
            region => "R" + region.Id.ToString("N")[..12].ToUpperInvariant());
        string drafts = string.Join(
            "\n",
            targets.Select(region =>
                $"[[{tokens[region]}]] ORIGINAL: {Compact(region.Original)} | " +
                $"BORRADOR: {FormatDraft(region.Translation)} [[/{tokens[region]}]]"));
        string documentedContext = ComicResearchAmbient.CurrentPrompt ?? string.Empty;

        string prompt =
            $"""
             Actúas como corrector final de un cómic ya traducido. Esta es una REVISIÓN rápida,
             no una traducción nueva ni una reescritura creativa. Compara cada ORIGINAL inglés
             con su BORRADOR español. Conserva el borrador exactamente igual cuando sea correcto
             y natural; modifícalo solo si existe un error claro de significado, contexto, sujeto,
             negación, nombre, continuidad, registro, gramática o variedad lingüística. Si el
             borrador figura como [SIN TRADUCCIÓN], crea únicamente la traducción que falta.

             {EuropeanSpanishDialect.ModelInstruction}

             Mantén la voz y la intensidad del personaje. No suavices tacos. Evita traducciones
             literales torpes. Conserva cada resultado conciso para el mismo bocadillo. No cambies
             nombres ni datos basándote en otro bocadillo. No añadas explicaciones.

             CONTEXTO DOCUMENTADO OPCIONAL:
             {documentedContext}

             PÁGINA COMPLETA EN ORDEN DE LECTURA:
             {pageContext}

             ELEMENTOS QUE DEBES REVISAR:
             {drafts}

             Devuelve todos los elementos dentro de sus mismas etiquetas exactas:
             [[ETIQUETA]] texto final [[/ETIQUETA]]
             No omitas, renumeres ni mezcles etiquetas. No devuelvas el original inglés.
             """;

        object payload = new
        {
            model,
            stream = false,
            think = false,
            keep_alive = "1m",
            messages = new[] { new { role = "user", content = prompt } },
            options = new
            {
                temperature = 0,
                seed = 131,
                num_ctx = 8192,
                num_predict = Math.Max(240, targets.Count * 100)
            }
        };

        using HttpResponseMessage response = await Client.PostAsJsonAsync(
            "api/chat",
            payload,
            cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(body)
                    ? $"Ollama respondió con HTTP {(int)response.StatusCode}."
                    : body);
        }

        using JsonDocument document = JsonDocument.Parse(body);
        string content = document.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        int changed = 0;
        int resolved = 0;
        foreach (ComicRegion region in targets)
        {
            string token = tokens[region];
            Match match = Regex.Match(
                content,
                $@"\[\[{Regex.Escape(token)}\]\]\s*(.*?)\s*\[\[/{Regex.Escape(token)}\]\]",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                if (region.HasRenderableTranslation)
                {
                    resolved++;
                }
                continue;
            }

            string candidate = Clean(match.Groups[1].Value);
            if (!IsUsableReview(region, candidate, fullPage))
            {
                if (region.HasRenderableTranslation)
                {
                    resolved++;
                }
                continue;
            }

            string previous = Compact(region.Translation);
            if (!string.Equals(previous, candidate, StringComparison.Ordinal))
            {
                region.Translation = candidate;
                if (region.HasRenderableTranslation)
                {
                    changed++;
                }
            }
            if (region.HasRenderableTranslation)
            {
                resolved++;
            }
        }

        return new TranslationReviewChunkResult(changed, resolved);
    }

    private static bool IsUsableReview(
        ComicRegion region,
        string candidate,
        IReadOnlyList<ComicRegion> fullPage)
    {
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Contains("[[", StringComparison.Ordinal)
            || candidate.Contains("ORIGINAL:", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("BORRADOR:", StringComparison.OrdinalIgnoreCase)
            || !candidate.Any(char.IsLetter)
            || candidate.Length > Math.Max(180, region.Original.Length * 4.2)
            || EuropeanSpanishDialect.RequiresRetry(region.Original, candidate)
            || IsCrossAssignedTranslation(region, candidate, fullPage))
        {
            return false;
        }

        string sourceLetters = new(region.Original.Where(char.IsLetterOrDigit).ToArray());
        string candidateLetters = new(candidate.Where(char.IsLetterOrDigit).ToArray());
        if (sourceLetters.Length >= 4
            && string.Equals(sourceLetters, candidateLetters, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                Compact(region.Translation),
                candidate,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] words = Regex.Matches(candidate.ToLowerInvariant(), @"[\p{L}']+")
            .Select(match => match.Value)
            .ToArray();
        string[] commonEnglish =
        [
            "the", "and", "but", "with", "from", "that", "this", "these", "those",
            "you", "your", "we", "they", "their", "what", "when", "where", "who",
            "have", "has", "had", "are", "was", "were", "will", "would", "could",
            "should", "just", "not", "for", "into", "about"
        ];
        int englishWords = words.Count(word =>
            commonEnglish.Contains(word, StringComparer.Ordinal));
        return englishWords < 2
               || englishWords / (double)Math.Max(1, words.Length) < 0.25;
    }

    private static bool IsCrossAssignedTranslation(
        ComicRegion region,
        string? candidate,
        IReadOnlyList<ComicRegion> fullPage)
    {
        string candidateKey = SemanticKey(candidate);
        if (candidateKey.Length < 4)
        {
            return false;
        }

        string ownSourceKey = SemanticKey(region.Original);
        if (string.Equals(candidateKey, ownSourceKey, StringComparison.Ordinal)
            || ownSourceKey.Contains(candidateKey, StringComparison.Ordinal))
        {
            return false;
        }

        return fullPage.Any(other =>
            !ReferenceEquals(other, region)
            && string.Equals(
                SemanticKey(other.Original),
                candidateKey,
                StringComparison.Ordinal));
    }

    private static string SemanticKey(string? value)
    {
        string decomposed = (value ?? string.Empty)
            .Normalize(NormalizationForm.FormD);
        return new string(decomposed
            .Where(character =>
                CharUnicodeInfo.GetUnicodeCategory(character)
                    != UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string FormatDraft(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "[SIN TRADUCCIÓN]"
            : Compact(value);

    private static string Clean(string value)
    {
        string cleaned = Regex.Replace(
            value ?? string.Empty,
            @"<think>.*?</think>",
            string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        cleaned = cleaned
            .Replace("```", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Trim();
        cleaned = Regex.Replace(
            cleaned,
            @"^(?:ESPAÑOL|SPANISH|REVISIÓN|REVISION|TRADUCCI[ÓO]N|TRANSLATION)\s*:\s*",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Compact(cleaned).Trim('"', '\'', '“', '”', '‘', '’');
    }

    private static string Compact(string? value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");

    private sealed record TranslationReviewChunkResult(int Changed, int Resolved);
}

public sealed record TranslationReviewResult(int Reviewed, int Changed, int Unresolved);
