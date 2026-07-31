using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Capa local de un único bocadillo. El texto se compone dentro de una selección irregular
/// extraída de la página limpia y se vuelve a colocar como una capa transparente independiente.
/// </summary>
public sealed class InteractiveComicTextElement : FrameworkElement
{
    private static readonly ShapeTextLayoutEngine LayoutEngine = new();
    private static readonly BalloonCropService CropService = new();
    private static readonly FontFamily FixedComicFont = ComicFontResolver.ResolveMangaDialogue();
    private BalloonCrop? _resolvedCrop;
    private bool _subscribed;

    public required ComicRegion Region { get; init; }
    public BalloonCrop? Crop { get; init; }
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
        BalloonCrop? crop = ResolveCrop();
        if (!Region.IsEnabled
            || string.IsNullOrWhiteSpace(text)
            || ActualWidth < 4
            || ActualHeight < 4
            || crop is null
            || crop.LayoutPolygon.Count < 3)
        {
            return;
        }

        Rect destination = ResolveDestination(crop);
        if (destination.Width < 4 || destination.Height < 4)
        {
            return;
        }

        double scaleX = destination.Width / Math.Max(1, crop.InteriorMask.PixelWidth);
        double scaleY = destination.Height / Math.Max(1, crop.InteriorMask.PixelHeight);
        Point[] polygon = crop.LayoutPolygon
            .Select(point => new Point(
                destination.X + point.X * scaleX,
                destination.Y + point.Y * scaleY))
            .ToArray();

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(
            FixedComicFont,
            FontStyles.Normal,
            FontWeights.Bold,
            FontStretches.SemiExpanded);
        Brush fill = ResolveFill(Region.Style.TextColor);
        double fontScale = Region.IsManual ? Region.ManualFontScale : Region.FontScale;

        if (!LayoutEngine.TryLayout(
                text,
                polygon,
                typeface,
                fill,
                pixelsPerDip,
                PageHeight,
                fontScale,
                1.0,
                out ShapeTextLayout? layout))
        {
            return;
        }

        var opacityMask = new ImageBrush(crop.InteriorMask)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            TileMode = TileMode.None,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = destination,
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = new Rect(0, 0, 1, 1)
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

    private BalloonCrop? ResolveCrop()
    {
        if (Crop is not null)
        {
            return Crop;
        }
        if (_resolvedCrop is not null)
        {
            return _resolvedCrop;
        }

        BitmapSource? page = ResolvePageBitmap();
        if (page is null)
        {
            return null;
        }

        _resolvedCrop = CropService.Create(page, Region);
        return _resolvedCrop;
    }

    private BitmapSource? ResolvePageBitmap()
    {
        if (Window.GetWindow(this) is MainWindow window)
        {
            return window.CurrentBalloonSourceBitmap;
        }

        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is Canvas canvas)
            {
                BitmapSource? source = canvas.Children
                    .OfType<Image>()
                    .Select(image => image.Source)
                    .OfType<BitmapSource>()
                    .FirstOrDefault();
                if (source is not null)
                {
                    return source;
                }
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private Rect ResolveDestination(BalloonCrop crop)
    {
        if (PageWidth <= 0 || PageHeight <= 0)
        {
            return new Rect(0, 0, ActualWidth, ActualHeight);
        }

        NormalizedRect frame = Region.RenderBox;
        double frameX = frame.X / 1000 * PageWidth;
        double frameY = frame.Y / 1000 * PageHeight;
        double frameWidth = Math.Max(1, frame.Width / 1000 * PageWidth);
        double frameHeight = Math.Max(1, frame.Height / 1000 * PageHeight);
        double localScaleX = ActualWidth / frameWidth;
        double localScaleY = ActualHeight / frameHeight;
        return new Rect(
            (crop.PageBounds.X - frameX) * localScaleX,
            (crop.PageBounds.Y - frameY) * localScaleY,
            crop.PageBounds.Width * localScaleX,
            crop.PageBounds.Height * localScaleY);
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
        new(fill, Math.Clamp(fontSize * 0.055, 1.0, 3.2))
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
        if (Window.GetWindow(this) is MainWindow window && ResolveCrop() is { } crop)
        {
            window.EnsureBalloonCropFrame(Region, crop);
        }
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
