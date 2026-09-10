using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TintaES.Core;

internal static class TranslateGemmaTimingRegression
{
    private static readonly MethodInfo SendMethod = typeof(OllamaClient).GetMethod(
        "SendChatAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo AppendMethod = typeof(OllamaClient).GetMethod(
        "TryAppendTranslateGemmaTiming", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly Type MetadataType = typeof(OllamaClient).Assembly.GetType(
        "TintaES.Core.TranslateGemmaRequestMetrics", throwOnError: true)!;

    [ModuleInitializer]
    internal static void Run()
    {
        // This console suite runs module initializers sequentially. Isolate only this
        // process's diagnostic destination; do not alter the user's existing JSONL.
        string directory = Path.Combine(Path.GetTempPath(), "TintaES-timing-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string[] variableNames = ["TMP", "TEMP", "TMPDIR"];
        string?[] previous = variableNames.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (string name in variableNames)
            {
                Environment.SetEnvironmentVariable(name, directory);
            }
            Require(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    == Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                "Las métricas de prueba deben quedar aisladas del registro del usuario.");

            VerifyMissingAndMalformedMetricsAsync(directory).GetAwaiter().GetResult();
            VerifyMetricsAndUnchangedRequestAsync(directory).GetAwaiter().GetResult();
            VerifyFailuresAndCancellationAsync(directory).GetAwaiter().GetResult();
            VerifyLogFailuresDoNotChangeTranslationAsync(directory).GetAwaiter().GetResult();
            VerifyAuxiliaryServiceFacade(directory);
            VerifyConcurrentAppendAndRotation(directory);
            Console.WriteLine("OK  Métricas de TranslateGemma opcionales y aisladas de traducción");
        }
        finally
        {
            for (int index = 0; index < variableNames.Length; index++)
            {
                Environment.SetEnvironmentVariable(variableNames[index], previous[index]);
            }
            // Only fixed files created by this test; no recursive removal or user-log deletion.
            foreach (string name in new[]
                     {
                         "tintaes-translategemma-timing.jsonl", "tintaes-translategemma-timing.jsonl.1",
                         "parallel.jsonl", "parallel.jsonl.1", "rotation.jsonl", "rotation.jsonl.1"
                     })
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            Directory.Delete(directory);
        }
    }

    private static async Task VerifyMissingAndMalformedMetricsAsync(string directory)
    {
        using var handler = new MockHandler(_ => Reply("""{"message":{"content":"Sin métricas"}}"""));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(httpClient: http);
        Require(await SendAsync(client, new { model = "mock" }, "missing") == "Sin métricas",
            "Una respuesta mock sin métricas debe devolver exactamente el contenido original.");
        using JsonDocument missing = ReadLastEntry(directory);
        foreach (string property in MetricProperties)
        {
            Require(missing.RootElement.GetProperty(property).ValueKind == JsonValueKind.Null,
                $"La métrica ausente {property} debe registrarse como null.");
        }

        handler.Response = _ => Reply("""
            {"message":{"content":"Con métricas desconocidas"},
             "load_duration":"invalid","prompt_eval_duration":{},"eval_duration":-1,
             "prompt_eval_count":1.5,"eval_count":9223372036854775808,"total_duration":null}
            """);
        Require(await SendAsync(client, new { model = "mock" }, "malformed") == "Con métricas desconocidas",
            "Los metadatos opcionales no válidos no deben impedir la traducción.");
        using JsonDocument malformed = ReadLastEntry(directory);
        foreach (string property in MetricProperties)
        {
            Require(malformed.RootElement.GetProperty(property).ValueKind == JsonValueKind.Null,
                $"La métrica no válida {property} debe omitirse sin convertirla en cero.");
        }
        Require(handler.Calls == 2, "La instrumentación no debe añadir llamadas HTTP.");
    }

    private static async Task VerifyMetricsAndUnchangedRequestAsync(string directory)
    {
        const string privateText = "Texto original privado que no debe aparecer en las métricas.";
        using var handler = new MockHandler(_ => Reply("""
            {"message":{"content":"Traducción privada"},
             "load_duration":12345678901,"prompt_eval_duration":234567890,
             "eval_duration":3456789012,"prompt_eval_count":694,"eval_count":104,"total_duration":16037035703}
            """));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(httpClient: http);
        var payload = new
        {
            model = "mock-metrics-model",
            messages = new[] { new { role = "user", content = privateText } },
            options = new { temperature = 0, seed = 73, num_ctx = 4096, num_predict = 268 }
        };
        Require(await SendAsync(client, payload, "complete") == "Traducción privada",
            "Las métricas no deben cambiar la respuesta.");
        string expectedPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Require(handler.LastRequest == expectedPayload && handler.Calls == 1,
            "La solicitud, el prompt y las opciones de traducción deben mantenerse idénticos.");
        string entry = File.ReadLines(LogPath(directory)).Last();
        Require(!entry.Contains(privateText) && !entry.Contains("Traducción privada") && !entry.Contains("messages"),
            "El registro no debe contener texto OCR, prompts ni traducciones.");
        using JsonDocument document = JsonDocument.Parse(entry);
        JsonElement root = document.RootElement;
        long[] values = [12345678901, 234567890, 3456789012, 694, 104, 16037035703];
        for (int index = 0; index < MetricProperties.Length; index++)
        {
            Require(root.GetProperty(MetricProperties[index]).GetInt64() == values[index],
                "Las métricas deben conservar las unidades y los valores originales de Ollama.");
        }
        Require(root.GetProperty("phase").GetString() == "complete"
                && root.GetProperty("model").GetString() == "mock-metrics-model"
                && root.GetProperty("target_count").GetInt32() == 2
                && root.GetProperty("context_count").GetInt32() == 4
                && root.GetProperty("context_characters").GetInt32() == 123
                && root.GetProperty("prompt_characters").GetInt32() == 456
                && root.GetProperty("wall_clock_ms").GetDouble() >= 0
                && root.GetProperty("outcome").GetString() == "completed",
            "Cada entrada debe identificar la fase y el tamaño de la petición medida.");

        int linesBefore = File.ReadLines(LogPath(directory)).Count();
        Require(await SendAsync(client, payload, null) == "Traducción privada"
                && File.ReadLines(LogPath(directory)).Count() == linesBefore,
            "Las llamadas sin metadatos de TranslateGemma no deben añadir entradas al registro.");
    }

    private static async Task VerifyFailuresAndCancellationAsync(string directory)
    {
        using var handler = new MockHandler(_ => Reply("{\"error\":\"test failure\"}", HttpStatusCode.BadRequest));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(httpClient: http);
        Exception? failure = null;
        try
        {
            await SendAsync(client, new { model = "mock" }, "http-failure");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        Require(failure is not null && failure.Message.Contains("test failure"),
            "La instrumentación debe conservar el error HTTP original.");
        using JsonDocument failed = ReadLastEntry(directory);
        Require(failed.RootElement.GetProperty("outcome").GetString() == "failed",
            "Debe distinguir errores de respuestas completadas.");

        using var cancel = new CancellationTokenSource();
        handler.Response = token =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(token);
        };
        bool canceled = false;
        try
        {
            await SendAsync(client, new { model = "mock" }, "cancellation", cancel.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        Require(canceled, "Las métricas nunca deben convertir una cancelación en éxito ni reintentarla.");
        using JsonDocument cancellation = ReadLastEntry(directory);
        Require(cancellation.RootElement.GetProperty("outcome").GetString() == "canceled",
            "Debe registrar la cancelación sin consumirla.");
        Require(handler.Calls == 2, "Registrar un fallo o cancelación no debe iniciar otra petición.");
    }

    private static async Task VerifyLogFailuresDoNotChangeTranslationAsync(string directory)
    {
        using var handler = new MockHandler(_ => Reply("""{"message":{"content":"Resultado válido"}}"""));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(httpClient: http);
        // An exclusive lock deliberately makes append fail without changing folder permissions.
        using (FileStream locked = new(LogPath(directory), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Require(await SendAsync(client, new { model = "mock" }, "locked-file") == "Resultado válido",
                "Un registro bloqueado no debe retrasar reintentos ni invalidar una traducción correcta.");
        }
        AppendMethod.Invoke(null, ["\0", "{}"]);
    }

    private static void VerifyConcurrentAppendAndRotation(string directory)
    {
        string parallelPath = Path.Combine(directory, "parallel.jsonl");
        Parallel.For(0, 40, index => AppendMethod.Invoke(null, [parallelPath, $"{{\"index\":{index}}}"]));
        string[] lines = File.ReadAllLines(parallelPath);
        Require(lines.Length == 40, "Las escrituras simultáneas no deben perder ni mezclar entradas JSONL.");
        int[] observed = lines.Select(line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("index").GetInt32();
        }).Order().ToArray();
        Require(observed.SequenceEqual(Enumerable.Range(0, 40)), "Cada petición debe conservar su entrada completa.");

        string rotationPath = Path.Combine(directory, "rotation.jsonl");
        File.WriteAllText(rotationPath, new string('x', 2 * 1024 * 1024), new UTF8Encoding(false));
        AppendMethod.Invoke(null, [rotationPath, "{\"after_rotation\":true}"]);
        Require(new FileInfo(rotationPath + ".1").Length == 2 * 1024 * 1024
                && File.ReadAllText(rotationPath).Trim() == "{\"after_rotation\":true}",
            "El registro debe rotar conservando un respaldo acotado y la nueva entrada.");
        File.WriteAllText(rotationPath, new string('y', 2 * 1024 * 1024), new UTF8Encoding(false));
        AppendMethod.Invoke(null, [rotationPath, "{\"second_rotation\":true}"]);
        Require(File.ReadAllText(rotationPath + ".1")[0] == 'y'
                && Directory.GetFiles(directory, "rotation.jsonl*").Length == 2,
            "Las rotaciones sucesivas no deben acumular archivos sin límite.");
    }

    private static void VerifyAuxiliaryServiceFacade(string directory)
    {
        const string response = """
            {"message":{"content":"Texto de rescate privado"},"load_duration":222,
             "prompt_eval_duration":333,"eval_duration":444,"prompt_eval_count":55,
             "eval_count":66,"total_duration":999}
            """;
        OllamaClient.RecordTranslateGemmaTiming(
            "final_recovery", "TranslateGemma:12b", 1, 1, 98, 765, response, 12.5, "completed");
        string entry = File.ReadLines(LogPath(directory)).Last();
        Require(!entry.Contains("Texto de rescate privado") && !entry.Contains("message"),
            "La fachada para servicios externos tampoco debe guardar respuestas o texto privado.");
        using (JsonDocument document = JsonDocument.Parse(entry))
        {
            JsonElement root = document.RootElement;
            Require(root.GetProperty("phase").GetString() == "final_recovery"
                    && root.GetProperty("target_count").GetInt32() == 1
                    && root.GetProperty("context_count").GetInt32() == 1
                    && root.GetProperty("context_characters").GetInt32() == 98
                    && root.GetProperty("prompt_characters").GetInt32() == 765
                    && root.GetProperty("load_duration").GetInt64() == 222
                    && root.GetProperty("prompt_eval_duration").GetInt64() == 333
                    && root.GetProperty("eval_duration").GetInt64() == 444
                    && root.GetProperty("prompt_eval_count").GetInt64() == 55
                    && root.GetProperty("eval_count").GetInt64() == 66
                    && root.GetProperty("total_duration").GetInt64() == 999
                    && root.GetProperty("wall_clock_ms").GetDouble() == 12.5,
                "La fachada debe conservar las métricas reales y las dimensiones de la petición auxiliar.");
        }

        int beforeForeignModel = File.ReadLines(LogPath(directory)).Count();
        foreach (string model in new[] { "qwen3.5:9b", "llama3:8b", "", "   " })
        {
            OllamaClient.RecordTranslateGemmaTiming("manual_review", model, 1, 1, 2, 3, response, 1, "completed");
        }
        Require(File.ReadLines(LogPath(directory)).Count() == beforeForeignModel,
            "La fachada no debe registrar modelos ajenos a TranslateGemma.");

        foreach (string? invalidResponse in new[] { null, "", "not-json", "{}", "[]" })
        {
            int before = File.ReadLines(LogPath(directory)).Count();
            OllamaClient.RecordTranslateGemmaTiming(
                "final_ocr_repair", "translategemma:12b", 1, 1, 98, 765, invalidResponse, 12.5, "failed");
            Require(File.ReadLines(LogPath(directory)).Count() == before + 1,
                "También deben contarse peticiones cuya respuesta no permite extraer métricas.");
            using JsonDocument document = ReadLastEntry(directory);
            Require(document.RootElement.GetProperty("outcome").GetString() == "failed"
                    && MetricProperties.All(property => document.RootElement.GetProperty(property).ValueKind == JsonValueKind.Null),
                "Una respuesta sin métricas debe declararlas desconocidas, nunca inventarlas.");
        }
        using FileStream locked = new(LogPath(directory), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        OllamaClient.RecordTranslateGemmaTiming(
            "manual_review", "translategemma:12b", 2, 6, 200, 900, response, 40, "completed");
    }

    private static string[] MetricProperties =>
        ["load_duration", "prompt_eval_duration", "eval_duration", "prompt_eval_count", "eval_count", "total_duration"];

    private static Task<string> SendAsync(OllamaClient client, object payload, string? phase, CancellationToken token = default)
    {
        object? metadata = phase is null
            ? null
            : Activator.CreateInstance(MetadataType, phase, "mock-metrics-model", 2, 4, 123, 456);
        return (Task<string>)SendMethod.Invoke(client, [payload, token, metadata])!;
    }

    private static string LogPath(string directory) => Path.Combine(directory, "tintaes-translategemma-timing.jsonl");
    private static JsonDocument ReadLastEntry(string directory) => JsonDocument.Parse(File.ReadLines(LogPath(directory)).Last());

    private static HttpResponseMessage Reply(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class MockHandler(Func<CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Func<CancellationToken, HttpResponseMessage> Response { get; set; } = response;
        public int Calls { get; private set; }
        public string? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.RequestUri?.AbsolutePath == "/api/chat" && request.Method == HttpMethod.Post,
                "Las métricas no deben consultar endpoints adicionales.");
            Calls++;
            LastRequest = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            return Response(token);
        }
    }
}
