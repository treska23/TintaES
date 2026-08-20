using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TintaES.Wpf;

/// <summary>
/// Entrada táctil robusta exclusiva del ejecutable Reader.
///
/// La navegación no puede depender de que el dedo caiga dentro de la imagen ni de que WPF llegue
/// a iniciar una Manipulation: los manejadores de traducción pueden capturar el mismo dedo antes.
/// Por eso esta capa escucha en todo el viewerHost con handledEventsToo y mide el gesto aunque un
/// bocadillo ya haya consumido el evento.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private const double DirectTouchBottomActivationHeight = 112d;
    private const double DirectTouchEdgeActivationWidth = 92d;
    private const double DirectTouchCancelTranslationDistance = 24d;

    private bool _directTouchNavigationInstalled;
    private TouchDevice? _directTouchDevice;
    private Point _directTouchStart;
    private Point _directTouchLast;
    private DateTime _directTouchStartedUtc;
    private bool _directTouchUiConsumed;

    private StylusDevice? _directTouchStylus;
    private Point _directStylusStart;
    private Point _directStylusLast;
    private DateTime _directStylusStartedUtc;
    private bool _directStylusUiConsumed;
    private DateTime _directLastTouchUtc;
    private DateTime _directLastPageTurnUtc;

    internal void EnsureDirectTouchNavigationInstalled()
    {
        if (_directTouchNavigationInstalled)
        {
            return;
        }

        _directTouchNavigationInstalled = true;

        // El antiguo cambio de página dependía de ManipulationCompleted. Conservamos las
        // manipulaciones para zoom/pan, pero el cambio de página lo decide esta capa explícita.
        _pageStage.ManipulationCompleted -= PageStage_ManipulationCompleted;

        _viewerHost.AddHandler(
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(DirectReader_PreviewTouchDown),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>(DirectReader_PreviewTouchMove),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>(DirectReader_PreviewTouchUp),
            handledEventsToo: true);

        _viewerHost.AddHandler(
            UIElement.PreviewStylusDownEvent,
            new StylusDownEventHandler(DirectReader_PreviewStylusDown),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewStylusMoveEvent,
            new StylusEventHandler(DirectReader_PreviewStylusMove),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewStylusUpEvent,
            new StylusEventHandler(DirectReader_PreviewStylusUp),
            handledEventsToo: true);

        Closed += (_, _) => ResetDirectReaderTouchState();
    }

    private void DirectReader_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        if (_pageIndex < 0 || PageCount <= 0 || IsInsideStandaloneBottomNavigation(e.OriginalSource))
        {
            return;
        }

        _directLastTouchUtc = DateTime.UtcNow;
        _directTouchStylus = null;
        _directStylusUiConsumed = false;

        if (_directTouchDevice is not null && _directTouchDevice != e.TouchDevice)
        {
            // Segundo dedo: dejamos que el sistema de Manipulation se encargue del pinch.
            return;
        }

        Point point = e.GetTouchPoint(_viewerHost).Position;
        _directTouchDevice = e.TouchDevice;
        _directTouchStart = point;
        _directTouchLast = point;
        _directTouchStartedUtc = DateTime.UtcNow;
        _directTouchUiConsumed = false;

        if (TryConsumeDirectNavigationUi(point, e.OriginalSource, fromTouch: true))
        {
            _directTouchUiConsumed = true;
            _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
            e.TouchDevice.Capture(_viewerHost);
            e.Handled = true;
        }
    }

    private void DirectReader_PreviewTouchMove(object? sender, TouchEventArgs e)
    {
        if (_directTouchDevice != e.TouchDevice)
        {
            return;
        }

        _directLastTouchUtc = DateTime.UtcNow;
        _directTouchLast = e.GetTouchPoint(_viewerHost).Position;
        Vector gesture = _directTouchLast - _directTouchStart;
        if (!_directTouchUiConsumed
            && Math.Abs(gesture.X) >= DirectTouchCancelTranslationDistance
            && Math.Abs(gesture.X) > Math.Abs(gesture.Y))
        {
            HideTranslationCard();
        }

        if (_directTouchUiConsumed)
        {
            e.Handled = true;
        }
    }

    private void DirectReader_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (_directTouchDevice != e.TouchDevice)
        {
            return;
        }

        _directLastTouchUtc = DateTime.UtcNow;
        _directTouchLast = e.GetTouchPoint(_viewerHost).Position;
        if (!_directTouchUiConsumed)
        {
            TryNavigateDirectSwipe(
                _directTouchLast - _directTouchStart,
                DateTime.UtcNow - _directTouchStartedUtc);
        }

        if (e.TouchDevice.Captured == _viewerHost)
        {
            e.TouchDevice.Capture(null);
        }

        bool consumed = _directTouchUiConsumed;
        _directTouchDevice = null;
        _directTouchUiConsumed = false;
        if (consumed)
        {
            e.Handled = true;
        }
    }

    private void DirectReader_PreviewStylusDown(object sender, StylusDownEventArgs e)
    {
        if (!IsTouchStylus(e.StylusDevice)
            || _pageIndex < 0
            || PageCount <= 0
            || IsInsideStandaloneBottomNavigation(e.OriginalSource))
        {
            return;
        }

        // Si Windows ya promovió esta pulsación a Touch, Touch es la fuente autoritativa.
        if (DateTime.UtcNow - _directLastTouchUtc < TimeSpan.FromMilliseconds(220))
        {
            return;
        }

        Point point = e.GetPosition(_viewerHost);
        _directTouchStylus = e.StylusDevice;
        _directStylusStart = point;
        _directStylusLast = point;
        _directStylusStartedUtc = DateTime.UtcNow;
        _directStylusUiConsumed = false;

        if (TryConsumeDirectNavigationUi(point, e.OriginalSource, fromTouch: true))
        {
            _directStylusUiConsumed = true;
            _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
            Stylus.Capture(_viewerHost, CaptureMode.Element);
            e.Handled = true;
        }
    }

    private void DirectReader_PreviewStylusMove(object sender, StylusEventArgs e)
    {
        if (_directTouchStylus != e.StylusDevice || !IsTouchStylus(e.StylusDevice))
        {
            return;
        }

        // Una promoción Touch posterior sustituye a la ruta Stylus.
        if (_directTouchDevice is not null)
        {
            _directTouchStylus = null;
            return;
        }

        _directStylusLast = e.GetPosition(_viewerHost);
        Vector gesture = _directStylusLast - _directStylusStart;
        if (!_directStylusUiConsumed
            && Math.Abs(gesture.X) >= DirectTouchCancelTranslationDistance
            && Math.Abs(gesture.X) > Math.Abs(gesture.Y))
        {
            HideTranslationCard();
        }

        if (_directStylusUiConsumed)
        {
            e.Handled = true;
        }
    }

    private void DirectReader_PreviewStylusUp(object sender, StylusEventArgs e)
    {
        if (_directTouchStylus != e.StylusDevice || !IsTouchStylus(e.StylusDevice))
        {
            return;
        }

        _directStylusLast = e.GetPosition(_viewerHost);
        if (!_directStylusUiConsumed && _directTouchDevice is null)
        {
            TryNavigateDirectSwipe(
                _directStylusLast - _directStylusStart,
                DateTime.UtcNow - _directStylusStartedUtc);
        }

        if (Stylus.Captured == _viewerHost)
        {
            Stylus.Capture(null);
        }

        bool consumed = _directStylusUiConsumed;
        _directTouchStylus = null;
        _directStylusUiConsumed = false;
        if (consumed)
        {
            e.Handled = true;
        }
    }

    private bool TryConsumeDirectNavigationUi(Point viewerPoint, object originalSource, bool fromTouch)
    {
        if (_viewerHost.ActualWidth <= 1 || _viewerHost.ActualHeight <= 1)
        {
            return false;
        }

        if (viewerPoint.Y >= _viewerHost.ActualHeight - DirectTouchBottomActivationHeight)
        {
            HideTranslationCard();
            ShowStandaloneBottomNavigation(fromTouch);
            return true;
        }

        // Dentro de la propia página ya existe la ruta de navegación del programa madre.
        // Esta capa cubre además los márgenes de pantalla y los botones superpuestos, que antes
        // quedaban fuera de _pageStage y por eso no recibían el toque.
        if (IsDescendantOfReaderElement(originalSource, _pageStage))
        {
            return false;
        }

        if (viewerPoint.X <= DirectTouchEdgeActivationWidth && _leftEdgeNavigationAvailable)
        {
            HideTranslationCard();
            NavigateDirectEdge(rightArrow: false);
            return true;
        }

        if (viewerPoint.X >= _viewerHost.ActualWidth - DirectTouchEdgeActivationWidth
            && _rightEdgeNavigationAvailable)
        {
            HideTranslationCard();
            NavigateDirectEdge(rightArrow: true);
            return true;
        }

        return false;
    }

    private void NavigateDirectEdge(bool rightArrow)
    {
        if (DateTime.UtcNow - _directLastPageTurnUtc < TimeSpan.FromMilliseconds(350))
        {
            return;
        }

        _directLastPageTurnUtc = DateTime.UtcNow;
        NavigateStandaloneEdge(rightArrow);
    }

    private void TryNavigateDirectSwipe(Vector gesture, TimeSpan elapsed)
    {
        if (DateTime.UtcNow - _directLastPageTurnUtc < TimeSpan.FromMilliseconds(350))
        {
            return;
        }

        bool towardRight = gesture.X > 0;
        bool atHorizontalEdge = _scrollViewer.ScrollableWidth <= 2
            || (towardRight
                ? _scrollViewer.HorizontalOffset <= 2
                : _scrollViewer.HorizontalOffset >= _scrollViewer.ScrollableWidth - 2);

        int pageDelta = ResolveSwipePageDelta(
            gesture,
            elapsed,
            scaledGesture: false,
            _rightToLeft,
            _pageIndex,
            PageCount,
            atHorizontalEdge,
            _scrollViewer.ViewportWidth);
        if (pageDelta == 0)
        {
            return;
        }

        _directLastPageTurnUtc = DateTime.UtcNow;
        HideTranslationCard();
        _ = ShowPageAsync(_pageIndex + pageDelta);
    }

    private bool IsInsideStandaloneBottomNavigation(object originalSource) =>
        _standaloneBottomNavigation is not null
        && IsDescendantOfReaderElement(originalSource, _standaloneBottomNavigation);

    private static bool IsDescendantOfReaderElement(object source, DependencyObject ancestor)
    {
        if (source is not DependencyObject current)
        {
            return false;
        }

        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return false;
    }

    private void ResetDirectReaderTouchState()
    {
        _directTouchDevice = null;
        _directTouchStylus = null;
        _directTouchUiConsumed = false;
        _directStylusUiConsumed = false;
    }
}
