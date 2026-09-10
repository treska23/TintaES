using System.Text;
using System.Text.Json;

namespace TintaES.Core;

internal sealed record TranslateGemmaRequestMetrics(
    string Phase,
    string Model,
    int TargetCount,
    int ContextCount,
    int ContextCharacters,
    int PromptCharacters);

public sealed partial class OllamaClient
{
    private const long TranslationTimingRotationBytes = 2 * 1024 * 1024;
    private static readonly object TranslationTimingLock = new();
    private static readonly UTF8Encoding TranslationTimingEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Records metadata for TranslateGemma calls made by auxiliary translation services.
    /// Response text is inspected only for numeric timing fields and is never persisted.
    /// Diagnostics cannot throw into the translation or cancellation path.
    /// </summary>
    public static void RecordTranslateGemmaTiming(
        string phase,
        string model,
        int targetCount,
        int contextCount,
        int contextCharacters,
        int promptCharacters,
        string? responseJson,
        double wallClockMs,
        string outcome)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(model)
                || !model.StartsWith("translategemma", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TranslateGemmaResponseMetrics response = default;
            if (!string.IsNullOrWhiteSpace(responseJson))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(responseJson);
                    response = ReadTranslateGemmaResponseMetrics(document.RootElement);
                }
                catch (JsonException)
                {
                    // Invalid/error responses still represent a real request with unknown metrics.
                }
            }
            TryWriteTranslateGemmaTiming(
                new TranslateGemmaRequestMetrics(
                    phase, model, targetCount, contextCount, contextCharacters, promptCharacters),
                response,
                wallClockMs,
                outcome);
        }
        catch (Exception)
        {
            // Includes parsing, metadata serialization and unavailable temporary directories.
        }
    }

    private readonly record struct TranslateGemmaResponseMetrics(
        long? LoadDuration,
        long? PromptEvalDuration,
        long? EvalDuration,
        long? PromptEvalCount,
        long? EvalCount,
        long? TotalDuration);

    private static TranslateGemmaResponseMetrics ReadTranslateGemmaResponseMetrics(JsonElement response) => new(
        ReadOptionalTimingInteger(response, "load_duration"),
        ReadOptionalTimingInteger(response, "prompt_eval_duration"),
        ReadOptionalTimingInteger(response, "eval_duration"),
        ReadOptionalTimingInteger(response, "prompt_eval_count"),
        ReadOptionalTimingInteger(response, "eval_count"),
        ReadOptionalTimingInteger(response, "total_duration"));

    private static long? ReadOptionalTimingInteger(JsonElement response, string property) =>
        response.ValueKind == JsonValueKind.Object
        && response.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number)
        && number >= 0
            ? number
            : null;

    private static string SerializeTranslateGemmaTiming(
        TranslateGemmaRequestMetrics request,
        TranslateGemmaResponseMetrics response,
        double wallClockMilliseconds,
        string outcome) => JsonSerializer.Serialize(new
        {
            timestamp_utc = DateTimeOffset.UtcNow,
            phase = request.Phase,
            model = request.Model,
            target_count = request.TargetCount,
            context_count = request.ContextCount,
            context_characters = request.ContextCharacters,
            prompt_characters = request.PromptCharacters,
            // Ollama reports these durations in nanoseconds; retain the original values.
            load_duration = response.LoadDuration,
            prompt_eval_duration = response.PromptEvalDuration,
            eval_duration = response.EvalDuration,
            prompt_eval_count = response.PromptEvalCount,
            eval_count = response.EvalCount,
            total_duration = response.TotalDuration,
            wall_clock_ms = wallClockMilliseconds,
            outcome
        });

    private static void TryWriteTranslateGemmaTiming(
        TranslateGemmaRequestMetrics request,
        TranslateGemmaResponseMetrics response,
        double wallClockMilliseconds,
        string outcome)
    {
        try
        {
            string entry = SerializeTranslateGemmaTiming(request, response, wallClockMilliseconds, outcome);
            string path = Path.Combine(Path.GetTempPath(), "tintaes-translategemma-timing.jsonl");
            TryAppendTranslateGemmaTiming(path, entry);
        }
        catch (Exception)
        {
            // Diagnostics must never replace a translation result or its original exception.
        }
    }

    private static void TryAppendTranslateGemmaTiming(string path, string entry)
    {
        try
        {
            lock (TranslationTimingLock)
            {
                if (File.Exists(path) && new FileInfo(path).Length >= TranslationTimingRotationBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, entry + Environment.NewLine, TranslationTimingEncoding);
            }
        }
        catch (Exception)
        {
            // Locked files, exhausted storage and temporary-folder failures are non-fatal.
        }
    }
}
