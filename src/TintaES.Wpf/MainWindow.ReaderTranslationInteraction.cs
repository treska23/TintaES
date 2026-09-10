using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Permite consultar una traducción directamente sobre la página principal. El texto
/// detectado es una zona invisible: la página original nunca se modifica ni se tapa.
/// Con ratón se muestra al pasar por encima; en pantalla táctil, mientras el dedo está apoyado.
/// La tarjeta correspondiente del inspector queda seleccionada al entrar en una zona distinta.
/// Ctrl+clic fija esa selección para poder editarla sin que el hover la cambie; Escape la libera.
/// </summary>
public partial class MainWindow
{
    private Grid? _mainTranslationOverlay;
    private Border? _mainTranslationCard;
    private TextBlock? _mainTranslationSpanish;
    private ComicRegion? _mainTranslationSelectionLock;

    private void InstallMainTranslationInteraction()
    {
        if (_mainTranslationOverlay is not null || ImageScrollViewer.Parent is not Grid host)
        {
            return;
        }

        var overlay = new Grid
        {
            Visibility = Visibility.Collapsed,
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(overlay, 1900);

        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 780,
            Margin = new Thickness(0),
            Padding = new Thickness(20, 13, 20, 14),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1)
        };

        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        _mainTranslationSpanish = new TextBlock
        {
            Foreground = Brushes.Black,
            FontSize = Math.Max(18d, SystemFonts.MessageFontSize * 1.45d),
            FontWeight = SystemFonts.MessageFontWeight,
            FontFamily = SystemFonts.MessageFontFamily,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 680,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = Math.Max(24d, SystemFonts.MessageFontSize * 1.9d)
        };
        content.Children.Add(_mainTranslationSpanish);
        card.Child = content;
        overlay.Children.Add(card);

