using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf.Controls;

public sealed class ComicTextElement : FrameworkElement
{
    private bool _subscribed;

    public required ComicRegion Region { get; init; }
    public double PageWidth { get; init; } = 1000;
    public double PageHeight { get; init; } = 1000;

    public ComicTextElement()
    {
        IsHitTestVisible = false;
        Loaded += ComicTextElement_Loaded;
        Unloaded += ComicTextElement_Unloaded;
    }

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

        IReadOnlyList<Point> safeShape = CreateEffectiveShape();
        Geometry? clip = safeShape.Count >= 3 ? CreatePolygonGeometry(safeShape) : null;

        Geometry geometry;
        double renderedSize;

        if (!Region.Vertical
            && safeShape.Count >= 3
            && TryFitShape(text, typeface, fill, pixelsPerDip, safeShape, out TextLayout? shaped))
        {
            geometry = BuildShapedGeometry(shaped!, typeface, fill, pixelsPerDip);
            renderedSize = shaped!.FontSize;
        }
        else
        {
            (geometry, renderedSize) = BuildRectangularGeometry(text, typeface, fill, pixelsPerDip);
        }

        // La forma segura siempre manda. Incluso si hubiera que caer al ajuste rectangular,
        // el texto jamás puede dibujarse fuera del bocadillo detectado.
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

    private bool TryFitShape(
        string text,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip,
        IReadOnlyList<Point> polygon,
        out TextLayout? layout)
    {
        const double minimumSize = 2.5;
        double automaticMaximum = Math.Max(6, Math.Min(ActualHeight * 0.9, Math.Max(ActualWidth * 0.48, 16)));
        double high = GetPreferredMaximumSize(automaticMaximum, minimumSize);
        double low = minimumSize;
        TextLayout? best = null;

        // Búsqueda binaria: el mayor tamaño posible solo se acepta si TODAS las líneas
        // caben dentro de la silueta durante toda la altura real de los glifos.
        for (int index = 0; index < 18; index++)
        {
            double size = (low + high) / 2;
            if (TryCreateShapeLayout(text, typeface, fill, pixelsPerDip, polygon, size, out TextLayout? candidate))
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
            && !TryCreateShapeLayout(text, typeface, fill, pixelsPerDip, polygon, minimumSize, out best))
        {
            layout = null;
            return false;
        }

        layout = best;
        return true;
    }

    private bool TryCreateShapeLayout(
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

        double lineHeightRatio = Math.Clamp(Region.Style.LineHeightRatio, 0.82, 1.8);
        double lineHeight = fontSize * lineHeightRatio;
        double outlinePixels = Region.Style.OutlineWidth / 1000 * PageWidth;
        double edgePadding = Math.Max(
            Math.Max(3.5, Math.Min(ActualWidth, ActualHeight) * 0.07),
            outlinePixels + fontSize * 0.14);

        Rect bounds = GetPolygonBounds(polygon);
        double usableTop = Math.Max(edgePadding, bounds.Top + edgePadding);
        double usableBottom = Math.Min(ActualHeight - edgePadding, bounds.Bottom - edgePadding);
        if (usableBottom <= usableTop)
        {
            layout = null;
            return false;
        }

        int maxLinesByHeight = Math.Max(1, (int)Math.Floor((usableBottom - usableTop) / lineHeight));
        int maxLines = Math.Min(words.Length, maxLinesByHeight);
        TextLayout? best = null;
        double bestScore = double.PositiveInfinity;
        double preferredCenterY = GetOriginalTextCenterY();
        double preferredCenterX = GetOriginalTextCenterX();

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
                    // FormattedText puede ocupar prácticamente una em completa. Comprobamos
                    // una banda vertical, no solo el centro de la línea, para evitar que las
                    // esquinas de las letras atraviesen un bocadillo ovalado.
                    double glyphTop = lineTop + Math.Max(0, (lineHeight - fontSize * 1.02) / 2);
                    double glyphBottom = Math.Min(
                        lineTop + lineHeight,
                        glyphTop + fontSize * 1.02);

                    if (!TryGetSafeSpanForBand(
                            polygon,
                            glyphTop,
                            glyphBottom,
                            preferredCenterX,
                            edgePadding,
                            out HorizontalSpan span)
                        || span.Width <= fontSize * 0.9)
                    {
                        usable = false;
                        break;
                    }

                    spans[line] = span;
                }

                if (!usable
                    || !TryBreakWords(words, spans, typeface, fontSize, fill, pixelsPerDip, out int[]? breaks, out double score))
                {
                    continue;
                }

                if (Region.Style.OriginalLineCount > 0)
                {
                    score += Math.Abs(lineCount - Region.Style.OriginalLineCount) * 0.12;
                }

