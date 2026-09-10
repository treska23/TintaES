using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Tarjeta flotante de traducción sobre la página del editor. Usa la misma resolución de
/// bocadillos y el mismo algoritmo de colocación que el Reader, pero no sustituye ni bloquea
/// las herramientas de edición del lienzo.
/// </summary>
public partial class MainWindow
{
    private bool _editorTranslationCardsInstalled;
    private Grid? _editorTranslationHost;
    private Border? _editorTranslationCard;
    private TextBlock? _editorTranslationText;
    private TouchDevice? _editorTranslationTouchDevice;
    private bool _editorTranslationMouseHeld;

    private void InstallEditorTranslationCards()
    {
        if (_editorTranslationCardsInstalled)
        {
            return;
        }

        if (ImageScrollViewer.Parent is not Grid host)
        {
            throw new InvalidOperationException("No se encontró el contenedor visual de la página del editor.");
        }

        _editorTranslationCardsInstalled = true;
        _editorTranslationHost = host;

        _editorTranslationText = new TextBlock
        {
            Foreground = Brushes.Black,
            FontSize = Math.Max(18d, SystemFonts.MessageFontSize * 1.45d),
            FontWeight = SystemFonts.MessageFontWeight,
            FontFamily = SystemFonts.MessageFontFamily,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = Math.Max(24d, SystemFonts.MessageFontSize * 1.9d),
            MaxWidth = 720
        };

        _editorTranslationCard = new Border
        {
            Child = _editorTranslationText,
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(20, 13, 20, 14),
            Margin = new Thickness(0),
            MaxWidth = 780,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        // La tarjeta queda sobre la página, pero la pantalla de progreso siempre queda por encima.
        Panel.SetZIndex(_editorTranslationCard, 900);
        Panel.SetZIndex(BusyOverlay, 1000);
        host.Children.Add(_editorTranslationCard);

        // handledEventsToo es deliberado: los Thumb, el paneo y las herramientas del editor
        // pueden marcar el evento como atendido. La tarjeta solo observa el puntero y no roba
        // la interacción de edición del ratón.
        ImageScrollViewer.AddHandler(
            Mouse.PreviewMouseMoveEvent,
            new MouseEventHandler(EditorTranslation_PreviewMouseMove),
            handledEventsToo: true);
        ImageScrollViewer.AddHandler(
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(EditorTranslation_PreviewMouseDown),
            handledEventsToo: true);
        ImageScrollViewer.AddHandler(
            Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(EditorTranslation_PreviewMouseUp),
            handledEventsToo: true);
        ImageScrollViewer.AddHandler(
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(EditorTranslation_PreviewTouchDown),
            handledEventsToo: true);
        ImageScrollViewer.AddHandler(
            UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>(EditorTranslation_PreviewTouchMove),
            handledEventsToo: true);
        ImageScrollViewer.AddHandler(
            UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>(EditorTranslation_PreviewTouchUp),
            handledEventsToo: true);

        ImageScrollViewer.MouseLeave += EditorTranslation_MouseLeave;
        ImageScrollViewer.LostTouchCapture += (_, _) => EndEditorTouchTranslation();
    }

    private bool CanShowEditorTranslationCard() =>
        _editorTranslationCard is not null
        && _editorTranslationHost is not null
        && _originalBitmap is not null
        && _regions.Count > 0
        && ImageScrollViewer.Visibility == Visibility.Visible
        && BusyOverlay.Visibility != Visibility.Visible
        && !_drawingRegion;

    private ComicRegion? ResolveEditorTranslationRegion(Point pagePoint, bool isTouch)
    {
        if (!CanShowEditorTranslationCard()
            || ImageStage.ActualWidth <= 1
            || ImageStage.ActualHeight <= 1
            || pagePoint.X < 0
            || pagePoint.Y < 0
            || pagePoint.X > ImageStage.ActualWidth
            || pagePoint.Y > ImageStage.ActualHeight)
        {
            return null;
        }

        double x = pagePoint.X / ImageStage.ActualWidth * 1000d;
        double y = pagePoint.Y / ImageStage.ActualHeight * 1000d;
        return isTouch
            ? ComicRegionHitResolver.ResolveForTouch(_regions, x, y)
            : ComicRegionHitResolver.Resolve(_regions, x, y);
    }

    private void EditorTranslation_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_editorTranslationTouchDevice is not null)
        {
            return;
        }

