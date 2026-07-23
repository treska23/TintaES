using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Renderiza la composición exacta escrita en el cuadro de traducción.
/// Cada salto de línea corresponde a una línea real. Cambiar los saltos NO cambia
/// automáticamente el tamaño: únicamente ManualFontScale controla la escala visual.
/// La silueta segura se usa como recorte, no como excusa para reducir la fuente.
/// </summary>
public sealed class ManualComicTextElement : FrameworkElement
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

        text = NormalizeNewLines(text);
        if (Region.Style.Uppercase)
        {
            text = text.ToUpper(CultureInfo.GetCultureInfo("es-ES"));
        }

        string[] lines = text.Split('\n', StringSplitOptions.None)
            .Select(line => line.TrimEnd())
            .ToArray();
        if (lines.Length == 0)
        {
            return;
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface typeface = CreateTypeface(Region);
        Brush fill = ParseBrush(Region.Style.TextColor, Brushes.Black) ?? Brushes.Black;
        Brush? outline = string.IsNullOrWhiteSpace(Region.Style.OutlineColor)
            ? null
            : ParseBrush(Region.Style.OutlineColor, null);

        IReadOnlyList<Point> safeShape = CreateEffectiveShape();
        Geometry? clip = safeShape.Count >= 3 ? CreatePolygonGeometry(safeShape) : null;
        ManualLayout layout = CreateFixedLayout(lines, typeface, fill, pixelsPerDip, safeShape);
        Geometry geometry = BuildGeometry(layout, typeface, fill, pixelsPerDip);

        if (clip is not null)
        {
            drawingContext.PushClip(clip);
        }

        if (Region.Style.Shadow)
        {
            drawingContext.PushTransform(new TranslateTransform(layout.FontSize * 0.06, layout.FontSize * 0.08));
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

    private ManualLayout CreateFixedLayout(
        IReadOnlyList<string> lines,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip,
        IReadOnlyList<Point> polygon)
    {
        const double minimumSize = 1.2;
        double automaticMaximum = Math.Max(6, Math.Min(ActualHeight * 0.9, Math.Max(ActualWidth * 0.48, 16)));
        double baseSize = GetPreferredMaximumSize(automaticMaximum, minimumSize);
        double fontSize = Math.Max(minimumSize, baseSize * Math.Clamp(Region.ManualFontScale, 0.25, 2.5));
        double lineHeight = fontSize * Math.Clamp(Region.Style.LineHeightRatio, 0.82, 1.8);
        double outlinePixels = Region.Style.OutlineWidth / 1000 * PageWidth;
        double edgePadding = Math.Max(
            Math.Max(2.5, Math.Min(ActualWidth, ActualHeight) * 0.045),
            outlinePixels + fontSize * 0.10);

        Rect bounds = polygon.Count >= 3
            ? GetPolygonBounds(polygon)
            : new Rect(0, 0, ActualWidth, ActualHeight);
        double usableTop = Math.Max(edgePadding, bounds.Top + edgePadding);
        double usableBottom = Math.Min(ActualHeight - edgePadding, bounds.Bottom - edgePadding);
        double usableLeft = Math.Max(edgePadding, bounds.Left + edgePadding);
        double usableRight = Math.Min(ActualWidth - edgePadding, bounds.Right - edgePadding);
        if (usableRight <= usableLeft)
        {
            usableLeft = edgePadding;
            usableRight = Math.Max(usableLeft + 1, ActualWidth - edgePadding);
        }

        double blockHeight = lines.Count * lineHeight;
        double preferredTop = GetOriginalTextCenterY() - blockHeight / 2;
        double top;
        if (usableBottom - usableTop >= blockHeight)
        {
            top = Math.Clamp(preferredTop, usableTop, usableBottom - blockHeight);
        }
        else
        {
            // En modo manual no reducimos la fuente por cambiar los Enter. Si el bloque es
            // demasiado alto, permanece centrado y el usuario decide la escala o posición.
            top = (usableTop + usableBottom - blockHeight) / 2;
        }

        double preferredCenterX = GetOriginalTextCenterX();
        var placements = new List<ManualLinePlacement>(lines.Count);
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            string lineText = lines[lineIndex];
            double lineTop = top + lineIndex * lineHeight;
            double glyphTop = lineTop + Math.Max(0, (lineHeight - fontSize * 1.02) / 2);
            double glyphBottom = Math.Min(lineTop + lineHeight, glyphTop + fontSize * 1.02);

            HorizontalSpan span;
            if (polygon.Count < 3
                || !TryGetSafeSpanForBand(
                    polygon,
                    glyphTop,
                    glyphBottom,
                    preferredCenterX,
                    edgePadding,
                    out span))
            {
                span = new HorizontalSpan(usableLeft, usableRight);
            }

            placements.Add(new ManualLinePlacement(lineText, span.Left, lineTop, span.Width));
        }

        return new ManualLayout(fontSize, lineHeight, placements);
    }

    private Geometry BuildGeometry(
        ManualLayout layout,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip)
    {
        var group = new GeometryGroup();
        foreach (ManualLinePlacement line in layout.Lines)
        {
            if (string.IsNullOrEmpty(line.Text))
            {
                continue;
            }

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
        if (Region.SafePolygon.Count >= 3 && PageWidth > 0 && PageHeight > 0)
        {
            NormalizedRect box = Region.RenderBox;
            return Region.SafePolygon
                .Select(point => new Point(
                    (point.X - box.X) / 1000 * PageWidth,
                    (point.Y - box.Y) / 1000 * PageHeight))
                .ToArray();
        }

        double insetX = Math.Max(2, ActualWidth * 0.035);
        double insetY = Math.Max(2, ActualHeight * 0.045);
        double left = insetX;
        double top = insetY;
        double width = Math.Max(2, ActualWidth - insetX * 2);
        double height = Math.Max(2, ActualHeight - insetY * 2);
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
        for (int sample = 0; sample < 5; sample++)
        {
            double y = top + height * sample / 4;
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

    private static Rect GetPolygonBounds(IReadOnlyList<Point> polygon)
    {
        double left = polygon.Min(point => point.X);
        double top = polygon.Min(point => point.Y);
        double right = polygon.Max(point => point.X);
        double bottom = polygon.Max(point => point.Y);
        return new Rect(new Point(left, top), new Point(right, bottom));
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

    private static Typeface CreateTypeface(ComicRegion region)
    {
        FontFamily family = ResolveFontFamily(region.Style.FontFamily, region.Style.FontCategory);
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

    private static FontFamily ResolveFontFamily(string? requestedFamily, string category)
    {
        if (!string.IsNullOrWhiteSpace(requestedFamily))
        {
            FontFamily? installed = Fonts.SystemFontFamilies.FirstOrDefault(font =>
                string.Equals(font.Source, requestedFamily.Trim(), StringComparison.OrdinalIgnoreCase));
            if (installed is not null)
            {
                return installed;
            }
        }

        return new FontFamily(category switch
        {
            "handwritten" => "Segoe Print",
            "sans" => "Arial",
            "condensed" => "Arial Narrow",
            "serif" => "Georgia",
            "display" => "Impact",
            "monospace" => "Consolas",
            _ => "Comic Sans MS"
        });
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

    private static Brush? ParseBrush(string? value, Brush? fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value)
                && ColorConverter.ConvertFromString(value) is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
        }
        return fallback;
    }

    private static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    private readonly record struct HorizontalSpan(double Left, double Right)
    {
        public double Width => Math.Max(0, Right - Left);
    }

    private sealed record ManualLinePlacement(string Text, double X, double Y, double Width);
    private sealed record ManualLayout(double FontSize, double LineHeight, IReadOnlyList<ManualLinePlacement> Lines);
}