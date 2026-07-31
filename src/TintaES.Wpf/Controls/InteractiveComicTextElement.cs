using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Renderizador canónico: fondo totalmente transparente, tipografía de cómic legible y
/// distribución libre de líneas. El texto se calcula exclusivamente dentro de una zona
/// rectangular que está completamente contenida en la silueta segura del bocadillo.
/// </summary>
public sealed class InteractiveComicTextElement : FrameworkElement
{
    private bool _subscribed;

    public required ComicRegion Region { get; init; }
    public double PageWidth { get; init; } = 1000;
    public double PageHeight { get; init; } = 1000;

    public InteractiveComicTextElement()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        Loaded += InteractiveComicTextElement_Loaded;
        Unloaded += InteractiveComicTextElement_Unloaded;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        string text = NormalizeText(Region.DisplayText);
        if (!Region.IsEnabled
            || string.IsNullOrWhiteSpace(text)
            || ActualWidth < 2
            || ActualHeight < 2)
        {
            return;
        }

        IReadOnlyList<Point> safePolygon = CreateLocalSafePolygon();
        Rect contentBounds = FindContainedContentRectangle(safePolygon);
        if (contentBounds.IsEmpty || contentBounds.Width < 4 || contentBounds.Height < 4)
        {
            return;
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(
            ComicFontResolver.Resolve(null, "comic"),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);
        Brush fill = ResolveReadableFill(Region.Style.TextColor);

        if (!TryFindLargestFittingText(
                text,
                typeface,
                fill,
                contentBounds.Width,
                contentBounds.Height,
                pixelsPerDip,
                out double fontSize,
                out FormattedText? formatted))
        {
            // Nunca se fuerza una traducción microscópica. La zona queda pendiente de revisión.
            return;
        }

        double x = contentBounds.Left;
        double y = contentBounds.Top + Math.Max(0, (contentBounds.Height - formatted!.Height) / 2);
        Geometry geometry = formatted.BuildGeometry(new Point(x, y));
        Pen outline = CreateContrastOutline(fill, fontSize);

        Geometry clip = safePolygon.Count >= 3
            ? CreatePolygonGeometry(safePolygon)
            : new RectangleGeometry(contentBounds);

        drawingContext.PushClip(clip);
        try
        {
            drawingContext.DrawGeometry(fill, outline, geometry);
        }
        finally
        {
            drawingContext.Pop();
        }
    }

    private bool TryFindLargestFittingText(
        string text,
        Typeface typeface,
        Brush fill,
        double availableWidth,
        double availableHeight,
        double pixelsPerDip,
        out double fontSize,
        out FormattedText? formatted)
    {
        double minimumReadable = Math.Clamp(PageHeight * 0.014, 22, 56);
        double rawScale = Region.IsManual ? Region.ManualFontScale : Region.FontScale;
        double scale = Math.Clamp(rawScale, 0.85, 1.65);
        double geometricMaximum = Math.Max(
            minimumReadable,
            Math.Min(availableHeight * 0.92, Math.Max(availableWidth * 0.58, minimumReadable)));
        double high = geometricMaximum * scale;
        double low = minimumReadable;

        FormattedText minimumCandidate = CreateFormattedText(
            text,
            typeface,
            minimumReadable,
            fill,
            availableWidth,
            pixelsPerDip);
        if (!TextFits(minimumCandidate, availableWidth, availableHeight))
        {
            fontSize = 0;
            formatted = null;
            return false;
        }

        fontSize = minimumReadable;
        formatted = minimumCandidate;
        for (int index = 0; index < 15; index++)
        {
            double candidateSize = (low + high) / 2;
            FormattedText candidate = CreateFormattedText(
                text,
                typeface,
                candidateSize,
                fill,
                availableWidth,
                pixelsPerDip);

            if (TextFits(candidate, availableWidth, availableHeight))
            {
                fontSize = candidateSize;
                formatted = candidate;
                low = candidateSize;
            }
            else
            {
                high = candidateSize;
            }
        }

        return true;
    }

    private static bool TextFits(
        FormattedText formatted,
        double availableWidth,
        double availableHeight) =>
        formatted.Height <= availableHeight + 0.5
        && formatted.WidthIncludingTrailingWhitespace <= availableWidth + 0.5;

    private static FormattedText CreateFormattedText(
        string text,
        Typeface typeface,
        double fontSize,
        Brush fill,
        double availableWidth,
        double pixelsPerDip)
    {
        return new FormattedText(
            text,
            CultureInfo.GetCultureInfo("es-ES"),
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            fill,
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(2, availableWidth),
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.None,
            LineHeight = fontSize * 1.02
        };
    }

