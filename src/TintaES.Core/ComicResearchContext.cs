using System.Text;

namespace TintaES.Core;

/// <summary>
/// Ficha documental compacta de una obra. No sustituye al OCR ni añade diálogo: sirve
/// únicamente para resolver identidades, relaciones, terminología y registro durante la
/// traducción. Las fuentes se conservan para que el contexto pueda revisarse.
/// </summary>
public sealed class ComicResearchContext
{
    public string IdentityKey { get; set; } = string.Empty;
    public string ComicTitle { get; set; } = string.Empty;
    public string ResearchQuery { get; set; } = string.Empty;
    public DateTimeOffset ResearchedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Findings { get; set; } = [];
    public List<ComicResearchSource> Sources { get; set; } = [];

    public bool HasUsefulContent =>
        !string.IsNullOrWhiteSpace(ComicTitle)
        && (Findings.Any(value => !string.IsNullOrWhiteSpace(value)) || Sources.Count > 0);

    public string ToTranslationPrompt(int maximumCharacters = 2200)
    {
        if (!HasUsefulContent)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("CONTEXTO DOCUMENTADO DE LA OBRA (no es diálogo ni OCR): ");
        builder.Append(ComicTitle.Trim());
        builder.Append(". Úsalo solo para aclarar nombres, relaciones, continuidad, terminología y registro; ");
        builder.Append("si contradice el texto visible del bocadillo, manda siempre el bocadillo. ");

        foreach (string finding in Findings
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(Compact)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (builder.Length + finding.Length + 3 > maximumCharacters)
            {
                break;
            }
            builder.Append(" • ");
            builder.Append(finding);
        }

        string result = builder.ToString();
        return result.Length <= maximumCharacters
            ? result
            : result[..maximumCharacters].TrimEnd() + "…";
    }

    public string ToDisplayText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(ComicTitle);
        builder.AppendLine($"Investigado: {ResearchedAtUtc.ToLocalTime():g}");
        builder.AppendLine();
        foreach (string finding in Findings.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            builder.Append("• ");
            builder.AppendLine(Compact(finding));
        }

        if (Sources.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Fuentes:");
            foreach (ComicResearchSource source in Sources.Take(10))
            {
                builder.Append("• ");
                builder.Append(source.Title);
                builder.Append(" — ");
                builder.AppendLine(source.Url);
            }
        }
        return builder.ToString().Trim();
    }

    private static string Compact(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}

public sealed class ComicResearchSource
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
}

/// <summary>
/// Contexto de la operación de traducción actual. AsyncLocal evita que una tarea auxiliar o
/// una segunda ventana herede accidentalmente la ficha de otro documento.
/// </summary>
public static class ComicResearchAmbient
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string? CurrentPrompt
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
