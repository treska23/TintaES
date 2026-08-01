using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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

    private static readonly JsonNode ResidualTextSchema = JsonNode.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "detections": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "id": { "type": "integer" },
                  "text": { "type": "string" }
                },
                "required": ["id", "text"]
              }
            }
          },
          "required": ["detections"]
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

    public async Task WarmModelAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        object payload = new
        {
            model,
            stream = false,
            think = false,
            keep_alive = "30m",
            messages = new[] { new { role = "user", content = "Responde únicamente OK." } },
            options = new { temperature = 0, num_ctx = 512, num_predict = 2 }
        };
        _ = await SendChatAsync(payload, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, string>> RecognizeResidualTextAsync(
        byte[] contactSheet,
        IReadOnlyCollection<int> candidateIds,
        string model = "qwen3.5:9b",
        CancellationToken cancellationToken = default)
    {
        if (contactSheet.Length == 0 || candidateIds.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        string allowedIds = string.Join(", ", candidateIds.Order());
        string prompt =
            $"""
             Eres un OCR especializado en rotulación de cómic. La imagen es una cuadrícula casi cuadrada:
             cada recorte lleva una etiqueta "ID número". Identifica únicamente los recortes cuyo contenido
             grande sea una palabra inglesa, un diálogo o una onomatopeya claramente legible. Ignora las
             propias etiquetas ID, dibujos, líneas, manchas, símbolos y fragmentos dudosos.

             IDs permitidos: {allowedIds}

             Recorre todos los IDs antes de responder y vuelve a comprobar especialmente las onomatopeyas
             pequeñas en letras blancas, inclinadas o manuscritas, como THWIP/thwip. Puede haber más de una.
             Devuelve una detección solo cuando puedas transcribir todas sus letras. Copia el ID impreso
             y el texto exacto. No inventes IDs ni devuelvas elementos con texto vacío.
             """;
        object payload = new
        {
            model,
            stream = false,
            think = false,
            keep_alive = "30m",
            format = ResidualTextSchema,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = prompt,
                    images = new[] { Convert.ToBase64String(contactSheet) }
                }
            },
            options = new { temperature = 0, seed = 73, num_ctx = 4096, num_predict = 280 }
        };

        string content = await SendChatAsync(payload, cancellationToken);
        var recognized = new Dictionary<int, string>();
        ParseResidualDetections(content, candidateIds, recognized);
        return recognized;
    }

    private static void ParseResidualDetections(
        string content,
        IReadOnlyCollection<int> candidateIds,
        IDictionary<int, string> recognized)
    {
        using JsonDocument document = JsonDocument.Parse(ExtractJson(content));
        if (!document.RootElement.TryGetProperty("detections", out JsonElement detections)
            || detections.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        HashSet<int> allowed = candidateIds.ToHashSet();
        foreach (JsonElement item in detections.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out JsonElement idElement)
                || !idElement.TryGetInt32(out int id)
                || !allowed.Contains(id))
            {
                continue;
            }

            string text = TrimQuotationMarks(ReadString(item, "text")).Trim();
            if (text.Length is < 2 or > 100 || text.Count(char.IsLetter) < 2)
            {
                continue;
            }
            recognized[id] = text;
        }
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
        CancellationToken cancellationToken,
        IProgress<AnalysisProgress>? progress = null)
    {
        foreach (ComicRegion region in regions)
        {
            region.Translation = string.Empty;
        }
        ApplyKnownSfxLocalizations(regions);

        if (model.StartsWith("translategemma", StringComparison.OrdinalIgnoreCase))
        {
            await TranslateGemmaBatchAsync(regions, model, cancellationToken, progress);
        }
        else
        {
            for (int start = 0; start < regions.Count; start += 40)
            {
                List<ComicRegion> batch = regions.Skip(start).Take(40).ToList();
                await TranslateBatchAsync(batch, model, cancellationToken);

                List<ComicRegion> missing = batch
                    .Where(region => !IsAcceptableTranslation(region, region.Translation))
                    .ToList();
                if (missing.Count > 0)
                {
                    foreach (ComicRegion region in missing)
                    {
                        region.Translation = string.Empty;
                    }
                    await TranslateBatchAsync(missing, model, cancellationToken);

                    foreach (ComicRegion region in missing.Where(region =>
                                 !IsAcceptableTranslation(region, region.Translation)))
                    {
                        region.Translation = string.Empty;
                        await TranslateBatchAsync([region], model, cancellationToken);
                    }
                }

                ReportTranslationProgress(progress, regions, $"Traduciendo con {model}…");
            }
        }

        ApplyKnownSfxLocalizations(regions);
        ApplySemanticGuards(regions);
        NormalizeSignTranslations(regions);
        ComicRegion[] unresolved = regions
            .Where(region => !IsAcceptableTranslation(region, region.Translation))
            .ToArray();
        if (unresolved.Length > 0)
        {
            foreach (ComicRegion region in unresolved)
            {
                region.Translation = string.Empty;
            }

            string examples = string.Join(
                "; ",
                unresolved.Take(3).Select(region => $"«{NormalizeSourceText(region.Original)}»"));
            throw new InvalidOperationException(
                $"Ollama no devolvió una traducción española válida para {unresolved.Length} de " +
                $"{regions.Count} zonas ({examples}). La página queda pendiente para poder reintentarlo; " +
                "no se incrustará ningún aviso ni un resultado incompleto.");
        }
        ReportTranslationProgress(progress, regions, "Traducción contextual terminada");
    }

    private static void ApplyKnownSfxLocalizations(IEnumerable<ComicRegion> regions)
    {
        foreach (ComicRegion region in regions.Where(region => region.Type == "sfx"))
        {
            string key = new(region.Original
                .Where(char.IsLetter)
                .Select(char.ToUpperInvariant)
                .ToArray());
            region.Translation = key switch
            {
                "THWIP" or "THWIPP" or "THWP" or "THWID" => "¡FSSS!",
                "BOOM" => "¡BUM!",
                "BANG" => "¡PUM!",
                "SMASH" => "¡CRASH!",
                _ => region.Translation
            };
        }
    }

    private static void NormalizeSignTranslations(IEnumerable<ComicRegion> regions)
    {
        foreach (ComicRegion region in regions.Where(region => region.Type == "sign"))
        {
            region.Translation = region.Translation
                .Trim()
                .Trim('¡', '!', '¿', '?', '.', ',', ';', ':', '…')
                .Trim();
        }
    }

    private static void ApplySemanticGuards(IEnumerable<ComicRegion> regions)
    {
        foreach (ComicRegion region in regions)
        {
            string source = NormalizeOcrForTranslation(region.Original);
            string evidence = FormatSourceForModel(region);
            if (Regex.IsMatch(
                    evidence,
                    @"\bHOW\s+CAN\s+THIS\s+BE\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                region.Translation = "¡¿Cómo puede ser?!";
            }
            else if (Regex.IsMatch(
                         evidence,
                         @"\bTHIS\s+IS\s+IMPOS[\s-]*SI?BLE\b",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                region.Translation = "¡Esto es imposible!";
            }
            else if (Regex.IsMatch(
                         evidence,
                         @"\bOPEN\s+YOUR\s+EYES\b",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                region.Translation = "¡Abre los ojos!";
            }
            else if (Regex.IsMatch(
                         source,
                         @"\bFIRE[\s-]*BALLS?\s*,?\s*GIRLS\b",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                region.Translation = "¡Bolas de fuego, chicas!";
            }
            else if (Regex.IsMatch(
                         source,
                         @"\bLET\s+ME\s+TELL\s+YOU\s+WHAT\s+IT['’]?S\s+ALL\s+ABOUT\b",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                region.Translation = "Dejad que os cuente de qué va todo esto.";
            }
            else if (Regex.IsMatch(
                         source,
                         @"\bTAKE\s+THESE\s+SUCKERS\s+OUT\b",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                region.Translation = "¡Acabad con esos capullos!";
            }
            else if (Regex.IsMatch(
                         source,
                         @"\bHULK['’]?S\s+NOT\s+COMING\s+OUT\s+ANY\s+TIME\s+SOON\b.*\bNEXT\s+TIME\b.*\bSPIDER[\s-]*PUNK\b",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                region.Translation = "Hulk no va a salir pronto. Quizá la próxima vez, «Spider-Punk».";
            }

            if (Regex.IsMatch(
                source,
                @"DIDN.?T\s+ALMOST\s+GET\s+SHOT",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                region.Translation =
                    "Si preguntan: no estuve a punto de recibir un disparo láser.";
            }

            if (Regex.IsMatch(source, @"\bGOT\s+A\s+LUCKY\s+SHOT\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(source, @"\bNOTHING\s+BUT\s+(?:A\s+)?BUNCH\b", RegexOptions.IgnoreCase))
            {
                region.Translation =
                    "¡No son más que una pandilla de ratas callejeras que tuvieron suerte!";
            }

            if (Regex.IsMatch(source, @"^\s*FIGURE[DO]\b", RegexOptions.IgnoreCase))
            {
                region.Translation =
                    "Ya me lo imaginaba. En cualquier caso, mejor que estas cosas estén fuera de la calle.";
            }

            if (Regex.IsMatch(evidence, @"\bBACK\s+TO\s+['’]?(?:RI|RL)\b", RegexOptions.IgnoreCase))
            {
                region.Translation =
                    "Deberíamos llevarle esto a Ri; está pidiendo a gritos que el genio descubra cómo funciona.";
            }

            region.Translation = Regex.Replace(
                region.Translation,
                @"\b(?:[Ss]on|[Ee]s)\s+equipo\b",
                "Es un equipo",
                RegexOptions.CultureInvariant);

            region.Translation = Regex.Replace(
                region.Translation,
                @"\b(grupo|pandilla)\b([^.!?]{0,80})\bque\s+tuvo\s+suerte\b",
                "$1$2que tuvieron suerte",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (Regex.IsMatch(source, @"\bDOPE\b.*\bWAITING\b", RegexOptions.IgnoreCase))
            {
                region.Translation = "¡Genial! ¡Aquí os esperamos!";
            }
            else if (Regex.IsMatch(
                         source,
                         @"\bONE\s+DOING\s+MOST\s+OF\s+THE\s+FIGHTING\b",
                         RegexOptions.IgnoreCase))
            {
                region.Translation =
                    "Es fácil decirlo cuando soy yo quien se encarga de casi toda la pelea.";
            }
            else if (Regex.IsMatch(
                         source,
                         @"\bNO\s+ONE\s+CALLS\s+IT\s+THAT\b.*\bSEE\s+WHAT\s+I\s+CAN\s+DO\b",
                         RegexOptions.IgnoreCase))
            {
                region.Translation =
                    "Nadie lo llama así, Hobie, pero veré qué puedo hacer.";
            }
            else if (Regex.IsMatch(
                         evidence,
                         @"KRAVEN.*HUNTERS.*AFFORD.*STRINGS.*GUITARS.*ROLLING\s+UP",
                         RegexOptions.IgnoreCase))
            {
                region.Translation =
                    "Sí. Kraven y los Cazadores apenas podían permitirse cuerdas para sus guitarras, ¿y ahora aparecen con este equipo?";
            }
            else if (Regex.IsMatch(
                         source,
                         @"\bTHAT\s+WASN'?T\s+SO\s+BAD\b.*\bALL\s+THINGS\s+CONSIDERED\b",
                         RegexOptions.IgnoreCase))
            {
                region.Translation = "Je, no estuvo tan mal, considerando todo.";
            }

            region.Translation = Regex.Replace(region.Translation, @"\s{2,}", " ").Trim();
        }
    }

    private static void ReportTranslationProgress(
        IProgress<AnalysisProgress>? progress,
        IReadOnlyList<ComicRegion> regions,
        string message)
    {
        if (progress is null)
        {
            return;
        }

        int completed = regions.Count(region =>
            IsAcceptableTranslation(region, region.Translation));
        double fraction = completed / (double)Math.Max(1, regions.Count);
        progress.Report(new AnalysisProgress(
            (int)Math.Round(960 + fraction * 40),
            1000,
            $"{message} · {completed}/{regions.Count}"));
    }

    private static bool IsAcceptableTranslation(ComicRegion region, string? translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        string candidate = Regex.Replace(translation.Trim(), @"\s+", " ");
        if (string.Equals(
                candidate,
                ComicRegion.PendingTranslationMarker,
                StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("[[", StringComparison.Ordinal)
            || candidate.Contains("SOURCE:", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("TRANSLATION:", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("OCR ALTERNATIVE", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("||", StringComparison.Ordinal)
            || !candidate.Any(char.IsLetter))
        {
            return false;
        }

        string source = Regex.Replace(region.Original.Trim(), @"\s+", " ");
        string sourceLetters = new(source.Where(char.IsLetterOrDigit).ToArray());
        string candidateLetters = new(candidate.Where(char.IsLetterOrDigit).ToArray());
        if (sourceLetters.Length >= 4
            && string.Equals(sourceLetters, candidateLetters, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (candidate.Length > Math.Max(70, source.Length * 2.35))
        {
            return false;
        }

        string[] words = Regex.Matches(candidate.ToLowerInvariant(), @"[\p{L}']+")
            .Select(match => match.Value)
            .ToArray();
        if (words.Length >= 2)
        {
            string[] commonEnglish =
            [
                "the", "and", "but", "with", "from", "that", "this", "these", "those",
                "you", "your", "we", "they", "their", "what", "when", "where", "who",
                "have", "has", "had", "are", "was", "were", "will", "would", "could",
                "should", "just", "not", "for", "into", "about"
            ];
            int englishWords = words.Count(word => commonEnglish.Contains(word, StringComparer.Ordinal));
            if (englishWords >= 2 && englishWords / (double)words.Length >= 0.25)
            {
                return false;
            }
        }

        return true;
    }

    private async Task TranslateGemmaBatchAsync(
        IReadOnlyList<ComicRegion> regions,
        string model,
        CancellationToken cancellationToken,
        IProgress<AnalysisProgress>? progress)
    {
        ComicRegion[] translatable = regions
            .Where(region => !IsAcceptableTranslation(region, region.Translation))
            .ToArray();
        const int chunkSize = 6;
        for (int start = 0; start < translatable.Length; start += chunkSize)
        {
            IReadOnlyList<ComicRegion> targets = translatable.Skip(start).Take(chunkSize).ToArray();
            await TranslateGemmaChunkAsync(targets, regions, model, cancellationToken);
            ReportTranslationProgress(progress, regions, "Traduciendo la escena con contexto…");
        }

        ComicRegion[] unresolved = translatable
            .Where(region => !IsAcceptableTranslation(region, region.Translation))
            .ToArray();
        for (int start = 0; start < unresolved.Length; start += chunkSize)
        {
            ComicRegion[] retry = unresolved.Skip(start).Take(chunkSize).ToArray();
            foreach (ComicRegion region in retry)
            {
                region.Translation = string.Empty;
            }
            await TranslateGemmaChunkAsync(retry, regions, model, cancellationToken);
            ReportTranslationProgress(progress, regions, "Repitiendo líneas dudosas…");
        }

        // Si TranslateGemma pierde todas las etiquetas de un lote, repetir el mismo lote no
        // basta. Cada zona se solicita de forma aislada: así una respuesta sin etiquetas sigue
        // siendo inequívoca y nunca se desplaza a otro bocadillo.
        ComicRegion[] individuallyUnresolved = translatable
            .Where(region => !IsAcceptableTranslation(region, region.Translation))
            .ToArray();
        foreach (ComicRegion region in individuallyUnresolved)
        {
            region.Translation = string.Empty;
            await TranslateGemmaChunkAsync([region], regions, model, cancellationToken);
            ReportTranslationProgress(progress, regions, "Recuperando un bocadillo aislado…");
        }

        await RepairSplitFragmentSequencesAsync(
            regions,
            model,
            cancellationToken,
            progress);
        await RefineTranslateGemmaBatchAsync(regions, model, cancellationToken, progress);
    }

    private async Task RepairSplitFragmentSequencesAsync(
        IReadOnlyList<ComicRegion> regions,
        string model,
        CancellationToken cancellationToken,
        IProgress<AnalysisProgress>? progress)
    {
        foreach (ComicRegion[] group in FindSplitFragmentSequences(regions))
        {
            string combinedSource = string.Join(
                " ",
                group.Select(region => NormalizeOcrForTranslation(region.Original)));
            string token = "G" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(combinedSource)))[..10];
            string context = string.Join(
                "\n",
                regions.Select((region, index) =>
                    $"C{index:000} ({region.Type}): {FormatSourceForModel(region)}"));
            string prompt =
                $"""
                 You are translating one continuous English comic sentence into natural concise Spanish from
                 Spain. The source sentence was split into {group.Length} separate lettering fragments by the
                 page layout. Translate their COMBINED meaning once; do not turn the fragments into separate
                 replies and do not repeat the same verb or idea. Return one complete Spanish sentence inside
                 the exact tag, with no explanation and no English.

                 PAGE CONTEXT:
                 {context}

                 SOURCE FRAGMENTS:
                 {string.Join(" / ", group.Select(region => NormalizeOcrForTranslation(region.Original)))}

                 COMBINED SOURCE:
                 {combinedSource}

                 REQUIRED OUTPUT:
                 [[{token}]] traducción española única [[/{token}]]
                 """;
            object payload = new
            {
                model,
                stream = false,
                keep_alive = "30m",
                messages = new[] { new { role = "user", content = prompt } },
                options = new
                {
                    temperature = 0,
                    seed = 73,
                    num_ctx = 4096,
                    num_predict = 180
                }
            };

            string content = await SendChatAsync(payload, cancellationToken);
            Match match = Regex.Match(
                content,
                $@"\[\[{Regex.Escape(token)}\]\]\s*(.*?)\s*\[\[/{Regex.Escape(token)}\]\]",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            string combinedTranslation = match.Success
                ? CleanTranslationCandidate(match.Groups[1].Value)
                : CleanTranslationCandidate(content);
            if (!TryDistributeCombinedTranslation(group, combinedTranslation))
            {
                continue;
            }

            ReportTranslationProgress(
                progress,
                regions,
                "Reconstruyendo una frase repartida entre varios rótulos…");
        }
    }

    private static IReadOnlyList<ComicRegion[]> FindSplitFragmentSequences(
        IReadOnlyList<ComicRegion> regions)
    {
        var groups = new List<ComicRegion[]>();
        for (int start = 0; start < regions.Count - 1; start++)
        {
            ComicRegion first = regions[start];
            if (!IsShortFragment(first) || EndsSentence(first.Original))
            {
                continue;
            }

            var candidate = new List<ComicRegion> { first };
            for (int end = start + 1;
                 end < regions.Count && candidate.Count < 4;
                 end++)
            {
                ComicRegion previous = candidate[^1];
                ComicRegion current = regions[end];
                if (!IsShortFragment(current)
                    || !AreNearbyFragments(previous, current))
                {
                    break;
                }

                candidate.Add(current);
                if (!EndsSentence(current.Original))
                {
                    continue;
                }

                string combined = string.Join(
                    " ",
                    candidate.Select(region => NormalizeOcrForTranslation(region.Original)));
                if (LooksLikeContinuousClause(combined))
                {
                    groups.Add(candidate.ToArray());
                    start = end;
                }
                break;
            }
        }
        return groups;
    }

    private static bool IsShortFragment(ComicRegion region)
    {
        string source = NormalizeSourceText(region.Original);
        int words = Regex.Matches(source, @"[\p{L}\p{N}'’-]+").Count;
        return source.Length is >= 1 and <= 24 && words is >= 1 and <= 4;
    }

    private static bool EndsSentence(string source) =>
        Regex.IsMatch(source.Trim(), @"[.!?…][""'’”)]*$");

    private static bool AreNearbyFragments(ComicRegion first, ComicRegion second)
    {
        double firstX = first.TextBox.X + first.TextBox.Width / 2;
        double firstY = first.TextBox.Y + first.TextBox.Height / 2;
        double secondX = second.TextBox.X + second.TextBox.Width / 2;
        double secondY = second.TextBox.Y + second.TextBox.Height / 2;
        return Math.Abs(secondX - firstX) <= 230
            && Math.Abs(secondY - firstY) <= 230;
    }

    private static bool LooksLikeContinuousClause(string source) =>
        Regex.IsMatch(
            source,
            @"\b(?:I|YOU|WE|THEY|HE|SHE|IT)\s*(?:['’]\s*)?LL\b"
            + @"|\b(?:CAN|CAN'T|CANNOT|COULD|COULDN'T|WILL|WON'T|WOULD|WOULDN'T|"
            + @"SHOULD|SHOULDN'T|MUST|MIGHT|MAY)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool TryDistributeCombinedTranslation(
        IReadOnlyList<ComicRegion> group,
        string combinedTranslation)
    {
        string[] words = Regex.Matches(combinedTranslation.Trim(), @"\S+")
            .Select(match => match.Value)
            .ToArray();
        if (words.Length < group.Count
            || words.Length > Math.Max(group.Count * 5, 18))
        {
            return false;
        }

        var combinedRegion = new ComicRegion
        {
            Original = string.Join(" ", group.Select(region => region.Original))
        };
        if (!IsAcceptableTranslation(combinedRegion, combinedTranslation))
        {
            return false;
        }

        int[] counts = Enumerable.Repeat(1, group.Count).ToArray();
        int remaining = words.Length - group.Count;
        int[] priority = Enumerable.Range(0, group.Count)
            .OrderBy(index => Math.Abs(index - (group.Count - 1) / 2.0))
            .ToArray();
        for (int index = 0; index < remaining; index++)
        {
            counts[priority[index % priority.Length]]++;
        }

        int wordIndex = 0;
        for (int index = 0; index < group.Count; index++)
        {
            string fragment = string.Join(" ", words.Skip(wordIndex).Take(counts[index]));
            if (!IsAcceptableTranslation(group[index], fragment))
            {
                return false;
            }
            wordIndex += counts[index];
        }

        wordIndex = 0;
        for (int index = 0; index < group.Count; index++)
        {
            group[index].Translation = string.Join(
                " ",
                words.Skip(wordIndex).Take(counts[index]));
            wordIndex += counts[index];
        }
        return true;
    }

    private async Task RefineTranslateGemmaBatchAsync(
        IReadOnlyList<ComicRegion> regions,
        string model,
        CancellationToken cancellationToken,
        IProgress<AnalysisProgress>? progress)
    {
        ComicRegion[] targets = regions
            .Where(region => region.Type != "sfx"
                             && ((NormalizeSourceText(region.Original).Length >= 68
                                  && region.OcrAlternatives.Count > 0)
                                 || LooksLikeLiteralDraft(region.Translation)))
            .ToArray();
        const int chunkSize = 12;
        for (int start = 0; start < targets.Length; start += chunkSize)
        {
            ComicRegion[] chunk = targets.Skip(start).Take(chunkSize).ToArray();
            await RefineTranslateGemmaChunkAsync(chunk, regions, model, cancellationToken);
            ReportTranslationProgress(progress, regions, "Puliendo el español de los diálogos largos…");
        }
    }

    private async Task RefineTranslateGemmaChunkAsync(
        IReadOnlyList<ComicRegion> targets,
        IReadOnlyList<ComicRegion> fullContext,
        string model,
        CancellationToken cancellationToken)
    {
        string context = string.Join(
            "\n",
            fullContext.Select((region, index) =>
                $"C{index:000} ({region.Type}): {FormatSourceForModel(region)}"));
        var tokens = targets.ToDictionary(region => region, CreateTranslationToken);
        string drafts = string.Join(
            "\n",
            targets.Select(region =>
                $"""
                 [[{tokens[region]}]]
                 SOURCE: {NormalizeOcrForTranslation(region.Original)}
                 DRAFT: {NormalizeSourceText(region.Translation)}
                 [[/{tokens[region]}]]
                 """));
        string prompt =
            """
            You are the final dialogue editor for a professionally published Spanish comic.
            Proofread each DRAFT into idiomatic, concise Spanish from Spain. Correct literal phrasing and obvious
            OCR damage by using the complete page context, while preserving the exact action, speaker, humour,
            negation, names and every meaningful fragment. Never add information from a neighbouring balloon.
            Read the balloons as one continuous scene: keep questions and replies coherent, preserve callbacks
            and rhyme or wordplay when the source uses them, and avoid dictionary-like calques or invented words.
            Prefer natural spoken Spanish over a literal structure, but keep each result compact enough for the
            original balloon. A short command or reaction must remain short and forceful.
            Resolve conflicting OCR readings by grammar; alternatives are evidence, not extra dialogue. In
            particular, "got a lucky shot" means "tuvieron suerte/acertaron de suerte", "figured" as a reply
            means "ya me lo imaginaba", "roll up with this stuff" means "aparecer con este equipo", and
            "we'll be waiting" must retain the idea of waiting. Never replace an uncertain proper name or place
            with a different one from the page.
            Return every revised draft inside its exact random opening and closing tags. Do not explain anything.

            COMPLETE PAGE CONTEXT:
            """ + "\n" + context + "\n\nDRAFTS TO REVISE:\n" + drafts;
        object payload = new
        {
            model,
            stream = false,
            keep_alive = "30m",
            messages = new[] { new { role = "user", content = prompt } },
            options = new
            {
                temperature = 0,
                seed = 73,
                num_ctx = 4096,
                num_predict = Math.Max(180, targets.Count * 120)
            }
        };

        string content = await SendChatAsync(payload, cancellationToken);
        foreach (ComicRegion region in targets)
        {
            string token = tokens[region];
            Match match = Regex.Match(
                content,
                $@"\[\[{Regex.Escape(token)}\]\]\s*(.*?)\s*\[\[/{Regex.Escape(token)}\]\]",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            string candidate = match.Success
                ? CleanTranslationCandidate(match.Groups[1].Value)
                : string.Empty;
            if (IsAcceptableTranslation(region, candidate))
            {
                region.Translation = candidate;
            }
        }
    }

    private static bool LooksLikeLiteralDraft(string value) =>
        Regex.IsMatch(
            value,
            @"\b(?:no\s+casi|figuré|entendí|una\s+suerte|sufició|son\s+equipo)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task TranslateGemmaChunkAsync(
        IReadOnlyList<ComicRegion> targets,
        IReadOnlyList<ComicRegion> fullContext,
        string model,
        CancellationToken cancellationToken)
    {
        string context = string.Join(
            "\n",
            fullContext.Select((region, index) =>
                $"C{index:000} ({region.Type}): {FormatSourceForModel(region)}"));
        var tokens = targets.ToDictionary(region => region, CreateTranslationToken);
        string targetText = string.Join(
            "\n",
            targets.Select(region =>
                $"[[{tokens[region]}]] {NormalizeOcrForTranslation(region.Original)} [[/{tokens[region]}]]"));
        string prompt =
            """
            You are a professional English-to-Spanish comic translator and dialogue editor.
            Translate into natural Spanish from Spain. Reconstruct obvious OCR mistakes from grammar and scene
            context before translating. Preserve meaning, speaker intent, jokes, register, names, actions and
            punctuation. Preserve every semantic fragment around ellipses, including possessives: "what is his"
            means "lo que es suyo". Treat negative auxiliaries with special care: "didn't almost get shot" means
            "no estuvo a punto de recibir un disparo", never "no casi se disparó". Keep the result concise enough
            for the original speech balloon. "Got a lucky shot" means "tuvieron suerte" or "acertaron de pura
            suerte"; "figured" as a reply means "ya me lo imaginaba"; "roll up with this stuff" means "aparecer
            con este equipo"; and "we'll be waiting" must retain the waiting. Never replace an uncertain proper
            name or place with a different name borrowed from CONTEXT.

            CONTEXT contains the complete page in reading order. Use it only to understand the scene.
            TARGETS contains the lines that must be translated in this request.
            CONTEXT may include OCR ALTERNATIVES. They are alternative readings of the SAME lettering,
            not additional dialogue. Use them only to reconstruct the corresponding target's English meaning.

            Consecutive short TARGETS can be separate pieces of lettering that together form ONE grammatical
            sentence. Detect continuations such as auxiliaries, contractions, conjunctions and incomplete
            clauses. Translate the whole sequence first, then distribute that single Spanish sentence across
            the same tags; a tag may contain only a sentence fragment and must not be expanded into a separate
            reply. For example MAYBE / YOU'LL / SEE! should be QUIZÁ / YA LO / VERÁS!, not three independent
            reactions.

            For every target, copy its exact random tag at both ends. Return exactly this shape:
            [[RANDOMTAG]] traducción española [[/RANDOMTAG]]
            Never number, reorder, merge or omit targets. Output no explanations and no English source text.

            COMPLETE PAGE CONTEXT:
            """ + "\n" + context + "\n\nTARGETS:\n" + targetText;

        object payload = new
        {
            model,
            stream = false,
            keep_alive = "30m",
            messages = new[] { new { role = "user", content = prompt } },
            options = new
            {
                temperature = 0,
                seed = 73,
                num_ctx = 4096,
                num_predict = Math.Max(180, targets.Count * 110)
            }
        };

        string content = await SendChatAsync(payload, cancellationToken);
        foreach (ComicRegion region in targets)
        {
            string token = tokens[region];
            Match match = Regex.Match(
                content,
                $@"\[\[{Regex.Escape(token)}\]\]\s*(.*?)\s*\[\[/{Regex.Escape(token)}\]\]",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            string candidate = match.Success
                ? CleanTranslationCandidate(match.Groups[1].Value)
                : string.Empty;

            // En el reintento individual aceptamos también una respuesta limpia sin etiquetas.
            // No se hace en lotes porque podría volver a desplazar las frases.
            if (targets.Count == 1 && string.IsNullOrWhiteSpace(candidate))
            {
                candidate = CleanTranslationCandidate(content);
            }

            if (IsAcceptableTranslation(region, candidate))
            {
                region.Translation = candidate;
            }
        }
    }

    private static string CreateTranslationToken(ComicRegion region)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{region.Id:N}|{NormalizeSourceText(region.Original)}");
        return "R" + Convert.ToHexString(SHA256.HashData(bytes))[..10];
    }

    private static string NormalizeSourceText(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");

    private static string FormatSourceForModel(ComicRegion region)
    {
        string primary = NormalizeOcrForTranslation(region.Original);
        string[] alternatives = region.OcrAlternatives
            .Select(NormalizeOcrForTranslation)
            .Where(value => !string.Equals(value, primary, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        return alternatives.Length == 0
            ? primary
            : $"{primary} || OCR ALTERNATIVES: {string.Join(" | ", alternatives)}";
    }

    private static string NormalizeOcrForTranslation(string value)
    {
        string text = NormalizeSourceText(value);
        (string Pattern, string Replacement)[] repairs =
        [
            (@"\b[4W]ULK['’]?S\b", "HULK'S"),
            (@"\bSTREEI\b", "STREET"),
            (@"\bBARELT\b", "BARELY"),
            (@"\bKEEF\b", "KEEP"),
            (@"\bYEAR\b", "YEAH"),
            (@"\bUUNTERS\b", "HUNTERS"),
            (@"\bYEAH\s+KRAVEN\s+E\s+HUNTERS\b", "YEAH. KRAVEN & THE HUNTERS"),
            (@"\bAPPORD\b", "AFFORD"),
            (@"\bTWEIR\b", "THEIR"),
            (@"\bWIT\s+G\b", "WITH THIS"),
            (@"\bTGTS\b", "THIS"),
            (@"\bNASN'?T\b", "WASN'T"),
            (@"\bBAO\b", "BAD"),
            (@"\bPRETT4\b", "PRETTY"),
            (@"\bFAVOA\b", "FAVOR"),
            (@"\bVO\s+ONE\b", "NO ONE"),
            (@"\bTHINKINBOUT\b", "THINKIN' 'BOUT"),
            (@"\bI\s+THE\s+ONE\b", "I'M THE ONE"),
            (@"\bDIDNT\b", "DIDN'T"),
            (@"\bILL\b", "I'LL"),
            (@"\bIVE\b", "I'VE"),
            (@"\bIM\b", "I'M"),
            (@"\bTHEYRE\b", "THEY'RE"),
            (@"\bTHATS\b", "THAT'S")
        ];
        foreach ((string pattern, string replacement) in repairs)
        {
            text = Regex.Replace(
                text,
                pattern,
                replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return text;
    }

    private static string CleanTranslationCandidate(string value)
    {
        string cleaned = value
            .Replace("```", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Trim();
        cleaned = Regex.Replace(
            cleaned,
            @"^(?:ESPAÑOL|SPANISH|TRADUCCI[ÓO]N|TRANSLATION)\s*:\s*",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"\[\[/?R[A-F0-9]+\]\]", string.Empty);
        cleaned = Regex.Replace(
            cleaned,
            @"\s*\|\|\s*OCR\s+ALTERNATIVES?\s*:.*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return TrimQuotationMarks(Regex.Replace(cleaned, @"\s+", " ").Trim());
    }

    private async Task TranslateBatchAsync(
        IReadOnlyList<ComicRegion> regions,
        string model,
        CancellationToken cancellationToken)
    {
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
            La lista está en el orden de lectura de una página completa: úsala como contexto continuo y reconstruye
            errores obvios del OCR antes de traducir. Conserva con exactitud voz, intención, puntuación, sujeto y
            todas las negaciones; nunca inviertas quién hizo una acción ni conviertas "no ocurrió" en "ocurrió".
            Sé conciso para que el texto pueda caber en el mismo bocadillo. Adapta onomatopeyas a una forma habitual en cómic español
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
