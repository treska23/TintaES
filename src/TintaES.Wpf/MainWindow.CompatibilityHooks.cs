namespace TintaES.Wpf;

/// <summary>
/// Puentes mínimos para nombres utilizados por navegación y deshacer. Todos terminan en la
/// única ruta visual: RebuildOverlay / RefreshRegionSelectionChrome.
/// </summary>
public partial class MainWindow
{
    private void QueueFastCanvasTextRefresh(bool forceLayout) =>
        RebuildOverlay();

    private void FinalizeProgressiveOverlayTextLayout(bool finalPass) =>
        RefreshRegionSelectionChrome();

    private void SyncSelectedTextFrameChrome() =>
        RefreshRegionSelectionChrome();
}
