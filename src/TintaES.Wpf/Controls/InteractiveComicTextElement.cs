using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Renderizador canónico orientado a legibilidad. Dibuja únicamente glifos transparentes,
/// usa una tipografía estándar y distribuye libremente las líneas para obtener el mayor
/// tamaño que cabe dentro de la caja. No intenta reproducir la composición original.
/// </summary>
public sealed class InteractiveComicTextElement : FrameworkElement
{
    private static readonly FontFamily ReadableFontFamily = new("Arial");
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

        Rect contentBounds = GetReadableContentBounds();
        if (contentBounds.Width < 2 || contentBounds.Height < 2)
        {
            return;
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface typeface = new(
            ReadableFontFamily,
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
            // No se reduce por debajo del umbral legible. Una zona demasiado larga queda
            // pendiente para revisión en vez de producir letras microscópicas o cortadas.
            return;
        }

        double x = contentBounds.Left;
        double y = contentBounds.Top + Math.Max(0, (contentBounds.Height - formatted!.Height) / 2);
        var origin = new Point(x, y);
        Geometry geometry = formatted.BuildGeometry(origin);
        Pen outline = CreateContrastOutline(fill, fontSize);

        drawingContext.PushClip(new RectangleGeometry(contentBounds));
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
        double minimumReadable = Math.Clamp(PageHeight * 0.012, 16, 48);
        double rawScale = Region.IsManual ? Region.ManualFontScale : Region.FontScale;
        double scale = Math.Clamp(rawScale, 0.80, 1.80);
        double geometricMaximum = Math.Max(
            minimumReadable,
            Math.Min(availableHeight * 0.94, Math.Max(availableWidth * 0.62, minimumReadable)));
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
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.None,
            LineHeight = fontSize * 1.02
        };
        return formatted;
    }

    private Rect GetReadableContentBounds()
    {
        double insetX = Math.Max(3, ActualWidth * 0.055);
        double insetY = Math.Max(3, ActualHeight * 0.070);
        return new Rect(
            insetX,
            insetY,
            Math.Max(2, ActualWidth - insetX * 2),
            Math.Max(2, ActualHeight - insetY * 2));
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
        return new Pen(outline, Math.Clamp(fontSize * 0.045, 1, 2.5))
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
