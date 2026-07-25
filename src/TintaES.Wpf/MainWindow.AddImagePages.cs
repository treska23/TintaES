using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TintaES.Wpf;

/// <summary>
/// Permite incorporar una o varias imágenes sueltas al proyecto actual sin sustituir la sesión.
/// Los archivos se copian al espacio temporal del proyecto para que permanezcan disponibles hasta
/// que el usuario guarde el archivo .tinta.
/// </summary>
public partial class MainWindow
{
    private static readonly bool AddImagePagesRegistered = RegisterAddImagePages();

    private Button? _addImagePagesButton;
    private MenuItem? _menuAddImagePages;
    private bool _addingImagePages;

    private static bool RegisterAddImagePages()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_AddImagePagesLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_AddImagePagesLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallAddImagePagesCommand,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallAddImagePagesCommand()
    {
        InstallComicBookHandlers();
        InstallClassicMenu();

        Style? toolbarStyle = FindResource("ToolbarButton") as Style;
        if (_addImagePagesButton is null && _openFolderButton?.Parent is StackPanel openPanel)
        {
            _addImagePagesButton = new Button
            {
                Content = "＋ Páginas",
                Style = toolbarStyle,
                Margin = new Thickness(7, 0, 0, 0),
                ToolTip = "Agregar uno o varios archivos JPG, PNG, WEBP o BMP al proyecto actual"
            };
            _addImagePagesButton.Click += AddImagePagesButton_Click;

            int folderIndex = openPanel.Children.IndexOf(_openFolderButton);
            openPanel.Children.Insert(
                Math.Min(openPanel.Children.Count, Math.Max(0, folderIndex + 1)),
                _addImagePagesButton);
        }

        if (_menuAddImagePages is null
            && _classicMenu?.Items.OfType<MenuItem>().FirstOrDefault() is MenuItem fileMenu)
        {
            _menuAddImagePages = CreateMenuItem(
                "_Agregar imágenes…",
                null,
                AddImagePagesButton_Click);

            // Abrir cómic, abrir proyecto, agregar imágenes, separador.
            int insertionIndex = Math.Min(2, fileMenu.Items.Count);
            fileMenu.Items.Insert(insertionIndex, _menuAddImagePages);
        }

        BusyOverlay.IsVisibleChanged -= BusyOverlay_AddImagePagesVisibilityChanged;
        BusyOverlay.IsVisibleChanged += BusyOverlay_AddImagePagesVisibilityChanged;
        UpdateAddImagePagesAvailability();
    }

    private void BusyOverlay_AddImagePagesVisibilityChanged(
        object sender,
        DependencyPropertyChangedEventArgs e) =>
        UpdateAddImagePagesAvailability();

    private void UpdateAddImagePagesAvailability()
    {
        bool available = !_addingImagePages
            && !_comicBatchBusy
            && !_pageNavigationBusy
            && BusyOverlay.Visibility != Visibility.Visible;

        if (_addImagePagesButton is not null)
        {
            _addImagePagesButton.IsEnabled = available;
        }
        if (_menuAddImagePages is not null)
        {
            _menuAddImagePages.IsEnabled = available;
        }
    }

