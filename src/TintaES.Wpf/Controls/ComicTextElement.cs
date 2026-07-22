using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf.Controls;

public sealed class ComicTextElement : FrameworkElement
{
    public required ComicRegion Region { get; init; }
    public double PageWidth { get; init; } = 1000;
    public double PageHeight { get; init; } = 1000;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        string text = Region.DisplayText;
        if (!Region.IsEnabled || string.IsNullOrWhiteSpace(text) || ActualWidth < 2 || ActualHeight < 2)
        {
            return;
        }

        if (Region.Style.Uppercase)
        {
            text = text.ToUpper(CultureInfo.GetCultureInfo("es-ES"));
        }
        if (Region.Vertical && Region.Type == "sfx")
        {
            text = string.Join(Environment.NewLine, text.Where(character => !char.IsWhiteSpace(character)));
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface typeface = CreateTypeface(Region);
        Brush fill = ParseBrush(Region.Style.TextColor, Brushes.Black) ?? Brushes.Black;
        Brush? outline = string.IsNullOrWhiteSpace(Region.Style.OutlineColor)
            ? null
            : ParseBrush(Region.Style.OutlineColor, null);

        IReadOnlyList<Point> polygon = CreateLocalPolygon();
        Geometry geometry;
        double renderedSize;
        Geometry? clip = null;
        if (!Region.Vertical
            && polygon.Count >= 3
            && TryFitPolygon(text, typeface, fill, pixelsPerDip, polygon, out TextLayout? shaped))
        {
            geometry = BuildShapedGeometry(shaped!, typeface, fill, pixelsPerDip);
            renderedSize = shaped!.FontSize;
            clip = CreatePolygonGeometry(polygon);
        }
        else
        {
            (geometry, renderedSize) = BuildRectangularGeometry(text, typeface, fill, pixelsPerDip);
        }

        if (clip is not null)
        {
            drawingContext.PushClip(clip);
        }
        if (Region.Style.Shadow)
        {
            drawingContext.PushTransform(new TranslateTransform(renderedSize * 0.06, renderedSize * 0.08));
            drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, geometry);
            drawingContext.Pop();
        }

