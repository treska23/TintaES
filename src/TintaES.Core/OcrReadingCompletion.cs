using System.Text;

namespace TintaES.Core;

/// <summary>
/// Recupera una lectura completa cuando el OCR principal solo conservó una parte del
/// bocadillo y otra lectura solapada contiene la frase entera.
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

            // Una traducción de la lectura corta no es válida para la frase ampliada.
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
                candidate.Tokens.Length >= currentTokens.Length
                && candidate.Tokens.Length - currentTokens.Length <= MaximumAddedWords)
            .Select(candidate => new
            {
                candidate.Text,
                candidate.Tokens,
                Alignment = FindBestAlignment(candidate.Tokens, currentTokens),
                Characters = candidate.Tokens.Sum(token => token.Length)
            })
            .Where(candidate =>
                candidate.Characters >= currentCharacters + 1
                && IsSafeCompletion(
                    candidate.Alignment,
                    currentTokens.Length,
                    candidate.Tokens.Length))
            .OrderByDescending(candidate => candidate.Tokens.Length)
            .ThenByDescending(candidate => candidate.Characters)
            .ThenByDescending(candidate => candidate.Text.Length)
            .Select(candidate => candidate.Text)
            .FirstOrDefault();
    }

    private static TokenAlignment FindBestAlignment(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string> fragment)
    {
        if (fragment.Count == 0 || fragment.Count > candidate.Count)
        {
            return TokenAlignment.None;
        }

        TokenAlignment best = TokenAlignment.None;
        for (int start = 0; start < candidate.Count; start++)
        {
            if (!TokensMatch(candidate[start], fragment[0]))
            {
                continue;
            }

            int candidateIndex = start;
            bool matches = true;
            for (int fragmentIndex = 1; fragmentIndex < fragment.Count; fragmentIndex++)
            {
                int next = -1;
                for (int search = candidateIndex + 1; search < candidate.Count; search++)
                {
                    if (TokensMatch(candidate[search], fragment[fragmentIndex]))
                    {
                        next = search;
                        break;
                    }
                }

                if (next < 0)
                {
                    matches = false;
                    break;
                }
                candidateIndex = next;
            }

            if (!matches)
            {
                continue;
            }

            var alignment = new TokenAlignment(start, candidateIndex);
            if (!best.Found
                || alignment.Span < best.Span
                || (alignment.Span == best.Span
                    && EdgeCount(alignment, candidate.Count) > EdgeCount(best, candidate.Count)))
            {
                best = alignment;
            }
        }

        return best;
    }

    private static bool IsSafeCompletion(
        TokenAlignment alignment,
        int fragmentCount,
        int candidateCount)
    {
        if (!alignment.Found)
        {
            return false;
        }

        int extraBefore = alignment.First;
        int extraAfter = candidateCount - alignment.Last - 1;
        if (extraBefore == 0 || extraAfter == 0)
        {
            return true;
        }

        // Una coincidencia enteramente interior solo es segura si la lectura principal
        // cubre casi todo el candidato; así no se absorbe texto de un globo vecino.
        return fragmentCount >= 3
            && fragmentCount / (double)candidateCount >= 0.60
            && extraBefore <= 2
            && extraAfter <= 2;
    }

    private static bool TokensMatch(string candidate, string fragment)
    {
        if (string.Equals(candidate, fragment, StringComparison.Ordinal))
        {
            return true;
        }

        int shorter = Math.Min(candidate.Length, fragment.Length);
        int longer = Math.Max(candidate.Length, fragment.Length);
        if (shorter >= 2
            && longer - shorter <= 3
            && (candidate.StartsWith(fragment, StringComparison.Ordinal)
                || fragment.StartsWith(candidate, StringComparison.Ordinal)))
        {
            return true;
        }

        int allowedDistance = shorter >= 7 ? 2 : shorter >= 3 ? 1 : 0;
        return allowedDistance > 0
            && Math.Abs(candidate.Length - fragment.Length) <= allowedDistance
            && EditDistance(candidate, fragment, allowedDistance) <= allowedDistance;
    }

    private static int EditDistance(string left, string right, int stopAfter)
    {
        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] current = new int[right.Length + 1];
        for (int leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            int rowMinimum = current[0];
            for (int rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                int substitution = previous[rightIndex - 1]
                    + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                    substitution);
                rowMinimum = Math.Min(rowMinimum, current[rightIndex]);
            }

            if (rowMinimum > stopAfter)
            {
                return rowMinimum;
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static int EdgeCount(TokenAlignment alignment, int count) =>
        (alignment.First == 0 ? 1 : 0) + (alignment.Last == count - 1 ? 1 : 0);

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

    private readonly record struct TokenAlignment(int First, int Last)
    {
        public static TokenAlignment None => new(-1, -1);
        public bool Found => First >= 0 && Last >= First;
        public int Span => Found ? Last - First + 1 : int.MaxValue;
    }
}
