using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Apertura directa de proyectos .tinta y consulta por hover. Está aislado del editor para que
/// el mismo código pueda compilarse dentro del ejecutable TintaESReader.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private static readonly bool ReaderTintaOpeningRegistered = RegisterReaderTintaOpening();
    private bool _readerTintaOpeningInstalled;
    private bool _readerFileOpening;

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
    /// Instala de forma explícita la interacción vigente del lector: con ratón la traducción
    /// aparece al pasar por encima del bocadillo, sin necesidad de hacer clic. El ejecutable
    /// independiente llama a este método antes de mostrar la ventana para no depender de Loaded.
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
        if (!_translationMouseHeld && !_viewerHost.AreAnyTouchesCapturedWithin)
        {
            HideTranslationCard();
        }
    }

    private void ReaderTintaOpening_Closed(object? sender, EventArgs e)
    {
        _readerDocument?.Dispose();
        _readerDocument = null;
    }
}
