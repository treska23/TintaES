using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf.Services;

/// <summary>
/// Calcula para el editor lateral la misma distribución automática de palabras que usa
/// el rotulador sobre la silueta del bocadillo. No modifica el modelo: únicamente devuelve
/// una versión del texto con saltos de línea para que el usuario vea y edite la composición.
/// </summary>
public sealed class ComicTextLineBreakService
{
    public string FormatForEditor(ComicRegion region, double pageWidth, double pageHeight)
    {
        string text = region.Translation;
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        text = NormalizeNewLines(text);
        if (HasExplicitLineBreaks(text) || region.Vertical)
        {
            return text;
        }

        if (region.Style.Uppercase)
        {
            text = text.ToUpper(CultureInfo.GetCultureInfo("es-ES"));
        }

        double width = Math.Max(2, region.RenderBox.Width / 1000 * pageWidth);
        double height = Math.Max(2, region.RenderBox.Height / 1000 * pageHeight);
        IReadOnlyList<Point> polygon = CreateEffectiveShape(region, pageWidth, pageHeight, width, height);
        Typeface typeface = CreateTypeface(region);
        Brush fill = Brushes.Black;

        if (polygon.Count >= 3
            && TryFitShape(region, text, typeface, fill, pageWidth, pageHeight, width, height, polygon, out TextLayout? layout)
            && layout is not null)
        {
            return string.Join(Environment.NewLine, layout.Lines.Select(line => line.Text));
        }

        return WrapRectangular(region, text, typeface, fill, pageWidth, pageHeight, width, height);
    }

    private static bool TryFitShape(
        ComicRegion region,
        string text,
        Typeface typeface,
        Brush fill,
        double pageWidth,
        double pageHeight,
        double actualWidth,
        double actualHeight,
        IReadOnlyList<Point> polygon,
        out TextLayout? layout)
    {
        const double minimumSize = 2.5;
        double automaticMaximum = Math.Max(6, Math.Min(actualHeight * 0.9, Math.Max(actualWidth * 0.48, 16)));
        double high = GetPreferredMaximumSize(region, automaticMaximum, minimumSize, pageHeight);
        double low = minimumSize;
        TextLayout? best = null;

        for (int index = 0; index < 18; index++)
        {
            double size = (low + high) / 2;
            if (TryCreateShapeLayout(region, text, typeface, fill, pageWidth, pageHeight, actualWidth, actualHeight, polygon, size, out TextLayout? candidate))
            {
                best = candidate;
                low = size;
            }
            else
            {
                high = size;
            }
        }

        if (best is null
            && !TryCreateShapeLayout(region, text, typeface, fill, pageWidth, pageHeight, actualWidth, actualHeight, polygon, minimumSize, out best))
        {
            layout = null;
            return false;
        }

        layout = best;
        return true;
    }

    private static bool TryCreateShapeLayout(
        ComicRegion region,
        string text,
        Typeface typeface,
        Brush fill,
        double pageWidth,
        double pageHeight,
        double actualWidth,
        double actualHeight,
        IReadOnlyList<Point> polygon,
        double fontSize,
        out TextLayout? layout)
    {
        string[] words = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            layout = null;
            return false;
        }

        double lineHeightRatio = Math.Clamp(region.Style.LineHeightRatio, 0.82, 1.8);
        double lineHeight = fontSize * lineHeightRatio;
        double outlinePixels = region.Style.OutlineWidth / 1000 * pageWidth;
        double edgePadding = Math.Max(
            Math.Max(2.5, Math.Min(actualWidth, actualHeight) * 0.045),
            outlinePixels + fontSize * 0.10);

        Rect bounds = GetPolygonBounds(polygon);
        double usableTop = Math.Max(edgePadding, bounds.Top + edgePadding);
        double usableBottom = Math.Min(actualHeight - edgePadding, bounds.Bottom - edgePadding);
        if (usableBottom <= usableTop)
        {
            layout = null;
            return false;
        }

        int maxLinesByHeight = Math.Max(1, (int)Math.Floor((usableBottom - usableTop) / lineHeight));
        int maxLines = Math.Min(words.Length, maxLinesByHeight);
        TextLayout? best = null;
        double bestScore = double.PositiveInfinity;
        double preferredCenterY = GetOriginalTextCenterY(region, pageHeight, actualHeight);
        double preferredCenterX = GetOriginalTextCenterX(region, pageWidth, actualWidth);

