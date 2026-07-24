using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Render final de una caja de texto manual. El ancho envuelve las palabras y la escala únicamente
/// multiplica el tamaño; nunca vuelve a ajustar la fuente contra la máscara o la altura disponible.
/// </summary>
public sealed class TextFrameComicTextElement : FrameworkElement
{
    public required ComicRegion Region { get; init; }
    public double PageWidth { get; init; } = 1000;
    public double PageHeight { get; init; } = 1000;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        string text = NormalizeNewLines(Region.DisplayText);
        if (!Region.IsEnabled || string.IsNullOrWhiteSpace(text) || ActualWidth < 2 || ActualHeight < 2)
        {
            return;
        }

        if (Region.Style.Uppercase)
        {
            text = text.ToUpper(CultureInfo.GetCultureInfo("es-ES"));
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface typeface = CreateTypeface(Region);
        Brush fill = ParseBrush(Region.Style.TextColor, Brushes.Black) ?? Brushes.Black;
        Brush? outline = string.IsNullOrWhiteSpace(Region.Style.OutlineColor)
            ? null
            : ParseBrush(Region.Style.OutlineColor, null);

        double padding = Math.Max(2, Math.Min(ActualWidth, ActualHeight) * 0.035);
        double availableWidth = Math.Max(2, ActualWidth - padding * 2);
        double baseSize = ResolveBaseSize();
        double fontSize = Math.Max(1.2, baseSize * Math.Clamp(Region.ManualFontScale, 0.25, 2.5));

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

        double y = (ActualHeight - formatted.Height) / 2;
        Geometry geometry = formatted.BuildGeometry(new Point(padding, y));

        if (Region.Style.Shadow)
        {
            drawingContext.PushTransform(new TranslateTransform(fontSize * 0.06, fontSize * 0.08));
            drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, geometry);
            drawingContext.Pop();
        }

        double outlinePixels = Region.Style.OutlineWidth / 1000 * PageWidth;
        Pen? pen = outline is null || outlinePixels <= 0
            ? null
            : new Pen(outline, Math.Max(1, outlinePixels * 2)) { LineJoin = PenLineJoin.Round };
        drawingContext.DrawGeometry(fill, pen, geometry);
    }

    private double ResolveBaseSize()
    {
        if (Region.ManualBaseFontSize > 0 && double.IsFinite(Region.ManualBaseFontSize))
        {
            return Region.ManualBaseFontSize;
        }

        if (Region.Style.FontSize > 0 && PageHeight > 0)
        {
            return Math.Max(1.2, Region.Style.FontSize / 1000 * PageHeight);
        }

        int lines = Math.Max(1, Region.Style.OriginalLineCount);
        double ratio = Math.Clamp(Region.Style.LineHeightRatio, 0.82, 1.8);
        return Math.Max(1.2, ActualHeight * 0.72 / (lines * ratio));
    }

    private static Typeface CreateTypeface(ComicRegion region)
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

    private static Brush? ParseBrush(string? value, Brush? fallback)
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
}