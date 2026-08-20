using System.Windows;
using System.Windows.Input;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Entrada de traducción exclusiva del Reader. Resuelve el texto desde la geometría realmente
/// dibujada en pantalla, después de zoom, centrado y scroll, para que ratón y dedo no dependan de
/// las coordenadas internas de un LayoutTransform. Se instala la última y escucha eventos ya
/// manejados para convivir con la navegación táctil.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private bool _standaloneDirectTranslationInstalled;
    private TouchDevice? _standaloneDirectTranslationTouch;
    private StylusDevice? _standaloneDirectTranslationStylus;
    private DateTime _standaloneDirectTranslationLastTouchUtc;

    internal void EnsureStandaloneDirectTranslationInputInstalled()
    {
        if (_standaloneDirectTranslationInstalled)
        {
            return;
        }

        _standaloneDirectTranslationInstalled = true;

        _viewerHost.AddHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(StandaloneDirectTranslation_PreviewMouseMove),
            handledEventsToo: true);

        _viewerHost.AddHandler(
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(StandaloneDirectTranslation_PreviewTouchDown),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>(StandaloneDirectTranslation_PreviewTouchMove),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>(StandaloneDirectTranslation_PreviewTouchUp),
            handledEventsToo: true);

        _viewerHost.AddHandler(
            UIElement.PreviewStylusDownEvent,
            new StylusDownEventHandler(StandaloneDirectTranslation_PreviewStylusDown),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewStylusMoveEvent,
            new StylusEventHandler(StandaloneDirectTranslation_PreviewStylusMove),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewStylusUpEvent,
            new StylusEventHandler(StandaloneDirectTranslation_PreviewStylusUp),
            handledEventsToo: true);

        Closed += (_, _) =>
        {
            _standaloneDirectTranslationTouch = null;
            _standaloneDirectTranslationStylus = null;
        };
    }

    private ComicRegion? ResolveStandaloneRegionAtViewerPoint(Point viewerPoint, bool touch)
    {
        if (_readerDocument is null
            || _pageIndex < 0
            || _pageIndex >= _readerDocument.Pages.Count
            || _pageImage.Source is null)
        {
            return null;
        }

        Point renderedOrigin;
        try
        {
            renderedOrigin = _pageImage.TranslatePoint(new Point(0, 0), _viewerHost);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        double scaleX = Math.Abs(_zoomTransform.ScaleX);
        double scaleY = Math.Abs(_zoomTransform.ScaleY);
        double renderedWidth = _pageImage.ActualWidth * scaleX;
        double renderedHeight = _pageImage.ActualHeight * scaleY;
        if (!double.IsFinite(renderedOrigin.X)
            || !double.IsFinite(renderedOrigin.Y)
            || !double.IsFinite(renderedWidth)
            || !double.IsFinite(renderedHeight)
            || renderedWidth <= 1
            || renderedHeight <= 1)
        {
            return null;
        }

        double localX = viewerPoint.X - renderedOrigin.X;
        double localY = viewerPoint.Y - renderedOrigin.Y;
        if (localX < 0 || localY < 0 || localX > renderedWidth || localY > renderedHeight)
        {
            return null;
        }

        double x = localX / renderedWidth * 1000d;
        double y = localY / renderedHeight * 1000d;
        IReadOnlyList<ComicRegion> regions = _readerDocument.Pages[_pageIndex].Regions;
        return touch
            ? ComicRegionHitResolver.ResolveForTouch(regions, x, y)
            : ComicRegionHitResolver.Resolve(regions, x, y);
    }

    private void StandaloneDirectTranslation_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice is not null
            || _dragging
            || _translationMouseHeld
            || _standaloneDirectTranslationTouch is not null
            || _standaloneDirectTranslationStylus is not null
            || DateTime.UtcNow < _ignoreSyntheticMouseUntilUtc
            || _standaloneBottomNavigation?.IsMouseOver == true)
        {
            return;
        }

        ComicRegion? region = ResolveStandaloneRegionAtViewerPoint(
            e.GetPosition(_viewerHost),
            touch: false);
        if (region is null)
        {
            HideTranslationCard();
        }
        else
        {
            ShowTranslationCard(region);
        }
    }

    private void StandaloneDirectTranslation_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        _standaloneDirectTranslationLastTouchUtc = DateTime.UtcNow;

        // La navegación directa se instala antes que esta capa. Si ha consumido la zona inferior
        // o un borde, la traducción no debe volver a aparecer encima.
        if (_directTouchUiConsumed || IsInsideStandaloneBottomNavigation(e.OriginalSource))
        {
            HideTranslationCard();
            return;
        }

        if (_standaloneDirectTranslationTouch is not null
            && _standaloneDirectTranslationTouch != e.TouchDevice)
        {
            return;
        }

        ComicRegion? region = ResolveStandaloneRegionAtViewerPoint(
            e.GetTouchPoint(_viewerHost).Position,
            touch: true);
        if (region is null)
        {
            HideTranslationCard();
            return;
        }

        _standaloneDirectTranslationTouch = e.TouchDevice;
        _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
        ShowTranslationCard(region);
        // No capturamos el dedo aquí. ReaderDirectTouchNavigation necesita seguir recibiendo el
        // gesto para convertir un arrastre horizontal en cambio de página.
        e.Handled = true;
    }

    private void StandaloneDirectTranslation_PreviewTouchMove(object? sender, TouchEventArgs e)
    {
        if (_standaloneDirectTranslationTouch != e.TouchDevice)
        {
            return;
        }

        _standaloneDirectTranslationLastTouchUtc = DateTime.UtcNow;
        Vector gesture = _directTouchLast - _directTouchStart;
        if (_directTouchUiConsumed
            || (Math.Abs(gesture.X) >= DirectTouchCancelTranslationDistance
                && Math.Abs(gesture.X) > Math.Abs(gesture.Y)))
        {
            HideTranslationCard();
            return;
        }

        ComicRegion? region = ResolveStandaloneRegionAtViewerPoint(
            e.GetTouchPoint(_viewerHost).Position,
            touch: true);
        if (region is null)
        {
            HideTranslationCard();
        }
        else
        {
            ShowTranslationCard(region);
        }
        e.Handled = true;
    }

    private void StandaloneDirectTranslation_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (_standaloneDirectTranslationTouch != e.TouchDevice)
        {
            return;
        }

        _standaloneDirectTranslationTouch = null;
        HideTranslationCard();
        e.Handled = true;
    }

    private void StandaloneDirectTranslation_PreviewStylusDown(object sender, StylusDownEventArgs e)
    {
        if (!IsTouchStylus(e.StylusDevice)
            || DateTime.UtcNow - _standaloneDirectTranslationLastTouchUtc < TimeSpan.FromMilliseconds(250)
            || _directStylusUiConsumed
            || IsInsideStandaloneBottomNavigation(e.OriginalSource))
        {
            return;
        }

        ComicRegion? region = ResolveStandaloneRegionAtViewerPoint(
            e.GetPosition(_viewerHost),
            touch: true);
        if (region is null)
        {
            HideTranslationCard();
            return;
        }

        _standaloneDirectTranslationStylus = e.StylusDevice;
        _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
        ShowTranslationCard(region);
        e.Handled = true;
    }

    private void StandaloneDirectTranslation_PreviewStylusMove(object sender, StylusEventArgs e)
    {
        if (_standaloneDirectTranslationStylus != e.StylusDevice || !IsTouchStylus(e.StylusDevice))
        {
            return;
        }

        Vector gesture = _directStylusLast - _directStylusStart;
        if (_directStylusUiConsumed
            || (Math.Abs(gesture.X) >= DirectTouchCancelTranslationDistance
                && Math.Abs(gesture.X) > Math.Abs(gesture.Y)))
        {
            HideTranslationCard();
            return;
        }

        ComicRegion? region = ResolveStandaloneRegionAtViewerPoint(
            e.GetPosition(_viewerHost),
            touch: true);
        if (region is null)
        {
            HideTranslationCard();
        }
        else
        {
            ShowTranslationCard(region);
        }
        e.Handled = true;
    }

    private void StandaloneDirectTranslation_PreviewStylusUp(object sender, StylusEventArgs e)
    {
        if (_standaloneDirectTranslationStylus != e.StylusDevice || !IsTouchStylus(e.StylusDevice))
        {
            return;
        }

        _standaloneDirectTranslationStylus = null;
        HideTranslationCard();
        e.Handled = true;
    }
}
