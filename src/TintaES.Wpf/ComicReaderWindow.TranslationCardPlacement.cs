using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Posicionamiento de la tarjeta de traducción del Reader. No registra eventos ni crea una
/// segunda ruta de interacción: ComicReaderWindow.Translations decide cuándo mostrarla y llama
/// aquí con el puntero o dedo real que está dentro del bocadillo.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private void PositionTranslationCard(ComicRegion region, Point pointer, bool isTouch)
    {
        if (_translationCard is null
            || _translationText is null
            || _viewerHost.ActualWidth <= 1
            || _viewerHost.ActualHeight <= 1
            || _pageStage.ActualWidth <= 1
            || _pageStage.ActualHeight <= 1)
        {
            return;
        }

        Rect visiblePage = GetVisiblePageBoundsInViewer();
        if (visiblePage.IsEmpty || visiblePage.Width <= 24 || visiblePage.Height <= 24)
        {
            return;
        }

        Rect bubbleBounds = GetRegionBoundsInViewer(region);

        double baseFontSize = Math.Max(18d, SystemFonts.MessageFontSize * 1.45d);
        double baseLineHeight = Math.Max(24d, SystemFonts.MessageFontSize * 1.9d);
        double availableWidth = Math.Max(120d, visiblePage.Width - 24d);
        double maxCardWidth = Math.Min(780d, availableWidth);

        _translationCard.HorizontalAlignment = HorizontalAlignment.Left;
        _translationCard.VerticalAlignment = VerticalAlignment.Top;
        _translationCard.Margin = new Thickness(0);
        _translationCard.MaxWidth = maxCardWidth;
        _translationText.MaxWidth = Math.Max(80d, maxCardWidth - 42d);
        _translationText.FontSize = baseFontSize;
        _translationText.LineHeight = baseLineHeight;

        _translationCard.Measure(new Size(maxCardWidth, double.PositiveInfinity));
        Size cardSize = _translationCard.DesiredSize;

        double availableHeight = Math.Max(80d, visiblePage.Height - 24d);
        if (cardSize.Height > availableHeight && baseFontSize > 15d)
        {
            double scale = Math.Clamp(availableHeight / Math.Max(1d, cardSize.Height), 0.72d, 1d);
            _translationText.FontSize = Math.Max(15d, baseFontSize * scale);
            _translationText.LineHeight = Math.Max(20d, baseLineHeight * scale);
            _translationCard.Measure(new Size(maxCardWidth, double.PositiveInfinity));
            cardSize = _translationCard.DesiredSize;
        }

        Point location = ResolveReaderTranslationCardPlacement(
            visiblePage,
            bubbleBounds,
            pointer,
            cardSize,
            isTouch);
        _translationCard.Margin = new Thickness(location.X, location.Y, 0, 0);
        Panel.SetZIndex(_translationCard, 2000);
    }

    private Rect GetVisiblePageBoundsInViewer()
    {
        Point first = _pageStage.TranslatePoint(new Point(0, 0), _viewerHost);
        Point second = _pageStage.TranslatePoint(
            new Point(_pageStage.ActualWidth, _pageStage.ActualHeight),
            _viewerHost);
        var page = new Rect(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Abs(second.X - first.X),
            Math.Abs(second.Y - first.Y));
        var viewer = new Rect(0, 0, _viewerHost.ActualWidth, _viewerHost.ActualHeight);
        page.Intersect(viewer);
        return page;
    }

    private Rect GetRegionBoundsInViewer(ComicRegion region)
    {
        NormalizedRect hit = ResolveReaderHitBox(region);
        Point first = _pageStage.TranslatePoint(
            new Point(
                hit.X / 1000d * _pageStage.ActualWidth,
                hit.Y / 1000d * _pageStage.ActualHeight),
            _viewerHost);
        Point second = _pageStage.TranslatePoint(
            new Point(
                hit.Right / 1000d * _pageStage.ActualWidth,
                hit.Bottom / 1000d * _pageStage.ActualHeight),
            _viewerHost);
        return new Rect(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Abs(second.X - first.X),
            Math.Abs(second.Y - first.Y));
    }

    internal static Point ResolveReaderTranslationCardPlacement(
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

        // Posición normal: completamente a la izquierda del bocadillo y del dedo/puntero.
        double leftX = obstacle.Left - width;
        if (leftX >= safe.Left)
        {
            return new Point(
                leftX,
                Clamp(pointer.Y - height / 2d, safe.Top, maxY));
        }

        // Si el margen izquierdo bloquea la tarjeta, se intenta la diagonal derecha superior.
        double rightX = Math.Max(obstacle.Right, pointer.X + pointerRadius + gap * 0.35d);
        if (rightX + width <= safe.Right)
        {
            double upperY = pointer.Y - pointerRadius - gap * 0.35d - height;
            if (upperY >= safe.Top)
            {
                return new Point(rightX, upperY);
            }

            // Si arriba es el límite, la misma salida por la derecha cae por debajo.
            double lowerY = Math.Max(obstacle.Bottom, pointer.Y + pointerRadius + gap * 0.35d);
            if (lowerY + height <= safe.Bottom)
            {
                return new Point(rightX, lowerY);
            }

            return new Point(
                rightX,
                Clamp(pointer.Y - height / 2d, safe.Top, maxY));
        }

        // Últimas posiciones naturales: encima o debajo, siempre dentro de la página visible.
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

        // Página o tarjeta extremadamente estrechas: elige el borde con menor solape y penaliza
        // de forma máxima cualquier posición que quede exactamente bajo el dedo/puntero.
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
}
