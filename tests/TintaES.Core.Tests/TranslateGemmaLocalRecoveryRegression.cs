using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TintaES.Core;

internal static class TranslateGemmaLocalRecoveryRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        VerifyClusteredRecoveryAsync().GetAwaiter().GetResult();
        VerifyIndividualRecoveryAsync().GetAwaiter().GetResult();
        VerifyDistantRecoveryAsync().GetAwaiter().GetResult();
        VerifyDistantUnlabelledRecoveryAsync().GetAwaiter().GetResult();
        VerifyCompletePageFlowAsync().GetAwaiter().GetResult();
        VerifyCompleteDraftsAreNotRetriedAsync().GetAwaiter().GetResult();
        VerifyQuestionNameAndNegationAsync().GetAwaiter().GetResult();
        Console.WriteLine("OK  Recuperación local de TranslateGemma: 7 escenarios sin alterar OCR ni borradores válidos");
    }

    private static async Task VerifyClusteredRecoveryAsync()
    {
        ComicRegion[] regions = CreatePage();
        int[] doubtful = [20, 21, 22];
        foreach (int index in doubtful)
        {
            regions[index].Translation = string.Empty;
        }
        string[] previous = regions.Select(region => region.Translation).ToArray();
        string[] sources = regions.Select(region => region.Original).ToArray();
        Guid[] ids = regions.Select(region => region.Id).ToArray();
        int[] orders = regions.Select(region => region.Order).ToArray();
        NormalizedRect[] boxes = regions.Select(region => region.TextBox).ToArray();
        string[][] alternatives = regions.Select(region => region.StoredOcrAlternatives.ToArray()).ToArray();
        var handler = new RecoveryHandler(regions, (_, position) => ExpectedTranslation(position),
            addUnrequestedTranslation: true);
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(httpClient: http);

        // Deliberately unordered and including a valid draft: recovery must only target doubtful lettering.
        await client.RecoverTranslateGemmaRegionsAsync(
            [regions[22], regions[19], regions[20], regions[21]], regions, "translategemma:12b");

        Require(handler.Calls.Count == 1, "Tres dudas cercanas deben recuperarse juntas en una llamada local.");
        AssertTargets(handler.Calls[0], doubtful);
        AssertContext(handler.Calls[0], Enumerable.Range(18, 7));
        Require(handler.Calls[0].Prompt.Contains(regions[20].StoredOcrAlternatives.Single(), StringComparison.Ordinal)
                && handler.Calls[0].Prompt.Contains(regions[18].StoredOcrAlternatives.Single(), StringComparison.Ordinal),
            "El contexto local debe conservar las lecturas OCR alternativas propias y de sus vecinos.");
        Require(handler.Calls[0].Prompt.Contains(previous[18], StringComparison.Ordinal)
                && handler.Calls[0].Prompt.Contains(previous[24], StringComparison.Ordinal),
            "Los borradores válidos de los vecinos deben seguir disponibles como orientación.");
        AssertGuards(handler.Calls[0]);

        for (int index = 0; index < regions.Length; index++)
        {
            Require(regions[index].Original == sources[index]
                    && regions[index].Id == ids[index]
                    && regions[index].Order == orders[index]
                    && regions[index].TextBox == boxes[index]
                    && regions[index].StoredOcrAlternatives.SequenceEqual(alternatives[index]),
                "Recuperar una traducción no debe modificar la detección ni la evidencia OCR de ninguna zona.");
            Require(regions[index].Translation == (doubtful.Contains(index) ? ExpectedTranslation(index) : previous[index]),
                "Las 48 traducciones válidas deben conservarse, aunque la respuesta incluya una clave ajena.");
        }
    }

    private static async Task VerifyIndividualRecoveryAsync()
    {
        ComicRegion[] regions = CreatePage();
        int[] doubtful = [20, 21, 22];
        foreach (int index in doubtful)
        {
            regions[index].Translation = string.Empty;
        }
        var handler = new RecoveryHandler(regions,
            (call, position) => call == 0 && position == 21 ? null : ExpectedTranslation(position));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(httpClient: http);

        await client.RecoverTranslateGemmaRegionsAsync(
            doubtful.Select(index => regions[index]).ToArray(), regions, "translategemma:12b");

        Require(handler.Calls.Count == 2,
            "Si dos dudas se resuelven juntas, solo la restante debe llegar a la recuperación individual.");
        AssertTargets(handler.Calls[0], doubtful);
        AssertContext(handler.Calls[0], Enumerable.Range(18, 7));
        AssertTargets(handler.Calls[1], [21]);
        AssertContext(handler.Calls[1], [20, 21, 22]);
        Require(handler.Calls[1].Prompt.Contains(ExpectedTranslation(20), StringComparison.Ordinal)
                && handler.Calls[1].Prompt.Contains(ExpectedTranslation(22), StringComparison.Ordinal),
            "El reintento individual debe recibir los vecinos ya corregidos, sin arrastrar la página completa.");
        Require(handler.Calls[1].Tokens.Single() == handler.Calls[0].Tokens[1],
            "La etiqueta de una zona debe mantenerse estable entre el grupo y el reintento individual.");
        Require(regions.All(region => region.HasRenderableTranslation), "Todas las zonas deben conservar una traducción utilizable.");
    }

    private static async Task VerifyDistantRecoveryAsync()
    {
        ComicRegion[] regions = CreatePage();
        int[] doubtful = [3, 25, 47];
        foreach (int index in doubtful)
        {
            regions[index].Translation = string.Empty;
        }
        var handler = new RecoveryHandler(regions, (_, position) => ExpectedTranslation(position));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(httpClient: http);

        await client.RecoverTranslateGemmaRegionsAsync(
            doubtful.Reverse().Select(index => regions[index]).ToArray(), regions, "translategemma:12b");

        Require(handler.Calls.Count == 3, "Las dudas lejanas deben formar grupos locales independientes.");
        for (int index = 0; index < doubtful.Length; index++)
        {
            AssertTargets(handler.Calls[index], [doubtful[index]]);
            AssertContext(handler.Calls[index], Enumerable.Range(doubtful[index] - 2, 5));
        }
        Require(regions.Select(region => region.Translation).SequenceEqual(Enumerable.Range(0, 51).Select(ExpectedTranslation)),
            "Separar los grupos no debe intercambiar resultados ni alterar las otras 48 traducciones.");
    }

    private static async Task VerifyCompletePageFlowAsync()
    {
        ComicRegion[] regions = CreatePage();
        int[] doubtful = [20, 21, 22];
        var handler = new RecoveryHandler(regions,
            (call, position) => call < 3 && doubtful.Contains(position) ? null : ExpectedTranslation(position));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(httpClient: http);

        await client.TranslateRegionsAsync(regions, "translategemma:12b", CancellationToken.None);

        Require(handler.Calls.Count == 4,
            "La página de 51 zonas debe mantener sus tres lotes iniciales y recuperar las tres dudas en una llamada local.");
        AssertTargets(handler.Calls[0], Enumerable.Range(0, 24));
        AssertTargets(handler.Calls[1], Enumerable.Range(24, 24));
        AssertTargets(handler.Calls[2], Enumerable.Range(48, 3));
        for (int index = 0; index < 3; index++)
        {
            AssertContext(handler.Calls[index], Enumerable.Range(0, 51));
            AssertGuards(handler.Calls[index]);
        }
        AssertTargets(handler.Calls[3], doubtful);
        AssertContext(handler.Calls[3], Enumerable.Range(18, 7));
        Require(handler.Calls[3].Tokens.SequenceEqual(handler.Calls[0].Tokens.Skip(20).Take(3)),
            "La recuperación integrada debe usar exactamente los mismos identificadores que el lote inicial.");
        Require(regions.Select(region => region.Translation).SequenceEqual(Enumerable.Range(0, 51).Select(ExpectedTranslation)),
            "El flujo público completo debe mantener la asociación de cada traducción con su bocadillo.");
    }

    private static async Task VerifyDistantUnlabelledRecoveryAsync()
    {
        ComicRegion[] regions = CreatePage();
        int[] doubtful = [3, 25, 47];
        foreach (int index in doubtful)
        {
            regions[index].Translation = string.Empty;
        }
        var handler = new RecoveryHandler(regions, (_, position) => ExpectedTranslation(position),
            unlabelledResponse: true);
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(httpClient: http);
        await client.RecoverTranslateGemmaRegionsAsync(
            doubtful.Select(index => regions[index]).ToArray(), regions, "translategemma:12b");

        // Previously one shared retry could not assign unlabelled replies to three targets,
        // requiring that failed batch plus three individual requests. Local single-target groups
        // can accept each unambiguous reply immediately, without removing the recovery fallback.
        Require(handler.Calls.Count == 3 && handler.Calls.All(call => call.Targets.Length == 1),
            "Tres dudas lejanas con respuesta sin etiquetas deben resolverse en tres llamadas inequívocas, sin el lote fallido adicional.");
        Require(regions.Select(region => region.Translation).SequenceEqual(Enumerable.Range(0, 51).Select(ExpectedTranslation)),
            "Aceptar respuestas individuales sin etiquetas debe conservar la asociación de las 51 zonas.");
    }

    private static async Task VerifyCompleteDraftsAreNotRetriedAsync()
    {
        ComicRegion[] regions = CreatePage();
        string[] previous = regions.Select(region => region.Translation).ToArray();
        var handler = new RecoveryHandler(regions, (_, position) => ExpectedTranslation(position));
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(httpClient: http);
        await client.RecoverTranslateGemmaRegionsAsync(regions, regions, "translategemma:12b");
        await client.RecoverTranslateGemmaRegionsAsync([], regions, "translategemma:12b");
        Require(handler.Calls.Count == 0 && previous.SequenceEqual(regions.Select(region => region.Translation)),
            "Si no quedan dudas, la recuperación no debe hacer llamadas ni borrar traducciones válidas.");
    }

    private static async Task VerifyQuestionNameAndNegationAsync()
    {
        ComicRegion[] regions = CreatePage();
        regions[21].Original = "WOULDN'T YOU LIKE TO ASK KRAVEN ABOUT THIS PERSONALLY?";
        regions[21].StoredOcrAlternatives = [];
        regions[21].Translation = string.Empty;
        const string complete = "¿No te gustaría preguntárselo personalmente a Kraven?";
        var handler = new RecoveryHandler(regions, (call, _) => call == 0 ? "Kraven." : complete);
        using var http = new HttpClient(handler);
        using var client = new OllamaClient(httpClient: http);
        await client.RecoverTranslateGemmaRegionsAsync([regions[21]], regions, "translategemma:12b");

        Require(handler.Calls.Count == 2 && regions[21].Translation == complete,
            "Una respuesta mutilada debe llegar al reintento y conservar pregunta, negación y nombre propio.");
        foreach (Call call in handler.Calls)
        {
            AssertGuards(call);
            Require(call.Prompt.Contains(regions[21].Original, StringComparison.Ordinal),
                "Acortar el contexto no debe mutilar el texto original de la zona dudosa.");
        }
    }

    private static ComicRegion[] CreatePage() => Enumerable.Range(0, 51)
        .Select(index => new ComicRegion
        {
            Id = new Guid(index + 1, 0, 0, new byte[8]),
            Order = index + 1,
            Original = $"WHERE IS ROOM {index:00}?",
            Translation = ExpectedTranslation(index),
            Type = "dialogue",
            TextBox = new NormalizedRect(20, index * 300, 160, 80),
            OcrAlternatives = index is >= 18 and <= 24 ? [$"WHERE IS THE ROOM {index:00}?"] : []
        })
        .ToArray();

    private static string ExpectedTranslation(int position) => $"¿Dónde está la sala {position:00}?";

    private static void AssertTargets(Call call, IEnumerable<int> positions)
    {
        Require(call.Targets.SequenceEqual(positions),
            "Solo las zonas solicitadas deben aparecer como objetivos, conservando el orden de lectura.");
        Require(call.StrictSchema, "El esquema debe exigir exactamente las etiquetas de los objetivos, sin incluir vecinos.");
    }

    private static void AssertContext(Call call, IEnumerable<int> positions) => Require(
        call.Context.SequenceEqual(positions),
        "El contexto debe contener exactamente los vecinos previstos y conservar el orden de lectura.");

    private static void AssertGuards(Call call)
    {
        Require(call.Prompt.Contains("Spain", StringComparison.OrdinalIgnoreCase)
                && call.Prompt.Contains("name", StringComparison.OrdinalIgnoreCase)
                && call.Prompt.Contains("negat", StringComparison.OrdinalIgnoreCase),
            "Los prompts deben conservar las instrucciones de español de España, nombres propios y negación.");
        Require(call.Model == "translategemma:12b" && call.ContextSize == 4096
                && call.Temperature == 0 && call.Seed == 73,
            "La recuperación debe mantener el modelo y los parámetros de calidad de TranslateGemma.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Regresión de recuperación local de TranslateGemma: {message}");
        }
    }

    private sealed record Call(string Prompt, int[] Targets, int[] Context, string[] Tokens,
        bool StrictSchema, string Model, int ContextSize, double Temperature, int Seed);

    private sealed class RecoveryHandler(
        IReadOnlyList<ComicRegion> regions,
        Func<int, int, string?> response,
        bool addUnrequestedTranslation = false,
        bool unlabelledResponse = false) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using JsonDocument document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            JsonElement root = document.RootElement;
            string prompt = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
            var regionByToken = regions.Select((region, index) => (Token: Token(region), Index: index))
                .ToDictionary(value => value.Token, value => value.Index);
            string[] tokens = Regex.Matches(prompt, @"\[\[(R[A-F0-9]+)\]\]\s*(.*?)\s*\[\[/\1\]\]",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant)
                .Select(match => match.Groups[1].Value).ToArray();
            int[] targets = tokens.Select(token => regionByToken[token]).ToArray();
            int[] context = Regex.Matches(prompt, @"^C\d+\s*\([^\r\n)]*\):\s*WHERE IS ROOM (\d+)\?",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant)
                .Select(match => int.Parse(match.Groups[1].Value)).ToArray();
            JsonElement schema = root.GetProperty("format").GetProperty("properties").GetProperty("translations");
            string[] required = schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()!).ToArray();
            string[] properties = schema.GetProperty("properties").EnumerateObject().Select(value => value.Name).ToArray();
            bool strictSchema = schema.GetProperty("additionalProperties").ValueKind == JsonValueKind.False
                                && required.Order().SequenceEqual(tokens.Order())
                                && properties.Order().SequenceEqual(tokens.Order());
            JsonElement options = root.GetProperty("options");
            int callIndex = Calls.Count;
            Calls.Add(new Call(prompt, targets, context, tokens, strictSchema,
                root.GetProperty("model").GetString()!, options.GetProperty("num_ctx").GetInt32(),
                options.GetProperty("temperature").GetDouble(), options.GetProperty("seed").GetInt32()));
            var translations = new Dictionary<string, string>();
            for (int index = 0; index < targets.Length; index++)
            {
                string? translated = response(callIndex, targets[index]);
                if (translated is not null)
                {
                    translations[tokens[index]] = translated;
                }
            }
            if (addUnrequestedTranslation)
            {
                translations[Token(regions[19])] = "Una traducción ajena que debe ignorarse.";
            }
            string content = unlabelledResponse
                ? string.Join("\n", translations.Values)
                : JsonSerializer.Serialize(new { translations });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { message = new { content } }),
                    Encoding.UTF8, "application/json")
            };
        }

        private static string Token(ComicRegion region)
        {
            string source = Regex.Replace(region.Original.Trim(), @"\s+", " ");
            byte[] bytes = Encoding.UTF8.GetBytes($"{region.Id:N}|{source}");
            return "R" + Convert.ToHexString(SHA256.HashData(bytes))[..10];
        }
    }
}
