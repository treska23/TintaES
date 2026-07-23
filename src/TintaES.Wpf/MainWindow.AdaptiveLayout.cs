using System.Collections.Specialized;
using System.Windows.Threading;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private readonly AdaptiveBubbleLayoutService _adaptiveBubbleLayoutService = new();
    private bool _adaptiveLayoutQueued;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _regions.CollectionChanged += Regions_CollectionChangedForAdaptiveLayout;
    }

    private void Regions_CollectionChangedForAdaptiveLayout(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueAdaptiveBubbleLayout();
    }

    private void QueueAdaptiveBubbleLayout()
    {
        if (_adaptiveLayoutQueued)
        {
            return;
        }

        _adaptiveLayoutQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _adaptiveLayoutQueued = false;
                ApplyAdaptiveBubbleLayout();
            }));
    }

    private void ApplyAdaptiveBubbleLayout()
    {
        if (_cleanedBaseBitmap is null || _regions.Count == 0)
        {
            return;
        }

        if (!_adaptiveBubbleLayoutService.Refine(_cleanedBaseBitmap, _regions))
        {
            return;
        }

        // La detección modifica únicamente el espacio de rotulación. El fondo limpio,
        // generado por el borrado/inpainting del texto original, permanece intacto.
        RebuildOverlay();
    }
}
