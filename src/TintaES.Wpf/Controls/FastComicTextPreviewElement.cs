using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Renderizador ligero para el lienzo. El modo manual funciona como una caja de texto normal:
/// el ancho de la caja produce los saltos y ManualFontScale solo cambia el tamaño tipográfico.
/// No consulta la máscara ni ejecuta búsquedas de ajuste durante la edición.
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
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
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

        text = NormalizeNewLines(text);
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

        if (Region.IsManual && Region.Type != "sfx")
        {
            DrawTextFrame(drawingContext, text, typeface, fill, pixelsPerDip);
        }
        else
        {
            DrawAutomaticText(drawingContext, text, typeface, fill, pixelsPerDip);
        }
    }

    private void DrawTextFrame(
        DrawingContext drawingContext,
        string text,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip)
    {
        double padding = Math.Max(2, Math.Min(ActualWidth, ActualHeight) * 0.035);
        double availableWidth = Math.Max(2, ActualWidth - padding * 2);
        double baseSize = GetManualBaseFontSize();
        double fontSize = Math.Max(1.2, baseSize * Math.Clamp(Region.ManualFontScale, 0.25, 2.5));

        FormattedText formatted = CreateFormattedText(
            text,
            typeface,
            fontSize,
            fill,
            availableWidth,
            pixelsPerDip);

        // Igual que una caja de texto de Word/Photoshop: el ancho envuelve palabras, pero la altura
        // no reduce la fuente. Si el bloque supera la caja, permanece visible para que el usuario
        // redimensione la caja o reduzca el tamaño conscientemente.
        double y = (ActualHeight - formatted.Height) / 2;
        drawingContext.DrawText(formatted, new Point(padding, y));
    }

    private void DrawAutomaticText(
        DrawingContext drawingContext,
        string text,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip)
    {
        double padding = Math.Max(2, Math.Min(ActualWidth, ActualHeight) * 0.055);
        double availableWidth = Math.Max(2, ActualWidth - padding * 2);
        double availableHeight = Math.Max(2, ActualHeight - padding * 2);
        double fontSize = GetAutomaticFontSize(text, availableWidth, availableHeight);

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
        drawingContext.DrawText(formatted, new Point(padding, y));
    }

    private double GetManualBaseFontSize()
    {
        if (Region.ManualBaseFontSize > 0 && double.IsFinite(Region.ManualBaseFontSize))
        {
            return Region.ManualBaseFontSize;
        }

        double baseSize;
        if (Region.Style.FontSize > 0 && PageHeight > 0)
        {
            baseSize = Region.Style.FontSize / 1000 * PageHeight;
        }
        else
        {
            int lines = Math.Max(1, Region.Style.OriginalLineCount);
            double ratio = Math.Clamp(Region.Style.LineHeightRatio, 0.82, 1.8);
            baseSize = ActualHeight * 0.72 / (lines * ratio);
        }

        Region.ManualBaseFontSize = Math.Max(1.2, baseSize);
        return Region.ManualBaseFontSize;
    }

    private double GetAutomaticFontSize(string text, double availableWidth, double availableHeight)
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

    private static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

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
        RenderTransform = Transform.Identity;
    }
}