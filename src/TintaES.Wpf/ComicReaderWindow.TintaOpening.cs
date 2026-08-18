using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Apertura directa de proyectos .tinta y consulta por hover/toque. Está aislado del editor para
/// que el mismo código pueda compilarse dentro del ejecutable TintaESReader.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private static readonly bool ReaderTintaOpeningRegistered = RegisterReaderTintaOpening();
    private bool _readerTintaOpeningInstalled;
    private bool _readerFileOpening;
    private TouchDevice? _readerTranslationTouchDevice;

    // El ejecutable Reader implementa este hook; dentro de TintaES.Wpf puede quedar sin
    // implementación. Así el visor compartido no adquiere ninguna dependencia del proyecto ligero.
    partial void OnStandaloneReaderContentOpened();

    private static bool RegisterReaderTintaOpening()
    {
        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(ReaderOpenButton_ClassClick));
        EventManager.RegisterClassHandler(
            typeof(ComicReaderWindow),
            LoadedEvent,
            new RoutedEventHandler(ComicReaderWindow_TintaOpeningLoaded),
            handledEventsToo: true);
        return true;
    }

    private static async void ReaderOpenButton_ClassClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || Window.GetWindow(button) is not ComicReaderWindow reader
            || !string.Equals(button.Content?.ToString(), "Abrir…", StringComparison.Ordinal))
        {
            return;
        }

        // El handler de clase se ejecuta antes que el handler antiguo de instancia. Al marcarlo
        // como atendido evitamos que se abra después un segundo selector exclusivo de CBZ.
        e.Handled = true;
        await reader.OpenReaderFileFromDialogAsync();
    }

    private static void ComicReaderWindow_TintaOpeningLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComicReaderWindow reader)
        {
            reader.EnsureReaderHoverInstalled();
        }
    }

    /// <summary>
    /// Instala la interacción vigente del lector: con ratón la traducción aparece al pasar por
    /// encima y con pantalla táctil aparece mientras el dedo permanece sobre el bocadillo.
    /// </summary>
    internal void EnsureReaderHoverInstalled()
    {
        if (_readerTintaOpeningInstalled)
        {
            return;
        }

        _readerTintaOpeningInstalled = true;
        _viewerHost.PreviewMouseMove += ReaderTranslationHover_PreviewMouseMove;
        _viewerHost.MouseLeave += ReaderTranslationHover_MouseLeave;

        // ScrollViewer y el sistema de manipulaciones de WPF pueden marcar un evento táctil
        // como manejado antes de que llegue a una suscripción normal. handledEventsToo hace que
        // el Reader siga recibiéndolo y pueda dar prioridad a un toque sobre un bocadillo.
        _viewerHost.AddHandler(
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(ReaderTranslationTouch_PreviewTouchDown),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>(ReaderTranslationTouch_PreviewTouchMove),
            handledEventsToo: true);
        _viewerHost.AddHandler(
            UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>(ReaderTranslationTouch_PreviewTouchUp),
            handledEventsToo: true);

        Closed += ReaderTintaOpening_Closed;

        if (_readerDocument is null && _archive is null)
        {
            _loadingText.Text = "Abre un proyecto .tinta o un CBZ para empezar.";
            _statusText.Text = "Sin proyecto abierto";
        }
    }

    private async Task OpenReaderFileFromDialogAsync()
    {
        if (_readerFileOpening)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Abrir proyecto o cómic",
            Filter =
                "Proyecto TintaES (*.tinta)|*.tinta|Comic Book ZIP (*.cbz)|*.cbz|" +
                "Proyectos y cómics (*.tinta;*.cbz)|*.tinta;*.cbz|Todos los archivos|*.*",
            FilterIndex = 3,
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await OpenReaderPathAsync(dialog.FileName);
        }
    }

    internal async Task OpenReaderPathAsync(string path)
    {
        if (_readerFileOpening || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _readerFileOpening = true;
        try
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".tinta")
            {
                ShowLoading("Abriendo proyecto de TintaES…");
                await Dispatcher.InvokeAsync(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.Render);

                ReaderComicDocument document = await ReaderTintaProjectLoader.LoadAsync(path);
                ReaderComicDocument? previous = _readerDocument;
                try
                {
                    DisposeArchive();
                    _readerDocument = document;
                    await OpenDocumentAsync();
                    previous?.Dispose();
                    OnStandaloneReaderContentOpened();
                }
                catch
                {
                    document.Dispose();
                    _readerDocument = previous;
                    throw;
                }
                return;
            }

            if (extension == ".cbz" || extension == ".zip")
            {
                _readerDocument?.Dispose();
                _readerDocument = null;
                await OpenArchiveAsync(path);
                OnStandaloneReaderContentOpened();
                return;
            }

            throw new InvalidOperationException(
                "El lector admite proyectos .tinta y cómics .cbz.");
        }
        catch (Exception exception)
        {
            ShowLoading("No se pudo abrir el archivo.");
            MessageBox.Show(
                this,
                $"No se pudo abrir el archivo.\n\n{exception.Message}",
                "Tinta ES Reader",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _readerFileOpening = false;
        }
    }

    private void ReaderTranslationHover_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_readerDocument is null
            || _translationMouseHeld
            || _readerTranslationTouchDevice is not null
            || DateTime.UtcNow < _ignoreSyntheticMouseUntilUtc)
        {
            return;
        }

        if (_dragging)
        {
            HideTranslationCard();
            return;
        }

        ComicRegion? region = ResolveReaderRegionAt(e.GetPosition(_pageStage));
        if (region is null)
        {
            HideTranslationCard();
            return;
        }

        // En escritorio no hace falta hacer clic: basta con colocar el puntero sobre el
        // bocadillo. La tarjeta no participa en hit-testing, así que el hover no se corta solo.
        ShowTranslationCard(region);
    }

    private void ReaderTranslationHover_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_translationMouseHeld && _readerTranslationTouchDevice is null)
        {
            HideTranslationCard();
        }
    }

    private ComicRegion? ResolveReaderTouchRegionAt(Point pagePoint)
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

    private void ReaderTranslationTouch_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(750);

        if (_readerTranslationTouchDevice is not null
            && _readerTranslationTouchDevice != e.TouchDevice)
        {
            return;
        }

        ComicRegion? region = ResolveReaderTouchRegionAt(
            e.GetTouchPoint(_pageStage).Position);
        if (region is null)
        {
            HideTranslationCard();
            return;
        }

        _readerTranslationTouchDevice = e.TouchDevice;
        ShowTranslationCard(region);
        e.TouchDevice.Capture(_viewerHost);
        e.Handled = true;
    }

    private void ReaderTranslationTouch_PreviewTouchMove(object? sender, TouchEventArgs e)
    {
        if (_readerTranslationTouchDevice != e.TouchDevice)
        {
            return;
        }

        _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(750);
        ComicRegion? region = ResolveReaderTouchRegionAt(
            e.GetTouchPoint(_pageStage).Position);
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

    private void ReaderTranslationTouch_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (_readerTranslationTouchDevice != e.TouchDevice)
        {
            return;
        }

        _readerTranslationTouchDevice = null;
        if (e.TouchDevice.Captured is not null)
        {
            e.TouchDevice.Capture(null);
        }
        HideTranslationCard();
        e.Handled = true;
    }

    private void ReaderTintaOpening_Closed(object? sender, EventArgs e)
    {
        _readerTranslationTouchDevice = null;
        _readerDocument?.Dispose();
        _readerDocument = null;
    }
}
