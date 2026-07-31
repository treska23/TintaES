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
        region.Style.FontWeight = 700;
        region.Style.Italic = false;
        region.Style.LineHeightRatio = 1.02;
        region.Style.OriginalLineCount = 0;
        if (string.Equals(
                region.Translation?.Trim(),
                ComicRegion.PendingTranslationMarker,
                StringComparison.OrdinalIgnoreCase))
        {
            region.Translation = string.Empty;
        }

        region.TextBox = (region.TextBox ?? new NormalizedRect(100, 100, 200, 80)).Clamp();
        region.RenderBox = (region.RenderBox ?? region.TextBox.Expand(0.1, 0.2)).Clamp();
        region.SafePolygon ??= [];

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
}
