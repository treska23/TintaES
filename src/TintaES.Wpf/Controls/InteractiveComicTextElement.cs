using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Renderizador interactivo de rotulación automática. Este elemento no tiene fondo, borde ni
/// superficie visual: únicamente dibuja los glifos. El tamaño se calcula buscando el mayor valor
/// que cabe realmente dentro de la zona segura, de modo que una estimación OCR pequeña nunca se
/// convierte en texto microscópico.
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

        if (Region.Style.Uppercase)
        {
            text = text.ToUpper(CultureInfo.GetCultureInfo("es-ES"));
        }
        if (Region.Vertical && Region.Type == "sfx")
        {
            text = string.Join(Environment.NewLine, text.Where(character => !char.IsWhiteSpace(character)));
        }

        IReadOnlyList<Point> safeShape = CreateEffectiveShape();
        Rect contentBounds = GetSafeContentBounds(safeShape);
        if (contentBounds.Width < 2 || contentBounds.Height < 2)
        {
            return;
        }

        double padding = Math.Max(1.5, Math.Min(contentBounds.Width, contentBounds.Height) * 0.022);
        double availableWidth = Math.Max(2, contentBounds.Width - padding * 2);
        double availableHeight = Math.Max(2, contentBounds.Height - padding * 2);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface typeface = CreateTypeface(Region);
        Brush fill = ParseBrush(Region.Style.TextColor, Brushes.Black) ?? Brushes.Black;
        Brush? outline = string.IsNullOrWhiteSpace(Region.Style.OutlineColor)
            ? null
            : ParseBrush(Region.Style.OutlineColor, null);

        (double fontSize, FormattedText formatted) = FindLargestFittingText(
            text,
            typeface,
            fill,
            availableWidth,
            availableHeight,
            pixelsPerDip);

        double minimumY = contentBounds.Top + padding;
        double maximumY = Math.Max(minimumY, contentBounds.Bottom - padding - formatted.Height);
        double preferredCenterY = GetPreferredCenterY(contentBounds);
        double y = Math.Clamp(preferredCenterY - formatted.Height / 2, minimumY, maximumY);
        var origin = new Point(contentBounds.Left + padding, y);

        Geometry clip = safeShape.Count >= 3
            ? CreatePolygonGeometry(safeShape)
            : new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));

        drawingContext.PushClip(clip);
        try
        {
            bool needsGeometry = Region.Style.Shadow || outline is not null;
            if (!needsGeometry)
            {
                drawingContext.DrawText(formatted, origin);
                return;
            }

            Geometry geometry = formatted.BuildGeometry(origin);
            if (Region.Style.Shadow)
            {
                drawingContext.PushTransform(new TranslateTransform(fontSize * 0.06, fontSize * 0.08));
                drawingContext.DrawGeometry(
                    new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)),
                    null,
                    geometry);
                drawingContext.Pop();
            }

            double outlinePixels = Region.Style.OutlineWidth / 1000 * PageWidth;
            Pen? pen = outline is null || outlinePixels <= 0
                ? null
                : new Pen(outline, Math.Max(1, outlinePixels * 2))
                {
                    LineJoin = PenLineJoin.Round
                };
            drawingContext.DrawGeometry(fill, pen, geometry);
        }
        finally
        {
            drawingContext.Pop();
        }
    }

    private (double FontSize, FormattedText Text) FindLargestFittingText(
        string text,
        Typeface typeface,
        Brush fill,
        double availableWidth,
        double availableHeight,
        double pixelsPerDip)
    {
        const double minimum = 2.5;

        // El OCR sirve para identificar el estilo, pero no puede imponer un techo pequeño. El
        // límite superior procede de la geometría del bocadillo y la búsqueda obtiene el mayor
        // tamaño que cabe. FontScale sigue permitiendo reducirlo o ampliarlo manualmente.
        double geometricMaximum = Math.Max(
            7,
            Math.Min(availableHeight * 0.94, Math.Max(availableWidth * 0.58, 18)));
        double scale = Math.Clamp(Region.FontScale, 0.35, 1.6);
        double high = Math.Max(minimum, geometricMaximum * scale);
        double low = minimum;
        double bestSize = minimum;
        FormattedText best = CreateFormattedText(
            text,
            typeface,
            minimum,
            fill,
            availableWidth,
            pixelsPerDip);

        for (int index = 0; index < 14; index++)
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
                bestSize = candidateSize;
                best = candidate;
                low = candidateSize;
            }
            else
            {
                high = candidateSize;
            }
        }

        return (bestSize, best);
    }

    private static bool TextFits(
        FormattedText formatted,
        double availableWidth,
        double availableHeight) =>
        formatted.Height <= availableHeight + 0.5
        && formatted.WidthIncludingTrailingWhitespace <= availableWidth + 0.5;

    private FormattedText CreateFormattedText(
        string text,
        Typeface typeface,
        double fontSize,
        Brush fill,
        double availableWidth,
        double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.GetCultureInfo("es-ES"),
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            fill,
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(2, availableWidth),
            TextAlignment = Region.Style.Alignment switch
            {
                "left" => TextAlignment.Left,
                "right" => TextAlignment.Right,
                _ => TextAlignment.Center
            },
            Trimming = TextTrimming.None
        };
        formatted.LineHeight = Math.Max(
            fontSize * 0.9,
            fontSize * Math.Clamp(Region.Style.LineHeightRatio, 0.82, 1.8));
        return formatted;
    }

    private IReadOnlyList<Point> CreateEffectiveShape()
    {
        IReadOnlyList<Point> detected = CreateLocalPolygon();
        if (detected.Count >= 3)
        {
            return detected;
        }

        double insetX = Math.Max(1.5, ActualWidth * 0.018);
        double insetY = Math.Max(1.5, ActualHeight * 0.018);
        double left = insetX;
        double top = insetY;
        double width = Math.Max(2, ActualWidth - insetX * 2);
        double height = Math.Max(2, ActualHeight - insetY * 2);

        if (Region.Type is "dialogue" or "thought")
        {
            var ellipse = new List<Point>(48);
            double centerX = left + width / 2;
            double centerY = top + height / 2;
            for (int index = 0; index < 48; index++)
            {
                double angle = Math.PI * 2 * index / 48;
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
            .Select(point => new Point(
                Math.Clamp(point.X, 0, ActualWidth),
                Math.Clamp(point.Y, 0, ActualHeight)))
            .Distinct()
            .ToArray();
    }

    private Rect GetSafeContentBounds(IReadOnlyList<Point> polygon)
    {
        if (polygon.Count < 3)
        {
            return new Rect(0, 0, ActualWidth, ActualHeight);
        }

        double left = Math.Clamp(polygon.Min(point => point.X), 0, ActualWidth);
        double top = Math.Clamp(polygon.Min(point => point.Y), 0, ActualHeight);
        double right = Math.Clamp(polygon.Max(point => point.X), left, ActualWidth);
        double bottom = Math.Clamp(polygon.Max(point => point.Y), top, ActualHeight);
        var bounds = new Rect(new Point(left, top), new Point(right, bottom));

        // El margen anterior del 16 % por cada lado eliminaba casi un tercio del bocadillo y
        // producía textos diminutos. La propia silueta y el clip ya protegen el borde.
        double insetRatio = Region.Type is "dialogue" or "thought" ? 0.055 : 0.025;
        double insetX = Math.Max(1, bounds.Width * insetRatio);
        double insetY = Math.Max(1, bounds.Height * insetRatio);
        return new Rect(
            bounds.Left + insetX,
            bounds.Top + insetY,
            Math.Max(2, bounds.Width - insetX * 2),
            Math.Max(2, bounds.Height - insetY * 2));
    }

    private double GetPreferredCenterY(Rect contentBounds)
    {
        if (PageHeight <= 0 || Region.RenderBox.Height <= 0)
        {
            return contentBounds.Top + contentBounds.Height / 2;
        }

        double center = Region.TextBox.Y + Region.TextBox.Height / 2;
        double local = (center - Region.RenderBox.Y) / 1000 * PageHeight;
        return Math.Clamp(local, contentBounds.Top, contentBounds.Bottom);
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

    private static Typeface CreateTypeface(ComicRegion region)
    {
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
            ComicFontResolver.Resolve(region.Style.FontFamily, region.Style.FontCategory),
            region.Style.Italic ? FontStyles.Italic : FontStyles.Normal,
            weight,
            ResolveFontStretch(region.Style.FontWidthRatio));
    }

    private static FontStretch ResolveFontStretch(double ratio) =>
        ratio switch
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

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

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
