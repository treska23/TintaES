using System.Text.Json;

namespace TintaES.Core;

/// <summary>
/// Formato de intercambio de traducciones de TintaES. Está pensado para guardarse como
/// texto UTF-8, abrirse en cualquier editor y poder entregarse a una IA para revisar el
/// castellano sin perder la relación estable con cada zona del cómic.
/// </summary>
public static class TranslationExchange
{
    public const string FormatName = "TintaES Translation Exchange";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string Serialize(TranslationExchangeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, SerializerOptions);
    }

    /// <summary>
    /// Extrae únicamente regionId + translation. El resto del archivo se considera contexto
    /// informativo y jamás se usa para mover zonas, cambiar geometría ni alterar el OCR.
    /// También tolera que una IA envuelva el JSON entre ```json ... ``` o añada una frase antes.
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> ReadTranslations(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("El guion de traducción está vacío.");
        }

        string payload = ExtractJsonPayload(text);
        using JsonDocument document = JsonDocument.Parse(
            payload,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

        var translations = new Dictionary<Guid, string>();
        CollectTranslations(document.RootElement, translations);
        if (translations.Count == 0)
        {
            throw new InvalidDataException(
                "No se encontraron traducciones con un regionId válido. " +
                "El archivo debe conservar los identificadores que exporta TintaES.");
        }

        return translations;
    }

    private static string ExtractJsonPayload(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidDataException("El archivo no contiene un guion JSON de TintaES válido.");
        }

        return text[start..(end + 1)];
    }

    private static void CollectTranslations(
        JsonElement element,
        IDictionary<Guid, string> translations)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("regionId", out JsonElement idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && Guid.TryParse(idElement.GetString(), out Guid id)
                    && element.TryGetProperty("translation", out JsonElement translationElement)
                    && translationElement.ValueKind == JsonValueKind.String)
                {
                    translations[id] = translationElement.GetString() ?? string.Empty;
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    CollectTranslations(property.Value, translations);
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    CollectTranslations(item, translations);
                }
                break;
        }
    }
}

public sealed class TranslationExchangeDocument
{
    public string Format { get; init; } = TranslationExchange.FormatName;
    public int Version { get; init; } = TranslationExchange.CurrentVersion;
    public string[] Instructions { get; init; } =
    [
        "Mejora únicamente los valores de \"translation\" al castellano de España.",
        "No cambies regionId, page, order, original, bubbleId, geometry ni ningún otro metadato.",
        "Usa el resto de textos de la página, el tipo de zona y el bocadillo como contexto para que el diálogo sea natural y coherente.",
        "Devuelve el JSON completo conservando todos los regionId para que TintaES pueda importarlo de nuevo."
    ];
    public string CoordinateSystem { get; init; } = "normalized-0-1000";
    public string TranslationLanguage { get; init; } = "es-ES";
    public string ComicTitle { get; init; } = "comic";
    public int PageCount { get; init; }
    public List<TranslationExchangePage> Pages { get; init; } = [];
}

public sealed class TranslationExchangePage
{
    public int Page { get; init; }
    public string Name { get; init; } = string.Empty;
    public string SourceLanguage { get; init; } = "en";
    public List<TranslationExchangeRegion> Regions { get; init; } = [];
}

public sealed class TranslationExchangeRegion
{
    public Guid RegionId { get; init; }
    public int Order { get; init; }
    public string Type { get; init; } = "dialogue";
    public bool Enabled { get; init; } = true;
    public string Original { get; init; } = string.Empty;
    public string Translation { get; init; } = string.Empty;
    public string? BubbleId { get; init; }
    public TranslationExchangeRect TextBox { get; init; } = new();
    public TranslationExchangeRect? BubbleBox { get; init; }
    public TranslationExchangeRect RenderBox { get; init; } = new();
    public double Rotation { get; init; }
    public bool Vertical { get; init; }
    public TranslationExchangeStyle Style { get; init; } = new();
}

public sealed class TranslationExchangeRect
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public sealed class TranslationExchangeStyle
{
    public string FontCategory { get; init; } = "comic";
    public int OriginalLineCount { get; init; }
    public bool Uppercase { get; init; }
    public bool Italic { get; init; }
    public string Alignment { get; init; } = "center";
}