    private IReadOnlyList<Point> CreateLocalSafePolygon()
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
            .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
            .Select(point => new Point(
                Math.Clamp(point.X, 0, ActualWidth),
                Math.Clamp(point.Y, 0, ActualHeight)))
            .Distinct()
            .ToArray();
    }

    private Rect FindContainedContentRectangle(IReadOnlyList<Point> polygon)
    {
        if (polygon.Count < 3)
        {
            double insetX = Math.Max(4, ActualWidth * 0.075);
            double insetY = Math.Max(4, ActualHeight * 0.095);
            return new Rect(
                insetX,
                insetY,
                Math.Max(2, ActualWidth - insetX * 2),
                Math.Max(2, ActualHeight - insetY * 2));
        }

        double left = Math.Clamp(polygon.Min(point => point.X), 0, ActualWidth);
        double top = Math.Clamp(polygon.Min(point => point.Y), 0, ActualHeight);
        double right = Math.Clamp(polygon.Max(point => point.X), left, ActualWidth);
        double bottom = Math.Clamp(polygon.Max(point => point.Y), top, ActualHeight);
        var candidate = new Rect(left, top, right - left, bottom - top);

        double initialInsetX = Math.Max(3, candidate.Width * 0.045);
        double initialInsetY = Math.Max(3, candidate.Height * 0.055);
        candidate = Inset(candidate, initialInsetX, initialInsetY);

        // Reducimos simétricamente hasta que toda la caja —no solo sus esquinas— esté dentro
        // del polígono. Así el ajuste de texto nunca considera parte de la ilustración como espacio.
        for (int attempt = 0; attempt < 90; attempt++)
        {
            if (candidate.Width < 4 || candidate.Height < 4)
            {
                return Rect.Empty;
            }

            if (RectangleIsSafelyInside(candidate, polygon))
            {
                return candidate;
            }

            candidate = ScaleAroundCenter(candidate, 0.965);
        }

        return Rect.Empty;
    }

    private static bool RectangleIsSafelyInside(Rect rectangle, IReadOnlyList<Point> polygon)
    {
        const int samplesPerEdge = 12;
        double margin = Math.Max(1.5, Math.Min(rectangle.Width, rectangle.Height) * 0.012);

        for (int index = 0; index <= samplesPerEdge; index++)
        {
            double ratio = index / (double)samplesPerEdge;
            double x = rectangle.Left + rectangle.Width * ratio;
            double y = rectangle.Top + rectangle.Height * ratio;

            if (!PointIsSafelyInside(new Point(x, rectangle.Top), polygon, margin)
                || !PointIsSafelyInside(new Point(x, rectangle.Bottom), polygon, margin)
                || !PointIsSafelyInside(new Point(rectangle.Left, y), polygon, margin)
                || !PointIsSafelyInside(new Point(rectangle.Right, y), polygon, margin))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PointIsSafelyInside(Point point, IReadOnlyList<Point> polygon, double margin)
    {
        if (!ContainsPoint(polygon, point))
        {
            return false;
        }

        double marginSquared = margin * margin;
        int previous = polygon.Count - 1;
        for (int current = 0; current < polygon.Count; current++)
        {
            if (DistanceToSegmentSquared(point, polygon[previous], polygon[current]) < marginSquared)
            {
                return false;
            }
            previous = current;
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

    private static double DistanceToSegmentSquared(Point point, Point first, Point second)
    {
        double deltaX = second.X - first.X;
        double deltaY = second.Y - first.Y;
        double lengthSquared = deltaX * deltaX + deltaY * deltaY;
        if (lengthSquared <= double.Epsilon)
        {
            double pointX = point.X - first.X;
            double pointY = point.Y - first.Y;
            return pointX * pointX + pointY * pointY;
        }

        double projection = Math.Clamp(
            ((point.X - first.X) * deltaX + (point.Y - first.Y) * deltaY) / lengthSquared,
            0,
            1);
        double nearestX = first.X + projection * deltaX;
        double nearestY = first.Y + projection * deltaY;
        double distanceX = point.X - nearestX;
        double distanceY = point.Y - nearestY;
        return distanceX * distanceX + distanceY * distanceY;
    }

    private static Rect Inset(Rect rectangle, double horizontal, double vertical) =>
        new(
            rectangle.Left + horizontal,
            rectangle.Top + vertical,
            Math.Max(0, rectangle.Width - horizontal * 2),
            Math.Max(0, rectangle.Height - vertical * 2));

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

    private static Brush ResolveReadableFill(string? detectedColor)
    {
        Color detected = Colors.Black;
        try
        {
            if (!string.IsNullOrWhiteSpace(detectedColor)
                && ColorConverter.ConvertFromString(detectedColor) is Color parsed)
            {
                detected = parsed;
            }
        }
        catch (FormatException)
        {
        }

        int luminance = (detected.R * 3 + detected.G * 6 + detected.B) / 10;
        return luminance >= 150 ? Brushes.White : Brushes.Black;
    }

    private static Pen CreateContrastOutline(Brush fill, double fontSize)
    {
        Brush outline = ReferenceEquals(fill, Brushes.White) ? Brushes.Black : Brushes.White;
        return new Pen(outline, Math.Clamp(fontSize * 0.035, 0.8, 2.0))
        {
            LineJoin = PenLineJoin.Round
        };
    }

    private static string NormalizeText(string text) =>
        string.Join(
            ' ',
            text.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private void InteractiveComicTextElement_Loaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Region.PropertyChanged += Region_PropertyChanged;
        Visibility = Region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InteractiveComicTextElement_Unloaded(object sender, RoutedEventArgs e)
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
        Visibility = Region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        InvalidateVisual();
    }
}
