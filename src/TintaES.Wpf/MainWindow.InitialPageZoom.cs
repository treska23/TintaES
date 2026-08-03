using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// El primer contenido de cada documento entra encajado verticalmente. Al navegar entre las
/// páginas del mismo cómic se conserva exactamente el zoom elegido por el usuario. El Slider,
/// la transformación real y el porcentaje visible comparten el mismo valor.
/// </summary>
public partial class MainWindow
{
    private static readonly bool InitialPageZoomRegistered = RegisterInitialPageZoom();

    private bool _initialPageZoomInstalled;
    private string? _lastAutoFitDocumentIdentity;
    private int _verticalFitRequestVersion;

    private static bool RegisterInitialPageZoom()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_InitialPageZoomLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_InitialPageZoomLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.InstallInitialPageZoom();
        }
    }

    private void InstallInitialPageZoom()
    {
        if (_initialPageZoomInstalled)
        {
            return;
        }

        _initialPageZoomInstalled = true;

        DependencyPropertyDescriptor? sourceDescriptor = DependencyPropertyDescriptor.FromProperty(
            Image.SourceProperty,
            typeof(Image));
        sourceDescriptor?.AddValueChanged(PageImage, PageImage_SourceChanged_AutoVerticalFit);

        ZoomSlider.ValueChanged += ZoomSlider_ValueChanged_ExactPercentage;
        PreviewKeyDown += MainWindow_InitialPageZoomPreviewKeyDown;
        SynchronizeZoomPercentageWithActualScale();
    }

    private void PageImage_SourceChanged_AutoVerticalFit(object? sender, EventArgs e)
    {
        if (_originalBitmap is null || PageImage.Source is null)
        {
            return;
        }

        string documentIdentity = ResolveZoomDocumentIdentity();
        if (string.Equals(
                documentIdentity,
                _lastAutoFitDocumentIdentity,
                StringComparison.OrdinalIgnoreCase))
        {
            // Cambiar de página, de vista previa o de bitmap dentro del mismo cómic no debe
            // recalcular el encaje. También invalidamos cualquier ajuste antiguo que aún
            // estuviera esperando en el Dispatcher y reafirmamos el porcentaje actual.
            _verticalFitRequestVersion++;
            ApplyExactZoomTransform(ZoomSlider.Value);
            SynchronizeZoomPercentageWithActualScale();
            return;
        }

        _lastAutoFitDocumentIdentity = documentIdentity;
        int requestVersion = ++_verticalFitRequestVersion;

        // La carga antigua todavía programa FitImageToViewport con prioridad Loaded. Ejecutamos
        // después de ella para que el encaje vertical sea el resultado definitivo al abrir un
        // documento nuevo. La navegación posterior conservará el zoom escogido por el usuario.
        Dispatcher.BeginInvoke(
            () => _ = FitCurrentPageVerticallyAsync(requestVersion),
            DispatcherPriority.ContextIdle);
    }

    private string ResolveZoomDocumentIdentity()
    {
        if (_comicPages.Count > 0)
        {
            string project = _currentProjectPath ?? string.Empty;
            string firstPage = _comicPages[0].SourcePath ?? string.Empty;
            return $"comic|{project}|{firstPage}|{_comicPages.Count}";
        }

        // En una imagen independiente cada carga constituye un documento nuevo, incluso si se
        // vuelve a abrir el mismo archivo durante la sesión.
        return $"image|{_sourcePath}|{RuntimeHelpers.GetHashCode(_originalBitmap!)}";
    }

    internal Task FitCurrentPageVerticallyAsync() =>
        FitCurrentPageVerticallyAsync(++_verticalFitRequestVersion);

    private async Task FitCurrentPageVerticallyAsync(int requestVersion)
    {
        if (_originalBitmap is null || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        // Esperamos a que el ScrollViewer tenga un viewport real. Una página puede asignarse
        // antes de que WPF termine de colocar el selector izquierdo y la mesa de rotulación.
        for (int attempt = 0; attempt < 4 && ImageScrollViewer.ViewportHeight <= 1; attempt++)
        {
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            if (requestVersion != _verticalFitRequestVersion)
            {
                return;
            }
        }

        // Dos pasadas: la primera puede hacer aparecer la barra horizontal en una página doble;
        // la segunda usa la altura definitiva que queda sobre esa barra.
        for (int pass = 0; pass < 2; pass++)
        {
            if (requestVersion != _verticalFitRequestVersion || _originalBitmap is null)
            {
                return;
            }

            double viewportHeight = ImageScrollViewer.ViewportHeight;
            if (viewportHeight <= 1)
            {
                viewportHeight = Math.Max(
                    1,
                    ImageScrollViewer.ActualHeight
                    - ImageScrollViewer.Padding.Top
                    - ImageScrollViewer.Padding.Bottom);
            }

            double targetPercent = viewportHeight / Math.Max(1, _originalBitmap.PixelHeight) * 100;
            targetPercent = Math.Clamp(targetPercent, ZoomSlider.Minimum, ZoomSlider.Maximum);

            if (Math.Abs(ZoomSlider.Value - targetPercent) > 0.001)
            {
                ZoomSlider.Value = targetPercent;
            }
            else
            {
                ApplyExactZoomTransform(targetPercent);
            }

            SynchronizeZoomPercentageWithActualScale();
            await Dispatcher.Yield(DispatcherPriority.Render);
        }

        ImageScrollViewer.ScrollToTop();
        SynchronizeZoomPercentageWithActualScale();
    }

    private void MainWindow_InitialPageZoomPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            || e.Key is not (Key.D0 or Key.NumPad0))
        {
            return;
        }

        // Ctrl+0 sigue permitiendo pedir expresamente un nuevo ajuste vertical.
        e.Handled = true;
        _ = FitCurrentPageVerticallyAsync();
    }

    private void ZoomSlider_ValueChanged_ExactPercentage(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        // El handler original aplica el LayoutTransform. Este segundo handler deja el indicador
        // fiel al valor realmente aplicado, sin redondearlo a otro porcentaje.
        SynchronizeZoomPercentageWithActualScale();
    }

    private void ApplyExactZoomTransform(double percent)
    {
        double scale = percent / 100;
        if (ImageStage.LayoutTransform is ScaleTransform current
            && Math.Abs(current.ScaleX - scale) < 0.0001
            && Math.Abs(current.ScaleY - scale) < 0.0001)
        {
            return;
        }

        ImageStage.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void SynchronizeZoomPercentageWithActualScale()
    {
        if (ZoomText is null || ZoomSlider is null)
        {
            return;
        }

        double percent = ImageStage?.LayoutTransform is ScaleTransform transform
            ? transform.ScaleX * 100
            : ZoomSlider.Value;

        // Enteros cuando son exactos; una décima cuando el ajuste vertical necesita un valor
        // fraccionario. De este modo 26,6 % no vuelve a presentarse engañosamente como 27 %.
        string formatted = Math.Abs(percent - Math.Round(percent)) < 0.05
            ? Math.Round(percent).ToString("0", CultureInfo.CurrentCulture)
            : percent.ToString("0.0", CultureInfo.CurrentCulture);
        ZoomText.Text = $"{formatted} %";
    }
}
