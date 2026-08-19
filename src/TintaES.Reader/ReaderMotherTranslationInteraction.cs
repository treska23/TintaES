using System.Windows;
using System.Windows.Input;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// El Reader independiente reproduce la interacción del programa madre: situarse sobre el texto
/// muestra la traducción en una tarjeta centrada. Con ratón ocurre por hover; con pantalla táctil
/// ocurre mientras el dedo permanece apoyado. Se escucha Touch y también Stylus táctil porque
/// algunos dispositivos Windows entregan el dedo por esa ruta antes de promoverlo a Touch.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private bool _motherTranslationInteractionInstalled;
    private bool _motherFingerActive;

    internal void EnsureMotherTranslationInteractionInstalled()
    {
        if (_motherTranslationInteractionInstalled)
        {
            return;
        }

        _motherTranslationInteractionInstalled = true;

        _pageStage.AddHandler(
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(MotherReader_PreviewTouchDown),
            handledEventsToo: true);
        _pageStage.AddHandler(
            UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>(MotherReader_PreviewTouchMove),
            handledEventsToo: true);
        _pageStage.AddHandler(
            UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>(MotherReader_PreviewTouchUp),
            handledEventsToo: true);

        _pageStage.AddHandler(
            UIElement.PreviewStylusDownEvent,
            new StylusDownEventHandler(MotherReader_PreviewStylusDown),
            handledEventsToo: true);
        _pageStage.AddHandler(
            UIElement.PreviewStylusMoveEvent,
            new StylusEventHandler(MotherReader_PreviewStylusMove),
            handledEventsToo: true);
        _pageStage.AddHandler(
            UIElement.PreviewStylusUpEvent,
            new StylusEventHandler(MotherReader_PreviewStylusUp),
            handledEventsToo: true);

        _pageStage.LostTouchCapture += (_, _) => EndMotherFingerInteraction();
        _pageStage.LostStylusCapture += (_, _) => EndMotherFingerInteraction();
    }

    private ComicRegion? ResolveMotherReaderTouchRegion(Point pagePoint)
    {
        if (_readerDocument is null
            || _pageIndex < 0
            || _pageIndex >= _readerDocument.Pages.Count
            || _pageStage.ActualWidth <= 1
            || _pageStage.ActualHeight <= 1
            || pagePoint.X < 0
            || pagePoint.Y < 0
            || pagePoint.X > _pageStage.ActualWidth
            || pagePoint.Y > _pageStage.ActualHeight)
        {
            return null;
        }

        double x = pagePoint.X / _pageStage.ActualWidth * 1000d;
        double y = pagePoint.Y / _pageStage.ActualHeight * 1000d;
        return ComicRegionHitResolver.ResolveForTouch(
            _readerDocument.Pages[_pageIndex].Regions,
            x,
            y);
    }

    private void ShowMotherReaderTranslationAt(Point pagePoint)
    {
        ComicRegion? region = ResolveMotherReaderTouchRegion(pagePoint);
        if (region is null)
        {
            HideTranslationCard();
            return;
        }

        _motherFingerActive = true;
        _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddSeconds(3);
        ShowTranslationCard(region);
    }

    private void MotherReader_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        Point viewerPoint = e.GetTouchPoint(_viewerHost).Position;
        if (TryHandleStandaloneImmersiveNavigation(viewerPoint))
        {
            EndMotherFingerInteraction();
            e.Handled = true;
            return;
        }

        ShowMotherReaderTranslationAt(e.GetTouchPoint(_pageStage).Position);
        if (!_motherFingerActive)
        {
            return;
        }

        e.TouchDevice.Capture(_pageStage);
        e.Handled = true;
    }

    private void MotherReader_PreviewTouchMove(object? sender, TouchEventArgs e)
    {
        if (!_motherFingerActive)
        {
            return;
        }

        ShowMotherReaderTranslationAt(e.GetTouchPoint(_pageStage).Position);
        e.Handled = true;
    }

    private void MotherReader_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (!_motherFingerActive)
        {
            return;
        }

        if (e.TouchDevice.Captured is not null)
        {
            e.TouchDevice.Capture(null);
        }
        EndMotherFingerInteraction();
        e.Handled = true;
    }

    private void MotherReader_PreviewStylusDown(object sender, StylusDownEventArgs e)
    {
        if (!IsTouchStylus(e.StylusDevice))
        {
            return;
        }

        Point viewerPoint = e.GetPosition(_viewerHost);
        if (TryHandleStandaloneImmersiveNavigation(viewerPoint))
        {
            EndMotherFingerInteraction();
            e.Handled = true;
            return;
        }

        ShowMotherReaderTranslationAt(e.GetPosition(_pageStage));
        if (!_motherFingerActive)
        {
            return;
        }

        Stylus.Capture(_pageStage, CaptureMode.Element);
        e.Handled = true;
    }

    private void MotherReader_PreviewStylusMove(object sender, StylusEventArgs e)
    {
        if (!_motherFingerActive || !IsTouchStylus(e.StylusDevice))
        {
            return;
        }

        ShowMotherReaderTranslationAt(e.GetPosition(_pageStage));
        e.Handled = true;
    }

    private void MotherReader_PreviewStylusUp(object sender, StylusEventArgs e)
    {
        if (!_motherFingerActive || !IsTouchStylus(e.StylusDevice))
        {
            return;
        }

        if (Stylus.Captured == _pageStage)
        {
            Stylus.Capture(null);
        }
        EndMotherFingerInteraction();
        e.Handled = true;
    }

    private static bool IsTouchStylus(StylusDevice? stylus) =>
        stylus?.TabletDevice?.Type == TabletDeviceType.Touch;

    private void EndMotherFingerInteraction()
    {
        if (!_motherFingerActive)
        {
            return;
        }

        _motherFingerActive = false;
        _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
        HideTranslationCard();
    }
}