        ComicRegion? region = ResolveEditorTranslationRegion(e.GetPosition(ImageStage), isTouch: false);
        if (region is null)
        {
            HideEditorTranslationCard();
            return;
        }

        ShowEditorTranslationCardAt(region, e.GetPosition(_editorTranslationHost!), isTouch: false);
    }

    private void EditorTranslation_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _editorTranslationTouchDevice is not null)
        {
            return;
        }

        ComicRegion? region = ResolveEditorTranslationRegion(e.GetPosition(ImageStage), isTouch: false);
        if (region is null)
        {
            _editorTranslationMouseHeld = false;
            HideEditorTranslationCard();
            return;
        }

        _editorTranslationMouseHeld = true;
        ShowEditorTranslationCardAt(region, e.GetPosition(_editorTranslationHost!), isTouch: false);
        // No se marca Handled y no se captura el ratón: mover/redimensionar cajas y panear
        // siguen perteneciendo al editor. La tarjeta es únicamente una capa de consulta.
    }

    private void EditorTranslation_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_editorTranslationMouseHeld)
        {
            return;
        }

        _editorTranslationMouseHeld = false;
        ComicRegion? region = ResolveEditorTranslationRegion(e.GetPosition(ImageStage), isTouch: false);
        if (region is null)
        {
            HideEditorTranslationCard();
        }
        else
        {
            ShowEditorTranslationCardAt(region, e.GetPosition(_editorTranslationHost!), isTouch: false);
        }
    }

    private void EditorTranslation_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_editorTranslationMouseHeld && _editorTranslationTouchDevice is null)
        {
            HideEditorTranslationCard();
        }
    }

    private void EditorTranslation_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        if (_editorTranslationTouchDevice is not null
            && _editorTranslationTouchDevice != e.TouchDevice)
        {
            return;
        }

        ComicRegion? region = ResolveEditorTranslationRegion(
            e.GetTouchPoint(ImageStage).Position,
            isTouch: true);
        if (region is null)
        {
            HideEditorTranslationCard();
            return;
        }

        _editorTranslationTouchDevice = e.TouchDevice;
        ShowEditorTranslationCardAt(
            region,
            e.GetTouchPoint(_editorTranslationHost!).Position,
            isTouch: true);
        e.TouchDevice.Capture(ImageScrollViewer);
        e.Handled = true;
    }

    private void EditorTranslation_PreviewTouchMove(object? sender, TouchEventArgs e)
    {
        if (_editorTranslationTouchDevice != e.TouchDevice)
        {
            return;
        }

        ComicRegion? region = ResolveEditorTranslationRegion(
            e.GetTouchPoint(ImageStage).Position,
            isTouch: true);
        if (region is null)
        {
            HideEditorTranslationCard();
        }
        else
        {
            ShowEditorTranslationCardAt(
                region,
                e.GetTouchPoint(_editorTranslationHost!).Position,
                isTouch: true);
        }
        e.Handled = true;
    }

    private void EditorTranslation_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (_editorTranslationTouchDevice != e.TouchDevice)
        {
            return;
        }

        EndEditorTouchTranslation();
        e.Handled = true;
    }

    private void EndEditorTouchTranslation()
    {
        TouchDevice? device = _editorTranslationTouchDevice;
        _editorTranslationTouchDevice = null;
        HideEditorTranslationCard();
        if (device?.Captured is not null)
        {
            device.Capture(null);
        }
    }

    private void ShowEditorTranslationCardAt(ComicRegion region, Point pointer, bool isTouch)
    {
        if (_editorTranslationCard is null
            || _editorTranslationText is null
            || _editorTranslationHost is null)
        {
            return;
        }

        _editorTranslationText.Text = region.HasRenderableTranslation
            ? region.Translation.Trim()
            : ComicRegion.PendingTranslationMarker;
        _editorTranslationText.Foreground = region.HasRenderableTranslation
            ? Brushes.Black
            : new SolidColorBrush(Color.FromRgb(120, 80, 20));
        _editorTranslationCard.Visibility = Visibility.Visible;
        PositionEditorTranslationCard(region, pointer, isTouch);
    }

    private void PositionEditorTranslationCard(ComicRegion region, Point pointer, bool isTouch)
    {
        if (_editorTranslationCard is null
            || _editorTranslationText is null
            || _editorTranslationHost is null
            || _editorTranslationHost.ActualWidth <= 1
            || _editorTranslationHost.ActualHeight <= 1
            || ImageStage.ActualWidth <= 1
            || ImageStage.ActualHeight <= 1)
        {
            return;
        }

        Rect visiblePage = GetVisibleEditorPageBounds();
        if (visiblePage.IsEmpty || visiblePage.Width <= 24 || visiblePage.Height <= 24)
        {
            return;
        }

        Rect bubbleBounds = GetEditorRegionBounds(region);
        double baseFontSize = Math.Max(18d, SystemFonts.MessageFontSize * 1.45d);
        double baseLineHeight = Math.Max(24d, SystemFonts.MessageFontSize * 1.9d);
        double availableWidth = Math.Max(120d, visiblePage.Width - 24d);
        double maxCardWidth = Math.Min(780d, availableWidth);

        _editorTranslationCard.HorizontalAlignment = HorizontalAlignment.Left;
        _editorTranslationCard.VerticalAlignment = VerticalAlignment.Top;
        _editorTranslationCard.Margin = new Thickness(0);
        _editorTranslationCard.MaxWidth = maxCardWidth;
        _editorTranslationText.MaxWidth = Math.Max(80d, maxCardWidth - 42d);
        _editorTranslationText.FontSize = baseFontSize;
        _editorTranslationText.LineHeight = baseLineHeight;
        _editorTranslationCard.Measure(new Size(maxCardWidth, double.PositiveInfinity));
        Size cardSize = _editorTranslationCard.DesiredSize;

        double availableHeight = Math.Max(80d, visiblePage.Height - 24d);
        if (cardSize.Height > availableHeight && baseFontSize > 15d)
        {
            double scale = Math.Clamp(availableHeight / Math.Max(1d, cardSize.Height), 0.72d, 1d);
            _editorTranslationText.FontSize = Math.Max(15d, baseFontSize * scale);
            _editorTranslationText.LineHeight = Math.Max(20d, baseLineHeight * scale);
            _editorTranslationCard.Measure(new Size(maxCardWidth, double.PositiveInfinity));
            cardSize = _editorTranslationCard.DesiredSize;
        }

        Point location = ComicReaderWindow.ResolveReaderTranslationCardPlacement(
            visiblePage,
            bubbleBounds,
            pointer,
            cardSize,
            isTouch);
        _editorTranslationCard.Margin = new Thickness(location.X, location.Y, 0, 0);
        Panel.SetZIndex(_editorTranslationCard, 900);
    }

    private Rect GetVisibleEditorPageBounds()
    {
        if (_editorTranslationHost is null)
        {
            return Rect.Empty;
        }

        Point first = ImageStage.TranslatePoint(new Point(0, 0), _editorTranslationHost);
        Point second = ImageStage.TranslatePoint(
            new Point(ImageStage.ActualWidth, ImageStage.ActualHeight),
            _editorTranslationHost);
        var page = new Rect(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Abs(second.X - first.X),
            Math.Abs(second.Y - first.Y));
        var viewer = new Rect(0, 0, _editorTranslationHost.ActualWidth, _editorTranslationHost.ActualHeight);
        page.Intersect(viewer);
        return page;
    }

    private Rect GetEditorRegionBounds(ComicRegion region)
    {
        if (_editorTranslationHost is null)
        {
            return Rect.Empty;
        }

        NormalizedRect hit = ComicRegionHitResolver.ResolveHitBox(region);
        Point first = ImageStage.TranslatePoint(
            new Point(
                hit.X / 1000d * ImageStage.ActualWidth,
                hit.Y / 1000d * ImageStage.ActualHeight),
            _editorTranslationHost);
        Point second = ImageStage.TranslatePoint(
            new Point(
                hit.Right / 1000d * ImageStage.ActualWidth,
                hit.Bottom / 1000d * ImageStage.ActualHeight),
            _editorTranslationHost);
        return new Rect(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Abs(second.X - first.X),
            Math.Abs(second.Y - first.Y));
    }

    private void HideEditorTranslationCard()
    {
        if (_editorTranslationCard is not null)
        {
            _editorTranslationCard.Visibility = Visibility.Collapsed;
        }
    }
}
