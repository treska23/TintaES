using System.Windows;
using System.Windows.Input;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Ruta directa y autoritativa para las traducciones del Reader.
/// Cada rectángulo físico creado sobre una región recibe sus propios eventos: no hay conversión
/// de coordenadas ni InputHitTest intermedio. El evento termina exactamente en la zona que lleva
/// la ComicRegion en Tag y muestra esa traducción.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private bool _standaloneTranslationTargetEventsInstalled;
    private readonly HashSet<FrameworkElement> _wiredStandaloneTranslationTargets = [];

    internal void EnsureStandaloneTranslationTargetEventsInstalled()
    {
        if (_standaloneTranslationTargetEventsInstalled)
        {
            return;
        }

        _standaloneTranslationTargetEventsInstalled = true;
        _translationHitCanvas.IsHitTestVisible = true;

        // Se instala después de ReaderTranslationHitLayer, por lo que en cada LayoutUpdated
        // primero se reconstruyen las regiones y después se cablean los nuevos elementos.
        _pageStage.LayoutUpdated += StandaloneTranslationTargets_LayoutUpdated;
        Closed += (_, _) => _wiredStandaloneTranslationTargets.Clear();

        RefreshAndWireStandaloneTranslationTargets(force: true);
    }

    private void StandaloneTranslationTargets_LayoutUpdated(object? sender, EventArgs e)
    {
        RefreshAndWireStandaloneTranslationTargets(force: false);
    }

    private void RefreshAndWireStandaloneTranslationTargets(bool force)
    {
        RefreshStandaloneTranslationHitLayer(force);
        _translationHitCanvas.IsHitTestVisible = true;

        // Los elementos viejos desaparecen al cambiar de página. Limpiamos referencias para no
        // conservarlos vivos y conectamos únicamente los targets que existen ahora mismo.
        _wiredStandaloneTranslationTargets.RemoveWhere(target => target.Parent is null);

        foreach (FrameworkElement target in _translationHitCanvas.Children.OfType<FrameworkElement>())
        {
            if (target.Tag is not ComicRegion || !_wiredStandaloneTranslationTargets.Add(target))
            {
                continue;
            }

            target.AddHandler(
                UIElement.PreviewMouseMoveEvent,
                new MouseEventHandler(StandaloneTranslationTarget_PreviewMouseMove),
                handledEventsToo: true);
            target.AddHandler(
                UIElement.PreviewTouchDownEvent,
                new EventHandler<TouchEventArgs>(StandaloneTranslationTarget_PreviewTouchDown),
                handledEventsToo: true);
            target.AddHandler(
                UIElement.PreviewTouchMoveEvent,
                new EventHandler<TouchEventArgs>(StandaloneTranslationTarget_PreviewTouchMove),
                handledEventsToo: true);
            target.AddHandler(
                UIElement.PreviewTouchUpEvent,
                new EventHandler<TouchEventArgs>(StandaloneTranslationTarget_PreviewTouchUp),
                handledEventsToo: true);
            target.AddHandler(
                UIElement.PreviewStylusDownEvent,
                new StylusDownEventHandler(StandaloneTranslationTarget_PreviewStylusDown),
                handledEventsToo: true);
            target.AddHandler(
                UIElement.PreviewStylusMoveEvent,
                new StylusEventHandler(StandaloneTranslationTarget_PreviewStylusMove),
                handledEventsToo: true);
            target.AddHandler(
                UIElement.PreviewStylusUpEvent,
                new StylusEventHandler(StandaloneTranslationTarget_PreviewStylusUp),
                handledEventsToo: true);
        }
    }

    private static ComicRegion? GetStandaloneTranslationTargetRegion(object sender) =>
        sender is FrameworkElement { Tag: ComicRegion region } ? region : null;

    private void StandaloneTranslationTarget_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice is not null
            || _dragging
            || _translationMouseHeld
            || DateTime.UtcNow < _ignoreSyntheticMouseUntilUtc)
        {
            return;
        }

        ComicRegion? region = GetStandaloneTranslationTargetRegion(sender);
        if (region is not null)
        {
            ShowTranslationCard(region);
        }
    }

    private void StandaloneTranslationTarget_PreviewTouchDown(object sender, TouchEventArgs e)
    {
        if (_directTouchUiConsumed || IsInsideStandaloneBottomNavigation(e.OriginalSource))
        {
            return;
        }

        ComicRegion? region = GetStandaloneTranslationTargetRegion(sender);
        if (region is null)
        {
            return;
        }

        _standaloneTranslationHitTouch = e.TouchDevice;
        _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
        ShowTranslationCard(region);
        // ReaderDirectTouchNavigation ya ha visto el PreviewTouchDown en el viewerHost y puede
        // seguir convirtiendo un arrastre horizontal en cambio de página.
        e.Handled = true;
    }

    private void StandaloneTranslationTarget_PreviewTouchMove(object sender, TouchEventArgs e)
    {
        if (_standaloneTranslationHitTouch != e.TouchDevice)
        {
            return;
        }

        Vector gesture = _directTouchLast - _directTouchStart;
        if (_directTouchUiConsumed
            || (Math.Abs(gesture.X) >= DirectTouchCancelTranslationDistance
                && Math.Abs(gesture.X) > Math.Abs(gesture.Y)))
        {
            HideTranslationCard();
            return;
        }

        ComicRegion? region = GetStandaloneTranslationTargetRegion(sender);
        if (region is not null)
        {
            ShowTranslationCard(region);
        }
        e.Handled = true;
    }

    private void StandaloneTranslationTarget_PreviewTouchUp(object sender, TouchEventArgs e)
    {
        if (_standaloneTranslationHitTouch != e.TouchDevice)
        {
            return;
        }

        _standaloneTranslationHitTouch = null;
        HideTranslationCard();
        e.Handled = true;
    }

    private void StandaloneTranslationTarget_PreviewStylusDown(object sender, StylusDownEventArgs e)
    {
        if (!IsTouchStylus(e.StylusDevice)
            || _directStylusUiConsumed
            || IsInsideStandaloneBottomNavigation(e.OriginalSource))
        {
            return;
        }

        ComicRegion? region = GetStandaloneTranslationTargetRegion(sender);
        if (region is null)
        {
            return;
        }

        _standaloneTranslationHitStylus = e.StylusDevice;
        _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
        ShowTranslationCard(region);
        e.Handled = true;
    }

    private void StandaloneTranslationTarget_PreviewStylusMove(object sender, StylusEventArgs e)
    {
        if (_standaloneTranslationHitStylus != e.StylusDevice || !IsTouchStylus(e.StylusDevice))
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

        ComicRegion? region = GetStandaloneTranslationTargetRegion(sender);
        if (region is not null)
        {
            ShowTranslationCard(region);
        }
        e.Handled = true;
    }

    private void StandaloneTranslationTarget_PreviewStylusUp(object sender, StylusEventArgs e)
    {
        if (_standaloneTranslationHitStylus != e.StylusDevice || !IsTouchStylus(e.StylusDevice))
        {
            return;
        }

        _standaloneTranslationHitStylus = null;
        HideTranslationCard();
        e.Handled = true;
    }
}
