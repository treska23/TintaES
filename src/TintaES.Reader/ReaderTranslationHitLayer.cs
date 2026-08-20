using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Capa de interacción exclusiva del ejecutable Reader.
///
/// El visor compartido ya contiene un Canvas exactamente encima de la página, pero históricamente
/// RebuildTranslationHitAreas lo dejaba vacío. Esta capa materializa una zona transparente por
/// región. Como el Canvas vive dentro de _pageStage, WPF aplica automáticamente el mismo zoom,
/// centrado y scroll que a la imagen. El hit-test deja así de depender de convertir coordenadas a
/// mano, que era la fuente de los fallos de hover/toque del lector independiente.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private bool _standaloneTranslationHitLayerInstalled;
    private int _standaloneTranslationHitPageIndex = int.MinValue;
    private int _standaloneTranslationHitRegionCount = -1;
    private TouchDevice? _standaloneTranslationHitTouch;
    private StylusDevice? _standaloneTranslationHitStylus;
    private DateTime _standaloneTranslationHitLastTouchUtc;

    internal void EnsureStandaloneTranslationHitLayerInstalled()
    {
        if (_standaloneTranslationHitLayerInstalled)
        {
            return;
        }

        _standaloneTranslationHitLayerInstalled = true;

        // Un Canvas con Background=null no intercepta los huecos vacíos, pero sus hijos
        // transparentes sí participan en el hit-testing. Así paneo, swipe y navegación lateral
        // siguen funcionando fuera de los textos.
        _translationHitCanvas.Background = null;
        _translationHitCanvas.IsHitTestVisible = true;

        _pageStage.LayoutUpdated += StandaloneTranslationHitLayer_LayoutUpdated;

        // Se registra después de las rutas heredadas y con handledEventsToo. Si una ruta vieja
        // oculta la tarjeta por un cálculo geométrico incorrecto, esta capa (basada en hit-test
        // real de WPF) es la última en decidir el resultado del evento.
        _viewerHost.AddHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(StandaloneTranslationHitLayer_PreviewMouseMove),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(StandaloneTranslationHitLayer_PreviewTouchDown),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>(StandaloneTranslationHitLayer_PreviewTouchMove),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>(StandaloneTranslationHitLayer_PreviewTouchUp),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewStylusDownEvent,
            new StylusDownEventHandler(StandaloneTranslationHitLayer_PreviewStylusDown),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewStylusMoveEvent,
            new StylusEventHandler(StandaloneTranslationHitLayer_PreviewStylusMove),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewStylusUpEvent,
            new StylusEventHandler(StandaloneTranslationHitLayer_PreviewStylusUp),
            handledEventsToo: true);

        Closed += (_, _) =>
        {
            _standaloneTranslationHitTouch = null;
            _standaloneTranslationHitStylus = null;
        };

        RefreshStandaloneTranslationHitLayer(force: true);
    }

    private void StandaloneTranslationHitLayer_LayoutUpdated(object? sender, EventArgs e)
    {
        RefreshStandaloneTranslationHitLayer(force: false);
    }

    private void RefreshStandaloneTranslationHitLayer(bool force)
    {
        if (_readerDocument is null
            || _pageIndex < 0
            || _pageIndex >= _readerDocument.Pages.Count
            || _pageImage.Source is not BitmapSource bitmap)
        {
            if (_translationHitCanvas.Children.Count > 0)
            {
                _translationHitCanvas.Children.Clear();
            }
            _standaloneTranslationHitPageIndex = int.MinValue;
            _standaloneTranslationHitRegionCount = -1;
            return;
        }

        ComicRegion[] regions = _readerDocument.Pages[_pageIndex].Regions
            .Where(IsStandaloneReadableRegion)
            .ToArray();

        bool layerWasClearedBySharedViewer = regions.Length > 0
            && _translationHitCanvas.Children.Count == 0;
        if (!force
            && !layerWasClearedBySharedViewer
            && _standaloneTranslationHitPageIndex == _pageIndex
            && _standaloneTranslationHitRegionCount == regions.Length)
        {
            return;
        }

        _translationHitCanvas.Children.Clear();
        _translationHitCanvas.Width = bitmap.PixelWidth;
        _translationHitCanvas.Height = bitmap.PixelHeight;
        _translationHitCanvas.Background = null;
        _translationHitCanvas.IsHitTestVisible = true;

        foreach (ComicRegion region in regions)
        {
            NormalizedRect interaction = ResolveStandaloneInteractionBox(region);
            var target = new Border
            {
                Width = Math.Max(2d, interaction.Width / 1000d * bitmap.PixelWidth),
                Height = Math.Max(2d, interaction.Height / 1000d * bitmap.PixelHeight),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Tag = region,
                IsHitTestVisible = true
            };

            Canvas.SetLeft(target, interaction.X / 1000d * bitmap.PixelWidth);
            Canvas.SetTop(target, interaction.Y / 1000d * bitmap.PixelHeight);
            Panel.SetZIndex(target, 10 + Math.Clamp(region.Order, 0, 1000));
            _translationHitCanvas.Children.Add(target);
        }

        _standaloneTranslationHitPageIndex = _pageIndex;
        _standaloneTranslationHitRegionCount = regions.Length;
    }

    private static bool IsStandaloneReadableRegion(ComicRegion region) =>
        region.IsEnabled
        && (!string.IsNullOrWhiteSpace(region.Original)
            || region.HasRenderableTranslation
            || !string.IsNullOrWhiteSpace(region.Translation));

    private static NormalizedRect ResolveStandaloneInteractionBox(ComicRegion region)
    {
        // El objetivo táctil rodea el texto sin abarcar media página. El ratón comparte esta zona:
        // es deliberadamente algo más tolerante que el editor para que un bocadillo siga siendo
        // fácil de señalar en pantallas pequeñas.
        NormalizedRect textTarget = ComicRegionHitResolver.ResolveTouchHitBox(region).Clamp();
        NormalizedRect render = region.RenderBox.Clamp();

        double renderRatio = render.Area / Math.Max(1d, region.TextBox.Clamp().Area);
        bool plausibleRender = render.Area <= 150_000
            && render.Width <= 520
            && render.Height <= 520
            && renderRatio is >= 0.75 and <= 40;
        if (!plausibleRender)
        {
            return textTarget;
        }

        // No usamos el RenderBox completo si es enorme. Solo ampliamos hasta una unión razonable
        // para cubrir textos donde la caja OCR quedó demasiado ceñida.
        double left = Math.Min(textTarget.X, render.X);
        double top = Math.Min(textTarget.Y, render.Y);
        double right = Math.Max(textTarget.Right, render.Right);
        double bottom = Math.Max(textTarget.Bottom, render.Bottom);
        var union = new NormalizedRect(left, top, right - left, bottom - top).Clamp();
        return union.Area <= 100_000 ? union : textTarget;
    }

    private ComicRegion? ResolveStandaloneHitRegionFromWpf(Point pointOnCanvas)
    {
        if (_readerDocument is null
            || _pageIndex < 0
            || _pageIndex >= _readerDocument.Pages.Count
            || pointOnCanvas.X < 0
            || pointOnCanvas.Y < 0
            || pointOnCanvas.X > _translationHitCanvas.ActualWidth
            || pointOnCanvas.Y > _translationHitCanvas.ActualHeight)
        {
            return null;
        }

        DependencyObject? current = _translationHitCanvas.InputHitTest(pointOnCanvas) as DependencyObject;
        while (current is not null && !ReferenceEquals(current, _translationHitCanvas))
        {
            if (current is FrameworkElement element && element.Tag is ComicRegion region)
            {
                return IsStandaloneReadableRegion(region) ? region : null;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        return null;
    }

    private void StandaloneTranslationHitLayer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging
            || _translationMouseHeld
            || _standaloneTranslationHitTouch is not null
            || _standaloneTranslationHitStylus is not null
            || e.StylusDevice is not null
            || DateTime.UtcNow < _ignoreSyntheticMouseUntilUtc
            || _standaloneBottomNavigation?.IsMouseOver == true)
        {
            return;
        }

        RefreshStandaloneTranslationHitLayer(force: false);
        ComicRegion? region = ResolveStandaloneHitRegionFromWpf(e.GetPosition(_translationHitCanvas));
        if (region is null)
        {
            HideTranslationCard();
        }
        else
        {
            ShowTranslationCard(region);
        }
    }

    private void StandaloneTranslationHitLayer_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        _standaloneTranslationHitLastTouchUtc = DateTime.UtcNow;
        if (_directTouchUiConsumed || IsInsideStandaloneBottomNavigation(e.OriginalSource))
        {
            HideTranslationCard();
            return;
        }

        RefreshStandaloneTranslationHitLayer(force: false);
        ComicRegion? region = ResolveStandaloneHitRegionFromWpf(
            e.GetTouchPoint(_translationHitCanvas).Position);
        if (region is null)
        {
            HideTranslationCard();
            return;
        }

        _standaloneTranslationHitTouch = e.TouchDevice;
        _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
        ShowTranslationCard(region);
        // No se captura el dedo: ReaderDirectTouchNavigation debe seguir midiendo el swipe.
        e.Handled = true;
    }

    private void StandaloneTranslationHitLayer_PreviewTouchMove(object? sender, TouchEventArgs e)
    {
        if (_standaloneTranslationHitTouch != e.TouchDevice)
        {
            return;
        }

        _standaloneTranslationHitLastTouchUtc = DateTime.UtcNow;
        Vector gesture = _directTouchLast - _directTouchStart;
        if (_directTouchUiConsumed
            || (Math.Abs(gesture.X) >= DirectTouchCancelTranslationDistance
                && Math.Abs(gesture.X) > Math.Abs(gesture.Y)))
        {
            HideTranslationCard();
            return;
        }

        ComicRegion? region = ResolveStandaloneHitRegionFromWpf(
            e.GetTouchPoint(_translationHitCanvas).Position);
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

    private void StandaloneTranslationHitLayer_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (_standaloneTranslationHitTouch != e.TouchDevice)
        {
            return;
        }

        _standaloneTranslationHitTouch = null;
        HideTranslationCard();
        e.Handled = true;
    }

    private void StandaloneTranslationHitLayer_PreviewStylusDown(object sender, StylusDownEventArgs e)
    {
        if (!IsTouchStylus(e.StylusDevice)
            || DateTime.UtcNow - _standaloneTranslationHitLastTouchUtc < TimeSpan.FromMilliseconds(250)
            || _directStylusUiConsumed
            || IsInsideStandaloneBottomNavigation(e.OriginalSource))
        {
            return;
        }

        RefreshStandaloneTranslationHitLayer(force: false);
        ComicRegion? region = ResolveStandaloneHitRegionFromWpf(e.GetPosition(_translationHitCanvas));
        if (region is null)
        {
            HideTranslationCard();
            return;
        }

        _standaloneTranslationHitStylus = e.StylusDevice;
        _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
        ShowTranslationCard(region);
        e.Handled = true;
    }

    private void StandaloneTranslationHitLayer_PreviewStylusMove(object sender, StylusEventArgs e)
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

        ComicRegion? region = ResolveStandaloneHitRegionFromWpf(e.GetPosition(_translationHitCanvas));
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

    private void StandaloneTranslationHitLayer_PreviewStylusUp(object sender, StylusEventArgs e)
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
