namespace TintaES.Core;

/// <summary>
/// Resuelve una pulsación sobre la página sin depender del orden visual de los
/// rectángulos WPF. Una zona solo participa cuando contiene realmente el punto pulsado;
/// la proximidad sirve para desempatar zonas solapadas, nunca para ampliar el clic fuera
/// del bocadillo.
/// </summary>
public static class ComicRegionHitResolver
{
    public static ComicRegion? Resolve(
        IEnumerable<ComicRegion> regions,
        double x,
        double y)
    {
        HitCandidate[] candidates = regions
            .Where(region => region.IsEnabled && !string.IsNullOrWhiteSpace(region.Original))
            .Select(region => CreateCandidate(region, x, y))
            .ToArray();

        HitCandidate[] direct = candidates
            .Where(candidate => candidate.DirectTextHit)
            .OrderBy(candidate => candidate.DistanceToTextCentre)
            .ThenBy(candidate => candidate.Region.Order)
            .ToArray();
        if (direct.Length > 0)
        {
            return direct[0].Region;
        }

        // Solo compiten zonas que contienen físicamente el clic. Esto conserva la
        // separación entre bocadillos vecinos sin crear un radio invisible alrededor.
        return candidates
            .Where(candidate => candidate.ContainerHit)
            .OrderBy(candidate => candidate.NormalizedDistanceToText)
            .ThenBy(candidate => candidate.NormalizedDistanceToTextCentre)
            .ThenBy(candidate => candidate.HitBox.Area)
            .ThenBy(candidate => candidate.Region.Order)
            .Select(candidate => candidate.Region)
            .FirstOrDefault();
    }

    public static NormalizedRect ResolveHitBox(ComicRegion region)
    {
        NormalizedRect text = region.TextBox.Clamp();

        // Un contenedor muy grande suele ser la unión accidental de varios globos. Aunque
        // tenga confianza alta, no puede convertirse en una zona activa de media viñeta.
        if (region.BubbleBox is { } bubble
            && IsPlausibleContainer(text, bubble, maximumAreaRatio: 9))
        {
            if (region.BubbleConfidence >= 0.12)
            {
                return bubble.Clamp();
            }

            NormalizedRect constrained = Intersect(bubble.Clamp(), CreateFallbackHitBox(text));
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
            if (IsPlausibleContainer(text, polygonBounds, maximumAreaRatio: 9))
            {
                return polygonBounds.Clamp();
            }
        }

        // Cuando no existe un contorno fiable, permitimos pulsar el blanco inmediato del
        // globo mediante márgenes absolutos y limitados. La caja nunca crece en función de
        // la longitud de toda la frase ni alcanza un bocadillo vecino.
        return CreateFallbackHitBox(text);
    }

    private static NormalizedRect CreateFallbackHitBox(NormalizedRect text)
    {
        double marginX = Math.Clamp(text.Height * 0.42, 12, 42);
        double marginY = Math.Clamp(text.Height * 0.30, 10, 32);
        return new NormalizedRect(
            text.X - marginX,
            text.Y - marginY,
            text.Width + marginX * 2,
            text.Height + marginY * 2).Clamp();
    }

    private static HitCandidate CreateCandidate(ComicRegion region, double x, double y)
    {
        NormalizedRect text = region.TextBox.Clamp();
        NormalizedRect hitBox = ResolveHitBox(region);
        double distanceToText = DistanceSquaredToRectangle(text, x, y);
        double distanceToTextCentre = DistanceSquaredToCenter(text, x, y);
        double textDiagonalSquared = Math.Max(
            1,
            text.Width * text.Width + text.Height * text.Height);

        return new HitCandidate(
            region,
            hitBox,
            Contains(text, x, y),
            Contains(hitBox, x, y),
            distanceToText,
            distanceToText / textDiagonalSquared,
            distanceToTextCentre,
            distanceToTextCentre / textDiagonalSquared);
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
            && candidate.Width <= Math.Max(110, text.Width * 4.0)
            && candidate.Height <= Math.Max(120, text.Height * 4.5)
            && candidate.Width <= 420
            && candidate.Height <= 420;
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
        bool ContainerHit,
        double DistanceToText,
        double NormalizedDistanceToText,
        double DistanceToTextCentre,
        double NormalizedDistanceToTextCentre);
}
