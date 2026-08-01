using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TintaES.Wpf;

/// <summary>
/// Las imágenes sueltas son documentos independientes. Solo los CBZ y las carpetas
/// representan un cómic multipágina y conservan el navegador lateral de páginas.
/// </summary>
public partial class MainWindow
{
    private bool _standaloneImageTabsInstalled;
    private bool _standaloneSidebarPolicyQueued;

    private void InstallStandaloneImageTabs()
    {
        if (_standaloneImageTabsInstalled)
        {
            QueueStandaloneSidebarPolicy();
            return;
        }

        _standaloneImageTabsInstalled = true;
        OpenImageButton.Click -= OpenComicFilesButton_Click;
        OpenImageButton.Click -= OpenStandaloneDocumentsButton_Click;
        OpenImageButton.Click += OpenStandaloneDocumentsButton_Click;
        LayoutUpdated += (_, _) => QueueStandaloneSidebarPolicy();
        QueueStandaloneSidebarPolicy();
    }

    private async void OpenStandaloneDocumentsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir cómic o páginas",
            Filter = "Cómic CBZ|*.cbz|Imágenes|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|Todos los archivos|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await AwaitCurrentDocumentReadyForOpenAsync();
            if (dialog.FileNames.Length == 1
                && string.Equals(
                    Path.GetExtension(dialog.FileName),
                    ".cbz",
                    StringComparison.OrdinalIgnoreCase))
            {
                LoadComicFromCbz(dialog.FileName);
                QueueStandaloneSidebarPolicy();
                return;
            }

            string[] images = dialog.FileNames
                .Where(IsSupportedComicImage)
                .OrderBy(path => path, NaturalPageComparer.Instance)
                .ToArray();
            if (images.Length == 0)
            {
                MessageBox.Show(
                    this,
                    "Selecciona un archivo CBZ o una o varias imágenes.",
                    "Tinta ES",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            foreach (string image in images)
            {
                LoadComicSession(
                    [image],
                    Path.GetFileNameWithoutExtension(image));
            }

            RefreshDocumentTabs();
            QueueStandaloneSidebarPolicy();
            SetFooterStatus(
                images.Length == 1
                    ? $"Página abierta · {Path.GetFileName(images[0])}"
                    : $"{images.Length} páginas abiertas en pestañas independientes.",
                "#58A77D");
        }
        catch (Exception exception)
        {
            ShowComicOpenError(exception);
        }
    }

    private void QueueStandaloneSidebarPolicy()
    {
        if (_standaloneSidebarPolicyQueued || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _standaloneSidebarPolicyQueued = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _standaloneSidebarPolicyQueued = false;
                ApplyStandaloneSidebarPolicy();
            },
            DispatcherPriority.Background);
    }

    private void ApplyStandaloneSidebarPolicy()
    {
        if (_pageSelectionPanel is null || _comicPages.Count != 1)
        {
            return;
        }

        // Una página suelta ya dispone de su propia pestaña. Mostrar además una miniatura
        // solitaria a la izquierda duplica la navegación y da la impresión de que pertenece
        // al mismo cómic que las demás pestañas.
        SetPageSelectionPanelVisible(false);
    }
}