        double outlinePixels = Region.Style.OutlineWidth / 1000 * PageWidth;
        Pen? pen = outline is null || outlinePixels <= 0
            ? null
            : new Pen(outline, Math.Max(1, outlinePixels * 2)) { LineJoin = PenLineJoin.Round };
        drawingContext.DrawGeometry(fill, pen, geometry);
        if (clip is not null)
        {
            drawingContext.Pop();
        }
    }

    private bool TryFitPolygon(
        string text,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip,
        IReadOnlyList<Point> polygon,
        out TextLayout? layout)
    {
        double low = 3.5;
        double high = Math.Max(6, Math.Min(ActualHeight * 0.9, Math.Max(ActualWidth * 0.48, 16)));
        TextLayout? best = null;
        for (int index = 0; index < 15; index++)
        {
            double size = (low + high) / 2;
            if (TryCreatePolygonLayout(text, typeface, fill, pixelsPerDip, polygon, size, out TextLayout? candidate))
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
            && !TryCreatePolygonLayout(text, typeface, fill, pixelsPerDip, polygon, low, out best))
        {
            layout = null;
            return false;
        }

        double requestedSize = Math.Max(3.5, best!.FontSize * Region.FontScale);
        if (requestedSize < best.FontSize
            && TryCreatePolygonLayout(text, typeface, fill, pixelsPerDip, polygon, requestedSize, out TextLayout? scaled))
        {
            best = scaled;
        }
        layout = best;
        return true;
    }

    private bool TryCreatePolygonLayout(
        string text,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip,
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

        double lineHeight = fontSize * 1.08;
        double padding = Math.Max(1.5, Math.Min(ActualWidth, ActualHeight) * 0.025);
        int maxLines = Math.Min(words.Length, Math.Max(1, (int)Math.Floor((ActualHeight - padding * 2) / lineHeight)));
        TextLayout? best = null;
        double bestScore = double.PositiveInfinity;

        for (int lineCount = 1; lineCount <= maxLines; lineCount++)
        {
            double blockHeight = lineCount * lineHeight;
            double top = (ActualHeight - blockHeight) / 2;
            if (top < padding)
            {
                continue;
            }

            var spans = new HorizontalSpan[lineCount];
            bool usable = true;
            for (int line = 0; line < lineCount; line++)
            {
                double centreY = top + (line + 0.5) * lineHeight;
                if (!TryGetHorizontalSpan(polygon, centreY, out HorizontalSpan span))
                {
                    usable = false;
                    break;
                }
                span = new HorizontalSpan(span.Left + padding, span.Right - padding);
                if (span.Width <= fontSize * 0.8)
                {
                    usable = false;
                    break;
                }
                spans[line] = span;
            }
            if (!usable)
            {
                continue;
            }

            if (!TryBreakWords(words, spans, typeface, fontSize, fill, pixelsPerDip, out int[]? breaks, out double score))
            {
                continue;
            }
            if (score >= bestScore)
            {
                continue;
            }

            var placements = new List<LinePlacement>(lineCount);
            int start = 0;
            for (int line = 0; line < lineCount; line++)
            {
                int end = breaks![line];
                placements.Add(new LinePlacement(
                    string.Join(' ', words[start..end]),
                    spans[line].Left,
                    top + line * lineHeight,
                    spans[line].Width));
                start = end;
            }
            bestScore = score;
            best = new TextLayout(fontSize, lineHeight, placements);
        }

        layout = best;
        return best is not null;
    }

    private static bool TryBreakWords(
        string[] words,
        IReadOnlyList<HorizontalSpan> spans,
        Typeface typeface,
        double fontSize,
        Brush fill,
        double pixelsPerDip,
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
                    double width = MeasureText(candidate, typeface, fontSize, fill, pixelsPerDip);
                    if (width > spans[line].Width)
                    {
                        break;
                    }
                    double unused = (spans[line].Width - width) / spans[line].Width;
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

    private Geometry BuildShapedGeometry(
        TextLayout layout,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip)
    {
        var group = new GeometryGroup();
        foreach (LinePlacement line in layout.Lines)
        {
            FormattedText formatted = CreateLineFormatted(line.Text, typeface, layout.FontSize, fill, pixelsPerDip);
            double width = formatted.WidthIncludingTrailingWhitespace;
            double x = Region.Style.Alignment switch
            {
                "left" => line.X,
                "right" => line.X + line.Width - width,
                _ => line.X + (line.Width - width) / 2
            };
            group.Children.Add(formatted.BuildGeometry(new Point(x, line.Y)));
        }
        group.Freeze();
        return group;
    }

    private (Geometry Geometry, double FontSize) BuildRectangularGeometry(
        string text,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip)
    {
        double padding = Math.Max(2, Math.Min(ActualWidth, ActualHeight) * 0.045);
        double availableWidth = Math.Max(2, ActualWidth - padding * 2);
        double availableHeight = Math.Max(2, ActualHeight - padding * 2);
        double low = 4;
        double high = Math.Max(6, Math.Min(availableHeight * 0.92, Math.Max(availableWidth * 0.48, 16)));
        double bestSize = low;
        FormattedText fitted = CreateFormatted(text, typeface, low, fill, availableWidth, pixelsPerDip);
        for (int index = 0; index < 14; index++)
        {
            double size = (low + high) / 2;
            FormattedText candidate = CreateFormatted(text, typeface, size, fill, availableWidth, pixelsPerDip);
            if (candidate.Height <= availableHeight)
            {
                fitted = candidate;
                bestSize = size;
                low = size;
            }
            else
            {
                high = size;
            }
        }

        double scaledSize = Math.Clamp(bestSize * Region.FontScale, 4, bestSize);
        fitted = CreateFormatted(text, typeface, scaledSize, fill, availableWidth, pixelsPerDip);
        fitted.TextAlignment = Region.Style.Alignment switch
        {
            "left" => TextAlignment.Left,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Center
        };
        double originY = Math.Max(padding, (ActualHeight - fitted.Height) / 2);
        return (fitted.BuildGeometry(new Point(padding, originY)), scaledSize);
    }

    private IReadOnlyList<Point> CreateLocalPolygon()
    {
        if (Region.SafePolygon.Count < 3 || PageWidth <= 0 || PageHeight <= 0)
        {
            return [];
        }
        NormalizedRect box = Region.RenderBox;
        return Region.SafePolygon
            .Select(point => new Point(
                (point.X - box.X) / 1000 * PageWidth,
                (point.Y - box.Y) / 1000 * PageHeight))
            .ToArray();
    }

    private static bool TryGetHorizontalSpan(
        IReadOnlyList<Point> polygon,
        double y,
        out HorizontalSpan span)
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
        for (int index = 0; index + 1 < intersections.Count; index += 2)
        {
            var candidate = new HorizontalSpan(intersections[index], intersections[index + 1]);
            if (candidate.Width > best.Width)
            {
                best = candidate;
            }
        }
        span = best;
        return best.Width > 0;
    }

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

    private static double MeasureText(
        string text,
        Typeface typeface,
        double size,
        Brush fill,
        double pixelsPerDip) =>
        CreateLineFormatted(text, typeface, size, fill, pixelsPerDip).WidthIncludingTrailingWhitespace;

    private static FormattedText CreateLineFormatted(
        string text,
        Typeface typeface,
        double size,
        Brush fill,
        double pixelsPerDip) =>
        new(
            text,
            CultureInfo.GetCultureInfo("es-ES"),
            FlowDirection.LeftToRight,
            typeface,
            size,
            fill,
            pixelsPerDip);

    private static FormattedText CreateFormatted(
        string text,
        Typeface typeface,
        double size,
        Brush fill,
        double maxWidth,
        double pixelsPerDip)
    {
        var formatted = CreateLineFormatted(text, typeface, size, fill, pixelsPerDip);
        formatted.MaxTextWidth = Math.Max(1, maxWidth);
        formatted.Trimming = TextTrimming.None;
        formatted.LineHeight = size * 1.08;
        return formatted;
    }

    private static Typeface CreateTypeface(ComicRegion region)
    {
        string family = region.Style.FontCategory switch
        {
            "handwritten" => "Segoe Print",
            "sans" => "Arial",
            "condensed" => "Arial Narrow",
            "serif" => "Georgia",
            "display" => "Impact",
            "monospace" => "Consolas",
            _ => "Comic Sans MS"
        };
        return new Typeface(
            new FontFamily(family),
            region.Style.Italic ? FontStyles.Italic : FontStyles.Normal,
            region.Style.FontWeight >= 650 ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
    }

    private static Brush? ParseBrush(string? value, Brush? fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                object converted = ColorConverter.ConvertFromString(value);
                if (converted is Color color)
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
            }
        }
        catch (FormatException)
        {
            // Usa el color de respaldo mientras el usuario termina de escribirlo.
        }
        return fallback;
    }

    private readonly record struct HorizontalSpan(double Left, double Right)
    {
        public double Width => Math.Max(0, Right - Left);
    }

    private sealed record LinePlacement(string Text, double X, double Y, double Width);
    private sealed record TextLayout(double FontSize, double LineHeight, IReadOnlyList<LinePlacement> Lines);
}
