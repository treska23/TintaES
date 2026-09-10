using System.Text.RegularExpressions;

namespace TintaES.Core;

/// <summary>
/// Conservador por diseño: decide si una lectura OCR contiene suficiente evidencia textual para
/// permitir que un traductor generativo actúe sobre ella. El contexto puede desambiguar una frase
/// legible, pero nunca debe convertir ruido visual en diálogo inventado.
/// </summary>
public static class TranslationSourceGuard
{
    public static bool IsReliable(ComicRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!region.IsEnabled || region.Confidence < 0.05)
        {
            return false;
        }

        string type = region.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        return EnumerateRealOcrReadings(region)
            .Any(reading => IsReliableText(reading, type));
    }

    public static bool IsReliableText(string? value, string? type = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string compact = Regex.Replace(value.Trim(), @"\s+", " ");
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

    private static IEnumerable<string> EnumerateRealOcrReadings(ComicRegion region)
    {
        yield return region.Original;
        foreach (string alternative in region.StoredOcrAlternatives
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(4))
        {
            yield return alternative;
        }
    }

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
