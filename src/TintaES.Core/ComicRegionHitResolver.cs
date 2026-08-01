namespace TintaES.Core;

/// <summary>
/// Resuelve una pulsación sobre la página usando únicamente una zona pequeña alrededor
/// del texto detectado. El contorno completo del bocadillo no amplía el área de clic.
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

    public static NormalizedRect ResolveHitBox(ComicRegion region) =>
        CreateTextHitBox(region.TextBox.Clamp());

    private static NormalizedRect CreateTextHitBox(NormalizedRect text)
    {
        // Un margen pequeño permite pulsar el blanco inmediato entre las letras y el borde,
        // pero nunca convierte todo el bocadillo ni la viñeta en una zona activa.
        double marginX = Math.Clamp(text.Height * 0.24, 9, 28);
        double marginY = Math.Clamp(text.Height * 0.17, 7, 20);
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
}
