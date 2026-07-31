using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TintaES.Wpf.Services;

/// <summary>
/// Compone rotulación española dentro de una silueta irregular. Elige conjuntamente el tamaño,
/// el número de líneas y los puntos de corte: no limita el problema a "meter palabras hasta
/// llenar la línea", porque eso produce líneas huérfanas y cortes antinaturales.
/// </summary>
public sealed class ShapeTextLayoutEngine
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private static readonly double[] VerticalShifts = [0, -0.055, 0.055, -0.11, 0.11];
    private static readonly double[] LineSamples = [0.18, 0.50, 0.82];

    private static readonly HashSet<string> WeakLineEndWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "al", "ante", "bajo", "con", "contra", "de", "del", "desde", "durante",
        "e", "el", "en", "entre", "hacia", "hasta", "la", "las", "lo", "los", "o",
        "para", "pero", "por", "que", "según", "sin", "sobre", "tras", "u", "un",
        "una", "unas", "unos", "y"
    };

    private static readonly HashSet<string> WeakLineStartWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "al", "del", "el", "la", "las", "lo", "los", "me", "se", "su", "sus",
        "te", "tu", "tus", "un", "una", "unas", "unos"
    };

    private static readonly HashSet<string> ArticlesAndDeterminers = new(StringComparer.OrdinalIgnoreCase)
    {
        "el", "la", "las", "lo", "los", "un", "una", "unas", "unos", "mi", "mis",
        "tu", "tus", "su", "sus", "este", "esta", "estos", "estas", "ese", "esa",
        "esos", "esas", "aquel", "aquella", "aquellos", "aquellas"
    };

    public bool TryLayout(
        string text,
        IReadOnlyList<Point> polygon,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip,
        double pageHeight,
        double scale,
        double lineHeightRatio,
        out ShapeTextLayout? layout)
    {
        layout = null;
        string normalized = Normalize(text);
        Rect bounds = Bounds(polygon);
        if (polygon.Count < 3
            || bounds.Width < 12
            || bounds.Height < 12
            || string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        // En una página de unas 3.000 px, 8-14 px es ilegible. El motor deja de sacrificar
        // lectura para fingir que el texto "cabe". Si no entra a este tamaño, queda pendiente.
        double readableMinimum = Math.Clamp(pageHeight * 0.0072, 18, 28);
        double maximum = Math.Clamp(
            Math.Min(bounds.Height * 0.50, bounds.Width * 0.29)
            * Math.Clamp(scale, 0.70, 1.80),
            readableMinimum,
            170);

        if (!TryAtSize(
                normalized,
                polygon,
                typeface,
                fill,
                pixelsPerDip,
                readableMinimum,
                lineHeightRatio,
                out layout))
        {
            return false;
        }

        double low = readableMinimum;
        double high = Math.Max(low, maximum);
        for (int index = 0; index < 11; index++)
        {
            double candidateSize = (low + high) / 2;
            if (TryAtSize(
                    normalized,
                    polygon,
                    typeface,
                    fill,
                    pixelsPerDip,
                    candidateSize,
                    lineHeightRatio,
                    out ShapeTextLayout? candidate))
            {
                layout = candidate;
                low = candidateSize;
            }
            else
            {
                high = candidateSize;
            }
        }

        return layout is not null;
    }

    private static bool TryAtSize(
        string text,
        IReadOnlyList<Point> polygon,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip,
        double fontSize,
        double lineHeightRatio,
        out ShapeTextLayout? layout)
    {
        layout = null;
        Rect bounds = Bounds(polygon);
        double lineHeight = fontSize * Math.Clamp(lineHeightRatio, 1.03, 1.16);
        int geometricMaximum = Math.Clamp((int)Math.Floor(bounds.Height / lineHeight), 1, 20);
        string[] rawTokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int preferredLineCount = PreferredLineCount(rawTokens.Length, bounds);
        ShapeTextLayout? best = null;
        Geometry container = CreatePolygonGeometry(polygon);

        for (int lineCount = 1; lineCount <= geometricMaximum; lineCount++)
        {
            foreach (double shift in VerticalShifts)
            {
                IReadOnlyList<ShapeLineSlot> slots = CreateSlots(
                    polygon,
                    bounds,
                    lineCount,
                    lineHeight,
                    fontSize,
                    shift);
                if (slots.Count != lineCount)
                {
                    continue;
                }

                string[] tokens = SplitOversizedTokens(
                    rawTokens,
                    slots.Max(slot => slot.Width),
                    typeface,
                    fontSize,
                    fill,
                    pixelsPerDip);
                if (tokens.Length < lineCount)
                {
                    continue;
                }

                if (!TryBreak(
                        tokens,
                        slots,
                        typeface,
                        fontSize,
                        fill,
                        pixelsPerDip,
                        out IReadOnlyList<string>? lines,
                        out double breakScore))
                {
                    continue;
                }

                var rendered = new List<ShapeTextLine>(lineCount);
                var lineWidths = new double[lineCount];
                var geometry = new GeometryGroup();
                bool invalid = false;

                for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
                {
                    FormattedText formatted = CreateText(
                        lines![lineIndex],
                        typeface,
                        fontSize,
                        fill,
                        pixelsPerDip);
                    ShapeLineSlot slot = slots[lineIndex];
                    double measuredWidth = formatted.WidthIncludingTrailingWhitespace;
                    if (measuredWidth > slot.Width + 0.20
                        || formatted.Height > lineHeight + 0.20)
                    {
                        invalid = true;
                        break;
                    }

                    double x = slot.Left + Math.Max(0, (slot.Width - measuredWidth) / 2);
                    double y = slot.Top + Math.Max(0, (lineHeight - formatted.Height) / 2);
                    Geometry glyphs = formatted.BuildGeometry(new Point(x, y));
                    Rect glyphBounds = glyphs.Bounds;
                    if (glyphBounds.Left < slot.Left - 0.20
                        || glyphBounds.Right > slot.Left + slot.Width + 0.20
                        || glyphBounds.Top < slot.Top - 0.20
                        || glyphBounds.Bottom > slot.Top + lineHeight + 0.20)
                    {
                        invalid = true;
                        break;
                    }

                    geometry.Children.Add(glyphs);
                    lineWidths[lineIndex] = measuredWidth / Math.Max(1, slot.Width);
                    rendered.Add(new ShapeTextLine(lines[lineIndex], x, y));
                }

                if (invalid
                    || geometry.Children.Count == 0
                    || container.FillContainsWithDetail(geometry)
                       != IntersectionDetail.FullyContains)
                {
                    continue;
                }

                double score = breakScore
                               + BalancePenalty(lines!, lineWidths)
                               + Math.Abs(lineCount - preferredLineCount) * 0.045
                               + Math.Abs(shift) * 0.18;
                var candidate = new ShapeTextLayout(fontSize, rendered, score);
                if (best is null || candidate.Score < best.Score)
                {
                    best = candidate;
                }
            }
        }

        layout = best;
        return best is not null;
    }

    private static int PreferredLineCount(int wordCount, Rect bounds)
    {
        double aspect = bounds.Width / Math.Max(1, bounds.Height);
        double wordsPerLine = aspect switch
        {
            >= 1.75 => 5.2,
            >= 1.30 => 4.4,
            >= 0.95 => 3.7,
            >= 0.70 => 3.1,
            _ => 2.5
        };
        return Math.Clamp((int)Math.Round(wordCount / wordsPerLine), 1, 16);
    }

    private static IReadOnlyList<ShapeLineSlot> CreateSlots(
        IReadOnlyList<Point> polygon,
        Rect bounds,
        int lineCount,
        double lineHeight,
        double fontSize,
        double shift)
    {
        double blockHeight = lineCount * lineHeight;
        double startY = bounds.Top
                        + (bounds.Height - blockHeight) / 2
                        + shift * bounds.Height;
        if (startY < bounds.Top || startY + blockHeight > bounds.Bottom)
        {
            return [];
        }

        var slots = new List<ShapeLineSlot>(lineCount);
        double margin = Math.Max(2.2, fontSize * 0.17);
        for (int line = 0; line < lineCount; line++)
        {
            double top = startY + line * lineHeight;
            HorizontalSegment? common = null;
            foreach (double sample in LineSamples)
            {
                HorizontalSegment? segment = WidestSegment(
                    polygon,
                    top + lineHeight * sample);
                if (segment is null)
                {
                    common = null;
                    break;
                }

                common = common is null
                    ? segment
                    : new HorizontalSegment(
                        Math.Max(common.Value.Left, segment.Value.Left),
                        Math.Min(common.Value.Right, segment.Value.Right));
                if (common.Value.Width <= margin * 2 + 4)
                {
                    common = null;
                    break;
                }
            }

            if (common is null)
            {
                return [];
            }

            slots.Add(new ShapeLineSlot(
                common.Value.Left + margin,
                top,
                common.Value.Width - margin * 2));
        }

        return slots;
    }

    private static HorizontalSegment? WidestSegment(
        IReadOnlyList<Point> polygon,
        double y)
    {
        var intersections = new List<double>();
        int previous = polygon.Count - 1;
        for (int current = 0; current < polygon.Count; current++)
        {
            Point first = polygon[previous];
            Point second = polygon[current];
            if ((first.Y <= y && second.Y > y)
                || (second.Y <= y && first.Y > y))
            {
                double ratio = (y - first.Y) / (second.Y - first.Y);
                intersections.Add(first.X + (second.X - first.X) * ratio);
            }
            previous = current;
        }

        if (intersections.Count < 2)
        {
            return null;
        }

        intersections.Sort();
        HorizontalSegment? widest = null;
        for (int index = 0; index + 1 < intersections.Count; index += 2)
        {
            var candidate = new HorizontalSegment(
                intersections[index],
                intersections[index + 1]);
            if (candidate.Width > 0
                && (widest is null || candidate.Width > widest.Value.Width))
            {
                widest = candidate;
            }
        }

        return widest;
    }

    private static bool TryBreak(
        IReadOnlyList<string> tokens,
        IReadOnlyList<ShapeLineSlot> slots,
        Typeface typeface,
        double fontSize,
        Brush fill,
        double pixelsPerDip,
        out IReadOnlyList<string>? lines,
        out double score)
    {
        int tokenCount = tokens.Count;
        int lineCount = slots.Count;
        double[] widths = tokens
            .Select(token => Measure(token, typeface, fontSize, fill, pixelsPerDip))
            .ToArray();
        double space = Measure(" ", typeface, fontSize, fill, pixelsPerDip);
        double[,] costs = new double[lineCount + 1, tokenCount + 1];
        int[,] previous = new int[lineCount + 1, tokenCount + 1];

        for (int line = 0; line <= lineCount; line++)
        {
            for (int token = 0; token <= tokenCount; token++)
            {
                costs[line, token] = double.PositiveInfinity;
                previous[line, token] = -1;
            }
        }
        costs[0, 0] = 0;

        for (int line = 0; line < lineCount; line++)
        {
            for (int start = 0; start < tokenCount; start++)
            {
                if (!double.IsFinite(costs[line, start]))
                {
                    continue;
                }

                double width = 0;
                int remainingLines = lineCount - line - 1;
                for (int end = start + 1; end <= tokenCount; end++)
                {
                    width += widths[end - 1] + (end - start > 1 ? space : 0);
                    if (width > slots[line].Width + 0.20)
                    {
                        break;
                    }
                    if (tokenCount - end < remainingLines)
                    {
                        continue;
                    }

                    int wordsOnLine = end - start;
                    double fillRatio = Math.Clamp(width / slots[line].Width, 0, 1);
                    double targetFill = line == lineCount - 1 ? 0.68 : 0.82;
                    double lineCost = Math.Pow(fillRatio - targetFill, 2);
                    lineCost += WordCountPenalty(
                        tokens,
                        start,
                        end,
                        line == lineCount - 1,
                        tokenCount,
                        lineCount);
                    lineCost += BreakBoundaryPenalty(
                        tokens[end - 1],
                        end < tokenCount ? tokens[end] : null);

                    if (fillRatio < 0.34)
                    {
                        lineCost += 0.26;
                    }
                    if (fillRatio > 0.97)
                    {
                        lineCost += 0.05;
                    }
                    if (wordsOnLine == 1 && tokenCount > lineCount)
                    {
                        lineCost += line == lineCount - 1 ? 0.62 : 0.42;
                    }

                    double candidate = costs[line, start] + lineCost;
                    if (candidate < costs[line + 1, end])
                    {
                        costs[line + 1, end] = candidate;
                        previous[line + 1, end] = start;
                    }
                }
            }
        }

        if (!double.IsFinite(costs[lineCount, tokenCount]))
        {
            lines = null;
            score = double.PositiveInfinity;
            return false;
        }

        var result = new string[lineCount];
        int cursor = tokenCount;
        for (int line = lineCount; line > 0; line--)
        {
            int start = previous[line, cursor];
            if (start < 0)
            {
                lines = null;
                score = double.PositiveInfinity;
                return false;
            }
            result[line - 1] = string.Join(
                ' ',
                tokens.Skip(start).Take(cursor - start));
            cursor = start;
        }

        lines = result;
        score = costs[lineCount, tokenCount] / Math.Max(1, lineCount);
        return true;
    }

    private static double WordCountPenalty(
        IReadOnlyList<string> tokens,
        int start,
        int end,
        bool isLastLine,
        int totalTokens,
        int totalLines)
    {
        int wordCount = end - start;
        int characterCount = tokens
            .Skip(start)
            .Take(wordCount)
            .Sum(token => TrimToken(token).Length);

        double penalty = 0;
        if (wordCount == 1 && totalTokens > totalLines)
        {
            penalty += isLastLine ? 0.55 : 0.34;
        }
        else if (wordCount == 2 && characterCount < 9)
        {
            penalty += isLastLine ? 0.20 : 0.10;
        }

        if (characterCount <= 4)
        {
            penalty += 0.28;
        }
        else if (characterCount <= 7)
        {
            penalty += 0.10;
        }

        return penalty;
    }

    private static double BreakBoundaryPenalty(string previousToken, string? nextToken)
    {
        if (nextToken is null)
        {
            return 0;
        }

        string previous = TrimToken(previousToken);
        string next = TrimToken(nextToken);
        double penalty = 0;

        if (EndsWithStrongPause(previousToken))
        {
            penalty -= 0.16;
        }
        else if (EndsWithSoftPause(previousToken))
        {
            penalty -= 0.08;
        }

        if (WeakLineEndWords.Contains(previous))
        {
            penalty += 0.34;
        }
        if (WeakLineStartWords.Contains(next))
        {
            penalty += 0.12;
        }

        if ((previous.Equals("de", StringComparison.OrdinalIgnoreCase)
             || previous.Equals("a", StringComparison.OrdinalIgnoreCase)
             || previous.Equals("con", StringComparison.OrdinalIgnoreCase)
             || previous.Equals("por", StringComparison.OrdinalIgnoreCase)
             || previous.Equals("sin", StringComparison.OrdinalIgnoreCase))
            && ArticlesAndDeterminers.Contains(next))
        {
            penalty += 0.52;
        }

        if (previous.Equals("para", StringComparison.OrdinalIgnoreCase)
            && next.Equals("que", StringComparison.OrdinalIgnoreCase))
        {
            penalty += 0.56;
        }

        if (previous.Equals("que", StringComparison.OrdinalIgnoreCase)
            && (ArticlesAndDeterminers.Contains(next)
                || next.Equals("me", StringComparison.OrdinalIgnoreCase)
                || next.Equals("te", StringComparison.OrdinalIgnoreCase)
                || next.Equals("se", StringComparison.OrdinalIgnoreCase)))
        {
            penalty += 0.48;
        }

        return penalty;
    }

    private static double BalancePenalty(
        IReadOnlyList<string> lines,
        IReadOnlyList<double> fillRatios)
    {
        if (fillRatios.Count == 0)
        {
            return 0;
        }

        double average = fillRatios.Average();
        double variance = fillRatios
            .Select(value => Math.Pow(value - average, 2))
            .Average();
        double penalty = variance * 0.70;

        if (fillRatios.Count > 1)
        {
            double last = fillRatios[^1];
            if (last < 0.34)
            {
                penalty += 0.30;
            }
            if (lines[^1].Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries).Length == 1)
            {
                penalty += 0.42;
            }
        }

        return penalty;
    }

    private static string[] SplitOversizedTokens(
        IReadOnlyList<string> source,
        double maxWidth,
        Typeface typeface,
        double fontSize,
        Brush fill,
        double pixelsPerDip)
    {
        var result = new List<string>();
        foreach (string token in source)
        {
            string remaining = token;
            while (remaining.Length > 4
                   && Measure(
                       remaining,
                       typeface,
                       fontSize,
                       fill,
                       pixelsPerDip) > maxWidth)
            {
                int best = FindSplitPoint(
                    remaining,
                    maxWidth,
                    typeface,
                    fontSize,
                    fill,
                    pixelsPerDip);
                if (best < 2 || remaining.Length - best < 2)
                {
                    break;
                }

                result.Add(remaining[..best] + "-");
                remaining = remaining[best..];
            }

            if (!string.IsNullOrWhiteSpace(remaining))
            {
                result.Add(remaining);
            }
        }

        return result.ToArray();
    }

    private static int FindSplitPoint(
        string word,
        double maxWidth,
        Typeface typeface,
        double fontSize,
        Brush fill,
        double pixelsPerDip)
    {
        int best = 0;
        for (int index = 2; index <= word.Length - 2; index++)
        {
            if (Measure(
                    word[..index] + "-",
                    typeface,
                    fontSize,
                    fill,
                    pixelsPerDip) <= maxWidth)
            {
                best = index;
            }
            else
            {
                break;
            }
        }

        if (best <= 2)
        {
            return best;
        }

        // Se acerca al último corte silábico razonable sin pretender implementar un
        // silabeador completo. Es preferible a partir mecánicamente en cualquier carácter.
        for (int index = best; index >= Math.Max(2, best - 3); index--)
        {
            if (IsVowel(word[index - 1]) != IsVowel(word[index]))
            {
                return index;
            }
        }
        return best;
    }

    private static bool IsVowel(char value) =>
        "aeiouáéíóúüAEIOUÁÉÍÓÚÜ".Contains(value);

    private static bool EndsWithStrongPause(string token) =>
        token.EndsWith('.')
        || token.EndsWith('!')
        || token.EndsWith('?')
        || token.EndsWith('…');

    private static bool EndsWithSoftPause(string token) =>
        token.EndsWith(',')
        || token.EndsWith(';')
        || token.EndsWith(':');

    private static string TrimToken(string token) =>
        token.Trim(
            '¡', '!', '¿', '?', '.', ',', ';', ':', '…',
            '"', '\'', '«', '»', '(', ')', '[', ']', '{', '}');

    private static string Normalize(string text) =>
        string.Join(
            ' ',
            text.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static FormattedText CreateText(
        string text,
        Typeface typeface,
        double fontSize,
        Brush fill,
        double pixelsPerDip) =>
        new(
            text,
            Spanish,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            fill,
            pixelsPerDip)
        {
            TextAlignment = TextAlignment.Left,
            Trimming = TextTrimming.None
        };

    private static double Measure(
        string text,
        Typeface typeface,
        double fontSize,
        Brush fill,
        double pixelsPerDip) =>
        CreateText(text, typeface, fontSize, fill, pixelsPerDip)
            .WidthIncludingTrailingWhitespace;

    private static Geometry CreatePolygonGeometry(IReadOnlyList<Point> polygon)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(polygon[0], true, true);
            context.PolyLineTo(polygon.Skip(1).ToArray(), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Rect Bounds(IReadOnlyList<Point> polygon)
    {
        double left = polygon.Min(point => point.X);
        double top = polygon.Min(point => point.Y);
        double right = polygon.Max(point => point.X);
        double bottom = polygon.Max(point => point.Y);
        return new Rect(
            left,
            top,
            Math.Max(0, right - left),
            Math.Max(0, bottom - top));
    }

    private readonly record struct HorizontalSegment(double Left, double Right)
    {
        public double Width => Right - Left;
    }
}

public sealed record ShapeTextLayout(
    double FontSize,
    IReadOnlyList<ShapeTextLine> Lines,
    double Score);

public sealed record ShapeTextLine(string Text, double X, double Y);

public readonly record struct ShapeLineSlot(double Left, double Top, double Width);