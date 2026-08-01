namespace TintaES.Core;

/// <summary>
/// Resuelve una pulsación sobre la página sin depender del orden visual de los
/// rectángulos WPF. Los contenedores de dos bocadillos pueden solaparse; en ese
/// caso se elige el texto realmente más próximo al punto pulsado.
/// </summary>
public static class ComicRegionHitResolver
{
    public static ComicRegion? Resolve(
        IEnumerable<ComicRegion> regions,
        double x,
        double y) =>
        regions
            .Where(region => region.IsEnabled && !string.IsNullOrWhiteSpace(region.Original))
            .Select(region => new HitCandidate(
                region,
                ResolveHitBox(region),
                Contains(region.TextBox.Clamp(), x, y),
                DistanceSquaredToRectangle(region.TextBox.Clamp(), x, y),
                DistanceSquaredToCenter(region.TextBox.Clamp(), x, y)))
            .Where(candidate => Contains(candidate.HitBox, x, y))
            .OrderByDescending(candidate => candidate.DirectTextHit)
            .ThenBy(candidate => candidate.DistanceToText)
            .ThenBy(candidate => candidate.DistanceToTextCentre)
            .ThenBy(candidate => candidate.HitBox.Area)
            .ThenBy(candidate => candidate.Region.Order)
            .Select(candidate => candidate.Region)
            .FirstOrDefault();

    public static NormalizedRect ResolveHitBox(ComicRegion region)
    {
        NormalizedRect text = region.TextBox.Clamp();

        if (region.BubbleBox is { } bubble
            && IsPlausibleContainer(text, bubble, maximumAreaRatio: 24))
        {
            if (region.BubbleConfidence >= 0.12)
            {
                return bubble.Clamp();
            }

            // Con confianza cero el detector suele prolongar el fondo blanco hasta
            // el globo vecino. Conservamos la parte del contenedor cercana al texto
            // y evitamos que esa caja invada otro bocadillo.
            NormalizedRect constrained = Intersect(bubble.Clamp(), text.Expand(1.2, 1.6));
            if (constrained.Area >= text.Area)
            {
                return constrained;
            }
        }

        if (region.SafePolygon.Count >= 3)
        {
            double left = region.SafePolygon.Min(point => point.X);
            double top = region.SafePolygon.Min(point => point.Y);
            double right = region.SafePolygon.Max(point => point.X);
            double bottom = region.SafePolygon.Max(point => point.Y);
            var polygonBounds = new NormalizedRect(left, top, right - left, bottom - top);
            if (IsPlausibleContainer(text, polygonBounds, maximumAreaRatio: 24))
            {
                return polygonBounds.Clamp();
            }
        }

        // Permite pulsar el blanco inmediato alrededor de las letras, pero no
        // convierte el resto de la viñeta en una zona activa.
        return text.Expand(0.46, 0.68).Clamp();
    }

    private static bool IsPlausibleContainer(
        NormalizedRect text,
        NormalizedRect candidate,
        double maximumAreaRatio)
    {
        candidate = candidate.Clamp();
        double centreX = text.X + text.Width / 2;
        double centreY = text.Y + text.Height / 2;
        double areaRatio = candidate.Area / Math.Max(1, text.Area);
        return candidate.Area >= text.Area * 1.02
            && areaRatio <= maximumAreaRatio
            && centreX >= candidate.X
            && centreX <= candidate.Right
            && centreY >= candidate.Y
            && centreY <= candidate.Bottom
            && candidate.Width <= Math.Max(120, text.Width * 6.5)
            && candidate.Height <= Math.Max(130, text.Height * 7.5)
            && candidate.Width <= 560
            && candidate.Height <= 560;
    }

    private static bool Contains(NormalizedRect rectangle, double x, double y) =>
        x >= rectangle.X && x <= rectangle.Right && y >= rectangle.Y && y <= rectangle.Bottom;

    private static NormalizedRect Intersect(NormalizedRect first, NormalizedRect second)
    {
        double left = Math.Max(first.X, second.X);
        double top = Math.Max(first.Y, second.Y);
        double right = Math.Min(first.Right, second.Right);
        double bottom = Math.Min(first.Bottom, second.Bottom);
        return new NormalizedRect(
            left,
            top,
            Math.Max(5, right - left),
            Math.Max(5, bottom - top)).Clamp();
    }

    private static double DistanceSquaredToRectangle(
        NormalizedRect rectangle,
        double x,
        double y)
    {
        double dx = x < rectangle.X
            ? rectangle.X - x
            : x > rectangle.Right
                ? x - rectangle.Right
                : 0;
        double dy = y < rectangle.Y
            ? rectangle.Y - y
            : y > rectangle.Bottom
                ? y - rectangle.Bottom
                : 0;
        return dx * dx + dy * dy;
    }

    private static double DistanceSquaredToCenter(
        NormalizedRect rectangle,
        double x,
        double y)
    {
        double dx = rectangle.X + rectangle.Width / 2 - x;
        double dy = rectangle.Y + rectangle.Height / 2 - y;
        return dx * dx + dy * dy;
    }

    private sealed record HitCandidate(
        ComicRegion Region,
        NormalizedRect HitBox,
        bool DirectTextHit,
        double DistanceToText,
        double DistanceToTextCentre);
}
