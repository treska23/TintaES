using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TintaES.Core;

public sealed record HunyuanTextSpot(string Text, NormalizedRect Box);

/// <summary>
/// Convierte la salida JSON de un OCR visual en zonas normalizadas y la cruza con las
/// regiones geométricas de TintaES. El nombre se conserva por compatibilidad binaria;
/// PaddleOCR-VL aporta ahora el texto y CTD la máscara y la geometría de edición.
/// </summary>
public static partial class HunyuanTextSpotting
{
    private static readonly string[] TextKeys = ["text", "content", "transcription", "label", "value"];
    private static readonly string[] BoxKeys = ["bbox", "box", "coordinates", "coordinate", "rect", "polygon", "points"];

    public static IReadOnlyList<HunyuanTextSpot> Parse(string response, int pageWidth, int pageHeight)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return [];
        }

        string cleaned = StripMarkdownFence(response.Trim());
        if (TryExtractJson(cleaned, out JsonDocument? document) && document is not null)
        {
            using (document)
            {
                var spots = new List<HunyuanTextSpot>();
                CollectJsonSpots(document.RootElement, spots, pageWidth, pageHeight);
                if (spots.Count > 0)
                {
                    return Deduplicate(spots);
                }
            }
        }

        // Compatibilidad con salidas de spotting tipo <ref>texto</ref><box>[[x1,y1,x2,y2]]</box>.
        var fallback = new List<HunyuanTextSpot>();
        foreach (Match match in RefBoxRegex().Matches(cleaned))
        {
            string text = NormalizeText(match.Groups["text"].Value);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (TryParseBoxNumbers(match.Groups["box"].Value, pageWidth, pageHeight, out NormalizedRect box))
            {
                fallback.Add(new HunyuanTextSpot(text, box));
            }
        }

        return Deduplicate(fallback);
    }

    public static int ApplyToRegions(
        IReadOnlyList<ComicRegion> regions,
        IReadOnlyList<HunyuanTextSpot> spots)
    {
        if (regions.Count == 0 || spots.Count == 0)
        {
            return 0;
        }

        var assigned = regions.ToDictionary(region => region, _ => new List<HunyuanTextSpot>());
        foreach (HunyuanTextSpot spot in spots)
        {
            ComicRegion? winner = null;
            double winnerScore = 0;
            foreach (ComicRegion region in regions.Where(region => region.IsEnabled))
            {
                NormalizedRect target = GetAssociationBox(region);
                double score = AssociationScore(target, spot.Box);
                if (score > winnerScore)
                {
                    winner = region;
                    winnerScore = score;
                }
            }

            if (winner is not null && winnerScore >= 0.36)
            {
                assigned[winner].Add(spot);
            }
        }

        int replacements = 0;
        foreach ((ComicRegion region, List<HunyuanTextSpot> regionSpots) in assigned)
        {
            if (regionSpots.Count == 0)
            {
                continue;
            }

            string candidate = BuildReading(regionSpots);
            if (!ShouldReplace(region.Original, candidate))
            {
                continue;
            }

            string previous = region.Original.Trim();
            if (!string.IsNullOrWhiteSpace(previous)
                && !string.Equals(previous, candidate, StringComparison.OrdinalIgnoreCase))
            {
                region.StoredOcrAlternatives = new[] { previous }
                    .Concat(region.StoredOcrAlternatives)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToArray();
            }

            region.Original = candidate;
            region.Translation = string.Empty;
            region.Style.OriginalLineCount = Math.Max(region.Style.OriginalLineCount, regionSpots.Count);
            replacements++;
        }

        return replacements;
    }

    private static void CollectJsonSpots(
        JsonElement element,
        ICollection<HunyuanTextSpot> spots,
        int pageWidth,
        int pageHeight)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                CollectJsonSpots(item, spots, pageWidth, pageHeight);
            }
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? text = null;
        JsonElement? boxElement = null;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (text is null
                && TextKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                text = property.Value.GetString();
            }

            if (boxElement is null
                && BoxKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                boxElement = property.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(text)
            && boxElement is { } boxValue
            && TryParseBox(boxValue, pageWidth, pageHeight, out NormalizedRect box))
        {
            spots.Add(new HunyuanTextSpot(NormalizeText(text), box));
            return;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                CollectJsonSpots(property.Value, spots, pageWidth, pageHeight);
            }
        }
    }

    private static bool TryParseBox(
        JsonElement element,
        int pageWidth,
        int pageHeight,
        out NormalizedRect box)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return TryParseBoxNumbers(element.GetString() ?? string.Empty, pageWidth, pageHeight, out box);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            box = new NormalizedRect(0, 0, 5, 5);
            return false;
        }

        var numbers = new List<double>();
        FlattenNumbers(element, numbers);
        return TryNormalizeNumbers(numbers, pageWidth, pageHeight, out box);
    }

    private static void FlattenNumbers(JsonElement element, ICollection<double> numbers)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double number))
        {
            numbers.Add(number);
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                FlattenNumbers(child, numbers);
            }
        }
    }

    private static bool TryParseBoxNumbers(
        string value,
        int pageWidth,
        int pageHeight,
        out NormalizedRect box)
    {
        List<double> numbers = NumberRegex().Matches(value)
            .Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture))
            .ToList();
        return TryNormalizeNumbers(numbers, pageWidth, pageHeight, out box);
    }

    private static bool TryNormalizeNumbers(
        IReadOnlyList<double> numbers,
        int pageWidth,
        int pageHeight,
        out NormalizedRect box)
    {
        if (numbers.Count < 4)
        {
            box = new NormalizedRect(0, 0, 5, 5);
            return false;
        }

        var xs = new List<double>();
        var ys = new List<double>();
        if (numbers.Count == 4)
        {
            xs.Add(numbers[0]);
            ys.Add(numbers[1]);
            xs.Add(numbers[2]);
            ys.Add(numbers[3]);
        }
        else
        {
            for (int index = 0; index + 1 < numbers.Count; index += 2)
            {
                xs.Add(numbers[index]);
                ys.Add(numbers[index + 1]);
            }
        }

        double maxCoordinate = Math.Max(xs.Max(), ys.Max());
        double scaleX;
        double scaleY;
        if (maxCoordinate <= 1.5)
        {
            scaleX = scaleY = 1000;
        }
        else if (maxCoordinate <= 1100)
        {
            scaleX = scaleY = 1;
        }
        else
        {
            scaleX = 1000d / Math.Max(1, pageWidth);
            scaleY = 1000d / Math.Max(1, pageHeight);
        }

        double left = xs.Min() * scaleX;
        double top = ys.Min() * scaleY;
        double right = xs.Max() * scaleX;
        double bottom = ys.Max() * scaleY;
        if (right - left < 2 || bottom - top < 2)
        {
            box = new NormalizedRect(0, 0, 5, 5);
            return false;
        }

        box = new NormalizedRect(left, top, right - left, bottom - top).Clamp();
        return true;
    }

    private static NormalizedRect GetAssociationBox(ComicRegion region)
    {
        if (region.BubbleBox is { } bubble
            && IsUsefulContainer(bubble, region.TextBox))
        {
            return bubble.Expand(0.05, 0.05);
        }

        NormalizedRect render = region.RenderBox;
        double left = Math.Min(region.TextBox.X, render.X);
        double top = Math.Min(region.TextBox.Y, render.Y);
        double right = Math.Max(region.TextBox.Right, render.Right);
        double bottom = Math.Max(region.TextBox.Bottom, render.Bottom);
        return new NormalizedRect(left, top, right - left, bottom - top)
            .Expand(0.22, 0.28)
            .Clamp();
    }

    private static bool IsUsefulContainer(NormalizedRect bubble, NormalizedRect text)
    {
        double ratio = bubble.Area / Math.Max(1, text.Area);
        double centerX = text.X + text.Width / 2;
        double centerY = text.Y + text.Height / 2;
        return ratio is >= 1 and <= 30
               && bubble.Area <= 180_000
               && centerX >= bubble.X && centerX <= bubble.Right
               && centerY >= bubble.Y && centerY <= bubble.Bottom;
    }

    private static double AssociationScore(NormalizedRect target, NormalizedRect spot)
    {
        double left = Math.Max(target.X, spot.X);
        double top = Math.Max(target.Y, spot.Y);
        double right = Math.Min(target.Right, spot.Right);
        double bottom = Math.Min(target.Bottom, spot.Bottom);
        double intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        double coverage = intersection / Math.Max(1, spot.Area);

        double spotCenterX = spot.X + spot.Width / 2;
        double spotCenterY = spot.Y + spot.Height / 2;
        bool centerInside = spotCenterX >= target.X && spotCenterX <= target.Right
                            && spotCenterY >= target.Y && spotCenterY <= target.Bottom;
        if (!centerInside && coverage < 0.22)
        {
            return 0;
        }

        double targetCenterX = target.X + target.Width / 2;
        double targetCenterY = target.Y + target.Height / 2;
        double dx = (spotCenterX - targetCenterX) / Math.Max(20, target.Width);
        double dy = (spotCenterY - targetCenterY) / Math.Max(20, target.Height);
        double distancePenalty = Math.Min(0.45, Math.Sqrt(dx * dx + dy * dy) * 0.18);
        return Math.Clamp(coverage + (centerInside ? 0.34 : 0) - distancePenalty, 0, 1.4);
    }

    private static string BuildReading(IEnumerable<HunyuanTextSpot> spots)
    {
        return NormalizeText(string.Join(
            " ",
            spots.OrderBy(spot => spot.Box.Y)
                .ThenBy(spot => spot.Box.X)
                .Select(spot => spot.Text)));
    }

    private static bool ShouldReplace(string current, string candidate)
    {
        candidate = NormalizeText(candidate);
        current = NormalizeText(current);
        if (candidate.Length < 2 || string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(current))
        {
            return true;
        }

        int currentWords = WordCount(current);
        int candidateWords = WordCount(candidate);
        if (candidateWords >= currentWords + 2)
        {
            return true;
        }
        if (candidate.Length >= current.Length * 1.35)
        {
            return true;
        }

        // El OCR visual es una fuente adicional, pero nunca degradamos una lectura larga a un fragmento.
        return candidateWords >= currentWords
               && candidate.Length >= current.Length * 0.82;
    }

    private static int WordCount(string value) => value.Split(
        [' ', '\t', '\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries).Length;

    private static string NormalizeText(string value) => WhitespaceRegex()
        .Replace(value.Replace("\r", " ").Replace("\n", " ").Trim(), " ");

    private static IReadOnlyList<HunyuanTextSpot> Deduplicate(IEnumerable<HunyuanTextSpot> spots)
    {
        var result = new List<HunyuanTextSpot>();
        foreach (HunyuanTextSpot spot in spots
                     .Where(spot => !string.IsNullOrWhiteSpace(spot.Text))
                     .OrderBy(spot => spot.Box.Y)
                     .ThenBy(spot => spot.Box.X))
        {
            bool duplicate = result.Any(existing =>
                string.Equals(existing.Text, spot.Text, StringComparison.OrdinalIgnoreCase)
                && AssociationScore(existing.Box.Expand(0.08, 0.08), spot.Box) >= 0.85);
            if (!duplicate)
            {
                result.Add(spot);
            }
        }
        return result;
    }

    private static string StripMarkdownFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal))
        {
            return value;
        }

        int firstLine = value.IndexOf('\n');
        int lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine
            ? value[(firstLine + 1)..lastFence].Trim()
            : value;
    }

    private static bool TryExtractJson(string value, out JsonDocument? document)
    {
        document = null;
        foreach ((char open, char close) in new[] { ('[', ']'), ('{', '}') })
        {
            int start = value.IndexOf(open);
            int end = value.LastIndexOf(close);
            if (start < 0 || end <= start)
            {
                continue;
            }

            try
            {
                document = JsonDocument.Parse(value[start..(end + 1)]);
                return true;
            }
            catch (JsonException)
            {
                // Se probará el siguiente formato o el parser de etiquetas.
            }
        }
        return false;
    }

    [GeneratedRegex(@"<ref>(?<text>.*?)</ref>\s*<box>(?<box>.*?)</box>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RefBoxRegex();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
