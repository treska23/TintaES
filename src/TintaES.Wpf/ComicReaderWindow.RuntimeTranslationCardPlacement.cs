using System.Windows;
using System.Windows.Input;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Conecta la colocación adaptativa de la tarjeta con las rutas reales de interacción del lector.
/// El lector actual muestra traducciones por hover y por toque; esas rutas históricas solo llamaban
/// a ShowTranslationCard y dejaban la tarjeta centrada. Este manejador se ejecuta en el mismo ciclo
/// de entrada y aplica PositionTranslationCard después de resolver la región bajo el puntero/dedo.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private static readonly bool RuntimeTranslationCardPlacementRegistered =
        RegisterRuntimeTranslationCardPlacement();

    private static bool RegisterRuntimeTranslationCardPlacement()
    {
        EventManager.RegisterClassHandler(
            typeof(ComicReaderWindow),
            UIElement.MouseMoveEvent,
            new MouseEventHandler(RuntimeTranslationCard_MouseMove),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(ComicReaderWindow),
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(RuntimeTranslationCard_Touch),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(ComicReaderWindow),
            UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>(RuntimeTranslationCard_Touch),
            handledEventsToo: true);
        return true;
    }

    private static void RuntimeTranslationCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ComicReaderWindow window
            || window._readerDocument is null
            || window._dragging
            || window._readerTranslationTouchDevice is not null
            || DateTime.UtcNow < window._ignoreSyntheticMouseUntilUtc)
        {
            return;
        }

        ComicRegion? region = window.ResolveReaderRegionAt(e.GetPosition(window._pageStage));
        if (region is null)
        {
            return;
        }

        window._translationPlacementRegion = region;
        window.ShowTranslationCard(region);
        window.PositionTranslationCard(
            region,
            e.GetPosition(window._viewerHost),
            isTouch: false);
    }

    private static void RuntimeTranslationCard_Touch(object sender, TouchEventArgs e)
    {
        if (sender is not ComicReaderWindow window || window._readerDocument is null)
        {
            return;
        }

        ComicRegion? region = window.ResolveReaderTouchRegionAt(
            e.GetTouchPoint(window._pageStage).Position);
        if (region is null)
        {
            return;
        }

        window._translationPlacementRegion = region;
        window.ShowTranslationCard(region);
        window.PositionTranslationCard(
            region,
            e.GetTouchPoint(window._viewerHost).Position,
            isTouch: true);
    }
}
