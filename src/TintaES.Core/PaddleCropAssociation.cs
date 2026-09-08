namespace TintaES.Core;

/// <summary>
/// Conserva la región que originó cada recorte de PaddleOCR. La caja devuelta por
/// el worker es la del recorte enviado, no una nueva detección del bocadillo.
/// </summary>
public static class PaddleCropAssociation
{
    private const double CoordinateTolerance = 0.000001;

    public static NormalizedRect GetCropBox(ComicRegion region)
    {
        NormalizedRect text = region.TextBox.Clamp();
        double horizontalMargin = text.Width < 45 ? 0.24 : 0.10;
        double verticalMargin = text.Height < 24 ? 0.28 : 0.14;
        return text.Expand(horizontalMargin, verticalMargin);
    }

    public static ComicRegion? FindOwner(
        IReadOnlyList<ComicRegion> regions,
        NormalizedRect cropBox)
    {
        ComicRegion? owner = null;
        foreach (ComicRegion region in regions)
        {
            if (!region.IsEnabled || !Matches(GetCropBox(region), cropBox))
            {
                continue;
            }

            // Dos regiones con el mismo recorte no permiten recuperar la identidad
            // del dueño a partir de la geometría: nunca elegimos por orden o cercanía.
            if (owner is not null && !ReferenceEquals(owner, region))
            {
                return null;
            }
            owner = region;
        }
        return owner;
    }

    public static int ApplyToRegions(
        IReadOnlyList<ComicRegion> regions,
        IReadOnlyList<HunyuanTextSpot> spots)
    {
        var assigned = new Dictionary<ComicRegion, List<HunyuanTextSpot>>();
        foreach (HunyuanTextSpot spot in spots)
        {
            ComicRegion? owner = FindOwner(regions, spot.Box);
            if (owner is null)
            {
                continue;
            }
            if (!assigned.TryGetValue(owner, out List<HunyuanTextSpot>? readings))
            {
                readings = [];
                assigned.Add(owner, readings);
            }
            readings.Add(spot);
        }

        int replacements = 0;
        foreach ((ComicRegion owner, List<HunyuanTextSpot> readings) in assigned)
        {
            // Mantiene la política de calidad, alternativas e invalidación de la
            // traducción existente, sin dejar competir a un bocadillo vecino.
            replacements += HunyuanTextSpotting.ApplyToRegions([owner], readings);
        }
        return replacements;
    }

    private static bool Matches(NormalizedRect expected, NormalizedRect actual) =>
        Math.Abs(expected.X - actual.X) <= CoordinateTolerance
        && Math.Abs(expected.Y - actual.Y) <= CoordinateTolerance
        && Math.Abs(expected.Right - actual.Right) <= CoordinateTolerance
        && Math.Abs(expected.Bottom - actual.Bottom) <= CoordinateTolerance;
}
