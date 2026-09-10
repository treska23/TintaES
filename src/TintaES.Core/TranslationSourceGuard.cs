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

        string[] words = Regex.Matches(compact, @"[\p{L}]+(?:['’\-][\p{L}]+)*")
            .Select(match => match.Value)
            .ToArray();
        if (words.Length == 0)
        {
            return false;
        }

        // Gritos reales como NOOO!, OOOH! o HA HA HA son válidos. Se reconocen antes de las
        // reglas de repetición para no confundir una vocalización deliberada con ruido OCR.
        if (LooksLikeLaughterOrVocalisation(compact)
            || LooksLikeKnownVocalisationSequence(words))
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

        int dominantCount = letters
            .GroupBy(character => character)
            .Max(group => group.Count());
        double dominantRatio = dominantCount / (double)letters.Length;

        // Caso real que provocó la regresión: "nooo jooo ooo". No contiene una frase inglesa;
        // son varios pseudo-tokens dominados por la misma vocal. El modelo lo convertía en una
        // reacción plausible aprovechando el contexto de la página. Dos o más tokens de diálogo
        // con tan poca diversidad no pueden llegar jamás a TranslateGemma.
        if (words.Length >= 2
            && letters.Length >= 7
            && distinct <= 3
            && dominantRatio >= 0.70)
        {
            return false;
        }

        if (words.Length >= 3)
        {
            int repetitiveTokens = words.Count(LooksLikeRepetitiveToken);
            if (repetitiveTokens >= words.Length - 1
                && !ContainsPlainLanguageAnchor(words))
            {
                return false;
            }
        }

        if (letters.Length >= 10)
        {
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

    private static bool LooksLikeKnownVocalisationSequence(IReadOnlyList<string> words)
    {
        if (words.Count == 0 || words.Count > 5)
        {
            return false;
        }

        string[] roots = words
            .Select(CollapseRepeatedLetters)
            .Where(value => value.Length > 0)
            .ToArray();
        if (roots.Length != words.Count)
        {
            return false;
        }

        string[] known =
        [
            "NO", "OH", "AH", "HA", "HE", "HI", "HO", "HU",
            "JA", "JE", "JI", "JO", "JU", "HM", "MM", "BO"
        ];
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
               && known.Contains(roots[0], StringComparer.OrdinalIgnoreCase);
    }

    private static string CollapseRepeatedLetters(string value)
    {
        var result = new List<char>();
        char previous = '\0';
        foreach (char character in value.Where(char.IsLetter).Select(char.ToUpperInvariant))
        {
            if (character == previous)
            {
                continue;
            }
            result.Add(character);
            previous = character;
        }
        return new string(result.ToArray());
    }

    private static bool ContainsPlainLanguageAnchor(IEnumerable<string> words)
    {
        string[] anchors =
        [
            "A", "AN", "AND", "ARE", "AS", "AT", "BE", "BUT", "BY", "CAN", "COME",
            "DID", "DO", "DON'T", "FOR", "FROM", "GO", "HAD", "HAS", "HAVE", "HE", "HER",
            "HERE", "HIM", "HIS", "HOW", "I", "IF", "IN", "IS", "IT", "ITS", "LET", "LOOK",
            "ME", "MY", "NO", "NOT", "NOW", "OF", "OH", "ON", "OR", "OUR", "OUT", "SHE",
            "SO", "STOP", "THAT", "THE", "THEIR", "THEM", "THERE", "THEY", "THIS", "TO", "UP",
            "US", "WAIT", "WAS", "WE", "WERE", "WHAT", "WHEN", "WHERE", "WHO", "WHY", "WILL",
            "WITH", "WOULD", "YES", "YOU", "YOUR"
        ];

        return words
            .Select(word => word.Trim('’', '\'' ).ToUpperInvariant())
            .Any(word => anchors.Contains(word, StringComparer.Ordinal));
    }

    private static bool LooksLikeRepetitiveToken(string token)
    {
        string letters = new(token
            .Where(char.IsLetter)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (letters.Length < 3)
        {
            return false;
        }

        int distinct = letters.Distinct().Count();
        int dominant = letters
            .GroupBy(character => character)
            .Max(group => group.Count());
        return distinct <= 2 && dominant / (double)letters.Length >= 0.66;
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
