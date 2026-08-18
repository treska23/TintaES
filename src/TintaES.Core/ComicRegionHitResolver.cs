namespace TintaES.Core;

/// <summary>
/// Resuelve zonas de interacción sobre la página. El ratón usa una caja pequeña alrededor
/// del texto; el toque dispone además de una zona táctil mayor y, cuando el detector está
/// suficientemente seguro, del interior del bocadillo.
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
            .Where(candidate => candidate.Hit)
            .OrderByDescending(candidate => candidate.DirectTextHit)
            .ThenBy(candidate => candidate.NormalizedDistanceToText)
            .ThenBy(candidate => candidate.NormalizedDistanceToTextCentre)
            .ThenBy(candidate => candidate.Region.Order)
            .ToArray();

        return candidates.FirstOrDefault()?.Region;
    }

    /// <summary>
    /// Resuelve un toque de dedo. Primero conserva exactamente la prioridad del ratón y, si no
    /// hay impacto, permite una tolerancia mayor alrededor del texto y el interior de un globo
    /// fiable. Así no hace falta acertar sobre las letras con el dedo.
    /// </summary>
    public static ComicRegion? ResolveForTouch(
        IEnumerable<ComicRegion> regions,
        double x,
        double y)
    {
        ComicRegion[] readable = regions
            .Where(region => region.IsEnabled && !string.IsNullOrWhiteSpace(region.Original))
            .ToArray();

        ComicRegion? direct = Resolve(readable, x, y);
        if (direct is not null)
        {
            return direct;
        }

        TouchCandidate[] candidates = readable
            .Select(region => CreateTouchCandidate(region, x, y))
            .Where(candidate => candidate.TouchTextHit || candidate.BubbleHit)
            .OrderByDescending(candidate => candidate.TouchTextHit)
            .ThenBy(candidate => candidate.BubbleArea)
            .ThenBy(candidate => candidate.DistanceToTextCentre)
            .ThenBy(candidate => candidate.Region.Order)
            .ToArray();

        return candidates.FirstOrDefault()?.Region;
    }

    public static NormalizedRect ResolveHitBox(ComicRegion region) =>
        CreateTextHitBox(region.TextBox.Clamp());

    public static NormalizedRect ResolveTouchHitBox(ComicRegion region) =>
        CreateTouchTextHitBox(region.TextBox.Clamp());

    private static NormalizedRect CreateTextHitBox(NormalizedRect text)
    {
        // Ratón: margen pequeño para que dos zonas próximas no compitan al pasar el puntero.
        double marginX = Math.Clamp(text.Height * 0.27, 10, 30);
        double marginY = Math.Clamp(text.Height * 0.19, 8, 22);
        return new NormalizedRect(
            text.X - marginX,
            text.Y - marginY,
            text.Width + marginX * 2,
            text.Height + marginY * 2).Clamp();
    }

    private static NormalizedRect CreateTouchTextHitBox(NormalizedRect text)
    {
        // Dedo: el objetivo debe ser bastante mayor que el trazo OCR. Sigue siendo una caja
        // local y no puede crecer media viñeta si la geometría detectada es defectuosa.
        double marginX = Math.Clamp(text.Height * 0.68, 18, 58);
        double marginY = Math.Clamp(text.Height * 0.48, 14, 44);
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
            Contains(hitBox, x, y),
            Contains(text, x, y),
            distanceToText / textDiagonalSquared,
            distanceToTextCentre / textDiagonalSquared);
    }

    private static TouchCandidate CreateTouchCandidate(ComicRegion region, double x, double y)
    {
        NormalizedRect text = region.TextBox.Clamp();
        NormalizedRect touchText = ResolveTouchHitBox(region);
        bool touchTextHit = Contains(touchText, x, y);
        bool bubbleHit = false;
        double bubbleArea = double.MaxValue;

        if (region.BubbleBox is { } detectedBubble)
        {
            NormalizedRect bubble = detectedBubble.Clamp();
            double areaRatio = bubble.Area / Math.Max(1, text.Area);
            bool plausibleBubble = region.BubbleConfidence >= 0.45
                && bubble.Area <= 150_000
                && bubble.Width <= 520
                && bubble.Height <= 520
                && areaRatio is >= 1.04 and <= 80
                && Contains(bubble, text.X + text.Width / 2, text.Y + text.Height / 2);
            if (plausibleBubble && Contains(bubble, x, y))
            {
                bubbleHit = true;
                bubbleArea = bubble.Area;
            }
        }

        return new TouchCandidate(
            region,
            touchTextHit,
            bubbleHit,
            bubbleArea,
            DistanceSquaredToCenter(text, x, y));
    }

    private static bool Contains(NormalizedRect rectangle, double x, double y) =>
        x >= rectangle.X && x <= rectangle.Right && y >= rectangle.Y && y <= rectangle.Bottom;

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
        bool Hit,
        bool DirectTextHit,
        double NormalizedDistanceToText,
        double NormalizedDistanceToTextCentre);

    private sealed record TouchCandidate(
        ComicRegion Region,
        bool TouchTextHit,
        bool BubbleHit,
        double BubbleArea,
        double DistanceToTextCentre);
}
