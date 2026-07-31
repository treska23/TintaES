using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Puente mínimo entre el lienzo principal y las capas locales de bocadillo. Mantiene el fondo
/// limpio como fuente de la selección y ajusta una sola vez el marco técnico al recorte real.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<Guid> _balloonCropFrameRefreshPending = [];

    internal BitmapSource? CurrentBalloonSourceBitmap =>
        _cleanedBitmap ?? _cleanedBaseBitmap ?? _originalBitmap;

    internal void EnsureBalloonCropFrame(ComicRegion region, BalloonCrop crop)
    {
        if (_originalBitmap is null || region.IsManual)
        {
            return;
        }

        NormalizedRect frame = crop.ToNormalized(
            _originalBitmap.PixelWidth,
            _originalBitmap.PixelHeight);
        if (NearlyEqual(region.RenderBox, frame))
        {
            return;
        }

        region.RenderBox = frame;
        region.SafePolygon = crop.LayoutPolygon
            .Select(point => new NormalizedPoint(
                (crop.PageBounds.X + point.X) / _originalBitmap.PixelWidth * 1000,
                (crop.PageBounds.Y + point.Y) / _originalBitmap.PixelHeight * 1000))
            .ToArray();

        if (!_balloonCropFrameRefreshPending.Add(region.Id))
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                _balloonCropFrameRefreshPending.Remove(region.Id);
                if (_regions.Contains(region))
                {
                    RebuildOverlay();
                }
            },
            DispatcherPriority.ContextIdle);
    }

    private static bool NearlyEqual(NormalizedRect first, NormalizedRect second) =>
        Math.Abs(first.X - second.X) < 0.35
        && Math.Abs(first.Y - second.Y) < 0.35
        && Math.Abs(first.Width - second.Width) < 0.35
        && Math.Abs(first.Height - second.Height) < 0.35;
}
