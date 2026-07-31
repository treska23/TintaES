using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf.Controls;

/// <summary>
/// Capa local de un único bocadillo. El recorte y la rotulación final se cachean; OnRender solo
/// pinta recursos congelados. Nunca crea una versión microscópica del texto para fingir que cabe.
/// </summary>
public sealed class InteractiveComicTextElement : FrameworkElement
{
    private static readonly ShapeTextLayoutEngine LayoutEngine = new();
    private static readonly BalloonCropService CropService = new();
    private static readonly FontFamily FixedComicFont = ComicFontResolver.ResolveMangaDialogue();
    private static readonly ConcurrentDictionary<RenderCacheKey, RenderCacheEntry> RenderCache = new();

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

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (VisualTreeHelper.GetParent(this) is Grid grid)
        {
            // El rectángulo del Grid es soporte técnico. La barrera física es la máscara.
            grid.ClipToBounds = false;
        }
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

        BalloonCrop? crop = ResolveCrop();
        if (crop is null
            || crop.LayoutPolygon.Count < 3
            || (!crop.IsReliableContainer && !Region.IsManual))
        {
            return;
        }

        Rect destination = ResolveDestination(crop);
        if (destination.Width < 4 || destination.Height < 4)
        {
            return;
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double fontScale = Region.IsManual ? Region.ManualFontScale : Region.FontScale;
        RenderCacheKey key = RenderCacheKey.Create(
            Region,
            crop,
            text,
            ActualWidth,
            ActualHeight,
            PageWidth,
            PageHeight,
            destination,
            pixelsPerDip,
            fontScale);

        if (RenderCache.Count > 512)
        {
            RenderCache.Clear();
        }

        RenderCacheEntry entry = RenderCache.GetOrAdd(
            key,
            _ => new RenderCacheEntry(
                BuildPlan(crop, text, destination, pixelsPerDip, fontScale)));
        CachedRenderPlan? plan = entry.Plan;
        if (plan is null)
        {
            return;
        }

        drawingContext.PushClip(
            new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        drawingContext.PushOpacityMask(plan.OpacityMask);
        try
        {
            // Se cubre el interior completo de la selección antes de escribir. Así no quedan
            // letras inglesas aunque la limpieza previa solo hubiese borrado una parte.
            drawingContext.DrawRectangle(
                plan.Background,
                null,
                plan.Destination);
            drawingContext.DrawGeometry(
                plan.Text,
                plan.WeightStroke,
                plan.TextGeometry);
        }
        finally
        {
            drawingContext.Pop();
            drawingContext.Pop();
        }
    }

    private CachedRenderPlan? BuildPlan(
        BalloonCrop crop,
        string text,
        Rect destination,
        double pixelsPerDip,
        double fontScale)
    {
        double scaleX = destination.Width / Math.Max(1, crop.InteriorMask.PixelWidth);
        double scaleY = destination.Height / Math.Max(1, crop.InteriorMask.PixelHeight);
        Point[] polygon = crop.LayoutPolygon
            .Select(point => new Point(
                destination.X + point.X * scaleX,
                destination.Y + point.Y * scaleY))
            .ToArray();
        if (polygon.Length < 3)
        {
            return null;
        }

        Brush textBrush = ResolveTextBrush(crop.InteriorColor);
        var typeface = new Typeface(
            FixedComicFont,
            FontStyles.Normal,
            FontWeights.Black,
            FontStretches.SemiExpanded);
        Geometry container = CreatePolygonGeometry(polygon);

        ShapeTextLayout? acceptedLayout = null;
        Geometry? acceptedGeometry = null;
        double adjustedScale = Math.Clamp(fontScale, 0.70, 1.80);

        // Si la primera composición roza el contorno, se vuelve a componer. No se escala a
        // posteriori hasta el 64 %, que era el origen de las letras microscópicas de r35.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (LayoutEngine.TryLayout(
                    text,
                    polygon,
                    typeface,
                    textBrush,
                    pixelsPerDip,
                    PageHeight,
                    adjustedScale,
                    1.08,
                    out ShapeTextLayout? layout))
            {
                Geometry geometry = CreateLayoutGeometry(
                    layout!,
                    typeface,
                    textBrush,
                    pixelsPerDip);
                if (!geometry.Bounds.IsEmpty
                    && container.FillContainsWithDetail(geometry)
                       == IntersectionDetail.FullyContains)
                {
                    acceptedLayout = layout;
                    acceptedGeometry = geometry;
                    break;
                }
            }