    private async void AddImagePagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_addingImagePages || _comicBatchBusy || _pageNavigationBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Agregar páginas de imagen",
            Filter = "Imágenes compatibles|*.png;*.jpg;*.jpeg;*.webp;*.bmp|PNG|*.png|JPEG|*.jpg;*.jpeg|WEBP|*.webp|BMP|*.bmp|Todos los archivos|*.*",
            FilterIndex = 1,
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string[] selectedFiles = dialog.FileNames
            .Where(IsSupportedComicImage)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, NaturalPageComparer.Instance)
            .ToArray();

        if (selectedFiles.Length == 0)
        {
            MessageBox.Show(
                this,
                "Selecciona una o varias imágenes JPG, PNG, WEBP o BMP.",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        PersistVisibleComicPageRegions();
        int firstAddedIndex = _comicPages.Count;
        bool wasEmpty = firstAddedIndex == 0;

        _addingImagePages = true;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitleText.Text = selectedFiles.Length == 1
            ? "Agregando una página…"
            : $"Agregando {selectedFiles.Length} páginas…";
        BusyProgressBar.IsIndeterminate = true;
        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = true;
        FooterStatusText.Text = "Copiando las imágenes al proyecto…";
        UpdateAddImagePagesAvailability();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            if (wasEmpty)
            {
                PrepareNewComicWorkspace();
                _comicTitle = ResolveAddedPagesTitle(selectedFiles);
                _comicPageIndex = -1;
                _visibleComicPageIndex = -1;
            }
            else
            {
                EnsureComicWorkspaceForAddedPages();
            }

            string workspace = _comicWorkspace
                ?? throw new InvalidOperationException("No se pudo preparar el espacio de trabajo del proyecto.");

            IReadOnlyList<AddedImagePage> imported = await Task.Run(() =>
                ImportAddedImagePages(selectedFiles, workspace, firstAddedIndex));

            foreach (AddedImagePage page in imported)
            {
                _comicPages.Add(new ComicBookPageState(page.SourcePath, page.DisplayName));
            }

            ClearComicPageBitmapCache();
            SynchronizeSelectorsAfterAddingPages(firstAddedIndex);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"No se pudieron agregar las páginas.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetFooterStatus("No se pudieron agregar las páginas.", "#EE594B");
            return;
        }
        finally
        {
            _addingImagePages = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
            BusyProgressBar.IsIndeterminate = false;
            FooterProgressBar.Visibility = Visibility.Collapsed;
            FooterProgressBar.IsIndeterminate = false;
            UpdateAddImagePagesAvailability();
            UpdateComicControls();
            UpdateProjectCommandAvailability();
            UpdateClassicMenuAvailability();
        }

        await ShowComicPageFastAsync(firstAddedIndex);
        SetFooterStatus(
            selectedFiles.Length == 1
                ? $"Página agregada · {_comicPages.Count} páginas en el proyecto"
                : $"{selectedFiles.Length} páginas agregadas · {_comicPages.Count} páginas en el proyecto",
            "#58A77D");
    }

    private void EnsureComicWorkspaceForAddedPages()
    {
        if (string.IsNullOrWhiteSpace(_comicWorkspace))
        {
            _comicWorkspace = Path.Combine(
                Path.GetTempPath(),
                "TintaES",
                "comic-" + Guid.NewGuid().ToString("N"));
        }

        Directory.CreateDirectory(_comicWorkspace);
        Directory.CreateDirectory(Path.Combine(_comicWorkspace, "processed"));
    }

    private static IReadOnlyList<AddedImagePage> ImportAddedImagePages(
        IReadOnlyList<string> sourceFiles,
        string workspace,
        int firstPageIndex)
    {
        string targetDirectory = Path.Combine(workspace, "added-source");
        Directory.CreateDirectory(targetDirectory);
        var imported = new List<AddedImagePage>(sourceFiles.Count);

        try
        {
            for (int index = 0; index < sourceFiles.Count; index++)
            {
                string source = sourceFiles[index];
                string extension = Path.GetExtension(source).ToLowerInvariant();
                string target = Path.Combine(
                    targetDirectory,
                    $"{firstPageIndex + index + 1:D4}-{Guid.NewGuid():N}{extension}");
                File.Copy(source, target, overwrite: false);
                imported.Add(new AddedImagePage(target, Path.GetFileName(source)));
            }
            return imported;
        }
        catch
        {
            foreach (AddedImagePage page in imported)
            {
                try
                {
                    File.Delete(page.SourcePath);
                }
                catch
                {
                }
            }
            throw;
        }
    }

    private void SynchronizeSelectorsAfterAddingPages(int firstAddedIndex)
    {
        for (int index = firstAddedIndex; index < _comicPages.Count; index++)
        {
            _selectedComicPageIndices.Add(index);
        }

        if (_pageSelectionPanel is not null)
        {
            _pageSelectionSessionKey = BuildCurrentPageSelectionSessionKey();
            _lastPageSelectionVisualIndex = -2;
            RebuildPageSelectionItems();
            SetPageSelectionPanelVisible(_comicPages.Count > 1);
        }

        UpdateComicControls();
        SyncDirectPageSelector();
        UpdateProjectCommandAvailability();
        UpdateClassicMenuAvailability();
    }

    private string BuildCurrentPageSelectionSessionKey() =>
        _comicPages.Count == 0
            ? string.Empty
            : $"{_comicPages.Count}|{_comicPages[0].SourcePath}|{_comicPages[^1].SourcePath}";

    private static string ResolveAddedPagesTitle(IReadOnlyList<string> images)
    {
        if (images.Count == 1)
        {
            return Path.GetFileNameWithoutExtension(images[0]);
        }

        string? firstDirectory = Path.GetDirectoryName(images[0]);
        bool sameDirectory = images.All(path =>
            string.Equals(
                Path.GetDirectoryName(path),
                firstDirectory,
                StringComparison.OrdinalIgnoreCase));
        return sameDirectory && !string.IsNullOrWhiteSpace(firstDirectory)
            ? new DirectoryInfo(firstDirectory).Name
            : "Páginas seleccionadas";
    }

    private sealed record AddedImagePage(string SourcePath, string DisplayName);
}
