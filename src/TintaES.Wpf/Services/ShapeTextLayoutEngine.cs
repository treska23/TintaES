using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TintaES.Wpf.Services;

/// <summary>
/// Compone el texto línea a línea dentro de la silueta del bocadillo. El número de líneas es
/// libre y el algoritmo nunca usa la caja exterior completa como permiso para invadir el dibujo.
/// </summary>
public sealed class ShapeTextLayoutEngine
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

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
        Rect bounds = Bounds(polygon);
        if (polygon.Count < 3 || bounds.Width < 8 || bounds.Height < 8)
        {
            return false;
        }

        // 38 px era demasiado alto para muchas viñetas de páginas de 3.000 px y provocaba
        // que una traducción válida desapareciera por completo. El mínimo normal sigue siendo
        // legible, pero existe un suelo de emergencia para no dejar un bocadillo limpio y vacío.
        double preferredMinimum = Math.Clamp(pageHeight * 0.0065, 16, 24);
        const double absoluteMinimum = 10;
        double maximum = Math.Clamp(
            Math.Min(bounds.Height * 0.60, bounds.Width * 0.36) * Math.Clamp(scale, 0.65, 2.10),
            preferredMinimum,
            180);

        double foundMinimum = preferredMinimum;
        if (!TryAtSize(text, polygon, typeface, fill, pixelsPerDip, foundMinimum, lineHeightRatio, out layout))
        {
            bool found = false;
            for (double candidate = preferredMinimum - 2; candidate >= absoluteMinimum; candidate -= 2)
            {
                if (TryAtSize(text, polygon, typeface, fill, pixelsPerDip, candidate, lineHeightRatio, out layout))
                {
                    foundMinimum = candidate;
                    found = true;
                    break;
                }
            }

            if (!found
                && !TryContainedRectangleFallback(
                    text,
                    polygon,
                    typeface,
                    fill,
                    pixelsPerDip,
                    absoluteMinimum,
                    lineHeightRatio,
                    out layout))
            {
                return false;
            }

            if (!found)
            {
                return layout is not null;
            }
        }

        double low = foundMinimum;
        double high = Math.Max(low, maximum);
        for (int i = 0; i < 13; i++)
        {
            double candidate = (low + high) / 2;
            if (TryAtSize(text, polygon, typeface, fill, pixelsPerDip, candidate, lineHeightRatio, out ShapeTextLayout? next))
            {
                layout = next;
                low = candidate;
            }
            else
            {
                high = candidate;
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
        double lineHeight = fontSize * Math.Clamp(lineHeightRatio, 0.96, 1.18);
        int maxLines = Math.Clamp((int)Math.Floor(bounds.Height / lineHeight), 1, 28);
        string[] rawTokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ShapeTextLayout? best = null;

        for (int lineCount = 1; lineCount <= maxLines; lineCount++)
        {
            foreach (double shift in new[] { 0d, -0.06, 0.06, -0.12, 0.12, -0.18, 0.18 })
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
                    out double score))
                {
                    continue;
                }

                var rendered = new List<ShapeTextLine>(lineCount);
                bool invalid = false;
                for (int i = 0; i < lineCount; i++)
                {
                    FormattedText formatted = CreateText(
                        lines![i],
                        typeface,
                        fontSize,
                        fill,
                        pixelsPerDip);
                    ShapeLineSlot slot = slots[i];
                    if (formatted.WidthIncludingTrailingWhitespace > slot.Width + 0.5)
                    {
                        invalid = true;
                        break;
                    }

                    rendered.Add(new ShapeTextLine(
                        lines[i],
                        slot.Left + Math.Max(
                            0,
                            (slot.Width - formatted.WidthIncludingTrailingWhitespace) / 2),
                        slot.Top + Math.Max(0, (lineHeight - formatted.Height) / 2)));
                }

                if (!invalid)
                {
                    var candidate = new ShapeTextLayout(
                        fontSize,
                        rendered,
                        score + Math.Abs(shift) * 0.18 + lineCount * 0.002);
                    if (best is null || candidate.Score < best.Score)
                    {
                        best = candidate;
                    }
                }
            }
        }

        layout = best;
        return best is not null;
    }

    private static bool TryContainedRectangleFallback(
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
        var candidate = new Rect(
            bounds.Left + bounds.Width * 0.15,
            bounds.Top + bounds.Height * 0.12,
            bounds.Width * 0.70,
            bounds.Height * 0.76);

        for (int attempt = 0; attempt < 30 && candidate.Width >= 8 && candidate.Height >= 8; attempt++)
        {
            if (RectangleInside(candidate, polygon))
            {
                double lineHeight = fontSize * Math.Clamp(lineHeightRatio, 0.96, 1.18);
                int maxLines = Math.Clamp((int)Math.Floor(candidate.Height / lineHeight), 1, 32);
                string[] tokens = SplitOversizedTokens(
                    text.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    candidate.Width,
                    typeface,
                    fontSize,
                    fill,
                    pixelsPerDip);

                for (int lineCount = 1; lineCount <= maxLines; lineCount++)
                {
                    var slots = Enumerable.Range(0, lineCount)
                        .Select(index => new ShapeLineSlot(
                            candidate.Left,
                            candidate.Top + (candidate.Height - lineCount * lineHeight) / 2 + index * lineHeight,
                            candidate.Width))
                        .ToArray();
                    if (TryBreak(tokens, slots, typeface, fontSize, fill, pixelsPerDip, out IReadOnlyList<string>? lines, out double score))
                    {
                        var rendered = new List<ShapeTextLine>(lineCount);
                        for (int i = 0; i < lineCount; i++)
                        {
                            FormattedText formatted = CreateText(lines![i], typeface, fontSize, fill, pixelsPerDip);
                            rendered.Add(new ShapeTextLine(
                                lines[i],
                                slots[i].Left + Math.Max(0, (slots[i].Width - formatted.WidthIncludingTrailingWhitespace) / 2),
                                slots[i].Top + Math.Max(0, (lineHeight - formatted.Height) / 2)));
                        }
                        layout = new ShapeTextLayout(fontSize, rendered, score + 1);
                        return true;
                    }
                }
            }

            candidate = ScaleAroundCenter(candidate, 0.94);
        }

        return false;
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
        double startY = bounds.Top + (bounds.Height - blockHeight) / 2 + shift * bounds.Height;
        if (startY < bounds.Top || startY + blockHeight > bounds.Bottom)
        {
            return [];
        }

        var slots = new List<ShapeLineSlot>(lineCount);
        double margin = Math.Max(1.8, fontSize * 0.12);
        for (int line = 0; line < lineCount; line++)
        {
            double top = startY + line * lineHeight;
            HorizontalSegment? common = null;
            foreach (double y in new[]
                     {
                         top + lineHeight * 0.20,
                         top + lineHeight * 0.50,
                         top + lineHeight * 0.80
                     })
            {
                HorizontalSegment? segment = WidestSegment(polygon, y);
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
                if (common.Value.Right - common.Value.Left <= margin * 2 + 3)
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
                common.Value.Right - common.Value.Left - margin * 2));
        }

        return slots;
    }

    private static HorizontalSegment? WidestSegment(IReadOnlyList<Point> polygon, double y)
    {
        var intersections = new List<double>();
        int previous = polygon.Count - 1;
        for (int current = 0; current < polygon.Count; current++)
        {
            Point a = polygon[previous];
            Point b = polygon[current];
            if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
            {
                double ratio = (y - a.Y) / (b.Y - a.Y);
                intersections.Add(a.X + (b.X - a.X) * ratio);
            }
            previous = current;
        }

        if (intersections.Count < 2)
        {
            return null;
        }

        intersections.Sort();
        HorizontalSegment? widest = null;
        for (int i = 0; i + 1 < intersections.Count; i += 2)
        {
            var candidate = new HorizontalSegment(intersections[i], intersections[i + 1]);
            if (candidate.Right > candidate.Left
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
        int n = tokens.Count;
        int m = slots.Count;
        double[] widths = tokens
            .Select(token => Measure(token, typeface, fontSize, fill, pixelsPerDip))
            .ToArray();
        double space = Measure(" ", typeface, fontSize, fill, pixelsPerDip);
        double[,] cost = new double[m + 1, n + 1];
        int[,] previous = new int[m + 1, n + 1];
        for (int line = 0; line <= m; line++)
        {
            for (int token = 0; token <= n; token++)
            {
                cost[line, token] = double.PositiveInfinity;
                previous[line, token] = -1;
            }
        }
        cost[0, 0] = 0;

        for (int line = 0; line < m; line++)
        {
            for (int start = 0; start < n; start++)
            {
                if (!double.IsFinite(cost[line, start]))
                {
                    continue;
                }

                double width = 0;
                int remainingLines = m - line - 1;
                for (int end = start + 1; end <= n; end++)
                {
                    width += widths[end - 1] + (end - start > 1 ? space : 0);
                    if (width > slots[line].Width + 0.5)
                    {
                        break;
                    }
                    if (n - end < remainingLines)
                    {
                        continue;
                    }

                    double fillRatio = Math.Clamp(width / slots[line].Width, 0, 1);
                    double raggedness = Math.Pow(1 - fillRatio, 2) * (line == m - 1 ? 0.42 : 1);
                    if (end - start == 1 && n > m)
                    {
                        raggedness += 0.10;
                    }
                    double candidate = cost[line, start] + raggedness;
                    if (candidate < cost[line + 1, end])
                    {
                        cost[line + 1, end] = candidate;
                        previous[line + 1, end] = start;
                    }
                }
            }
        }

        if (!double.IsFinite(cost[m, n]))
        {
            lines = null;
            score = double.PositiveInfinity;
            return false;
        }

        var result = new string[m];
        int cursor = n;
        for (int line = m; line > 0; line--)
        {
            int start = previous[line, cursor];
            if (start < 0)
            {
                lines = null;
                score = double.PositiveInfinity;
                return false;
            }
            result[line - 1] = string.Join(' ', tokens.Skip(start).Take(cursor - start));
            cursor = start;
        }

        lines = result;
        score = cost[m, n] / m;
        return true;
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
            while (Measure(remaining, typeface, fontSize, fill, pixelsPerDip) > maxWidth
                   && remaining.Length > 1)
            {
                int low = 1;
                int high = remaining.Length - 1;
                int best = 0;
                while (low <= high)
                {
                    int middle = (low + high) / 2;
                    if (Measure(
                        remaining[..middle] + "-",
                        typeface,
                        fontSize,
                        fill,
                        pixelsPerDip) <= maxWidth)
                    {
                        best = middle;
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle - 1;
                    }
                }

                if (best <= 0)
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

    public static FormattedText CreateText(
        string text,
        Typeface typeface,
        double fontSize,
        Brush fill,
        double pixelsPerDip) =>
        new(text, Spanish, FlowDirection.LeftToRight, typeface, fontSize, fill, pixelsPerDip)
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

    private static bool RectangleInside(Rect rectangle, IReadOnlyList<Point> polygon)
    {
        foreach (Point point in new[]
                 {
                     rectangle.TopLeft,
                     rectangle.TopRight,
                     rectangle.BottomLeft,
                     rectangle.BottomRight,
                     new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top),
                     new Point(rectangle.Left + rectangle.Width / 2, rectangle.Bottom),
                     new Point(rectangle.Left, rectangle.Top + rectangle.Height / 2),
                     new Point(rectangle.Right, rectangle.Top + rectangle.Height / 2)
                 })
        {
            if (!ContainsPoint(polygon, point))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ContainsPoint(IReadOnlyList<Point> polygon, Point point)
    {
        bool inside = false;
        int previous = polygon.Count - 1;
        for (int current = 0; current < polygon.Count; current++)
        {
            Point first = polygon[previous];
            Point second = polygon[current];
            bool crosses = (second.Y > point.Y) != (first.Y > point.Y)
                && point.X < (first.X - second.X) * (point.Y - second.Y)
                    / (first.Y - second.Y) + second.X;
            if (crosses)
            {
                inside = !inside;
            }
            previous = current;
        }
        return inside;
    }

    private static Rect ScaleAroundCenter(Rect rectangle, double scale)
    {
        double width = rectangle.Width * scale;
        double height = rectangle.Height * scale;
        return new Rect(
            rectangle.Left + (rectangle.Width - width) / 2,
            rectangle.Top + (rectangle.Height - height) / 2,
            width,
            height);
    }

    private static Rect Bounds(IReadOnlyList<Point> polygon)
    {
        double left = polygon.Min(point => point.X);
        double top = polygon.Min(point => point.Y);
        double right = polygon.Max(point => point.X);
        double bottom = polygon.Max(point => point.Y);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
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
