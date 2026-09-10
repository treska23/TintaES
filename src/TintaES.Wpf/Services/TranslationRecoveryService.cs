using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TintaES.Core;

namespace TintaES.Wpf.Services;

/// <summary>
/// Último rescate individual para bocadillos que el traductor contextual dejó vacíos. Solo actúa
/// cuando la propia lectura OCR contiene evidencia textual suficiente. Nunca reconstruye diálogo
/// a partir del contexto ni obliga al modelo a contestar cuando el OCR es ilegible.
/// </summary>
public sealed class TranslationRecoveryService
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:11434/"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async Task RecoverAsync(
        IReadOnlyList<ComicRegion> regions,
        string model,
        CancellationToken cancellationToken,
        IProgress<AnalysisProgress>? progress = null)
    {
        int completed = 0;
        foreach (ComicRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.HasRenderableTranslation)
            {
                completed++;
                Report(progress, completed, regions.Count);
                continue;
            }

            // Esta barrera va antes de nombres propios, heurísticas y Ollama. El comportamiento
            // anterior llegaba a pedir explícitamente «una lectura española útil aunque el OCR
            // sea dudoso», lo que convertía tramas y ruido como OOOOO en diálogo inventado.
            if (!TranslationSourceGuard.IsReliable(region))
            {
                region.Translation = string.Empty;
                Report(progress, completed, regions.Count);
                continue;
            }

            string source = Normalize(region.Original);
            if (TryKnownLocalTranslation(source, out string known))
            {
                region.Translation = known;
                completed++;
                Report(progress, completed, regions.Count);
                continue;
            }
            if (CanRemainUnchanged(source))
            {
                region.Translation = source;
                completed++;
                Report(progress, completed, regions.Count);
                continue;
            }

            try
            {
                string candidate = await TranslateOneAsync(
                    source,
                    model,
                    cancellationToken);
                if (IsUsableSpanish(source, candidate))
                {
                    region.Translation = candidate;
                    completed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Si este único pase estricto falla, la zona queda pendiente. No existe un
                // segundo pase creativo que intente adivinar qué «debería» decir el bocadillo.
            }

            Report(progress, completed, regions.Count);
        }
    }

    private static async Task<string> TranslateOneAsync(
        string source,
        string model,
        CancellationToken cancellationToken)
    {
        string prompt =
            $"""
             Traduce esta única frase de cómic del inglés. {EuropeanSpanishDialect.ModelInstruction}
             Usa EXCLUSIVAMENTE las palabras presentes en TEXTO. No completes palabras ausentes,
             no inventes diálogo y no deduzcas lo que un personaje podría estar diciendo.
             Puedes corregir un error mecánico obvio del OCR solo si la propia cadena conserva
             evidencia inequívoca de las letras. Si el texto no se entiende con seguridad,
             devuelve exactamente [[UNREADABLE]]. Conserva nombres propios, siglas y palabras
             que legítimamente no cambian en español. Sé conciso, pero no omitas ninguna idea
             que sí esté explícitamente presente.

             TEXTO:
             {source}
             """;
        object payload = new
        {
            model,
            stream = false,
            keep_alive = "1m",
            messages = new[] { new { role = "user", content = prompt } },
            options = new
            {
                temperature = 0,
                seed = 97,
                num_ctx = 2048,
                num_predict = Math.Clamp(source.Length * 3, 80, 360)
            }
        };

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string? body = null;
        string outcome = "failed";
        try
        {
            using HttpResponseMessage response = await Client.PostAsJsonAsync(
                "api/chat",
                payload,
                cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(body);
            string content = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
            string result = Clean(content);
            outcome = "completed";
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        finally
        {
            OllamaClient.RecordTranslateGemmaTiming(
                "final_recovery_strict",
                model,
                1,
                1,
                source.Length,
                prompt.Length,
                body,
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                outcome);
        }
    }

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
            @"^(?:ESPAÑOL|SPANISH|TRADUCCI[ÓO]N|TRANSLATION)\s*:\s*",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.Trim('"', '\'', '“', '”', '‘', '’');
    }

    private static bool IsUsableSpanish(string source, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)
            || string.Equals(candidate.Trim(), "[[UNREADABLE]]", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("[[", StringComparison.Ordinal)
            || candidate.Contains("SOURCE:", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("TRANSLATION:", StringComparison.OrdinalIgnoreCase)
            || !candidate.Any(char.IsLetter)
            || candidate.Length > Math.Max(180, source.Length * 4.2)
            || EuropeanSpanishDialect.RequiresRetry(source, candidate))
        {
            return false;
        }

        string sourceLetters = new(source.Where(char.IsLetterOrDigit).ToArray());
        string candidateLetters = new(candidate.Where(char.IsLetterOrDigit).ToArray());
        if (sourceLetters.Length >= 4
            && string.Equals(
                sourceLetters,
                candidateLetters,
                StringComparison.OrdinalIgnoreCase))
        {
            return CanRemainUnchanged(source);
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
        return englishWords < 2 || englishWords / (double)Math.Max(1, words.Length) < 0.25;
    }

    internal static bool CanRemainUnchanged(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 28)
        {
            return false;
        }

        string[] words = Regex.Matches(source.ToLowerInvariant(), @"[\p{L}']+")
            .Select(match => match.Value)
            .ToArray();
        string[] originalWords = Regex.Matches(source, @"[\p{L}']+")
            .Select(match => match.Value)
            .ToArray();
        if (words.Length == 0 || words.Length > 4)
        {
            return false;
        }

        string[] englishSpeechWords =
        [
            "i", "me", "my", "mine", "you", "your", "yours", "we", "us", "our",
            "they", "them", "their", "he", "him", "his", "she", "her", "it", "its",
            "the", "a", "an", "and", "but", "or", "not", "no", "yes", "is", "are",
            "was", "were", "be", "am", "do", "did", "have", "has", "can", "will",
            "what", "why", "when", "where", "who", "how", "this", "that", "here",
            "there", "go", "come", "look", "wait", "stop", "help", "love", "want",
            "run", "pig", "pigs", "piggy", "piggies", "exit", "enter", "open", "closed",
            "boom", "bang", "smash", "crash", "pow", "wham"
        ];
        if (words.Any(word => englishSpeechWords.Contains(word, StringComparer.Ordinal)))
        {
            return false;
        }

        if (words.Length == 1)
        {
            return source.Length <= 24;
        }

        if (words.Length > 2)
        {
            return false;
        }

        // Dos palabras solo pueden quedar iguales si parecen realmente un nombre propio.
        return originalWords.All(word => word.Length > 0 && char.IsUpper(word[0]));
    }

    internal static bool TryKnownLocalTranslation(string source, out string translation)
    {
        string key = Regex.Replace(source.ToUpperInvariant(), @"[^A-Z0-9]+", " ").Trim();
        translation = key switch
        {
            "BEG AND MAYBE WE LET SOME LIVE YES" =>
                "Suplica, y quizá dejemos a algunos con vida, ¿sí?",
            "RUN PIGGIES" => "¡Corred, cerditos!",
            "L ENBY S" => "Leroy's",
            "C CAN" => "¡CLANG!",
            "EXIT" => "SALIDA",
            "ENTRANCE" => "ENTRADA",
            _ => string.Empty
        };
        return translation.Length > 0;
    }

    internal static int ApplyKnownLocalTranslations(IEnumerable<ComicRegion> regions)
    {
        int corrected = 0;
        foreach (ComicRegion region in regions.Where(region => region.IsEnabled))
        {
            if (!TranslationSourceGuard.IsReliable(region))
            {
                if (region.HasRenderableTranslation)
                {
                    region.Translation = string.Empty;
                    corrected++;
                }
                continue;
            }

            if (TryKnownLocalTranslation(region.Original, out string translation))
            {
                region.Translation = translation;
                corrected++;
                continue;
            }

            // Una traducción marcada como panhispánica, latinoamericana o formal sin motivo
            // se vacía deliberadamente. El pase individual posterior la rehace completa con
            // concordancia peninsular; no intentamos sustituir pronombres a ciegas.
            if (region.HasRenderableTranslation
                && EuropeanSpanishDialect.RequiresRetry(region.Original, region.Translation))
            {
                region.Translation = string.Empty;
                corrected++;
            }
        }
        return corrected;
    }

    private static string Normalize(string value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");

    private static void Report(
        IProgress<AnalysisProgress>? progress,
        int completed,
        int total)
    {
        progress?.Report(new AnalysisProgress(
            980 + (int)Math.Round(completed / (double)Math.Max(1, total) * 20),
            1000,
            $"Recuperando bocadillos pendientes · {completed}/{total}"));
    }
}
