using System.Text;

namespace TintaES.Core;

/// <summary>
/// Recupera una lectura completa cuando el OCR principal se quedó únicamente con el comienzo o
/// el final de un bocadillo y otro OCR solapado conservó la frase entera. Las alternativas ya
/// llegan asociadas por geometría; aquí se exige además inclusión por palabras para no mezclar
/// globos vecinos.
/// </summary>
public static class OcrReadingCompletion
{
    private const int MaximumReadingLength = 280;
    private const int MaximumAddedWords = 12;

    public static int PromoteCompleteAlternatives(IEnumerable<ComicRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);

        int promoted = 0;
        foreach (ComicRegion region in regions)
        {
            string current = Compact(region.Original);
            if (current.Length == 0)
            {
                continue;
            }

            string? completion = ChooseCompletion(current, region.StoredOcrAlternatives);
            if (completion is null)
            {
                continue;
            }

            string[] retainedAlternatives = new[] { current }
                .Concat(region.StoredOcrAlternatives)
                .Select(Compact)
                .Where(value => value.Length > 0)
                .Where(value => !string.Equals(value, completion, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

            region.StoredOcrAlternatives = retainedAlternatives;
            region.Original = completion;

            // Una traducción de la lectura corta nunca puede considerarse válida para la frase
            // ampliada. Se fuerza un nuevo pase en vez de conservar media traducción.
            region.Translation = string.Empty;
            promoted++;
        }

        return promoted;
    }

    public static string? ChooseCompletion(
        string original,
        IEnumerable<string>? alternatives)
    {
        string current = Compact(original);
        string[] currentTokens = Tokenize(current);
        int currentCharacters = currentTokens.Sum(token => token.Length);
        if (currentTokens.Length == 0 || currentCharacters < 4)
        {
            return null;
        }

        return (alternatives ?? [])
            .Select(Compact)
            .Where(candidate => candidate.Length > current.Length)
            .Where(candidate => candidate.Length <= MaximumReadingLength)
            .Select(candidate => new
            {
                Text = candidate,
                Tokens = Tokenize(candidate)
            })
            .Where(candidate =>
                candidate.Tokens.Length > currentTokens.Length
                && candidate.Tokens.Length - currentTokens.Length <= MaximumAddedWords)
            .Select(candidate => new
            {
                candidate.Text,
                candidate.Tokens,
                SequenceIndex = FindSequence(candidate.Tokens, currentTokens)
            })
            // La lectura incompleta debe ser el comienzo o el final de la completa. Una
            // coincidencia en medio suele indicar que Windows OCR abarcó otro globo cercano.
            .Where(candidate =>
                candidate.SequenceIndex == 0
                || candidate.SequenceIndex + currentTokens.Length == candidate.Tokens.Length)
            .OrderByDescending(candidate => candidate.Tokens.Length)
            .ThenByDescending(candidate => candidate.Text.Length)
            .Select(candidate => candidate.Text)
            .FirstOrDefault();
    }

    private static int FindSequence(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string> fragment)
    {
        if (fragment.Count == 0 || fragment.Count > candidate.Count)
        {
            return -1;
        }

        for (int start = 0; start <= candidate.Count - fragment.Count; start++)
        {
            bool matches = true;
            for (int offset = 0; offset < fragment.Count; offset++)
            {
                if (!string.Equals(
                        candidate[start + offset],
                        fragment[offset],
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return start;
            }
        }

        return -1;
    }

    private static string[] Tokenize(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (char character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToUpperInvariant(character));
                continue;
            }

            FlushToken(current, tokens);
        }
        FlushToken(current, tokens);
        return tokens.ToArray();
    }

    private static void FlushToken(StringBuilder current, ICollection<string> tokens)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }

    private static string Compact(string? value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
}