        for (int lineCount = 1; lineCount <= maxLines; lineCount++)
        {
            double blockHeight = lineCount * lineHeight;
            double minimumTop = usableTop;
            double maximumTop = usableBottom - blockHeight;
            if (maximumTop < minimumTop)
            {
                continue;
            }

            foreach (double top in GetCandidateTops(minimumTop, maximumTop, preferredCenterY - blockHeight / 2))
            {
                var spans = new HorizontalSpan[lineCount];
                bool usable = true;
                for (int line = 0; line < lineCount; line++)
                {
                    double lineTop = top + line * lineHeight;
                    double glyphTop = lineTop + Math.Max(0, (lineHeight - fontSize * 1.02) / 2);
                    double glyphBottom = Math.Min(lineTop + lineHeight, glyphTop + fontSize * 1.02);
                    if (!TryGetSafeSpanForBand(polygon, glyphTop, glyphBottom, preferredCenterX, edgePadding, out HorizontalSpan span)
                        || span.Width <= fontSize * 0.9)
                    {
                        usable = false;
                        break;
                    }
                    spans[line] = span;
                }

                if (!usable || !TryBreakWords(words, spans, typeface, fontSize, fill, out int[]? breaks, out double score))
                {
                    continue;
                }

                if (region.Style.OriginalLineCount > 0)
                {
                    score += Math.Abs(lineCount - region.Style.OriginalLineCount) * 0.12;
                }

                double actualCenterY = top + blockHeight / 2;
                score += Math.Abs(actualCenterY - preferredCenterY) / Math.Max(1, actualHeight) * 0.08;
                if (score >= bestScore)
                {
                    continue;
                }

                var placements = new List<LinePlacement>(lineCount);
                int start = 0;
                for (int line = 0; line < lineCount; line++)
                {
                    int end = breaks![line];
                    placements.Add(new LinePlacement(string.Join(' ', words[start..end])));
                    start = end;
                }

                bestScore = score;
                best = new TextLayout(fontSize, placements);
            }
        }