            adjustedScale *= 0.94;
        }

        if (acceptedLayout is null
            || acceptedGeometry is null
            || acceptedGeometry.Bounds.IsEmpty)
        {
            // El texto queda pendiente en vez de convertirse en una mancha ilegible.
            return null;
        }

        var mask = new ImageBrush(crop.InteriorMask)
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
        mask.Freeze();

        var background = new SolidColorBrush(crop.InteriorColor);
        background.Freeze();

        var stroke = new Pen(
            textBrush,
            Math.Clamp(acceptedLayout.FontSize * 0.060, 1.15, 3.5))
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        stroke.Freeze();

        if (acceptedGeometry.CanFreeze)
        {
            acceptedGeometry.Freeze();
        }

        return new CachedRenderPlan(
            destination,
            mask,
            background,
            textBrush,
            stroke,
            acceptedGeometry);
    }

    private static Geometry CreateLayoutGeometry(
        ShapeTextLayout layout,
        Typeface typeface,
        Brush fill,
        double pixelsPerDip)
    {
        var group = new GeometryGroup();
        foreach (ShapeTextLine line in layout.Lines)
        {
            FormattedText formatted = ShapeTextLayoutEngine.CreateText(
                line.Text,
                typeface,
                layout.FontSize,
                fill,
                pixelsPerDip);
            group.Children.Add(
                formatted.BuildGeometry(new Point(line.X, line.Y)));
        }
        return group;
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

    private static Brush ResolveTextBrush(Color background)
    {
        int luminance = (background.R * 3 + background.G * 6 + background.B) / 10;
        return luminance < 118 ? Brushes.White : Brushes.Black;
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
        if (e.PropertyName == nameof(ComicRegion.Type))
        {
            _resolvedCrop = null;
        }
        InvalidateVisual();
    }

    private sealed record RenderCacheEntry(CachedRenderPlan? Plan);

    private sealed record CachedRenderPlan(
        Rect Destination,
        ImageBrush OpacityMask,
        Brush Background,
        Brush Text,
        Pen WeightStroke,
        Geometry TextGeometry);

    private readonly record struct RenderCacheKey(
        Guid RegionId,
        string Text,
        int MaskIdentity,
        double Width,
        double Height,
        double PageWidth,
        double PageHeight,
        double DestinationX,
        double DestinationY,
        double DestinationWidth,
        double DestinationHeight,
        double FontScale,
        double PixelsPerDip,
        byte BackgroundR,
        byte BackgroundG,
        byte BackgroundB,
        int PolygonHash)
    {
        public static RenderCacheKey Create(
            ComicRegion region,
            BalloonCrop crop,
            string text,
            double width,
            double height,
            double pageWidth,
            double pageHeight,
            Rect destination,
            double pixelsPerDip,
            double fontScale)
        {
            var polygonHash = new HashCode();
            foreach (Point point in crop.LayoutPolygon.Take(180))
            {
                polygonHash.Add(Math.Round(point.X, 2));
                polygonHash.Add(Math.Round(point.Y, 2));
            }

            return new RenderCacheKey(
                region.Id,
                text,
                RuntimeHelpers.GetHashCode(crop.InteriorMask),
                Math.Round(width, 2),
                Math.Round(height, 2),
                Math.Round(pageWidth, 2),
                Math.Round(pageHeight, 2),
                Math.Round(destination.X, 2),
                Math.Round(destination.Y, 2),
                Math.Round(destination.Width, 2),
                Math.Round(destination.Height, 2),
                Math.Round(fontScale, 3),
                Math.Round(pixelsPerDip, 3),
                crop.InteriorColor.R,
                crop.InteriorColor.G,
                crop.InteriorColor.B,
                polygonHash.ToHashCode());
        }
    }
}
