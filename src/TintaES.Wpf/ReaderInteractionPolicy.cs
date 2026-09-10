using System.Windows;

namespace TintaES.Wpf;

/// <summary>
/// Reglas puras compartidas por la experiencia de lectura. La UI solo transforma coordenadas y
/// delega aquí; así posición de tarjetas y gestos no vuelven a divergir entre rutas distintas.
/// </summary>
internal static class ReaderInteractionPolicy
{
    internal static Point ResolveTranslationCardPlacement(
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

        // Prioridad: izquierda del bocadillo y del dedo/puntero.
        double leftX = obstacle.Left - width;
        if (leftX >= safe.Left)
        {
            return new Point(
                leftX,
                Clamp(pointer.Y - height / 2d, safe.Top, maxY));
        }

        // Si el margen izquierdo bloquea la tarjeta, saltamos a la derecha y preferimos arriba.
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

        // Caso extremo: elegimos el borde visible con menos solape, penalizando por completo
        // cualquier posición que coloque la tarjeta bajo el punto de entrada.
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

    internal static int ResolveSwipePageDelta(
        Vector gesture,
        TimeSpan elapsed,
        int pageIndex,
        int pageCount,
        double viewportWidth)
    {
        if (pageCount < 2 || elapsed.TotalMilliseconds > 1_500)
        {
            return 0;
        }

        double horizontal = Math.Abs(gesture.X);
        double required = Math.Max(90d, viewportWidth * 0.12d);
        if (horizontal < required || Math.Abs(gesture.Y) > horizontal * 0.62d)
        {
            return 0;
        }

        int delta = gesture.X < 0 ? 1 : -1;
        int target = pageIndex + delta;
        return target >= 0 && target < pageCount ? delta : 0;
    }
}
