using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SharpCompress.Archives;

namespace TintaES.Wpf;

/// <summary>
/// Abre directamente cómics empaquetados. CBZ utiliza ZIP; CBR y RAR utilizan RAR. Las páginas
/// se extraen a nombres temporales controlados para evitar que rutas internas del archivo puedan
/// escribir fuera del espacio de trabajo de TintaES.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] ComicArchiveExtensions = [".cbz", ".cbr", ".rar"];

    private static readonly bool ComicArchiveOpeningRegistered = RegisterComicArchiveOpening();
    private bool _comicArchiveOpeningInstalled;

    private static bool RegisterComicArchiveOpening()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ComicArchiveOpeningLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ComicArchiveOpeningLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            // InstallComicBookHandlers se registra durante Loaded. Esta sustitución se ejecuta
            // después para que ningún controlador antiguo vuelva a limitar Abrir a imágenes/CBZ.
            window.Dispatcher.BeginInvoke(
                window.InstallComicArchiveOpening,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallComicArchiveOpening()
    {
        if (_comicArchiveOpeningInstalled || OpenImageButton is null)
        {
            return;
        }

        OpenImageButton.Click -= OpenImageButton_Click;
        OpenImageButton.Click -= OpenComicFilesButton_Click;
        OpenImageButton.Click -= OpenComicArchiveFilesButton_Click;
        OpenImageButton.Click += OpenComicArchiveFilesButton_Click;
        OpenImageButton.ToolTip =
            "Abrir un cómic CBZ, CBR o RAR, o seleccionar una o varias imágenes";
        _comicArchiveOpeningInstalled = true;
    }

    private async void OpenComicArchiveFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir cómic o páginas",
            Filter =
                "Archivos de cómic (*.cbz;*.cbr;*.rar)|*.cbz;*.cbr;*.rar|" +
                "Cómic CBZ (*.cbz)|*.cbz|" +
                "Cómic CBR o RAR (*.cbr;*.rar)|*.cbr;*.rar|" +
                "Imágenes (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff)|" +
                "*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|" +
                "Todos los archivos (*.*)|*.*",
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

            string[] archives = dialog.FileNames
                .Where(IsSupportedComicArchive)
                .ToArray();
            if (archives.Length > 0)
            {
                if (dialog.FileNames.Length != 1)
                {
                    MessageBox.Show(
                        this,
                        "Abre un único archivo CBZ, CBR o RAR cada vez. Para imágenes sueltas sí puedes seleccionar varias.",
                        "Tinta ES",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                await LoadComicFromArchiveAsync(archives[0]);
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
                    "Selecciona un archivo CBZ, CBR o RAR, o una o varias imágenes compatibles.",
                    "Tinta ES",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string title = images.Length == 1
                ? Path.GetFileNameWithoutExtension(images[0])
                : new DirectoryInfo(Path.GetDirectoryName(images[0]) ?? string.Empty).Name;
            LoadComicSession(images, title);
        }
        catch (Exception exception)
        {
            ShowComicOpenError(CreateFriendlyArchiveException(exception, dialog.FileName));
        }
    }

    private async Task LoadComicFromArchiveAsync(string archivePath)
    {
        string extension = Path.GetExtension(archivePath);
        if (string.Equals(extension, ".cbz", StringComparison.OrdinalIgnoreCase))
        {
            // Se conserva la ruta ZIP probada que ya utilizaba TintaES.
            LoadComicFromCbz(archivePath);
            return;
        }

        PrepareNewComicWorkspace();
        string sourceDirectory = Path.Combine(_comicWorkspace!, "source");
        Directory.CreateDirectory(sourceDirectory);

        using IArchive archive = ArchiveFactory.Open(archivePath);
        var entries = archive.Entries
            .Where(entry => !entry.IsDirectory)
            .Where(entry => IsSupportedComicImage(entry.Key ?? string.Empty))
            .OrderBy(entry => entry.Key ?? string.Empty, ArchivePageNameComparer.Instance)
            .ToArray();
        if (entries.Length == 0)
        {
            throw new InvalidOperationException(
                "El archivo no contiene páginas PNG, JPEG, WebP, BMP o TIFF compatibles.");
        }

        var paths = new List<string>(entries.Length);
        for (int index = 0; index < entries.Length; index++)
        {
            string entryName = entries[index].Key ?? string.Empty;
            string imageExtension = Path.GetExtension(entryName).ToLowerInvariant();
            string target = Path.Combine(sourceDirectory, $"{index + 1:D4}{imageExtension}");

            using Stream input = entries[index].OpenEntryStream();
            await using FileStream output = new(
                target,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await input.CopyToAsync(output);
            paths.Add(target);
        }

        InitializeComicPages(paths, Path.GetFileNameWithoutExtension(archivePath));
    }

    private static bool IsSupportedComicArchive(string path) =>
        ComicArchiveExtensions.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase);

    private static Exception CreateFriendlyArchiveException(Exception exception, string path)
    {
        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".cbr", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".rar", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                "No se pudo leer el archivo RAR/CBR. Comprueba que no esté dañado, dividido en varias partes " +
                "o protegido con contraseña.\n\nDetalle: " + exception.Message,
                exception);
        }

        return exception;
    }

    /// <summary>
    /// Orden natural sobre la ruta completa dentro del archivo. Así conserva correctamente
    /// carpetas como capitulo-01/001.jpg, capitulo-02/001.jpg y números sin rellenar con ceros.
    /// </summary>
    private sealed class ArchivePageNameComparer : IComparer<string>
    {
        public static ArchivePageNameComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            string left = x.Replace('\\', '/');
            string right = y.Replace('\\', '/');
            int leftIndex = 0;
            int rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
                {
                    int leftStart = leftIndex;
                    int rightStart = rightIndex;
                    while (leftIndex < left.Length && char.IsDigit(left[leftIndex])) leftIndex++;
                    while (rightIndex < right.Length && char.IsDigit(right[rightIndex])) rightIndex++;

                    string leftNumber = left[leftStart..leftIndex].TrimStart('0');
                    string rightNumber = right[rightStart..rightIndex].TrimStart('0');
                    leftNumber = leftNumber.Length == 0 ? "0" : leftNumber;
                    rightNumber = rightNumber.Length == 0 ? "0" : rightNumber;
                    if (leftNumber.Length != rightNumber.Length)
                    {
                        return leftNumber.Length.CompareTo(rightNumber.Length);
                    }

                    int numberComparison = string.Compare(
                        leftNumber,
                        rightNumber,
                        StringComparison.Ordinal);
                    if (numberComparison != 0)
                    {
                        return numberComparison;
                    }
                    continue;
                }

                int characterComparison = char.ToUpperInvariant(left[leftIndex])
                    .CompareTo(char.ToUpperInvariant(right[rightIndex]));
                if (characterComparison != 0)
                {
                    return characterComparison;
                }
                leftIndex++;
                rightIndex++;
            }

            return left.Length.CompareTo(right.Length);
        }
    }
}
