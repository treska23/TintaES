using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TintaES.Wpf;

/// <summary>
/// Apertura de proyectos .tinta y archivos CBZ desde el Reader. La interacción con bocadillos
/// vive exclusivamente en ComicReaderWindow.Translations; este archivo no registra gestos ni
/// rutas alternativas para mostrar la tarjeta.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private static readonly bool ReaderTintaOpeningRegistered = RegisterReaderTintaOpening();
    private bool _readerFileOpening;
    private bool _readerFileLifecycleInstalled;

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

        // ComicReaderWindow conserva por compatibilidad un manejador antiguo que solo conoce CBZ.
        // Esta es la única entrada de apertura para el botón y admite tanto .tinta como .cbz.
        e.Handled = true;
        await reader.OpenReaderFileFromDialogAsync();
    }

    private static void ComicReaderWindow_TintaOpeningLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComicReaderWindow reader)
        {
            reader.EnsureReaderFileLifecycle();
        }
    }

    private void EnsureReaderFileLifecycle()
    {
        if (_readerFileLifecycleInstalled)
        {
            return;
        }

        _readerFileLifecycleInstalled = true;
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

    public async Task OpenReaderPathAsync(string path)
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

    private void ReaderTintaOpening_Closed(object? sender, EventArgs e)
    {
        _readerDocument?.Dispose();
        _readerDocument = null;
    }
}
