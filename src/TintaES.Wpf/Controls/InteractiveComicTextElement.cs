using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Renderizador ligero para el lienzo interactivo. Evita que el ajuste tipográfico preciso
/// bloquee el dispatcher de WPF mientras el usuario visualiza o edita una página.
/// La exportación sigue usando ComicTextElement en un hilo STA independiente.
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
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        Loaded += InteractiveComicTextElement_Loaded;
        Unloaded += InteractiveComicTextElement_Unloaded;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        string text = Region.DisplayText;
        if (!Region.IsEnabled || string.IsNullOrWhiteSpace(text) || ActualWidth < 2 || ActualHeight < 2)
        {
            return;
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
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

        double preferredCenterY = GetPreferredCenterY();
        double y = Math.Clamp(
            preferredCenterY - formatted.Height / 2,
            padding,
            Math.Max(padding, ActualHeight - padding - formatted.Height));
        var origin = new Point(padding, y);

        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));

        bool needsGeometry = Region.Style.Shadow || outline is not null;
        if (!needsGeometry)
        {
            drawingContext.DrawText(formatted, origin);
            drawingContext.Pop();
            return;
        }

        Geometry geometry = formatted.BuildGeometry(origin);
        if (Region.Style.Shadow)
        {
            drawingContext.PushTransform(new TranslateTransform(fontSize * 0.06, fontSize * 0.08));
            drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)), null, geometry);
            drawingContext.Pop();
        }

        double outlinePixels = Region.Style.OutlineWidth / 1000 * PageWidth;
        Pen? pen = outline is null || outlinePixels <= 0
            ? null
            : new Pen(outline, Math.Max(1, outlinePixels * 2)) { LineJoin = PenLineJoin.Round };
        drawingContext.DrawGeometry(fill, pen, geometry);
        drawingContext.Pop();
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

    private double GetPreferredCenterY()
    {
        if (PageHeight <= 0 || Region.RenderBox.Height <= 0)
        {
            return ActualHeight / 2;
        }

        double center = Region.TextBox.Y + Region.TextBox.Height / 2;
        double local = (center - Region.RenderBox.Y) / 1000 * PageHeight;
        return Math.Clamp(local, 0, ActualHeight);
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
        formatted.LineHeight = Math.Max(
            fontSize * 0.9,
            fontSize * Math.Clamp(Region.Style.LineHeightRatio, 0.82, 1.8));
        return formatted;
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

    private void InteractiveComicTextElement_Loaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Region.PropertyChanged += Region_PropertyChanged;
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
