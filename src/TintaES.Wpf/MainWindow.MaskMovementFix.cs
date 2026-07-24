using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Corrige el desplazamiento conjunto de texto y máscara. El arrastre del editor modifica
/// TextOffsetX/TextOffsetY; comparar RenderBox siempre producía un desplazamiento cero.
/// </summary>
public partial class MainWindow
{
    private static readonly bool MaskMovementFixRegistered = RegisterMaskMovementFix();

    private bool _maskMovementFixInstalled;
    private CorrectedMaskMoveState? _correctedMaskMoveState;

    private static bool RegisterMaskMovementFix()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_MaskMovementFixLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_MaskMovementFixLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        // MaskEditing se instala en SystemIdle. Esperamos únicamente hasta que exista y después
        // retiramos este LayoutUpdated; no dejamos otro trabajo permanente en cada frame.
        window.LayoutUpdated -= window.MainWindow_TryInstallMaskMovementFix;
        window.LayoutUpdated += window.MainWindow_TryInstallMaskMovementFix;
        window.BusyOverlay.IsVisibleChanged -= window.BusyOverlay_MaskToolsVisibilityChanged;
        window.BusyOverlay.IsVisibleChanged += window.BusyOverlay_MaskToolsVisibilityChanged;
        window.Dispatcher.BeginInvoke(
            window.TryInstallMaskMovementFix,
            DispatcherPriority.SystemIdle);
    }

    private void MainWindow_TryInstallMaskMovementFix(object? sender, EventArgs e) =>
        TryInstallMaskMovementFix();

    private void TryInstallMaskMovementFix()
    {
        if (_maskMovementFixInstalled || !_manualMaskEditingInstalled)
        {
            return;
        }

        _maskMovementFixInstalled = true;
        LayoutUpdated -= MainWindow_TryInstallMaskMovementFix;

        OverlayCanvas.RemoveHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(ManualMaskThumb_DragStarted));
        OverlayCanvas.RemoveHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(ManualMaskThumb_DragCompleted));

        OverlayCanvas.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(CorrectedMaskThumb_DragStarted),
            handledEventsToo: true);
        OverlayCanvas.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(CorrectedMaskThumb_DragCompleted),
            handledEventsToo: true);

        _maskMoveState = null;
        RefreshManualMaskAvailability();
    }

    private void BusyOverlay_MaskToolsVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (BusyOverlay.IsVisible)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                RefreshManualMaskAvailability();
                RefreshPageSaveAvailability();
                RefreshEditorToolAvailability();
            },
            DispatcherPriority.ContextIdle);
    }

    private void CorrectedMaskThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (_manualMaskTool != ManualMaskTool.None
            || _maskEditorBusy
            || _maskBitmap is null
            || _originalBitmap is null
            || e.OriginalSource is not Thumb { Tag: RegionVisual visual } thumb
            || thumb.Cursor != Cursors.SizeAll)
        {
            _correctedMaskMoveState = null;
            return;
        }

        ComicRegion region = visual.Region;
        NormalizedRect currentMaskBounds = TranslateMaskBounds(
            GetRegionMaskBounds(region),
            region.TextOffsetX,
            region.TextOffsetY);

        _correctedMaskMoveState = new CorrectedMaskMoveState(
            _comicPageIndex,
            region.Id,
            region.TextOffsetX,
            region.TextOffsetY,
            currentMaskBounds);
    }

    private void CorrectedMaskThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        CorrectedMaskMoveState? state = _correctedMaskMoveState;
        _correctedMaskMoveState = null;

        if (state is null
            || _maskEditorBusy
            || _maskBitmap is null
            || _originalBitmap is null
            || e.OriginalSource is not Thumb { Tag: RegionVisual visual } thumb
            || thumb.Cursor != Cursors.SizeAll
            || visual.Region.Id != state.RegionId
            || state.PageIndex != _comicPageIndex)
        {
            return;
        }

        double deltaX = visual.Region.TextOffsetX - state.OriginalTextOffsetX;
        double deltaY = visual.Region.TextOffsetY - state.OriginalTextOffsetY;
        if (Math.Abs(deltaX) < 0.01 && Math.Abs(deltaY) < 0.01)
        {
            return;
        }

        var moveState = new MaskMoveState(
            state.PageIndex,
            state.RegionId,
            visual.Region.RenderBox,
            state.OriginalMaskBounds);
        _ = MoveRegionMaskAsync(moveState, deltaX, deltaY);
    }

    private static NormalizedRect TranslateMaskBounds(
        NormalizedRect bounds,
        double offsetX,
        double offsetY)
    {
        return new NormalizedRect(
            bounds.X + offsetX,
            bounds.Y + offsetY,
            bounds.Width,
            bounds.Height).Clamp();
    }

    private sealed record CorrectedMaskMoveState(
        int PageIndex,
        Guid RegionId,
        double OriginalTextOffsetX,
        double OriginalTextOffsetY,
        NormalizedRect OriginalMaskBounds);
}