                double actualCenterY = top + blockHeight / 2;
                score += Math.Abs(actualCenterY - preferredCenterY) / Math.Max(1, ActualHeight) * 0.08;

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
        }

        layout = best;
        return best is not null;
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

        // Siete cortes son baratos y suficientemente conservadores para óvalos,
        // polígonos irregulares y bocadillos con laterales inclinados.
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
            Geometry unpositioned = formatted.BuildGeometry(new Point());
            Rect ink = unpositioned.Bounds;
            double originX = Region.Style.Alignment switch
            {
                "left" => line.X - ink.Left,
                "right" => line.X + line.Width - ink.Right,
                _ => line.X + line.Width / 2 - (ink.Left + ink.Right) / 2
            };
            group.Children.Add(formatted.BuildGeometry(new Point(originX, line.Y)));
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
        double padding = Math.Max(3, Math.Min(ActualWidth, ActualHeight) * 0.065);
        double availableWidth = Math.Max(2, ActualWidth - padding * 2);
        double availableHeight = Math.Max(2, ActualHeight - padding * 2);
        const double minimumSize = 2.5;
        double automaticMaximum = Math.Max(6, Math.Min(availableHeight * 0.92, Math.Max(availableWidth * 0.48, 16)));
        double high = GetPreferredMaximumSize(automaticMaximum, minimumSize);
        double low = minimumSize;
        double bestSize = minimumSize;
        FormattedText fitted = CreateFormatted(text, typeface, minimumSize, fill, availableWidth, pixelsPerDip);

        for (int index = 0; index < 18; index++)
        {
            double size = (low + high) / 2;
            FormattedText candidate = CreateFormatted(text, typeface, size, fill, availableWidth, pixelsPerDip);
            if (candidate.Height <= availableHeight
                && candidate.WidthIncludingTrailingWhitespace <= availableWidth + 0.5)
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

        fitted = CreateFormatted(text, typeface, bestSize, fill, availableWidth, pixelsPerDip);
        fitted.TextAlignment = Region.Style.Alignment switch
        {
            "left" => TextAlignment.Left,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Center
        };

        double originY = Math.Clamp(
            GetOriginalTextCenterY() - fitted.Height / 2,
            padding,
            Math.Max(padding, ActualHeight - padding - fitted.Height));

        return (fitted.BuildGeometry(new Point(padding, originY)), bestSize);
    }

    private double GetPreferredMaximumSize(double automaticMaximum, double minimum)
    {
        double scale = Math.Clamp(Region.FontScale, 0.35, 1.6);
        if (Region.Style.FontSize <= 0 || PageHeight <= 0)
        {
            return Math.Clamp(automaticMaximum * scale, minimum, automaticMaximum);
        }

        double originalPixels = Region.Style.FontSize / 1000 * PageHeight;
        return Math.Clamp(originalPixels * 1.03 * scale, minimum, automaticMaximum);
    }

    private double GetOriginalTextCenterY()
    {
        if (PageHeight <= 0 || Region.RenderBox.Height <= 0)
        {
            return ActualHeight / 2;
        }

        double centre = Region.TextBox.Y + Region.TextBox.Height / 2;
        double local = (centre - Region.RenderBox.Y) / 1000 * PageHeight;
        return Math.Clamp(local, 0, ActualHeight);
    }

    private double GetOriginalTextCenterX()
    {
        if (PageWidth <= 0 || Region.RenderBox.Width <= 0)
        {
            return ActualWidth / 2;
        }

        double centre = Region.TextBox.X + Region.TextBox.Width / 2;
        double local = (centre - Region.RenderBox.X) / 1000 * PageWidth;
        return Math.Clamp(local, 0, ActualWidth);
    }

    private IReadOnlyList<Point> CreateEffectiveShape()
    {
        IReadOnlyList<Point> detected = CreateLocalPolygon();
        if (detected.Count >= 3)
        {
            return detected;
        }

        double insetX = Math.Max(2, ActualWidth * 0.035);
        double insetY = Math.Max(2, ActualHeight * 0.045);
        double left = insetX;
        double top = insetY;
        double width = Math.Max(2, ActualWidth - insetX * 2);
        double height = Math.Max(2, ActualHeight - insetY * 2);

        if (Region.Type is "dialogue" or "thought")
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

    private static Rect GetPolygonBounds(IReadOnlyList<Point> polygon)
    {
        double left = polygon.Min(point => point.X);
        double top = polygon.Min(point => point.Y);
        double right = polygon.Max(point => point.X);
        double bottom = polygon.Max(point => point.Y);
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    private static bool TryGetHorizontalSpan(
        IReadOnlyList<Point> polygon,
        double y,
        double preferredX,
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
        double pixelsPerDip)
    {
        FormattedText formatted = CreateLineFormatted(text, typeface, size, fill, pixelsPerDip);
        Rect ink = formatted.BuildGeometry(new Point()).Bounds;
        return ink.IsEmpty
            ? formatted.WidthIncludingTrailingWhitespace
            : ink.Width;
    }

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

    private FormattedText CreateFormatted(
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
        formatted.LineHeight = size * Math.Clamp(Region.Style.LineHeightRatio, 0.82, 1.8);
        return formatted;
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

    private static FontStretch ResolveFontStretch(double ratio)
    {
        return ratio switch
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

    private void ComicTextElement_Loaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Region.PropertyChanged += Region_PropertyChanged;
        SynchronizeVisibility();
        InvalidateVisual();
    }

    private void ComicTextElement_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        Region.PropertyChanged -= Region_PropertyChanged;
    }

    private void Region_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SynchronizeVisibility();
        InvalidateVisual();
    }

    private void SynchronizeVisibility()
    {
        bool usesAccurateAutomaticRenderer = !Region.IsManual || Region.Type == "sfx";
        Visibility = Region.IsEnabled && usesAccurateAutomaticRenderer
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private readonly record struct HorizontalSpan(double Left, double Right)
    {
        public double Width => Math.Max(0, Right - Left);
    }

    private sealed record LinePlacement(string Text, double X, double Y, double Width);
    private sealed record TextLayout(double FontSize, double LineHeight, IReadOnlyList<LinePlacement> Lines);
}
