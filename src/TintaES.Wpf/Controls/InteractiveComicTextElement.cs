using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Capa local de un único bocadillo. El texto se compone en el recorte independiente y se
/// enmascara con la selección irregular del interior antes de volver a dibujarse en la página.
/// </summary>
public sealed class InteractiveComicTextElement : FrameworkElement
{
    private static readonly ShapeTextLayoutEngine LayoutEngine = new();
    private static readonly FontFamily FixedComicFont = ComicFontResolver.ResolveMangaDialogue();
    private bool _subscribed;

    public required ComicRegion Region { get; init; }
    public required BalloonCrop Crop { get; init; }
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
            || ActualHeight < 4
            || Crop.LayoutPolygon.Count < 3)
        {
            return;
        }

        double sourceWidth = Math.Max(1, Crop.InteriorMask.PixelWidth);
        double sourceHeight = Math.Max(1, Crop.InteriorMask.PixelHeight);
        double scaleX = ActualWidth / sourceWidth;
        double scaleY = ActualHeight / sourceHeight;
        Point[] polygon = Crop.LayoutPolygon
            .Select(point => new Point(point.X * scaleX, point.Y * scaleY))
            .ToArray();

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(
            FixedComicFont,
            FontStyles.Normal,
            FontWeights.Bold,
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
                1.0,
                out ShapeTextLayout? layout))
        {
            return;
        }

        var opacityMask = new ImageBrush(Crop.InteriorMask)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            TileMode = TileMode.None
        };

        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        drawingContext.PushOpacityMask(opacityMask);
        try
        {
            Pen weightStroke = CreateWeightStroke(fill, layout!.FontSize);
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
                    weightStroke,
                    formatted.BuildGeometry(new Point(line.X, line.Y)));
            }
        }
        finally
        {
            drawingContext.Pop();
            drawingContext.Pop();
        }
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

    private static Pen CreateWeightStroke(Brush fill, double fontSize) =>
        new(fill, Math.Clamp(fontSize * 0.045, 0.9, 2.8))
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
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
