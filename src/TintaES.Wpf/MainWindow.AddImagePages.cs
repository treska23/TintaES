using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TintaES.Wpf;

/// <summary>
/// Incorpora una o varias imágenes sueltas a la pestaña activa. Las selecciones sucesivas son
/// acumulativas: nunca sustituyen las páginas existentes ni crean un documento nuevo.
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
                ToolTip = "Añadir uno o varios archivos de imagen al final de la pestaña actual"
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
                "_Añadir páginas a la pestaña actual…",
                null,
                AddImagePagesButton_Click);

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
        // Añadir páginas es una orden de apertura, no una acción dependiente del documento visible.
        // Debe seguir disponible incluso durante navegación, análisis o guardado. El manejador espera
        // de forma segura a que termine la operación actual antes de modificar la colección.
        if (_addImagePagesButton is not null)
        {
            _addImagePagesButton.IsEnabled = true;
        }
        if (_menuAddImagePages is not null)
        {
            _menuAddImagePages.IsEnabled = true;
        }
    }

    private async void AddImagePagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_addingImagePages)
        {
            SetFooterStatus("Ya se están añadiendo páginas…", "#C99A35");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Añadir páginas a la pestaña actual",
            Filter = "Imágenes compatibles|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|PNG|*.png|JPEG|*.jpg;*.jpeg|WEBP|*.webp|BMP|*.bmp|TIFF|*.tif;*.tiff|Todos los archivos|*.*",
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
                "Selecciona una o varias imágenes JPG, PNG, WEBP, BMP o TIFF.",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // La orden ya ha sido aceptada. Si existe una operación breve en curso, se espera a que
        // termine en vez de desactivar el botón o descartar silenciosamente la selección.
        await AwaitCurrentDocumentReadyForOpenAsync();

        PersistVisibleComicPageRegions();
        int firstAddedIndex = _comicPages.Count;
        bool wasEmpty = firstAddedIndex == 0;

        _addingImagePages = true;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitleText.Text = selectedFiles.Length == 1
            ? "Añadiendo una página…"
            : $"Añadiendo {selectedFiles.Length} páginas…";
        BusyProgressBar.IsIndeterminate = true;
        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = true;
        FooterStatusText.Text = "Copiando las imágenes a la pestaña actual…";
        UpdateAddImagePagesAvailability();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            PrepareActiveDocumentForAccumulatedPages(selectedFiles, wasEmpty);
            string workspace = _comicWorkspace
                ?? throw new InvalidOperationException("No se pudo preparar el espacio de trabajo del proyecto.");

            IReadOnlyList<AddedImagePage> imported = await Task.Run(() =>
                ImportAddedImagePages(selectedFiles, workspace, firstAddedIndex));

            foreach (AddedImagePage page in imported)
            {
                _comicPages.Add(new ComicBookPageState(page.SourcePath, page.DisplayName));
            }

            if (_activeDocumentSession is not null)
            {
                for (int index = firstAddedIndex; index < _comicPages.Count; index++)
                {
                    _activeDocumentSession.DirtyPages.Add(index);
                }
            }

            SynchronizeActiveDocumentState();
            ClearComicPageBitmapCache();
            SynchronizeSelectorsAfterAddingPages(firstAddedIndex);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"No se pudieron añadir las páginas.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetFooterStatus("No se pudieron añadir las páginas.", "#EE594B");
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
                ? $"Página añadida · {_comicPages.Count} páginas en esta pestaña"
                : $"{selectedFiles.Length} páginas añadidas · {_comicPages.Count} páginas en esta pestaña",
            "#58A77D");
    }

    private void PrepareActiveDocumentForAccumulatedPages(
        IReadOnlyList<string> selectedFiles,
        bool wasEmpty)
    {
        // A diferencia de Abrir cómic/Abrir carpeta, esta ruta jamás llama a
        // PrepareNewComicWorkspace ni a BeginNewDocumentWorkspace: no crea pestañas.
        EnsureComicWorkspaceForAddedPages();
        InstallDocumentTabs();

        if (_activeDocumentSession is null)
        {
            var session = new ComicDocumentSession
            {
                Title = ResolveAddedPagesTitle(selectedFiles),
                Workspace = _comicWorkspace,
                PageIndex = -1,
                VisiblePageIndex = -1
            };
            _documentSessions.Add(session);
            _activeDocumentSession = session;
        }
        else
        {
            _activeDocumentSession.Workspace = _comicWorkspace;
        }

        if (wasEmpty)
        {
            _comicTitle = ResolveAddedPagesTitle(selectedFiles);
            _comicPageIndex = -1;
            _visibleComicPageIndex = -1;
            _activeDocumentSession.Title = _comicTitle;
        }

        RefreshDocumentTabs();
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
        BuildActiveDocumentSessionKey();

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
