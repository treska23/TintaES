using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Renderizador ligero para el lienzo de edición. El modo automático puede ajustar el texto para
/// mostrar una aproximación rápida, pero el modo manual conserva exactamente los saltos y el tamaño
/// congelado por el usuario: nunca vuelve a encoger para entrar en la caja.
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
            DrawManualText(drawingContext, text, typeface, fill, pixelsPerDip);
            return;
        }

        DrawAutomaticText(drawingContext, text, typeface, fill, pixelsPerDip);
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

        FormattedText formatted = CreateWrappedText(
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
            formatted = CreateWrappedText(
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

    private void DrawManualText(
        DrawingContext drawingContext,
        string text,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip)
    {
        string[] lines = text.Split('\n', StringSplitOptions.None)
            .Select(line => line.TrimEnd())
            .ToArray();
        if (lines.Length == 0)
        {
            return;
        }

        double baseSize = GetOrCreateManualBaseFontSize(lines, typeface, fill, pixelsPerDip);
        double fontSize = Math.Max(1.2, baseSize * Math.Clamp(Region.ManualFontScale, 0.25, 2.5));
        double lineHeight = fontSize * Math.Clamp(Region.Style.LineHeightRatio, 0.82, 1.8);
        double blockHeight = lines.Length * lineHeight;
        double y = (ActualHeight - blockHeight) / 2;

        foreach (string line in lines)
        {
            if (!string.IsNullOrEmpty(line))
            {
                FormattedText formatted = CreateSingleLineText(line, typeface, fontSize, fill, pixelsPerDip);
                double width = formatted.WidthIncludingTrailingWhitespace;
                double x = Region.Style.Alignment switch
                {
                    "left" => 0,
                    "right" => ActualWidth - width,
                    _ => (ActualWidth - width) / 2
                };
                drawingContext.DrawText(formatted, new Point(x, y));
            }
            y += lineHeight;
        }
    }

    private double GetOrCreateManualBaseFontSize(
        IReadOnlyList<string> currentLines,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip)
    {
        if (Region.ManualBaseFontSize > 0)
        {
            return Region.ManualBaseFontSize;
        }

        string seedText = NormalizeNewLines(
            string.IsNullOrWhiteSpace(Region.ManualLayoutSeedText)
                ? string.Join("\n", currentLines)
                : Region.ManualLayoutSeedText!);
        if (Region.Style.Uppercase)
        {
            seedText = seedText.ToUpper(CultureInfo.GetCultureInfo("es-ES"));
        }

        string[] seedLines = seedText.Split('\n', StringSplitOptions.None)
            .Select(line => line.TrimEnd())
            .ToArray();
        if (seedLines.Length == 0)
        {
            seedLines = currentLines.ToArray();
        }

        const double minimum = 1.2;
        double padding = Math.Max(2.5, Math.Min(ActualWidth, ActualHeight) * 0.045);
        double availableWidth = Math.Max(2, ActualWidth - padding * 2);
        double availableHeight = Math.Max(2, ActualHeight - padding * 2);
        double automaticMaximum = Math.Max(6, Math.Min(ActualHeight * 0.9, Math.Max(ActualWidth * 0.48, 16)));
        double preferred = Region.Style.FontSize > 0 && PageHeight > 0
            ? Region.Style.FontSize / 1000 * PageHeight * Math.Clamp(Region.FontScale, 0.35, 1.6)
            : automaticMaximum;
        double high = Math.Max(minimum, Math.Min(automaticMaximum, preferred * 1.03));
        double low = minimum;
        double best = minimum;
        double lineHeightRatio = Math.Clamp(Region.Style.LineHeightRatio, 0.82, 1.8);

        for (int iteration = 0; iteration < 14; iteration++)
        {
            double candidate = (low + high) / 2;
            bool fitsHeight = seedLines.Length * candidate * lineHeightRatio <= availableHeight + 0.25;
            bool fitsWidth = seedLines.All(line =>
                string.IsNullOrEmpty(line)
                || CreateSingleLineText(line, typeface, candidate, fill, pixelsPerDip)
                    .WidthIncludingTrailingWhitespace <= availableWidth + 0.25);

            if (fitsHeight && fitsWidth)
            {
                best = candidate;
                low = candidate;
            }
            else
            {
                high = candidate;
            }
        }

        Region.ManualBaseFontSize = Math.Max(minimum, best);
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

    private FormattedText CreateWrappedText(
        string text,
        Typeface typeface,
        double fontSize,
        Brush fill,
        double availableWidth,
        double pixelsPerDip)
    {
        var formatted = CreateSingleLineText(text, typeface, fontSize, fill, pixelsPerDip);
        formatted.MaxTextWidth = availableWidth;
        return formatted;
    }

    private FormattedText CreateSingleLineText(
        string text,
        Typeface typeface,
        double fontSize,
        Brush fill,
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