        host.Children.Add(overlay);
        _mainTranslationOverlay = overlay;
        _mainTranslationCard = card;
    }

    private bool TryShowMainTranslationAt(Point imagePoint, bool isTouch)
    {
        ComicRegion? region = ResolveMainTranslationRegionAt(imagePoint);
        if (region is null)
        {
            return false;
        }

        ComicRegion? lockedRegion = GetActiveMainTranslationSelectionLock();
        if (lockedRegion is null
            && (!ReferenceEquals(_selectedRegion, region)
                || !ReferenceEquals(RegionListBox.SelectedItem, region)))
        {
            SelectRegionFromCanvas(region);
        }

        ShowMainTranslation(region);
        PositionMainTranslationCard(region, imagePoint, isTouch);
        return true;
    }

    private ComicRegion? ResolveMainTranslationRegionAt(Point imagePoint)
    {
        if (_mainTranslationOverlay is null
            || _regions.Count == 0
            || ImageStage.ActualWidth <= 1
            || ImageStage.ActualHeight <= 1
            || imagePoint.X < 0
            || imagePoint.Y < 0
            || imagePoint.X > ImageStage.ActualWidth
            || imagePoint.Y > ImageStage.ActualHeight)
        {
            return null;
        }

        NormalizedPoint normalized = NormalizeImagePoint(
            imagePoint.X,
            imagePoint.Y,
            ImageStage.ActualWidth,
            ImageStage.ActualHeight);
        return ResolveMainTranslationRegion(_regions, normalized.X, normalized.Y);
    }

    private ComicRegion? GetActiveMainTranslationSelectionLock()
    {
        if (_mainTranslationSelectionLock is not null
            && !_regions.Contains(_mainTranslationSelectionLock))
        {
            _mainTranslationSelectionLock = null;
        }

        return _mainTranslationSelectionLock;
    }

    private bool ToggleMainTranslationSelectionLockAt(Point imagePoint)
    {
        ComicRegion? region = ResolveMainTranslationRegionAt(imagePoint);
        if (region is null)
        {
            return false;
        }

        if (ReferenceEquals(GetActiveMainTranslationSelectionLock(), region))
        {
            return ReleaseMainTranslationSelectionLock();
        }

        _mainTranslationSelectionLock = region;
        SelectRegionFromCanvas(region);
        SetFooterStatus($"Zona {region.Order} fijada · Ctrl+clic sobre ella o Esc para soltar.", "#4CB2BB");
        return true;
    }

    private bool ReleaseMainTranslationSelectionLock()
    {
        ComicRegion? region = GetActiveMainTranslationSelectionLock();
        if (region is null)
        {
            return false;
        }

        _mainTranslationSelectionLock = null;
        SetFooterStatus($"Zona {region.Order} liberada · el hover vuelve a seguir el puntero.", "#6C747A");
        return true;
    }

    internal static ComicRegion? ResolveMainTranslationRegion(
        IEnumerable<ComicRegion> regions,
        double x,
        double y) => ComicRegionHitResolver.Resolve(regions, x, y);

    internal static NormalizedPoint NormalizeImagePoint(
        double x,
        double y,
        double pageWidth,
        double pageHeight) => new(
        x / Math.Max(1, pageWidth) * 1000d,
        y / Math.Max(1, pageHeight) * 1000d);

    private void ShowMainTranslation(ComicRegion region)
    {
        if (_mainTranslationOverlay is null
            || _mainTranslationSpanish is null)
        {
            return;
        }

        _mainTranslationSpanish.Text = region.HasRenderableTranslation
            ? region.Translation.Trim()
            : "Traducción pendiente";
        _mainTranslationSpanish.Foreground = region.HasRenderableTranslation
            ? Brushes.Black
            : new SolidColorBrush(Color.FromRgb(120, 80, 20));
        _mainTranslationOverlay.Visibility = Visibility.Visible;
    }

    private void PositionMainTranslationCard(ComicRegion region, Point imagePoint, bool isTouch)
    {
        if (_mainTranslationOverlay is null
            || _mainTranslationCard is null
            || _mainTranslationSpanish is null
            || _mainTranslationOverlay.ActualWidth <= 1
            || _mainTranslationOverlay.ActualHeight <= 1
            || ImageStage.ActualWidth <= 1
            || ImageStage.ActualHeight <= 1)
        {
            return;
        }

        Point pageTopLeft = ImageStage.TranslatePoint(new Point(0, 0), _mainTranslationOverlay);
        Point pageBottomRight = ImageStage.TranslatePoint(
            new Point(ImageStage.ActualWidth, ImageStage.ActualHeight),
            _mainTranslationOverlay);
        var pageBounds = new Rect(
            Math.Min(pageTopLeft.X, pageBottomRight.X),
            Math.Min(pageTopLeft.Y, pageBottomRight.Y),
            Math.Abs(pageBottomRight.X - pageTopLeft.X),
            Math.Abs(pageBottomRight.Y - pageTopLeft.Y));
        var viewportBounds = new Rect(
            0,
            0,
            _mainTranslationOverlay.ActualWidth,
            _mainTranslationOverlay.ActualHeight);
        pageBounds.Intersect(viewportBounds);
        if (pageBounds.IsEmpty || pageBounds.Width <= 24 || pageBounds.Height <= 24)
        {
            return;
        }

        NormalizedRect hit = ComicRegionHitResolver.ResolveHitBox(region);
        Point bubbleTopLeft = ImageStage.TranslatePoint(
            new Point(
                hit.X / 1000d * ImageStage.ActualWidth,
                hit.Y / 1000d * ImageStage.ActualHeight),
            _mainTranslationOverlay);
        Point bubbleBottomRight = ImageStage.TranslatePoint(
            new Point(
                hit.Right / 1000d * ImageStage.ActualWidth,
                hit.Bottom / 1000d * ImageStage.ActualHeight),
            _mainTranslationOverlay);
        var bubbleBounds = new Rect(
            Math.Min(bubbleTopLeft.X, bubbleBottomRight.X),
            Math.Min(bubbleTopLeft.Y, bubbleBottomRight.Y),
            Math.Abs(bubbleBottomRight.X - bubbleTopLeft.X),
            Math.Abs(bubbleBottomRight.Y - bubbleTopLeft.Y));
        Point pointer = ImageStage.TranslatePoint(imagePoint, _mainTranslationOverlay);

        double baseFontSize = Math.Max(18d, SystemFonts.MessageFontSize * 1.45d);
        double baseLineHeight = Math.Max(24d, SystemFonts.MessageFontSize * 1.9d);
        double maxCardWidth = Math.Min(780d, Math.Max(120d, pageBounds.Width - 24d));
        _mainTranslationCard.MaxWidth = maxCardWidth;
        _mainTranslationSpanish.MaxWidth = Math.Max(80d, maxCardWidth - 42d);
        _mainTranslationSpanish.FontSize = baseFontSize;
        _mainTranslationSpanish.LineHeight = baseLineHeight;
        _mainTranslationCard.Measure(new Size(maxCardWidth, double.PositiveInfinity));
        Size cardSize = _mainTranslationCard.DesiredSize;

        double availableHeight = Math.Max(80d, pageBounds.Height - 24d);
        if (cardSize.Height > availableHeight && baseFontSize > 15d)
        {
            double scale = Math.Clamp(availableHeight / Math.Max(1d, cardSize.Height), 0.72d, 1d);
            _mainTranslationSpanish.FontSize = Math.Max(15d, baseFontSize * scale);
            _mainTranslationSpanish.LineHeight = Math.Max(20d, baseLineHeight * scale);
            _mainTranslationCard.Measure(new Size(maxCardWidth, double.PositiveInfinity));
            cardSize = _mainTranslationCard.DesiredSize;
        }

        Point position = ResolveMainTranslationCardPlacement(
            pageBounds,
            bubbleBounds,
            pointer,
            cardSize,
            isTouch);
        _mainTranslationCard.Margin = new Thickness(position.X, position.Y, 0, 0);
    }

    internal static Point ResolveMainTranslationCardPlacement(
        Rect pageBounds,
        Rect bubbleBounds,
        Point pointer,
        Size cardSize,
        bool isTouch)
    {
        if (pageBounds.IsEmpty || pageBounds.Width <= 0 || pageBounds.Height <= 0)
        {
            return new Point(pageBounds.X, pageBounds.Y);
        }

        double inset = Math.Min(12d, Math.Min(pageBounds.Width, pageBounds.Height) * 0.04d);
        var safe = new Rect(
            pageBounds.Left + inset,
            pageBounds.Top + inset,
            Math.Max(1d, pageBounds.Width - inset * 2d),
            Math.Max(1d, pageBounds.Height - inset * 2d));
        double width = Math.Min(Math.Max(1d, cardSize.Width), safe.Width);
        double height = Math.Min(Math.Max(1d, cardSize.Height), safe.Height);
        double gap = isTouch ? 38d : 20d;
        double pointerRadius = isTouch ? 30d : 10d;

        Rect obstacle = bubbleBounds.IsEmpty
            ? new Rect(pointer.X - pointerRadius, pointer.Y - pointerRadius, pointerRadius * 2d, pointerRadius * 2d)
            : bubbleBounds;
        obstacle.Inflate(gap, gap);
        var pointerObstacle = new Rect(
            pointer.X - pointerRadius,
            pointer.Y - pointerRadius,
            pointerRadius * 2d,
            pointerRadius * 2d);
        obstacle.Union(pointerObstacle);

        static double Clamp(double value, double minimum, double maximum) =>
            maximum <= minimum ? minimum : Math.Clamp(value, minimum, maximum);

        double maxX = safe.Right - width;
        double maxY = safe.Bottom - height;

        double leftX = obstacle.Left - width;
        if (leftX >= safe.Left)
        {
            return new Point(
                leftX,
                Clamp(pointer.Y - height / 2d, safe.Top, maxY));
        }

        double rightX = Math.Max(obstacle.Right, pointer.X + pointerRadius + gap * 0.35d);
        if (rightX + width <= safe.Right)
        {
            double upperY = pointer.Y - pointerRadius - gap * 0.35d - height;
            if (upperY >= safe.Top)
            {
                return new Point(rightX, upperY);
            }

            double lowerY = Math.Max(obstacle.Bottom, pointer.Y + pointerRadius + gap * 0.35d);
            if (lowerY + height <= safe.Bottom)
            {
                return new Point(rightX, lowerY);
            }

            return new Point(
                rightX,
                Clamp(pointer.Y - height / 2d, safe.Top, maxY));
        }

        double aboveY = obstacle.Top - height;
        if (aboveY >= safe.Top)
        {
            return new Point(
                Clamp(pointer.X - width / 2d, safe.Left, maxX),
                aboveY);
        }

        double belowY = obstacle.Bottom;
        if (belowY + height <= safe.Bottom)
        {
            return new Point(
                Clamp(pointer.X - width / 2d, safe.Left, maxX),
                belowY);
        }

        Point[] candidates =
        [
            new Point(safe.Left, Clamp(pointer.Y - height / 2d, safe.Top, maxY)),
            new Point(maxX, Clamp(pointer.Y - height / 2d, safe.Top, maxY)),
            new Point(Clamp(pointer.X - width / 2d, safe.Left, maxX), safe.Top),
            new Point(Clamp(pointer.X - width / 2d, safe.Left, maxX), maxY)
        ];

        Point best = candidates[0];
        double bestScore = double.PositiveInfinity;
        foreach (Point candidate in candidates)
        {
            var card = new Rect(candidate, new Size(width, height));
            Rect overlap = Rect.Intersect(card, obstacle);
            double overlapArea = overlap.IsEmpty ? 0d : overlap.Width * overlap.Height;
            double pointerPenalty = card.Contains(pointer) ? 1_000_000_000d : 0d;
            double score = pointerPenalty + overlapArea * 10_000d + Math.Abs(candidate.X - pointer.X);
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    private void HideMainTranslation()
    {
        if (_mainTranslationOverlay is not null)
        {
            _mainTranslationOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void MainImage_MouseMoveForTranslation(object? sender, MouseEventArgs e)
    {
        if (e.StylusDevice is not null)
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed || _isSpacePanning)
        {
            HideMainTranslation();
            return;
        }

        if (!TryShowMainTranslationAt(e.GetPosition(ImageStage), isTouch: false))
        {
            HideMainTranslation();
        }
    }

    private void MainImage_MouseLeaveForTranslation(object? sender, MouseEventArgs e)
    {
        if (!ImageStage.AreAnyTouchesCapturedWithin)
        {
            HideMainTranslation();
        }
    }

    private void MainImage_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        Point point = e.GetTouchPoint(ImageStage).Position;
        if (!TryShowMainTranslationAt(point, isTouch: true)
            || _mainTranslationOverlay is null)
        {
            return;
        }

        e.TouchDevice.Capture(ImageStage);
        e.Handled = true;
    }

    private void MainImage_PreviewTouchMove(object? sender, TouchEventArgs e)
    {
        if (!ImageStage.AreAnyTouchesCapturedWithin)
        {
            return;
        }

        Point point = e.GetTouchPoint(ImageStage).Position;
        if (!TryShowMainTranslationAt(point, isTouch: true))
        {
            HideMainTranslation();
        }
        e.Handled = true;
    }

    private void MainImage_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (!ImageStage.AreAnyTouchesCapturedWithin)
        {
            return;
        }

        e.TouchDevice.Capture(null);
        HideMainTranslation();
        e.Handled = true;
    }
}
