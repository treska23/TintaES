using System.Text.RegularExpressions;

namespace TintaES.Core;

/// <summary>
/// Conservador por diseño: decide si una lectura OCR contiene suficiente evidencia textual para
/// permitir que un traductor generativo actúe sobre ella. El contexto puede desambiguar una frase
/// legible, pero nunca debe convertir ruido visual en diálogo inventado.
/// </summary>
public static class TranslationSourceGuard
{
    public static bool IsReliable(ComicRegion region) =>
        TryGetReliableReading(region, out _);

    /// <summary>
    /// Devuelve una lectura OCR real y suficientemente fiable. Si el OCR principal es ruido pero
    /// una segunda pasada leyó texto coherente, se utiliza esa segunda lectura; nunca se usa
    /// contexto documental ni texto procedente del traductor.
    /// </summary>
    public static bool TryGetReliableReading(ComicRegion region, out string reading)
    {
        ArgumentNullException.ThrowIfNull(region);
        reading = string.Empty;
        if (!region.IsEnabled || region.Confidence < 0.05)
        {
            return false;
        }

        string type = region.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        string primary = Compact(region.Original);
        if (IsReliableText(primary, type))
        {
            reading = primary;
            return true;
        }

        reading = region.StoredOcrAlternatives
            .Where(value => IsReliableText(value, type))
            .Select(Compact)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(ReadingEvidenceScore)
            .FirstOrDefault() ?? string.Empty;
        return reading.Length > 0;
    }

    /// <summary>
    /// Prepara la evidencia que verá el traductor. Elimina alternativas que también sean ruido y,
    /// cuando sea necesario, asciende una lectura OCR secundaria fiable a Original. De esta forma
    /// TARGET nunca contiene OOOOO mientras CONTEXT contiene la frase que el modelo debería adivinar.
    /// </summary>
    public static bool NormalizeEvidence(ComicRegion region)
    {
        if (!TryGetReliableReading(region, out string selected))
        {
            region.Translation = string.Empty;
            return false;
        }

        string type = region.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        string previousPrimary = Compact(region.Original);
        string[] reliable = new[] { previousPrimary }
            .Concat(region.StoredOcrAlternatives)
            .Select(Compact)
            .Where(value => IsReliableText(value, type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(ReadingEvidenceScore)
            .ToArray();

        if (!string.Equals(previousPrimary, selected, StringComparison.Ordinal))
        {
            region.Original = selected;
        }

        region.StoredOcrAlternatives = reliable
            .Where(value => !string.Equals(value, selected, StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToArray();
        return true;
    }

    public static bool IsReliableText(string? value, string? type = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string compact = Compact(value);
        string letters = new(compact
            .Where(char.IsLetter)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (letters.Length < 2)
        {
            return false;
        }

        bool sfx = string.Equals(type, "sfx", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(type, "sound_effect", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(type, "sound-effect", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(type, "onomatopoeia", StringComparison.OrdinalIgnoreCase);

        // Un efecto sonoro puede ser deliberadamente repetitivo (AAAA, BZZZZ, HAHAHA). Para
        // diálogo normal, en cambio, una cadena formada casi solo por O/C/A es el patrón típico
        // que producen tramas, puntos de semitono y bordes confundidos con letras.
        if (sfx)
        {
            return letters.Length <= 80;
        }

        if (LooksLikeLaughterOrVocalisation(compact))
        {
            return true;
        }

        int distinct = letters.Distinct().Count();
        if (letters.Length >= 4 && distinct == 1)
        {
            return false;
        }

        int longestRun = LongestIdenticalRun(letters);
        if (longestRun >= 7)
        {
            return false;
        }

        if (letters.Length >= 10)
        {
            int dominantCount = letters
                .GroupBy(character => character)
                .Max(group => group.Count());
            double dominantRatio = dominantCount / (double)letters.Length;
            double entropy = CharacterEntropy(letters);

            if (distinct <= 2 && dominantRatio >= 0.62)
            {
                return false;
            }
            if (letters.Length >= 14 && distinct <= 4 && dominantRatio >= 0.54)
            {
                return false;
            }
            if (letters.Length >= 14 && entropy < 1.55)
            {
                return false;
            }
        }

        string[] words = Regex.Matches(compact, @"[\p{L}]+(?:['’\-][\p{L}]+)*")
            .Select(match => match.Value)
            .ToArray();
        if (words.Length == 0)
        {
            return false;
        }

        if (letters.Length >= 12 && words.All(LooksLikeRepetitiveToken))
        {
            return false;
        }

        return true;
    }

    private static int ReadingEvidenceScore(string value)
    {
        string compact = Compact(value);
        int letters = compact.Count(char.IsLetter);
        int words = Regex.Matches(compact, @"[\p{L}]+(?:['’\-][\p{L}]+)*").Count;
        int diversity = compact.Where(char.IsLetter)
            .Select(char.ToUpperInvariant)
            .Distinct()
            .Count();
        return Math.Min(letters, 180) + words * 12 + diversity * 3;
    }

    private static string Compact(string? value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");

    private static bool LooksLikeLaughterOrVocalisation(string text)
    {
        string compact = Regex.Replace(text.ToUpperInvariant(), @"[^A-ZÁÉÍÓÚÜÑ]", string.Empty);
        return Regex.IsMatch(
            compact,
            @"^(?:(?:HA|HE|HI|HO|HU|JA|JE|JI|JO|JU)){2,}$",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeRepetitiveToken(string token)
    {
        string letters = new(token
            .Where(char.IsLetter)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (letters.Length < 4)
        {
            return false;
        }

        int distinct = letters.Distinct().Count();
        int dominant = letters
            .GroupBy(character => character)
            .Max(group => group.Count());
        return distinct <= 2 && dominant / (double)letters.Length >= 0.70;
    }

    private static int LongestIdenticalRun(string value)
    {
        int best = 0;
        int current = 0;
        char previous = '\0';
        foreach (char character in value)
        {
            if (character == previous)
            {
                current++;
            }
            else
            {
                previous = character;
                current = 1;
            }
            best = Math.Max(best, current);
        }
        return best;
    }

    private static double CharacterEntropy(string value)
    {
        double total = value.Length;
        double entropy = 0;
        foreach (IGrouping<char, char> group in value.GroupBy(character => character))
        {
            double probability = group.Count() / total;
            entropy -= probability * Math.Log2(probability);
        }
        return entropy;
    }
}
