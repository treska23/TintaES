using System.Globalization;
using System.Text;

namespace TintaES.Core;

public static class RegionMerger
{
    public static IReadOnlyList<ComicRegion> Merge(IEnumerable<ComicRegion> source)
    {
        var valid = source
            .Where(region => !string.IsNullOrWhiteSpace(region.Original))
            .Select(Sanitize)
            .Where(region => region.TextBox.Area is >= 15 and <= 90_000)
            .OrderByDescending(region => region.Confidence)
            .ToList();

        var merged = new List<ComicRegion>();
        foreach (ComicRegion candidate in valid)
        {
            int duplicateIndex = merged.FindIndex(existing => IsDuplicate(existing, candidate));
            if (duplicateIndex < 0)
            {
                merged.Add(candidate);
                continue;
            }

            ComicRegion existing = merged[duplicateIndex];
            if (candidate.Confidence > existing.Confidence
                || candidate.Original.Length > existing.Original.Length)
            {
                merged[duplicateIndex] = candidate;
            }
        }

        var ordered = merged
            .OrderBy(region => Math.Round((region.RenderBox.Y + region.RenderBox.Height / 2) / 45))
            .ThenBy(region => region.RenderBox.X)
            .ThenBy(region => region.RenderBox.Y)
            .Take(150)
            .ToList();

        for (int index = 0; index < ordered.Count; index++)
        {
            ordered[index].Order = index + 1;
        }

        return ordered;
    }

    public static ComicRegion Sanitize(ComicRegion region)
    {
        region.Original = TrimQuotationMarks(region.Original);
        region.Translation = TrimQuotationMarks(region.Translation);
        region.TextBox = region.TextBox.Clamp();
        region.RenderBox = region.RenderBox.Clamp();
        region.Confidence = Math.Clamp(region.Confidence, 0, 1);
        region.Rotation = Math.Clamp(region.Rotation, -180, 180);

        double ratio = region.RenderBox.Area / Math.Max(1, region.TextBox.Area);
        bool implausible = ratio > 7
            || region.RenderBox.Area > 70_000
            || region.RenderBox.Width > Math.Max(140, region.TextBox.Width * 4.2)
            || region.RenderBox.Height > Math.Max(120, region.TextBox.Height * 4.2);

        if (implausible)
        {
            double expansionX = region.Type is "dialogue" or "thought" ? 1.25 : 0.45;
            double expansionY = region.Type is "dialogue" or "thought" ? 1.2 : 0.45;
            region.RenderBox = region.TextBox.Expand(expansionX, expansionY);
        }

        region.Style.FontFamily = string.IsNullOrWhiteSpace(region.Style.FontFamily)
            ? null
            : region.Style.FontFamily.Trim()[..Math.Min(80, region.Style.FontFamily.Trim().Length)];
        region.Style.FontWeight = Math.Clamp(
            (int)Math.Round(region.Style.FontWeight / 50d) * 50,
            100,
            900);
        region.Style.FontSize = Math.Clamp(region.Style.FontSize, 0, 250);
        region.Style.FontWidthRatio = Math.Clamp(region.Style.FontWidthRatio, 0.55, 1.5);
        region.Style.LineHeightRatio = Math.Clamp(region.Style.LineHeightRatio, 0.8, 1.8);
        region.Style.OriginalLineCount = Math.Clamp(region.Style.OriginalLineCount, 0, 20);
        region.Style.OutlineWidth = Math.Clamp(region.Style.OutlineWidth, 0, 8);
        region.Style.TextColor = NormalizeColor(region.Style.TextColor, "#111111");
        region.Style.OutlineColor = NormalizeNullableColor(region.Style.OutlineColor);
        region.Style.BackgroundColor = NormalizeNullableColor(region.Style.BackgroundColor);
        region.Style.Alignment = region.Style.Alignment is "left" or "right" ? region.Style.Alignment : "center";
        return region;
    }

    public static double IntersectionOverUnion(NormalizedRect a, NormalizedRect b)
    {
        double left = Math.Max(a.X, b.X);
        double top = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.Right, b.Right);
        double bottom = Math.Min(a.Bottom, b.Bottom);
        double intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        double union = a.Area + b.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static bool IsDuplicate(ComicRegion left, ComicRegion right)
    {
        double overlap = IntersectionOverUnion(left.TextBox, right.TextBox);
        if (overlap < 0.16)
        {
            return false;
        }

        string leftText = NormalizeText(left.Original);
        string rightText = NormalizeText(right.Original);
        return leftText == rightText
            || leftText.Contains(rightText, StringComparison.Ordinal)
            || rightText.Contains(leftText, StringComparison.Ordinal)
            || overlap > 0.58;
    }

    private static string NormalizeText(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }
        return builder.ToString();
    }

    private static string TrimQuotationMarks(string value)
    {
        return (value ?? string.Empty).Trim().Trim('"', '\'', '“', '”', '‘', '’').Trim();
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string candidate = value.Trim().ToUpperInvariant();
        if (candidate.Length == 7 && candidate[0] == '#'
            && candidate[1..].All(Uri.IsHexDigit))
        {
            return candidate;
        }

        return fallback;
    }

    private static string? NormalizeNullableColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = NormalizeColor(value, string.Empty);
        return normalized.Length == 7 ? normalized : null;
    }
}