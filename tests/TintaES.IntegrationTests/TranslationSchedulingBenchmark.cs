using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TintaES.Core;
using TintaES.Wpf;
using TintaES.Wpf.Services;

internal static class TranslationSchedulingBenchmark
{
    private const string Model = "translategemma:12b";
    private const int WindowSize = 4;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Manual, opt-in benchmark: uses the installed local OCR and translation models.
    // See TranslationSchedulingBenchmark.md for the cold/cache comparison boundaries.
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 5 || args[0] is not ("baseline" or "window"))
        {
            PrintUsage();
            return 2;
        }

        string mode = args[0];
        string outputDirectory = Path.GetFullPath(args[1]);
        bool reloadBeforeOcr = false;
        string? frozenAnalysisDirectory = null;
        var imagePaths = new List<string>();
        for (int index = 2; index < args.Length; index++)
        {
            if (args[index] == "--reload-before-ocr")
            {
                reloadBeforeOcr = true;
            }
            else if (args[index] == "--analysis-from" && index + 1 < args.Length)
            {
                frozenAnalysisDirectory = Path.GetFullPath(args[++index]);
            }
            else if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                PrintUsage();
                return 2;
            }
            else
            {
                imagePaths.Add(Path.GetFullPath(args[index]));
            }
        }
        if (imagePaths.Count < 3 || imagePaths.Any(path => !File.Exists(path)))
        {
            Console.Error.WriteLine("Se necesitan al menos tres imágenes existentes, en el mismo orden para ambos modos.");
            return 2;
        }
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new InvalidOperationException("El directorio de resultados debe estar vacío para no sobrescribir mediciones.");
        }
        if (frozenAnalysisDirectory is not null)
        {
            for (int index = 0; index < imagePaths.Count; index++)
            {
                if (!File.Exists(Path.Combine(frozenAnalysisDirectory, PageFile(index, "input"))))
                {
                    throw new FileNotFoundException($"Falta la entrada de traducción congelada de la página {index + 1}.");
                }
            }
        }

        Directory.CreateDirectory(outputDirectory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        CancellationToken token = cancellation.Token;
        using var handler = new TimingHandler(outputDirectory);
        using var http = new HttpClient(handler);
        using var ollama = new OllamaClient(httpClient: http);
        // OllamaClient sets its default timeout in the constructor. Match the WPF long-request override.
        http.Timeout = Timeout.InfiniteTimeSpan;
        var organic = new OrganicEngineService();
        var preparations = new List<PreparationTiming>();
        var translations = new List<TranslationTiming>();
        var events = new List<object>();
        var runTimer = new Stopwatch();
        string? failure = null;

        await WriteJsonAsync(outputDirectory, "configuration.json", new
        {
            mode,
            model = Model,
            windowSize = mode == "window" ? WindowSize : 1,
            firstPagePreparedAlone = true,
            reloadBeforeOcr,
            frozenAnalysisDirectory,
            endpoint = "http://127.0.0.1:11434",
            timeoutMinutes = 30,
            startedUtc = DateTimeOffset.UtcNow,
            sources = imagePaths.Select(path => new
            {
                path,
                lastWriteUtc = File.GetLastWriteTimeUtc(path),
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            }),
            interpretation = reloadBeforeOcr
                ? "Coordinación y traducción con descarga forzada antes de cada preparación OCR; consultar los aciertos de caché. No es una medida de OCR frío."
                : "Flujo local con estado de caché observado, sin invalidar ni borrar las cachés del usuario."
        }, token);
        await handler.SaveEnvironmentAsync(token);

        // Give both modes the same unloaded translation-model starting state, outside the timed run.
        await ollama.UnloadModelAsync(Model, token);
        runTimer.Start();
        try
        {
            var window = new PreparedPageWindow<ComicAnalysis>(imagePaths.Count, WindowSize);
            for (int position = 0; position < imagePaths.Count; position++)
            {
                ComicAnalysis analysis = mode == "window"
                    ? await window.TakeAsync(position, PrepareAsync, token)
                    : await PrepareAsync(position, token);
                handler.PageNumber = position + 1;
                int callsBefore = handler.Calls.Count;
                events.Add(new { action = "translation-start", page = position + 1, elapsedMs = runTimer.Elapsed.TotalMilliseconds });
                var translationTimer = Stopwatch.StartNew();
                string? translationError = null;
                try
                {
                    await ollama.TranslateRegionsAsync(
                        analysis.Regions, Model, token,
                        new ConsoleProgress($"TRADUCCION_PAGINA={position + 1}"));
                }
                catch (Exception exception)
                {
                    translationError = $"{exception.GetType().Name}: {exception.Message}";
                    throw;
                }
                finally
                {
                    translationTimer.Stop();
                    translations.Add(new TranslationTiming(
                        position + 1, translationTimer.Elapsed.TotalMilliseconds,
                        runTimer.Elapsed.TotalMilliseconds, handler.Calls.Count - callsBefore,
                        analysis.Regions.Count, analysis.Regions.Count(region => region.HasRenderableTranslation), translationError));
                    await WriteJsonAsync(outputDirectory, PageFile(position, "translations"), analysis, CancellationToken.None);
                    events.Add(new { action = "translation-end", page = position + 1, elapsedMs = runTimer.Elapsed.TotalMilliseconds });
                    Console.WriteLine($"PAGINA={position + 1}; traduccion_ms={translationTimer.Elapsed.TotalMilliseconds:F1}; lote_ms={runTimer.Elapsed.TotalMilliseconds:F1}");
                }
            }
            return 0;
        }
        catch (Exception exception)
        {
            failure = $"{exception.GetType().Name}: {exception.Message}";
            throw;
        }
        finally
        {
            runTimer.Stop();
            await WriteJsonAsync(outputDirectory, "summary.json", new
            {
                mode,
                model = Model,
                reloadBeforeOcr,
                frozenTranslationInputs = frozenAnalysisDirectory is not null,
                completed = failure is null && translations.Count == imagePaths.Count,
                failure,
                totalWallMs = runTimer.Elapsed.TotalMilliseconds,
                firstPageCompletedMs = translations.FirstOrDefault()?.CompletedAtMs,
                preparationWallMs = preparations.Sum(value => value.TotalMs),
                ocrWallMs = preparations.Sum(value => value.AnalyzeMs),
                translationWallMs = translations.Sum(value => value.TotalMs),
                reusableOcrPages = preparations.Count(value => value.CacheWasReusable),
                requestedUnloadsBeforeOcr = preparations.Count(value => value.UnloadRequested),
                chatCalls = handler.Calls.Count,
                observedColdModelLoads = handler.Calls.Count(value => value.ModelWasResident == false && value.HttpSuccess),
                residencyProbeFailures = handler.Calls.Count(value => value.ModelWasResident is null),
                loadsLongerThan5Seconds = handler.Calls.Count(value => value.LoadMs > 5000),
                serverLoadMs = handler.Calls.Sum(value => value.LoadMs ?? 0),
                serverPromptMs = handler.Calls.Sum(value => value.PromptMs ?? 0),
                serverEvaluationMs = handler.Calls.Sum(value => value.EvaluationMs ?? 0),
                promptTokens = handler.Calls.Sum(value => value.PromptTokens ?? 0),
                generatedTokens = handler.Calls.Sum(value => value.GeneratedTokens ?? 0),
                preparations,
                translations,
                calls = handler.Calls,
                events
            }, CancellationToken.None);
            Console.WriteLine($"BENCHMARK={mode}; completo={failure is null}; total_ms={runTimer.Elapsed.TotalMilliseconds:F1}; cargas_frias_observadas={handler.Calls.Count(value => value.ModelWasResident == false && value.HttpSuccess)}");
        }

        async Task<ComicAnalysis> PrepareAsync(int position, CancellationToken preparationToken)
        {
            events.Add(new { action = "ocr-start", page = position + 1, elapsedMs = runTimer.Elapsed.TotalMilliseconds });
            var preparationTimer = Stopwatch.StartNew();
            bool reusable = await organic.HasReusableAnalysisAsync(imagePaths[position], preparationToken);
            bool unload = reloadBeforeOcr || !reusable;
            var unloadTimer = new Stopwatch();
            if (unload)
            {
                unloadTimer.Start();
                await ollama.UnloadModelAsync(Model, preparationToken);
                unloadTimer.Stop();
            }
            var ocrTimer = Stopwatch.StartNew();
            OrganicAnalysisResult result = await organic.AnalyzeAsync(
                imagePaths[position], new ConsoleProgress($"OCR_PAGINA={position + 1}"), preparationToken);
            ocrTimer.Stop();
            int promoted = OcrReadingCompletion.PromoteCompleteAlternatives(result.Analysis.Regions);
            // Preserve the actual OCR result and its original GUIDs before normalization or translation.
            await WriteJsonAsync(outputDirectory, PageFile(position, "ocr"), result.Analysis, preparationToken);
            var analysis = new ComicAnalysis(
                result.Analysis.SourceLanguage,
                result.Analysis.Regions.Where(MainWindow.IsReadableLetteringCandidate).ToArray());
            if (analysis.Regions.Count == 0)
            {
                throw new InvalidOperationException($"La página {position + 1} no contiene zonas legibles; no sirve para medir traducción.");
            }
            if (frozenAnalysisDirectory is not null)
            {
                string frozenJson = await File.ReadAllTextAsync(
                    Path.Combine(frozenAnalysisDirectory, PageFile(position, "input")), preparationToken);
                ComicAnalysis frozen = JsonSerializer.Deserialize<ComicAnalysis>(frozenJson)
                    ?? throw new InvalidOperationException("El análisis congelado está vacío.");
                if (!JsonNode.DeepEquals(WithoutIds(analysis), WithoutIds(frozen)))
                {
                    await WriteJsonAsync(outputDirectory, PageFile(position, "different-input"), analysis, preparationToken);
                    throw new InvalidOperationException($"El OCR de la página {position + 1} difiere del baseline más allá de sus Id. Comparación cancelada.");
                }
                // Only this separate translation copy reuses the original baseline identities.
                // The fresh OCR output above remains intact and available for independent comparison.
                analysis = frozen;
            }
            await WriteJsonAsync(outputDirectory, PageFile(position, "input"), analysis, preparationToken);
            preparationTimer.Stop();
            preparations.Add(new PreparationTiming(
                position + 1, preparationTimer.Elapsed.TotalMilliseconds, ocrTimer.Elapsed.TotalMilliseconds,
                reusable, result.FromCache, unload, unloadTimer.Elapsed.TotalMilliseconds,
                promoted, result.Analysis.Regions.Count, analysis.Regions.Count));
            events.Add(new { action = "ocr-end", page = position + 1, elapsedMs = runTimer.Elapsed.TotalMilliseconds });
            return analysis;
        }
    }

    private static JsonNode? WithoutIds(ComicAnalysis analysis)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(analysis);
        if (node?[nameof(ComicAnalysis.Regions)] is JsonArray regions)
        {
            foreach (JsonNode? region in regions)
            {
                (region as JsonObject)?.Remove(nameof(ComicRegion.Id));
            }
        }
        return node;
    }

    private static string PageFile(int position, string stage) => $"page-{position + 1:000}-{stage}.json";

    private static Task WriteJsonAsync(string directory, string name, object value, CancellationToken token) =>
        File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, JsonOptions), token);

    private static void PrintUsage() => Console.Error.WriteLine(
        "Uso: --translation-scheduling-benchmark baseline|window directorio-vacío " +
        "[--reload-before-ocr] [--analysis-from directorio-baseline] imagen1 imagen2 imagen3 [imagen4 ...]");

    private sealed class ConsoleProgress(string prefix) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => Console.WriteLine($"{prefix}; progreso={value.Percentage:F0}; {value.Message}");
    }

    private sealed record PreparationTiming(
        int Page, double TotalMs, double AnalyzeMs, bool CacheWasReusable, bool OrganicFromCache,
        bool UnloadRequested, double UnloadMs, int PromotedReadings, int OcrRegions, int ReadableRegions);

    private sealed record TranslationTiming(
        int Page, double TotalMs, double CompletedAtMs, int Calls, int Regions, int TranslatedRegions, string? Error);

    private sealed record ChatTiming(
        int Call, int Page, double WallMs, bool? ModelWasResident, bool HttpSuccess,
        double? LoadMs, double? PromptMs, double? EvaluationMs, long? PromptTokens, long? GeneratedTokens);

    private sealed class TimingHandler(string directory) : DelegatingHandler(new HttpClientHandler())
    {
        private readonly HttpClient _probe = new()
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        internal List<ChatTiming> Calls { get; } = [];
        internal int PageNumber { get; set; }

        internal async Task SaveEnvironmentAsync(CancellationToken token)
        {
            foreach (string endpoint in new[] { "version", "tags", "ps" })
            {
                string json = await _probe.GetStringAsync($"api/{endpoint}", token);
                await File.WriteAllTextAsync(Path.Combine(directory, $"ollama-{endpoint}.json"), json, token);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.AbsolutePath != "/api/chat")
            {
                return await base.SendAsync(request, token);
            }
            int call = Calls.Count + 1;
            string payload = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            await File.WriteAllTextAsync(Path.Combine(directory, $"request-{call:000}.json"), payload, token);
            bool? resident = await IsModelResidentAsync(token);
            var timer = Stopwatch.StartNew();
            HttpResponseMessage response = await base.SendAsync(request, token);
            string raw = await response.Content.ReadAsStringAsync(token);
            timer.Stop();
            await File.WriteAllTextAsync(Path.Combine(directory, $"response-{call:000}.json"), raw, token);
            using JsonDocument document = JsonDocument.Parse(raw);
            JsonElement root = document.RootElement;
            var timing = new ChatTiming(
                call, PageNumber, timer.Elapsed.TotalMilliseconds, resident, response.IsSuccessStatusCode,
                Nanoseconds(root, "load_duration"), Nanoseconds(root, "prompt_eval_duration"),
                Nanoseconds(root, "eval_duration"), Integer(root, "prompt_eval_count"), Integer(root, "eval_count"));
            Calls.Add(timing);
            Console.WriteLine("OLLAMA=" + JsonSerializer.Serialize(timing));
            return response;
        }

        private async Task<bool?> IsModelResidentAsync(CancellationToken token)
        {
            try
            {
                string json = await _probe.GetStringAsync("api/ps", token);
                using JsonDocument document = JsonDocument.Parse(json);
                return document.RootElement.GetProperty("models").EnumerateArray().Any(item =>
                    (item.TryGetProperty("name", out JsonElement name) && name.GetString() == Model)
                    || (item.TryGetProperty("model", out JsonElement model) && model.GetString() == Model));
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException
                                             || exception is OperationCanceledException && !token.IsCancellationRequested)
            {
                return null;
            }
        }

        private static long? Integer(JsonElement root, string property) =>
            root.TryGetProperty(property, out JsonElement value) && value.TryGetInt64(out long result) ? result : null;

        private static double? Nanoseconds(JsonElement root, string property) => Integer(root, property) / 1_000_000.0;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _probe.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
