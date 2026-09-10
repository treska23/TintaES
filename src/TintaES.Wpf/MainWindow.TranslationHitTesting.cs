using TintaES.Core;

namespace TintaES.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// Convierte una coordenada visual de la página a la escala normalizada 0..1000 usada por
    /// todas las regiones. Es independiente del zoom: media página siempre produce (500, 500).
    /// </summary>
    internal static NormalizedPoint NormalizeImagePoint(
        double x,
        double y,
        double imageWidth,
        double imageHeight)
    {
        if (!double.IsFinite(x)
            || !double.IsFinite(y)
            || !double.IsFinite(imageWidth)
            || !double.IsFinite(imageHeight)
            || imageWidth <= 0
            || imageHeight <= 0)
        {
            return new NormalizedPoint(0, 0);
        }

        return new NormalizedPoint(
            Math.Clamp(x / imageWidth * 1000d, 0d, 1000d),
            Math.Clamp(y / imageHeight * 1000d, 0d, 1000d));
    }

    /// <summary>
    /// Única resolución pura de una tarjeta sobre el lienzo principal. Ratón y toque comparten
    /// el mismo motor que ComicReaderWindow; el toque solo amplía el objetivo físico.
    /// </summary>
    internal static ComicRegion? ResolveMainTranslationRegion(
        IEnumerable<ComicRegion> regions,
        double normalizedX,
        double normalizedY,
        bool isTouch = false) =>
        isTouch
            ? ComicRegionHitResolver.ResolveForTouch(regions, normalizedX, normalizedY)
            : ComicRegionHitResolver.Resolve(regions, normalizedX, normalizedY);
}
