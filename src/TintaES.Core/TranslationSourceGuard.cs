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
        if (IsReliableRegionReading(region, primary, type))
        {
            reading = primary;
            return true;
        }

        reading = region.StoredOcrAlternatives
            .Select(Compact)
            .Where(value => IsReliableRegionReading(region, value, type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(ReadingEvidenceScore)
            .FirstOrDefault() ?? string.Empty;
        return reading.Length > 0;
    }

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
            .Where(value => IsReliableRegionReading(region, value, type))
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

        int distinct = letters.Distinct().Count();
        int longestRun = LongestIdenticalRun(letters);

        if (IsSfx(type))
        {
            if (letters.Length > 80)
            {
                return false;
            }

            if (letters.Length is >= 3 and <= 8
                && distinct <= 2
                && longestRun >= 3
                && !IsKnownSfxRoot(CollapseRepeatedLetters(compact)))
            {
                return false;
            }
            return true;
        }

        string[] words = ExtractWords(compact);
        if (words.Length == 0)
        {
            return false;
        }

        if (LooksLikeLaughterOrVocalisation(compact)
            || LooksLikeKnownVocalisationSequence(words))
        {
            return true;
        }

        if (letters.Length >= 4 && distinct == 1)
        {
            return false;
        }
        if (longestRun >= 7)
        {
            return false;
        }

        int dominantCount = letters
            .GroupBy(character => character)
            .Max(group => group.Count());
        double dominantRatio = dominantCount / (double)letters.Length;

        if (words.Length == 1
            && letters.Length is >= 3 and <= 8
            && distinct <= 2
            && longestRun >= 3
            && !IsKnownExpressiveRoot(CollapseRepeatedLetters(words[0])))
        {
            return false;
        }

        // También rechazamos mezclas cortas como "muuu no" o "jooo sir": una palabra que
        // consiste casi por completo en una letra repetida no se convierte en diálogo solo porque
        // al lado haya una palabra inglesa válida. Si la repetición es intencional (NOOO, YEEES,
        // SOOO, etc.) su raíz está explícitamente permitida.
        if (words.Length <= 3 && words.Any(IsSuspiciousPseudoToken))
        {
            return false;
        }

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

    private static bool IsReliableRegionReading(ComicRegion region, string value, string type)
    {
        if (!IsReliableText(value, type))
        {
            return false;
        }
        if (region.IsManual || IsSfx(type) || type is "sign" or "caption" or "narration")
        {
            return true;
        }

        string[] words = ExtractWords(value);
        int letterCount = value.Count(char.IsLetter);

        if (words.Length == 1 && letterCount <= 8)
        {
            string token = words[0];
            if (IsKnownShortDialogueToken(token)
                || IsKnownExpressiveRoot(CollapseRepeatedLetters(token)))
            {
                return true;
            }
            return region.BubbleConfidence >= 0.10;
        }

        if (words.Length == 2
            && letterCount <= 10
            && !ContainsPlainLanguageAnchor(words)
            && region.BubbleConfidence < 0.08)
        {
            return false;
        }

        return true;
    }

    private static bool IsSfx(string? type) =>
        string.Equals(type, "sfx", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "sound_effect", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "sound-effect", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "onomatopoeia", StringComparison.OrdinalIgnoreCase);

    private static string[] ExtractWords(string value) =>
        Regex.Matches(value, @"[\p{L}]+(?:['’\-][\p{L}]+)*")
            .Select(match => match.Value)
            .ToArray();

    private static int ReadingEvidenceScore(string value)
    {
        string compact = Compact(value);
        int letters = compact.Count(char.IsLetter);
        int words = ExtractWords(compact).Length;
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
            @"^(?:(?:HA|HE|HI|HO|HU)){2,}$",
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
        return roots.Length == words.Count
               && roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
               && IsKnownExpressiveRoot(roots[0]);
    }

    private static bool IsKnownExpressiveRoot(string value)
    {
        string root = value.ToUpperInvariant();
        string[] known =
        [
            "NO", "OH", "AH", "UH", "HA", "HE", "HI", "HO", "HU",
            "HM", "M", "MM", "BO", "BOO", "BR", "GR", "SH", "PS", "PSST",
            "YES", "YEAH", "SO", "OK", "OKAY", "PLEASE", "WHOA", "WOW", "UGH", "ARGH"
        ];
        return known.Contains(root, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsKnownSfxRoot(string value)
    {
        string root = value.ToUpperInvariant();
        string[] known =
        [
            "BZ", "BR", "GR", "HM", "SH", "PS", "PSST", "Z",
            "NO", "OH", "AH", "UH", "HA", "HE", "HI", "HO", "HU",
            "BO", "BOO", "BANG", "BOOM", "POW", "WHAM", "THUD", "THWIP", "CRASH", "SMASH"
        ];
        return known.Contains(root, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsKnownShortDialogueToken(string value)
    {
        string token = value.Trim('’', '\'').ToUpperInvariant();
        string[] known =
        [
            "A", "AN", "AM", "AND", "ARE", "AS", "AT", "BE", "BUT", "BY", "CAN",
            "COME", "DID", "DO", "DON'T", "FOR", "FROM", "GO", "GOOD", "HAD", "HAS",
            "HAVE", "HE", "HELP", "HER", "HERE", "HEY", "HIM", "HIS", "HOW", "I", "IF",
            "IN", "IS", "IT", "ITS", "LET", "LOOK", "ME", "MY", "NO", "NOT", "NOW", "OF",
            "OH", "OK", "OKAY", "ON", "OR", "OUR", "OUT", "RUN", "SHE", "SO", "STOP",
            "THAT", "THE", "THEM", "THEY", "THIS", "TO", "UP", "US", "WAIT", "WAS", "WE",
            "WERE", "WHAT", "WHEN", "WHERE", "WHO", "WHY", "WILL", "WITH", "WOW", "YES",
            "YOU", "YOUR"
        ];
        return known.Contains(token, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSuspiciousPseudoToken(string token)
    {
        string letters = new(token
            .Where(char.IsLetter)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (letters.Length < 3
            || LongestIdenticalRun(letters) < 3
            || letters.Distinct().Count() > 2)
        {
            return false;
        }

        return !IsKnownExpressiveRoot(CollapseRepeatedLetters(token));
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
            .Select(word => word.Trim('’', '\'').ToUpperInvariant())
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