        layout = best;
        return best is not null;
    }

    private static string WrapRectangular(
        ComicRegion region,
        string text,
        Typeface typeface,
        Brush fill,
        double pageWidth,
        double pageHeight,
        double actualWidth,
        double actualHeight)
    {
        double padding = Math.Max(3, Math.Min(actualWidth, actualHeight) * 0.065);
        double availableWidth = Math.Max(2, actualWidth - padding * 2);
        double availableHeight = Math.Max(2, actualHeight - padding * 2);
        const double minimumSize = 2.5;
        double automaticMaximum = Math.Max(6, Math.Min(availableHeight * 0.92, Math.Max(availableWidth * 0.48, 16)));
        double high = GetPreferredMaximumSize(region, automaticMaximum, minimumSize, pageHeight);
        double low = minimumSize;
        double bestSize = minimumSize;

        for (int index = 0; index < 18; index++)
        {
            double size = (low + high) / 2;
            IReadOnlyList<string> lines = WrapWords(text, typeface, size, fill, availableWidth);
            double lineHeight = size * Math.Clamp(region.Style.LineHeightRatio, 0.82, 1.8);
            if (lines.Count * lineHeight <= availableHeight)
            {
                bestSize = size;
                low = size;
            }
            else
            {
                high = size;
            }
        }

        return string.Join(Environment.NewLine, WrapWords(text, typeface, bestSize, fill, availableWidth));
    }

    private static IReadOnlyList<string> WrapWords(string text, Typeface typeface, double fontSize, Brush fill, double width)
    {
        string[] words = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [];
        }

        var lines = new List<string>();
        var current = new List<string>();
        foreach (string word in words)
        {
            string candidate = current.Count == 0 ? word : string.Join(' ', current) + " " + word;
            if (current.Count > 0 && MeasureText(candidate, typeface, fontSize, fill) > width)
            {
                lines.Add(string.Join(' ', current));
                current.Clear();
            }
            current.Add(word);
        }
        if (current.Count > 0)
        {
            lines.Add(string.Join(' ', current));
        }
        return lines;
    }

    private static bool TryBreakWords(
        string[] words,
        IReadOnlyList<HorizontalSpan> spans,
        Typeface typeface,
        double fontSize,
        Brush fill,
        out int[]? breaks,
        out double score)
    {
        int lineCount = spans.Count;
        int wordCount = words.Length;
        var costs = new double[lineCount + 1, wordCount + 1];
        var previous = new int[lineCount + 1, wordCount + 1];
        for (int line = 0; line <= lineCount; line++)
        {
            for (int word = 0; word <= wordCount; word++)
            {
                costs[line, word] = double.PositiveInfinity;
                previous[line, word] = -1;
            }
        }
        costs[0, 0] = 0;

        for (int line = 0; line < lineCount; line++)
        {
            for (int start = 0; start < wordCount; start++)
            {
                if (double.IsPositiveInfinity(costs[line, start]))
                {
                    continue;
                }
                int wordsStillNeeded = lineCount - line - 1;
                for (int end = start + 1; end <= wordCount - wordsStillNeeded; end++)
                {
                    string candidate = string.Join(' ', words[start..end]);
                    double measured = MeasureText(candidate, typeface, fontSize, fill);
                    if (measured > spans[line].Width)
                    {
                        break;
                    }
                    double unused = (spans[line].Width - measured) / spans[line].Width;
                    double raggedness = unused * unused * (line == lineCount - 1 ? 0.45 : 1);
                    double candidateCost = costs[line, start] + raggedness;
                    if (candidateCost < costs[line + 1, end])
                    {
                        costs[line + 1, end] = candidateCost;
                        previous[line + 1, end] = start;
                    }
                }
            }
        }

        if (double.IsPositiveInfinity(costs[lineCount, wordCount]))
        {
            breaks = null;
            score = double.PositiveInfinity;
            return false;
        }

        breaks = new int[lineCount];
        int cursor = wordCount;
        for (int line = lineCount; line > 0; line--)
        {
            breaks[line - 1] = cursor;
            cursor = previous[line, cursor];
        }
        score = costs[lineCount, wordCount] + lineCount * 0.008;
        return true;
    }

    private static IEnumerable<double> GetCandidateTops(double minimum, double maximum, double preferred)
    {
        if (maximum <= minimum)
        {
            yield return minimum;
            yield break;
        }

        var candidates = new List<double>
        {
            Math.Clamp(preferred, minimum, maximum),
            (minimum + maximum) / 2,
            minimum,
            maximum
        };
        const int steps = 12;
        for (int index = 1; index < steps; index++)
        {
            candidates.Add(minimum + (maximum - minimum) * index / steps);
        }
        foreach (double value in candidates.Distinct().OrderBy(value => Math.Abs(value - preferred)))
        {
            yield return value;
        }
    }

    private static bool TryGetSafeSpanForBand(
        IReadOnlyList<Point> polygon,
        double top,
        double bottom,
        double preferredX,
        double padding,
        out HorizontalSpan safeSpan)
    {
        double left = double.NegativeInfinity;
        double right = double.PositiveInfinity;
        double height = Math.Max(0.5, bottom - top);
        for (int sample = 0; sample < 7; sample++)
        {
            double y = top + height * sample / 6;
            if (!TryGetHorizontalSpan(polygon, y, preferredX, out HorizontalSpan span))
            {
                safeSpan = default;
                return false;
            }
            left = Math.Max(left, span.Left);
            right = Math.Min(right, span.Right);
        }
        left += padding;
        right -= padding;
        safeSpan = new HorizontalSpan(left, right);
        return safeSpan.Width > 0;
    }

    private static bool TryGetHorizontalSpan(IReadOnlyList<Point> polygon, double y, double preferredX, out HorizontalSpan span)
    {
        var intersections = new List<double>();
        for (int index = 0; index < polygon.Count; index++)
        {
            Point first = polygon[index];
            Point second = polygon[(index + 1) % polygon.Count];
            if ((first.Y <= y && second.Y > y) || (second.Y <= y && first.Y > y))
            {
                double ratio = (y - first.Y) / (second.Y - first.Y);
                intersections.Add(first.X + ratio * (second.X - first.X));
            }
        }
        intersections.Sort();
        if (intersections.Count < 2)
        {
            span = default;
            return false;
        }

        HorizontalSpan best = default;
        bool foundPreferred = false;
        for (int index = 0; index + 1 < intersections.Count; index += 2)
        {
            var candidate = new HorizontalSpan(intersections[index], intersections[index + 1]);
            bool containsPreferred = candidate.Left <= preferredX && preferredX <= candidate.Right;
            if ((containsPreferred && !foundPreferred)
                || (containsPreferred == foundPreferred && candidate.Width > best.Width))
            {
                best = candidate;
                foundPreferred = containsPreferred;
            }
        }
        span = best;
        return best.Width > 0;
    }

    private static IReadOnlyList<Point> CreateEffectiveShape(
        ComicRegion region,
        double pageWidth,
        double pageHeight,
        double actualWidth,
        double actualHeight)
    {
        if (region.SafePolygon.Count >= 3)
        {
            NormalizedRect box = region.RenderBox;
            return region.SafePolygon
                .Select(point => new Point(
                    (point.X - box.X) / 1000 * pageWidth,
                    (point.Y - box.Y) / 1000 * pageHeight))
                .ToArray();
        }

        double insetX = Math.Max(2, actualWidth * 0.035);
        double insetY = Math.Max(2, actualHeight * 0.045);
        double left = insetX;
        double top = insetY;
        double width = Math.Max(2, actualWidth - insetX * 2);
        double height = Math.Max(2, actualHeight - insetY * 2);
        if (region.Type is "dialogue" or "thought")
        {
            var ellipse = new List<Point>(40);
            double centerX = left + width / 2;
            double centerY = top + height / 2;
            for (int index = 0; index < 40; index++)
            {
                double angle = Math.PI * 2 * index / 40;
                ellipse.Add(new Point(
                    centerX + Math.Cos(angle) * width / 2,
                    centerY + Math.Sin(angle) * height / 2));
            }
            return ellipse;
        }

        return
        [
            new Point(left, top),
            new Point(left + width, top),
            new Point(left + width, top + height),
            new Point(left, top + height)
        ];
    }

    private static Rect GetPolygonBounds(IReadOnlyList<Point> polygon)
    {
        double left = polygon.Min(point => point.X);
        double top = polygon.Min(point => point.Y);
        double right = polygon.Max(point => point.X);
        double bottom = polygon.Max(point => point.Y);
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    private static double GetOriginalTextCenterY(ComicRegion region, double pageHeight, double actualHeight)
    {
        if (pageHeight <= 0 || region.RenderBox.Height <= 0)
        {
            return actualHeight / 2;
        }
        double centre = region.TextBox.Y + region.TextBox.Height / 2;
        double local = (centre - region.RenderBox.Y) / 1000 * pageHeight;
        return Math.Clamp(local, 0, actualHeight);
    }

    private static double GetOriginalTextCenterX(ComicRegion region, double pageWidth, double actualWidth)
    {
        if (pageWidth <= 0 || region.RenderBox.Width <= 0)
        {
            return actualWidth / 2;
        }
        double centre = region.TextBox.X + region.TextBox.Width / 2;
        double local = (centre - region.RenderBox.X) / 1000 * pageWidth;
        return Math.Clamp(local, 0, actualWidth);
    }

    private static double GetPreferredMaximumSize(ComicRegion region, double automaticMaximum, double minimum, double pageHeight)
    {
        double scale = Math.Clamp(region.FontScale, 0.35, 1.6);
        if (region.Style.FontSize <= 0 || pageHeight <= 0)
        {
            return Math.Clamp(automaticMaximum * scale, minimum, automaticMaximum);
        }
        double originalPixels = region.Style.FontSize / 1000 * pageHeight;
        return Math.Clamp(originalPixels * 1.03 * scale, minimum, automaticMaximum);
    }

    private static double MeasureText(string text, Typeface typeface, double size, Brush fill)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.GetCultureInfo("es-ES"),
            FlowDirection.LeftToRight,
            typeface,
            size,
            fill,
            1);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static Typeface CreateTypeface(ComicRegion region)
    {
        FontFamily family = ComicFontResolver.Resolve(region.Style.FontFamily, region.Style.FontCategory);
        FontWeight weight;
        try
        {
            weight = FontWeight.FromOpenTypeWeight(Math.Clamp(region.Style.FontWeight, 1, 999));
        }
        catch (ArgumentOutOfRangeException)
        {
            weight = region.Style.FontWeight >= 650 ? FontWeights.Bold : FontWeights.Normal;
        }

        return new Typeface(
            family,
            region.Style.Italic ? FontStyles.Italic : FontStyles.Normal,
            weight,
            ResolveFontStretch(region.Style.FontWidthRatio));
    }

    private static FontStretch ResolveFontStretch(double ratio) => ratio switch
    {
        <= 0.62 => FontStretches.UltraCondensed,
        <= 0.72 => FontStretches.ExtraCondensed,
        <= 0.82 => FontStretches.Condensed,
        <= 0.92 => FontStretches.SemiCondensed,
        < 1.08 => FontStretches.Normal,
        < 1.18 => FontStretches.SemiExpanded,
        < 1.28 => FontStretches.Expanded,
        < 1.4 => FontStretches.ExtraExpanded,
        _ => FontStretches.UltraExpanded
    };

    private static string NormalizeNewLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    private static bool HasExplicitLineBreaks(string text) => text.Contains('\n') || text.Contains('\r');

    private readonly record struct HorizontalSpan(double Left, double Right)
    {
        public double Width => Math.Max(0, Right - Left);
    }

    private sealed record LinePlacement(string Text);
    private sealed record TextLayout(double FontSize, IReadOnlyList<LinePlacement> Lines);
}
