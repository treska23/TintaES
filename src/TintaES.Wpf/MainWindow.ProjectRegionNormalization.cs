using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Normalización de datos persistidos. No registra eventos ni reconstruye el lienzo por su cuenta.
/// </summary>
public partial class MainWindow
{
    private static void NormalizeLoadedProjectRegion(ComicRegion region)
    {
        region.Style ??= new ComicTextStyle();
        region.Style.FontCategory = "comic";
        region.Style.FontFamily = null;
        region.Style.FontWeight = 900;
        region.Style.FontWidthRatio = 1.12;
        region.Style.Italic = false;
        region.Style.LineHeightRatio = 1.08;
        region.Style.OriginalLineCount = 0;
        if (string.Equals(
                region.Translation?.Trim(),
                ComicRegion.PendingTranslationMarker,
                StringComparison.OrdinalIgnoreCase))
        {
            region.Translation = string.Empty;
        }

        region.TextBox = (region.TextBox ?? new NormalizedRect(100, 100, 200, 80)).Clamp();
        region.SafePolygon ??= [];
        region.RenderBox = ResolveConservativeTextFrame(region);

        if (!double.IsFinite(region.FontScale) || region.FontScale <= 0)
        {
            region.FontScale = 1;
        }
        if (!double.IsFinite(region.ManualFontScale) || region.ManualFontScale <= 0)
        {
            region.ManualFontScale = 1;
        }
        if (!double.IsFinite(region.ManualBaseFontSize) || region.ManualBaseFontSize < 0)
        {
            region.ManualBaseFontSize = 0;
        }
        if (!double.IsFinite(region.TextOffsetX))
        {
            region.TextOffsetX = 0;
        }
        if (!double.IsFinite(region.TextOffsetY))
        {
            region.TextOffsetY = 0;
        }
    }

    /// <summary>
    /// RenderBox solo puede derivarse de la silueta segura o del bloque OCR original. BubbleBox
    /// es una aproximación del detector y no vuelve a ensanchar automáticamente la capa.
    /// </summary>
    private static NormalizedRect ResolveConservativeTextFrame(ComicRegion region)
    {
        if (region.SafePolygon.Count >= 3)
        {
            double left = region.SafePolygon.Min(point => point.X);
            double top = region.SafePolygon.Min(point => point.Y);
            double right = region.SafePolygon.Max(point => point.X);
            double bottom = region.SafePolygon.Max(point => point.Y);
            if (double.IsFinite(left)
                && double.IsFinite(top)
                && double.IsFinite(right)
                && double.IsFinite(bottom)
                && right - left >= 5
                && bottom - top >= 5)
            {
                return new NormalizedRect(left, top, right - left, bottom - top)
                    .Expand(0.035, 0.045)
                    .Clamp();
            }
        }

        bool rectangular = region.Type is "narration" or "caption";
        return region.TextBox
            .Expand(rectangular ? 0.18 : 0.24, rectangular ? 0.30 : 0.40)
            .Clamp();
    }
}
