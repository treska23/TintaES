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

        ResolveCompetingRenderAreas(ordered);
        return ordered;
    }

    public static void ResolveCompetingRenderAreas(IReadOnlyList<ComicRegion> regions)
    {
        for (int firstIndex = 0; firstIndex < regions.Count; firstIndex++)
        {
            for (int secondIndex = firstIndex + 1; secondIndex < regions.Count; secondIndex++)
            {
                ComicRegion first = regions[firstIndex];
                ComicRegion second = regions[secondIndex];
                double intersection = IntersectionArea(first.RenderBox, second.RenderBox);
                double overlap = intersection / Math.Max(1, Math.Min(first.RenderBox.Area, second.RenderBox.Area));
                if (overlap < 0.015 || IntersectionArea(first.TextBox, second.TextBox) > 1)
                {
                    continue;
                }

                double firstCentreX = first.TextBox.X + first.TextBox.Width / 2;
                double firstCentreY = first.TextBox.Y + first.TextBox.Height / 2;
                double secondCentreX = second.TextBox.X + second.TextBox.Width / 2;
                double secondCentreY = second.TextBox.Y + second.TextBox.Height / 2;
                double deltaX = Math.Abs(secondCentreX - firstCentreX);
                double deltaY = Math.Abs(secondCentreY - firstCentreY);

                if (deltaX >= deltaY
                    && TrySeparateHorizontally(first, second, firstCentreX, secondCentreX))
                {
                    continue;
                }

                TrySeparateVertically(first, second, firstCentreY, secondCentreY);
            }
        }
    }

    public static ComicRegion Sanitize(ComicRegion region)
    {
        region.Original = TrimQuotationMarks(region.Original);
        region.OcrAlternatives = (region.OcrAlternatives ?? [])
            .Select(TrimQuotationMarks)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !string.Equals(value, region.Original, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        region.Translation = TrimQuotationMarks(region.Translation);
        region.TextBox = region.TextBox.Clamp();
        region.RenderBox = region.RenderBox.Clamp();
        region.Confidence = Math.Clamp(region.Confidence, 0, 1);
        region.Rotation = Math.Clamp(region.Rotation, -180, 180);

        region.SafePolygon = SanitizePolygon(region.SafePolygon);

        if (region.Type is "dialogue" or "thought")
        {
            region.SafePolygon = ConstrainDialoguePolygon(region.SafePolygon, region.TextBox);

            // El texto original ya estaba dentro del bocadillo. Ese bloque es nuestra referencia
            // más fiable. Una silueta de bocadillo detectada solo se acepta si permanece cerca de
            // esa zona; así una detección antigua o una cola conectada al fondo nunca puede convertir
            // media viñeta en una zona válida de rotulación.
            if (IsUsableDialoguePolygon(region.SafePolygon, region.TextBox))
            {
                region.RenderBox = BoundsFromPolygon(region.SafePolygon).Clamp();
            }
            else
            {
                region.RenderBox = CreateConservativeDialogueBox(region.TextBox, region.Type);
                region.SafePolygon = CreateEllipsePolygon(region.RenderBox);
            }
        }
        else
        {
            if (IsUsableGeneralPolygon(region.SafePolygon, region.TextBox))
            {
                region.RenderBox = BoundsFromPolygon(region.SafePolygon).Clamp();
            }
            else
            {
                region.SafePolygon = [];
                double ratio = region.RenderBox.Area / Math.Max(1, region.TextBox.Area);
                bool implausible = ratio > 7
                    || region.RenderBox.Area > 70_000
                    || region.RenderBox.Width > Math.Max(140, region.TextBox.Width * 4.2)
                    || region.RenderBox.Height > Math.Max(120, region.TextBox.Height * 4.2);

                if (implausible)
                {
                    region.RenderBox = region.TextBox.Expand(0.30, 0.30);
                }
            }
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

    private static double IntersectionArea(NormalizedRect first, NormalizedRect second)
    {
        double left = Math.Max(first.X, second.X);
        double top = Math.Max(first.Y, second.Y);
        double right = Math.Min(first.Right, second.Right);
        double bottom = Math.Min(first.Bottom, second.Bottom);
        return Math.Max(0, right - left) * Math.Max(0, bottom - top);
    }

    private static bool TrySeparateHorizontally(
        ComicRegion first,
        ComicRegion second,
        double firstCentre,
        double secondCentre)
    {
        ComicRegion left = firstCentre <= secondCentre ? first : second;
        ComicRegion right = ReferenceEquals(left, first) ? second : first;
        if (left.TextBox.Right > right.TextBox.X + 1)
        {
            return false;
        }

        double boundary = (left.TextBox.Right + right.TextBox.X) / 2;
        double gutter = Math.Clamp(
            Math.Min(left.TextBox.Width, right.TextBox.Width) * 0.08,
            5,
            9);
        NormalizedRect leftBox = left.RenderBox;
        NormalizedRect rightBox = right.RenderBox;
        SetStrictSeparatedBox(
            left,
            new NormalizedRect(
                leftBox.X,
                leftBox.Y,
                Math.Max(5, Math.Min(leftBox.Right, boundary - gutter) - leftBox.X),
                leftBox.Height));
        SetStrictSeparatedBox(
            right,
            new NormalizedRect(
                Math.Max(rightBox.X, boundary + gutter),
                rightBox.Y,
                Math.Max(5, rightBox.Right - Math.Max(rightBox.X, boundary + gutter)),
                rightBox.Height));
        return true;
    }

    private static bool TrySeparateVertically(
        ComicRegion first,
        ComicRegion second,
        double firstCentre,
        double secondCentre)
    {
        ComicRegion top = firstCentre <= secondCentre ? first : second;
        ComicRegion bottom = ReferenceEquals(top, first) ? second : first;
        if (top.TextBox.Bottom > bottom.TextBox.Y + 1)
        {
            return false;
        }

        double boundary = (top.TextBox.Bottom + bottom.TextBox.Y) / 2;
        double gutter = Math.Clamp(
            Math.Min(top.TextBox.Height, bottom.TextBox.Height) * 0.08,
            5,
            9);
        NormalizedRect topBox = top.RenderBox;
        NormalizedRect bottomBox = bottom.RenderBox;
        SetStrictSeparatedBox(
            top,
            new NormalizedRect(
                topBox.X,
                topBox.Y,
                topBox.Width,
                Math.Max(5, Math.Min(topBox.Bottom, boundary - gutter) - topBox.Y)));
        SetStrictSeparatedBox(
            bottom,
            new NormalizedRect(
                bottomBox.X,
                Math.Max(bottomBox.Y, boundary + gutter),
                bottomBox.Width,
                Math.Max(5, bottomBox.Bottom - Math.Max(bottomBox.Y, boundary + gutter))));
        return true;
    }

    private static void SetStrictSeparatedBox(ComicRegion region, NormalizedRect candidate)
    {
        region.RenderBox = candidate.Clamp();
        region.SafePolygon = region.Type is "dialogue" or "thought"
            ? CreateEllipsePolygon(region.RenderBox)
            :
            [
                new NormalizedPoint(region.RenderBox.X, region.RenderBox.Y),
                new NormalizedPoint(region.RenderBox.Right, region.RenderBox.Y),
                new NormalizedPoint(region.RenderBox.Right, region.RenderBox.Bottom),
                new NormalizedPoint(region.RenderBox.X, region.RenderBox.Bottom)
            ];
    }

    private static IReadOnlyList<NormalizedPoint> SanitizePolygon(IReadOnlyList<NormalizedPoint>? polygon)
    {
        if (polygon is null || polygon.Count < 3)
        {
            return [];
        }

        return polygon
            .Select(point => new NormalizedPoint(
                Math.Clamp(point.X, 0, 1000),
                Math.Clamp(point.Y, 0, 1000)))
            .Distinct()
            .ToArray();
    }

    private static bool IsUsableDialoguePolygon(
        IReadOnlyList<NormalizedPoint> polygon,
        NormalizedRect textBox)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        NormalizedRect bounds = BoundsFromPolygon(polygon);
        if (bounds.Area < Math.Max(15, textBox.Area * 0.55)
            || bounds.Area > textBox.Area * 3.2)
        {
            return false;
        }

        // El interior seguro de un bocadillo puede ser mayor que las letras originales,
        // pero no varias veces mayor ni desplazarse lejos de ellas.
        NormalizedRect allowedEnvelope = textBox.Expand(0.16, 0.06);
        bool insideEnvelope = bounds.X >= allowedEnvelope.X - 1
            && bounds.Y >= allowedEnvelope.Y - 1
            && bounds.Right <= allowedEnvelope.Right + 1
            && bounds.Bottom <= allowedEnvelope.Bottom + 1;
        if (!insideEnvelope)
        {
            return false;
        }

        double textCentreX = textBox.X + textBox.Width / 2;
        double textCentreY = textBox.Y + textBox.Height / 2;
        double boundsCentreX = bounds.X + bounds.Width / 2;
        double boundsCentreY = bounds.Y + bounds.Height / 2;

        return Math.Abs(boundsCentreX - textCentreX) <= Math.Max(12, textBox.Width * 0.28)
            && Math.Abs(boundsCentreY - textCentreY) <= Math.Max(10, textBox.Height * 0.30);
    }

    private static IReadOnlyList<NormalizedPoint> ConstrainDialoguePolygon(
        IReadOnlyList<NormalizedPoint> polygon,
        NormalizedRect textBox)
    {
        if (polygon.Count < 3)
        {
            return polygon;
        }

        NormalizedRect originalBounds = BoundsFromPolygon(polygon);
        if (originalBounds.Area > textBox.Area * 3.2)
        {
            return [];
        }

        // Algunos contornos de visión incluyen la cola del bocadillo, el borde de la viñeta
        // o incluso un globo vecino. Las letras originales son una referencia mucho más
        // fiable: permitimos un 12 % de margen por lado para la traducción, pero ninguna
        // silueta puede crecer más allá de ese corredor.
        // En vertical apenas ampliamos el bloque OCR: las colas de los bocadillos suelen
        // quedar conectadas al contorno y no son superficie válida para rotulación.
        NormalizedRect envelope = textBox.Expand(0.16, 0.06);
        IReadOnlyList<NormalizedPoint> constrained = SanitizePolygon(
            polygon.Select(point => new NormalizedPoint(
                    Math.Clamp(point.X, envelope.X, envelope.Right),
                    Math.Clamp(point.Y, envelope.Y, envelope.Bottom)))
                .ToArray());
        return constrained.Count >= 3 ? constrained : [];
    }

    private static bool IsUsableGeneralPolygon(
        IReadOnlyList<NormalizedPoint> polygon,
        NormalizedRect textBox)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        NormalizedRect bounds = BoundsFromPolygon(polygon);
        if (bounds.Area < Math.Max(15, textBox.Area * 0.35)
            || bounds.Area > Math.Max(25_000, textBox.Area * 6))
        {
            return false;
        }

        double textCentreX = textBox.X + textBox.Width / 2;
        double textCentreY = textBox.Y + textBox.Height / 2;
        return textCentreX >= bounds.X
            && textCentreX <= bounds.Right
            && textCentreY >= bounds.Y
            && textCentreY <= bounds.Bottom;
    }

    private static NormalizedRect CreateConservativeDialogueBox(NormalizedRect textBox, string type)
    {
        // Ampliamos solo un poco el bloque OCR. El texto traducido puede usar ese margen,
        // pero si es más largo tendrá que reducir el tamaño de fuente en lugar de invadir
        // el dibujo o otro bocadillo.
        double expansionX = type == "thought" ? 0.12 : 0.14;
        double expansionY = type == "thought" ? 0.10 : 0.08;
        return textBox.Expand(expansionX, expansionY);
    }

    private static NormalizedRect BoundsFromPolygon(IReadOnlyList<NormalizedPoint> polygon)
    {
        double left = polygon.Min(point => point.X);
        double top = polygon.Min(point => point.Y);
        double right = polygon.Max(point => point.X);
        double bottom = polygon.Max(point => point.Y);
        return new NormalizedRect(left, top, Math.Max(5, right - left), Math.Max(5, bottom - top));
    }

    private static IReadOnlyList<NormalizedPoint> CreateEllipsePolygon(NormalizedRect box)
    {
        const int pointCount = 36;
        double centreX = box.X + box.Width / 2;
        double centreY = box.Y + box.Height / 2;
        double radiusX = box.Width / 2;
        double radiusY = box.Height / 2;
        var points = new NormalizedPoint[pointCount];
        for (int index = 0; index < pointCount; index++)
        {
            double angle = index * Math.PI * 2 / pointCount;
            points[index] = new NormalizedPoint(
                Math.Clamp(centreX + Math.Cos(angle) * radiusX, 0, 1000),
                Math.Clamp(centreY + Math.Sin(angle) * radiusY, 0, 1000));
        }
        return points;
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
