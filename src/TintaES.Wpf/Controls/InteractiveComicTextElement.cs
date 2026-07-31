using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Capa de texto transparente compartida por la vista y la exportación. Toda la aplicación usa
/// la misma fuente de cómic y el mismo motor de composición dentro de la silueta del bocadillo.
/// </summary>
public sealed class InteractiveComicTextElement : FrameworkElement
{
    private static readonly ShapeTextLayoutEngine LayoutEngine = new();
    private static readonly FontFamily FixedComicFont = ComicFontResolver.Resolve(null, "comic");
    private bool _subscribed;

    public required ComicRegion Region { get; init; }
    public double PageWidth { get; init; } = 1000;
    public double PageHeight { get; init; } = 1000;

    public InteractiveComicTextElement()
    {
        IsHitTestVisible = false;
        Focusable = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        string text = Normalize(Region.DisplayText);
        if (!Region.IsEnabled
            || string.IsNullOrWhiteSpace(text)
            || ActualWidth < 4
            || ActualHeight < 4)
        {
            return;
        }

        IReadOnlyList<Point> polygon = CreateLocalSafePolygon();
        if (polygon.Count < 3)
        {
            return;
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(
            FixedComicFont,
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);
        Brush fill = ResolveFill(Region.Style.TextColor);
        double scale = Region.IsManual ? Region.ManualFontScale : Region.FontScale;

        if (!LayoutEngine.TryLayout(
                text,
                polygon,
                typeface,
                fill,
                pixelsPerDip,
                PageHeight,
                scale,
                1.02,
                out ShapeTextLayout? layout))
        {
            return;
        }

        drawingContext.PushClip(CreateGeometry(polygon));
        try
        {
            Pen outline = CreateOutline(fill, layout!.FontSize);
            foreach (ShapeTextLine line in layout.Lines)
            {
                FormattedText formatted = ShapeTextLayoutEngine.CreateText(
                    line.Text,
                    typeface,
                    layout.FontSize,
                    fill,
                    pixelsPerDip);
                drawingContext.DrawGeometry(
                    fill,
                    outline,
                    formatted.BuildGeometry(new Point(line.X, line.Y)));
            }
        }
        finally
        {
            drawingContext.Pop();
        }
    }

    private IReadOnlyList<Point> CreateLocalSafePolygon()
    {
        // BubbleBox representa el contenedor completo y es la referencia preferida para
        // maquetar. SafePolygon puede proceder de una máscara de glifos y ser mucho más
        // estrecho que el bocadillo, por lo que solo se usa cuando no hay caja de globo.
        if (Region.BubbleBox is { } bubble)
        {
            Rect local = ToLocal(bubble);
            if (local.Width >= 4 && local.Height >= 4)
            {
                Rect inset = Inset(
                    local,
                    Math.Max(2, local.Width * 0.035),
                    Math.Max(2, local.Height * 0.045));
                return Region.Type is "dialogue" or "thought"
                    ? Ellipse(inset)
                    : Rectangle(inset);
            }
        }

        if (Region.SafePolygon.Count >= 3)
        {
            Point[] detected = Region.SafePolygon
                .Select(ToLocal)
                .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
                .Select(Clamp)
                .Distinct()
                .ToArray();
            if (detected.Length >= 3)
            {
                return detected;
            }
        }

        Rect fallback = ToLocal(Region.TextBox.Expand(0.36, 0.54));
        return fallback.Width >= 4 && fallback.Height >= 4
            ? Rectangle(fallback)
            : [];
    }

    private Point ToLocal(NormalizedPoint point)
    {
        NormalizedRect box = Region.RenderBox;
        return new Point(
            (point.X - box.X) / 1000 * PageWidth,
            (point.Y - box.Y) / 1000 * PageHeight);
    }

    private Rect ToLocal(NormalizedRect source)
    {
        NormalizedRect box = Region.RenderBox;
        double left = Math.Clamp(
            (source.X - box.X) / 1000 * PageWidth,
            0,
            ActualWidth);
        double top = Math.Clamp(
            (source.Y - box.Y) / 1000 * PageHeight,
            0,
            ActualHeight);
        double right = Math.Clamp(
            (source.Right - box.X) / 1000 * PageWidth,
            left,
            ActualWidth);
        double bottom = Math.Clamp(
            (source.Bottom - box.Y) / 1000 * PageHeight,
            top,
            ActualHeight);
        return new Rect(left, top, right - left, bottom - top);
    }

    private Point Clamp(Point point) =>
        new(
            Math.Clamp(point.X, 0, ActualWidth),
            Math.Clamp(point.Y, 0, ActualHeight));

    private static IReadOnlyList<Point> Ellipse(Rect rect)
    {
        var points = new Point[64];
        double centerX = rect.Left + rect.Width / 2;
        double centerY = rect.Top + rect.Height / 2;
        for (int index = 0; index < points.Length; index++)
        {
            double angle = Math.PI * 2 * index / points.Length;
            points[index] = new Point(
                centerX + Math.Cos(angle) * rect.Width / 2,
                centerY + Math.Sin(angle) * rect.Height / 2);
        }
        return points;
    }

    private static IReadOnlyList<Point> Rectangle(Rect rect) =>
    [
        new Point(rect.Left, rect.Top),
        new Point(rect.Right, rect.Top),
        new Point(rect.Right, rect.Bottom),
        new Point(rect.Left, rect.Bottom)
    ];

    private static Rect Inset(Rect rect, double x, double y) =>
        new(
            rect.Left + x,
            rect.Top + y,
            Math.Max(0, rect.Width - x * 2),
            Math.Max(0, rect.Height - y * 2));

    private static Geometry CreateGeometry(IReadOnlyList<Point> polygon)
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

    private static Brush ResolveFill(string? value)
    {
        Color color = Colors.Black;
        try
        {
            if (!string.IsNullOrWhiteSpace(value)
                && ColorConverter.ConvertFromString(value) is Color parsed)
            {
                color = parsed;
            }
        }
        catch (FormatException)
        {
        }

        return (color.R * 3 + color.G * 6 + color.B) / 10 >= 150
            ? Brushes.White
            : Brushes.Black;
    }

    private static Pen CreateOutline(Brush fill, double fontSize) =>
        new(
            ReferenceEquals(fill, Brushes.White) ? Brushes.Black : Brushes.White,
            Math.Clamp(fontSize * 0.028, 0.55, 1.6))
        {
            LineJoin = PenLineJoin.Round
        };

    private static string Normalize(string text) =>
        string.Join(
            ' ',
            text.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Region.PropertyChanged += RegionChanged;
        Visibility = Region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        Region.PropertyChanged -= RegionChanged;
    }

    private void RegionChanged(object? sender, PropertyChangedEventArgs e)
    {
        Visibility = Region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        InvalidateVisual();
    }
}
