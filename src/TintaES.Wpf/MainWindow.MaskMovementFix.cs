using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Corrige el desplazamiento conjunto de texto y máscara. El arrastre del editor modifica
/// TextOffsetX/TextOffsetY; comparar RenderBox siempre producía un desplazamiento cero.
/// También detecta una máscara que ya hubiera quedado atrás por el fallo anterior.
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

        // MaskEditing se instala en SystemIdle. Esperamos solo hasta que exista y retiramos el
        // evento inmediatamente; no queda trabajo permanente asociado a LayoutUpdated.
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
        NormalizedRect baseBounds = GetRegionMaskBounds(region);
        NormalizedRect movedBounds = TranslateMaskBounds(
            baseBounds,
            region.TextOffsetX,
            region.TextOffsetY);

        long baseCoverage = MeasureMaskCoverage(baseBounds);
        long movedCoverage = MeasureMaskCoverage(movedBounds);
        bool maskAlreadyFollowedText = movedCoverage > baseCoverage;

        _correctedMaskMoveState = new CorrectedMaskMoveState(
            _comicPageIndex,
            region.Id,
            maskAlreadyFollowedText ? region.TextOffsetX : 0,
            maskAlreadyFollowedText ? region.TextOffsetY : 0,
            maskAlreadyFollowedText ? movedBounds : baseBounds);
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

        ComicRegion region = visual.Region;
        double deltaX = region.TextOffsetX - state.ActualMaskOffsetX;
        double deltaY = region.TextOffsetY - state.ActualMaskOffsetY;
        if (Math.Abs(deltaX) < 0.01 && Math.Abs(deltaY) < 0.01)
        {
            return;
        }

        var moveState = new MaskMoveState(
            state.PageIndex,
            state.RegionId,
            region.RenderBox,
            state.ActualMaskBounds);
        _ = MoveRegionMaskAsync(moveState, deltaX, deltaY);
    }

    private long MeasureMaskCoverage(NormalizedRect bounds)
    {
        if (_maskBitmap is null)
        {
            return 0;
        }

        BitmapSource mask = _maskBitmap.Format == PixelFormats.Gray8
            ? _maskBitmap
            : new FormatConvertedBitmap(_maskBitmap, PixelFormats.Gray8, null, 0);
        int width = mask.PixelWidth;
        int height = mask.PixelHeight;
        int x = Math.Clamp((int)Math.Floor(bounds.X / 1000 * width), 0, Math.Max(0, width - 1));
        int y = Math.Clamp((int)Math.Floor(bounds.Y / 1000 * height), 0, Math.Max(0, height - 1));
        int right = Math.Clamp((int)Math.Ceiling(bounds.Right / 1000 * width), x + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom / 1000 * height), y + 1, height);
        var rect = new Int32Rect(x, y, right - x, bottom - y);

        int stride = rect.Width;
        byte[] pixels = new byte[stride * rect.Height];
        mask.CopyPixels(rect, pixels, stride, 0);

        long coverage = 0;
        int step = Math.Max(1, Math.Min(rect.Width, rect.Height) / 80);
        for (int row = 0; row < rect.Height; row += step)
        {
            int offset = row * stride;
            for (int column = 0; column < rect.Width; column += step)
            {
                coverage += pixels[offset + column];
            }
        }
        return coverage;
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
        double ActualMaskOffsetX,
        double ActualMaskOffsetY,
        NormalizedRect ActualMaskBounds);
}
