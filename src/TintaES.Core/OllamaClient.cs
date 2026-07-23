using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TintaES.Core;

public sealed class OllamaClient : IDisposable
{
    private static readonly JsonNode DetectionSchema = JsonNode.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "source_language": { "type": "string" },
            "regions": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "original": { "type": "string" },
                  "type": { "type": "string", "enum": ["dialogue", "thought", "narration", "caption", "sfx", "sign", "other"] },
                  "confidence": { "type": "number" },
                  "text_box": { "$ref": "#/$defs/box" },
                  "render_box": { "$ref": "#/$defs/box" },
                  "rotation": { "type": "number" },
                  "vertical": { "type": "boolean" },
                  "style": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "font_category": { "type": "string", "enum": ["comic", "handwritten", "sans", "condensed", "serif", "display", "monospace"] },
                      "font_family": { "type": ["string", "null"] },
                      "font_weight": { "type": "integer" },
                      "font_size": { "type": "number" },
                      "font_width_ratio": { "type": "number" },
                      "line_height_ratio": { "type": "number" },
                      "line_count": { "type": "integer" },
                      "italic": { "type": "boolean" },
                      "uppercase": { "type": "boolean" },
                      "text_color": { "type": "string" },
                      "outline_color": { "type": ["string", "null"] },
                      "outline_width": { "type": "number" },
                      "alignment": { "type": "string", "enum": ["left", "center", "right"] },
                      "background_color": { "type": ["string", "null"] },
                      "shadow": { "type": "boolean" }
                    },
                    "required": ["font_category", "font_family", "font_weight", "font_size", "font_width_ratio", "line_height_ratio", "line_count", "italic", "uppercase", "text_color", "outline_color", "outline_width", "alignment", "background_color", "shadow"]
                  }
                },
                "required": ["original", "type", "confidence", "text_box", "render_box", "rotation", "vertical", "style"]
              }
            }
          },
          "required": ["source_language", "regions"],
          "$defs": {
            "box": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "x": { "type": "number" },
                "y": { "type": "number" },
                "width": { "type": "number" },
                "height": { "type": "number" }
              },
              "required": ["x", "y", "width", "height"]
            }
          }
        }
        """)!;

    private static readonly JsonNode TranslationSchema = JsonNode.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "translations": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "id": { "type": "string" },
                  "translation": { "type": "string" }
                },
                "required": ["id", "translation"]
              }
            }
          },
          "required": ["translations"]
        }
        """)!;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public OllamaClient(string baseUrl = "http://127.0.0.1:11434", HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(75);
    }

    public async Task<IReadOnlyList<OllamaModel>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync("api/tags", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var models = new List<OllamaModel>();
        if (!document.RootElement.TryGetProperty("models", out JsonElement array))
        {
            return models;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            string name = ReadString(item, "name", "model");
            long size = item.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long value)
                ? value
                : 0;
            if (!string.IsNullOrWhiteSpace(name))
            {
                models.Add(new OllamaModel(name, size));
            }
        }
        return models;
    }

    public async Task<ComicAnalysis> AnalyzePageAsync(
        IReadOnlyList<ComicImageTile> tiles,
        string model,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (tiles.Count == 0)
        {
            throw new ArgumentException("La página no contiene fragmentos analizables.", nameof(tiles));
        }

        var detected = new List<ComicRegion>();
        string sourceLanguage = "desconocido";
        int totalSteps = tiles.Count + 1;

        for (int index = 0; index < tiles.Count; index++)
        {
            ComicImageTile tile = tiles[index];
            progress?.Report(new AnalysisProgress(index, totalSteps, $"Leyendo fragmento {index + 1} de {tiles.Count}…"));

            TileAnalysis analysis = await AnalyzeTileAsync(tile, model, cancellationToken);
            detected.AddRange(analysis.Regions);
            if (sourceLanguage == "desconocido" && !string.IsNullOrWhiteSpace(analysis.SourceLanguage))
            {
                sourceLanguage = analysis.SourceLanguage;
            }

            progress?.Report(new AnalysisProgress(
                index + 1,
                totalSteps,
                $"Fragmento {index + 1} terminado · {detected.Count} lecturas provisionales"));
        }

        IReadOnlyList<ComicRegion> regions = RegionMerger.Merge(detected);
        if (regions.Count == 0)
        {
            progress?.Report(new AnalysisProgress(totalSteps, totalSteps, "No se encontró texto legible."));
            return new ComicAnalysis(sourceLanguage, regions);
        }

        progress?.Report(new AnalysisProgress(tiles.Count, totalSteps, $"Traduciendo {regions.Count} textos al español…"));
        await TranslateRegionsAsync(regions, model, cancellationToken);
        progress?.Report(new AnalysisProgress(totalSteps, totalSteps, $"Listo · {regions.Count} textos traducidos"));
        return new ComicAnalysis(sourceLanguage, regions);
    }

    private async Task<TileAnalysis> AnalyzeTileAsync(
        ComicImageTile tile,
        string model,
        CancellationToken cancellationToken)
    {
        string prompt =
            """
            Eres un rotulista profesional de cómics realizando OCR y reconstrucción tipográfica, NO una descripción de imágenes.
            Examina este fragmento de una página y localiza absolutamente todo el texto legible: bocadillos,
            pensamientos, cartuchos, letreros y onomatopeyas. Devuelve una región distinta por cada bloque.

            Reglas críticas:
            - Transcribe el texto original exactamente, sin comillas añadidas y sin traducirlo todavía.
            - No describas personajes ni dibujos. Si no hay letras legibles, devuelve regions vacío.
            - Las coordenadas van de 0 a 1000, con origen arriba a la izquierda de ESTE fragmento.
            - text_box rodea MUY AJUSTADAMENTE solo las letras impresas que deben borrarse.
            - render_box es la zona interior segura para volver a rotular sin tocar el borde del bocadillo o cartucho.
            - Nunca uses como render_box un panel completo ni una gran zona de la ilustración.
            - Para texto sobre dibujo y efectos sonoros, render_box debe ser parecido a text_box.
            - Separa textos cercanos que pertenezcan a bocadillos diferentes.
            - confidence va de 0 a 1. No inventes texto ilegible.
            - rotation debe reproducir el ángulo real del texto original.
            - Estima el aspecto de la rotulación original, no un estilo genérico.
            - font_family: si reconoces una familia tipográfica concreta, devuelve su nombre real (por ejemplo Anime Ace, Wild Words, CCMeanwhile, Arial Narrow, Impact). Si no puedes reconocerla con suficiente confianza, usa null; no inventes nombres.
            - font_size: tamaño visual aproximado de la fuente en la escala vertical 0..1000 de ESTE fragmento. Debe representar el tamaño de las letras, no la altura total del bocadillo.
            - font_width_ratio: proporción aproximada de anchura de los glifos; 1.0 normal, menor de 1 condensada, mayor de 1 expandida.
            - line_height_ratio: distancia entre líneas dividida por font_size. Normalmente está entre 0.9 y 1.4.
            - line_count: número de líneas visuales del texto original.
            - Conserva peso, cursiva, mayúsculas, colores, contorno, alineación, fondo y sombra observados.
            Responde únicamente con el JSON solicitado.
            """;

        object payload = new
        {
            model,
            stream = false,
            think = false,
            keep_alive = "15m",
            format = DetectionSchema,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = prompt,
                    images = new[] { Convert.ToBase64String(tile.ImageBytes) }
                }
            },
            options = new
            {
                temperature = 0,
                seed = 42,
                num_ctx = 8192
            }
        };

        string content = await SendChatAsync(payload, cancellationToken);
        try
        {
            return ParseTileAnalysis(content, tile);
        }
        catch (JsonException)
        {
            content = await SendChatAsync(payload, cancellationToken);
            return ParseTileAnalysis(content, tile);
        }
    }

    public async Task TranslateRegionsAsync(
        IReadOnlyList<ComicRegion> regions,
        string model,
        CancellationToken cancellationToken)
    {
        for (int start = 0; start < regions.Count; start += 40)
        {
            List<ComicRegion> batch = regions.Skip(start).Take(40).ToList();
            await TranslateBatchAsync(batch, model, cancellationToken);

            List<ComicRegion> missing = batch
                .Where(region => string.IsNullOrWhiteSpace(region.Translation))
                .ToList();
            if (missing.Count > 0 && missing.Count < batch.Count)
            {
                await TranslateBatchAsync(missing, model, cancellationToken);
            }

            foreach (ComicRegion region in batch.Where(region => string.IsNullOrWhiteSpace(region.Translation)))
            {
                region.Translation = region.Original;
            }
        }
    }

    private async Task TranslateBatchAsync(
        IReadOnlyList<ComicRegion> regions,
        string model,
        CancellationToken cancellationToken)
    {
        if (model.StartsWith("translategemma", StringComparison.OrdinalIgnoreCase))
        {
            await TranslateGemmaBatchAsync(regions, model, cancellationToken);
            return;
        }

        var input = regions.Select(region => new
        {
            id = region.Id.ToString("N"),
            text = region.Original,
            type = region.Type
        });
        string items = JsonSerializer.Serialize(input);
        string prompt =
            """
            Traduce todos los elementos de la lista a español natural de España.
            Conserva la voz, intención, puntuación y tratamiento de cada personaje. Sé conciso para que el texto
            pueda caber en el mismo bocadillo. Adapta onomatopeyas a una forma habitual en cómic español
            (por ejemplo BOOM puede ser ¡BUM! y SMASH puede ser ¡CRASH! según el contexto).
            Devuelve exactamente una traducción no vacía para cada id, sin comillas añadidas ni comentarios.

            ELEMENTOS:
            """ + items;

        object payload = new
        {
            model,
            stream = false,
            think = false,
            keep_alive = "15m",
            format = TranslationSchema,
            messages = new[] { new { role = "user", content = prompt } },
            options = new { temperature = 0.1, seed = 73, num_ctx = 4096, num_predict = 2048 }
        };

        string content = await SendChatAsync(payload, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(ExtractJson(content));
        if (!document.RootElement.TryGetProperty("translations", out JsonElement translations))
        {
            return;
        }

        Dictionary<string, ComicRegion> byId = regions.ToDictionary(region => region.Id.ToString("N"));
        foreach (JsonElement item in translations.EnumerateArray())
        {
            string id = ReadString(item, "id");
            string translation = TrimQuotationMarks(ReadString(item, "translation"));
            if (byId.TryGetValue(id, out ComicRegion? region) && !string.IsNullOrWhiteSpace(translation))
            {
                region.Translation = translation;
            }
        }
    }

    private async Task TranslateGemmaBatchAsync(
        IReadOnlyList<ComicRegion> regions,
        string model,
        CancellationToken cancellationToken)
    {
        var blocks = regions.Select((region, index) =>
            $"{index:000}||| {System.Text.RegularExpressions.Regex.Replace(region.Original.Trim(), @"\s+", " ")}");
        string text = string.Join("\n", blocks);
        string prompt =
            """
            You are a professional English (en) to Spanish (es) translator. Your goal is to accurately convey
            the meaning, voice and nuances of comic dialogue while using natural Spanish from Spain.
            Produce only the Spanish translation, without explanations or commentary.
            Every input line starts with a three-digit number and |||. Copy that prefix exactly at the start of
            the translated line. The lines appear in page reading order and belong to the same scene: use every
            preceding and following line as context for pronouns, jokes, tone and speaker intent, while still returning
            exactly one translation for each numbered line. Keep each translation concise enough for its speech bubble.
            The English comes from OCR and may contain an obvious substituted or missing character. Silently restore
            the intended English word from grammar and scene context before translating; never transliterate an OCR typo.
            Translate common exclamations idiomatically: for example, VICTORY! is ¡VICTORIA!, never ¡VICTORIO!.
            Preserve actions exactly: hitting someone with an object is not giving that object to them.
            There are two blank lines before the text to translate.


            """ + text;

        object payload = new
        {
            model,
            stream = false,
            keep_alive = "15m",
            messages = new[] { new { role = "user", content = prompt } },
            options = new
            {
                temperature = 0,
                num_ctx = 4096,
                num_predict = 1200
            }
        };

        string content = await SendChatAsync(payload, cancellationToken);
        System.Text.RegularExpressions.MatchCollection matches = System.Text.RegularExpressions.Regex.Matches(
            content,
            @"^(\d{3})\|\|\|\s*(.+?)\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (!int.TryParse(match.Groups[1].Value, out int index)
                || index < 0
                || index >= regions.Count)
            {
                continue;
            }

            string translation = TrimQuotationMarks(match.Groups[2].Value.Replace("\r", string.Empty).Trim());
            if (!string.IsNullOrWhiteSpace(translation))
            {
                regions[index].Translation = translation;
            }
        }

        if (matches.Count == 0)
        {
            string[] translations = System.Text.RegularExpressions.Regex
                .Split(content.Trim(), @"\r?\n\s*\r?\n")
                .Select(value => TrimQuotationMarks(value.Trim()))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            if (translations.Length == regions.Count)
            {
                for (int index = 0; index < regions.Count; index++)
                {
                    regions[index].Translation = translations[index];
                }
            }
        }
    }

    private async Task<string> SendChatAsync(object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(payload, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("content", out JsonElement content))
        {
            throw new InvalidOperationException("Ollama no devolvió contenido analizable.");
        }
        return content.GetString() ?? throw new InvalidOperationException("Ollama devolvió una respuesta vacía.");
    }

    private static TileAnalysis ParseTileAnalysis(string content, ComicImageTile tile)
    {
        using JsonDocument document = JsonDocument.Parse(ExtractJson(content));
        string language = ReadString(document.RootElement, "source_language");
        var regions = new List<ComicRegion>();
        if (!document.RootElement.TryGetProperty("regions", out JsonElement array))
        {
            return new TileAnalysis(language, regions);
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            string original = TrimQuotationMarks(ReadString(item, "original"));
            if (string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            NormalizedRect localText = ReadBox(item, "text_box");
            NormalizedRect localRender = ReadBox(item, "render_box");
            var region = new ComicRegion
            {
                Original = original,
                Type = NormalizeType(ReadString(item, "type")),
                Confidence = ReadDouble(item, "confidence", 0.7),
                TextBox = ToPageBox(localText, tile),
                RenderBox = ToPageBox(localRender, tile),
                Rotation = ReadDouble(item, "rotation", 0),
                Vertical = ReadBool(item, "vertical"),
                Style = ReadStyle(item, tile)
            };
            regions.Add(RegionMerger.Sanitize(region));
        }

        return new TileAnalysis(language, regions);
    }

    private static ComicTextStyle ReadStyle(JsonElement region, ComicImageTile tile)
    {
        if (!region.TryGetProperty("style", out JsonElement style))
        {
            return new ComicTextStyle();
        }

        double localFontSize = ReadDouble(style, "font_size", 0);
        double pageFontSize = localFontSize <= 0
            ? 0
            : localFontSize * tile.Height / Math.Max(1d, tile.PageHeight);

        return new ComicTextStyle
        {
            FontCategory = ReadString(style, "font_category") is { Length: > 0 } category ? category : "comic",
            FontFamily = ReadNullableString(style, "font_family"),
            FontWeight = (int)ReadDouble(style, "font_weight", 700),
            FontSize = pageFontSize,
            FontWidthRatio = ReadDouble(style, "font_width_ratio", 1),
            LineHeightRatio = ReadDouble(style, "line_height_ratio", 1.08),
            OriginalLineCount = (int)Math.Round(ReadDouble(style, "line_count", 0)),
            Italic = ReadBool(style, "italic"),
            Uppercase = ReadBool(style, "uppercase"),
            TextColor = ReadString(style, "text_color") is { Length: > 0 } textColor ? textColor : "#111111",
            OutlineColor = ReadNullableString(style, "outline_color"),
            OutlineWidth = ReadDouble(style, "outline_width", 0),
            Alignment = ReadString(style, "alignment") is { Length: > 0 } alignment ? alignment : "center",
            BackgroundColor = ReadNullableString(style, "background_color"),
            Shadow = ReadBool(style, "shadow")
        };
    }

    private static NormalizedRect ToPageBox(NormalizedRect local, ComicImageTile tile)
    {
        return new NormalizedRect(
            (tile.X + local.X / 1000 * tile.Width) / tile.PageWidth * 1000,
            (tile.Y + local.Y / 1000 * tile.Height) / tile.PageHeight * 1000,
            local.Width / 1000 * tile.Width / tile.PageWidth * 1000,
            local.Height / 1000 * tile.Height / tile.PageHeight * 1000).Clamp();
    }

    private static NormalizedRect ReadBox(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement box))
        {
            return new NormalizedRect(100, 100, 200, 80);
        }
        return new NormalizedRect(
            ReadDouble(box, "x", 100),
            ReadDouble(box, "y", 100),
            ReadDouble(box, "width", 200),
            ReadDouble(box, "height", 80)).Clamp();
    }

    private static string NormalizeType(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "dialogue" or "thought" or "narration" or "caption" or "sfx" or "sign" => value.ToLowerInvariant(),
            _ => "other"
        };
    }

    private static string ExtractJson(string content)
    {
        string trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = trimmed.IndexOf('\n');
            int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }

        int objectStart = trimmed.IndexOf('{');
        int objectEnd = trimmed.LastIndexOf('}');
        return objectStart >= 0 && objectEnd > objectStart
            ? trimmed[objectStart..(objectEnd + 1)]
            : trimmed;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string message = body;
        try
        {
            using JsonDocument error = JsonDocument.Parse(body);
            message = ReadString(error.RootElement, "error");
        }
        catch (JsonException)
        {
            // Conserva la respuesta original si Ollama no devolvió JSON.
        }
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"Ollama respondió con HTTP {(int)response.StatusCode}."
            : message);
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static string? ReadNullableString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double ReadDouble(JsonElement element, string name, double fallback)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double number)
            ? number
            : fallback;
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();
    }

    private static string TrimQuotationMarks(string value)
    {
        return (value ?? string.Empty).Trim().Trim('"', '\'', '“', '”', '‘', '’').Trim();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed record TileAnalysis(string SourceLanguage, IReadOnlyList<ComicRegion> Regions);
}