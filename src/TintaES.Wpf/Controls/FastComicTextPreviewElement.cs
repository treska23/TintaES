using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Renderizador ligero para el lienzo de edición. No busca la composición óptima dentro del
/// polígono ni construye geometrías de glifos: mide el texto una o dos veces y lo dibuja con
/// DrawText. La exportación continúa usando ComicTextElement para conservar la calidad final.
/// </summary>
public sealed class FastComicTextPreviewElement : FrameworkElement
{
    private bool _subscribed;

    public required ComicRegion Region { get; init; }
    public double PageWidth { get; init; } = 1000;
    public double PageHeight { get; init; } = 1000;

    public FastComicTextPreviewElement()
    {
        IsHitTestVisible = false;
        Loaded += FastComicTextPreviewElement_Loaded;
        Unloaded += FastComicTextPreviewElement_Unloaded;
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
        Brush fill = ParseBrush(Region.Style.TextColor, Brushes.Black);
        Typeface typeface = CreatePreviewTypeface(Region);
        double padding = Math.Max(2, Math.Min(ActualWidth, ActualHeight) * 0.055);
        double availableWidth = Math.Max(2, ActualWidth - padding * 2);
        double availableHeight = Math.Max(2, ActualHeight - padding * 2);
        double fontSize = GetInitialFontSize(text, availableWidth, availableHeight);

        FormattedText formatted = CreateFormattedText(
            text,
            typeface,
            fontSize,
            fill,
            availableWidth,
            pixelsPerDip);

        if (formatted.Height > availableHeight + 0.5)
        {
            double ratio = Math.Clamp(availableHeight / Math.Max(1, formatted.Height), 0.18, 1);
            fontSize = Math.Max(2.5, fontSize * ratio * 0.96);
            formatted = CreateFormattedText(
                text,
                typeface,
                fontSize,
                fill,
                availableWidth,
                pixelsPerDip);
        }

        double y = padding + Math.Max(0, (availableHeight - formatted.Height) / 2);

        // Durante la edición el usuario tiene que ver el texto completo al desplazarlo o
        // ampliarlo. El renderer anterior recortaba por el rectángulo original y daba la falsa
        // impresión de que faltaban letras. El recorte preciso sigue aplicándose al exportar.
        drawingContext.DrawText(formatted, new Point(padding, y));
    }

    private double GetInitialFontSize(string text, double availableWidth, double availableHeight)
    {
        double preferred = Region.Style.FontSize > 0 && PageHeight > 0
            ? Region.Style.FontSize / 1000 * PageHeight
            : Math.Sqrt(availableWidth * availableHeight / Math.Max(4, text.Length)) * 1.35;

        double scale = Math.Clamp(Region.FontScale, 0.35, 1.6);
        double maximum = Math.Max(5, Math.Min(availableHeight * 0.82, availableWidth * 0.42));
        return Math.Clamp(preferred * scale, 2.5, maximum);
    }

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
            MaxTextWidth = availableWidth,
            TextAlignment = Region.Style.Alignment switch
            {
                "left" => TextAlignment.Left,
                "right" => TextAlignment.Right,
                _ => TextAlignment.Center
            },
            Trimming = TextTrimming.None
        };

        double lineHeightRatio = Math.Clamp(Region.Style.LineHeightRatio, 0.82, 1.8);
        formatted.LineHeight = Math.Max(fontSize * 0.9, fontSize * lineHeightRatio);
        return formatted;
    }

    private static Typeface CreatePreviewTypeface(ComicRegion region)
    {
        string family = !string.IsNullOrWhiteSpace(region.Style.FontFamily)
            ? region.Style.FontFamily
            : region.Style.FontCategory switch
            {
                "comic" => "Comic Sans MS",
                "handwritten" => "Segoe Print",
                "condensed" => "Arial Narrow",
                "serif" => "Georgia",
                "display" => "Impact",
                "monospace" => "Consolas",
                _ => "Arial"
            };

        FontStyle style = region.Style.Italic ? FontStyles.Italic : FontStyles.Normal;
        FontWeight weight = FontWeight.FromOpenTypeWeight(Math.Clamp(region.Style.FontWeight, 100, 999));
        return new Typeface(new FontFamily(family), style, weight, FontStretches.Normal);
    }

    private static Brush ParseBrush(string? value, Brush fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
        catch
        {
            return fallback;
        }
    }

    private void FastComicTextPreviewElement_Loaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            return;
        }
        _subscribed = true;
        Region.PropertyChanged += Region_PropertyChanged;
        SynchronizeVisualState();
    }

    private void FastComicTextPreviewElement_Unloaded(object sender, RoutedEventArgs e)
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
        SynchronizeVisualState();
        InvalidateVisual();
    }

    private void SynchronizeVisualState()
    {
        Visibility = Region.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        RenderTransformOrigin = new Point(0.5, 0.5);
        double scale = Math.Clamp(Region.ManualFontScale, 0.25, 2.5);
        RenderTransform = new ScaleTransform(scale, scale);
    }
}
