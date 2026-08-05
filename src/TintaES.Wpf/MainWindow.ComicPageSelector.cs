using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TintaES.Wpf;

/// <summary>
/// Navegación directa por el cómic, apertura multipágina/proyectos y enganche de la navegación
/// rápida con caché. Los botones anteriores conservan sus handlers originales, pero el class
/// handler intercepta anterior/siguiente antes de que ejecuten la carga síncrona antigua.
/// </summary>
public partial class MainWindow
{
    private ComboBox? _pageSelectorComboBox;
    private bool _syncingPageSelector;

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ComicPageSelectorLoaded),
            handledEventsToo: true);

        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(MainWindow_ComicNavigationButtonClassClick));
    }

    private static void MainWindow_ComicPageSelectorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.Dispatcher.BeginInvoke(
            window.InstallDirectPageSelector,
            DispatcherPriority.ApplicationIdle);
    }

    private static void MainWindow_ComicNavigationButtonClassClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Window.GetWindow(button) is not MainWindow window)
        {
            return;
        }

        if (ReferenceEquals(button, window._openFolderButton))
        {
            return;
        }

        if (window._comicBatchBusy || window._pageNavigationBusy)
        {
            return;
        }

        int targetIndex;
        if (ReferenceEquals(button, window._previousPageButton))
        {
            targetIndex = window._comicPageIndex - 1;
        }
        else if (ReferenceEquals(button, window._nextPageButton))
        {
            targetIndex = window._comicPageIndex + 1;
        }
        else
        {
            return;
        }

        if (targetIndex < 0 || targetIndex >= window._comicPages.Count)
        {
            return;
        }

        e.Handled = true;
        _ = window.ShowComicPageFastAsync(targetIndex);
    }

    private void InstallDirectPageSelector()
    {
        InstallComicBookHandlers();

        // Existe un único selector para abrir proyectos, archivos de cómic o páginas sueltas.
        // Se eliminan expresamente todos los controladores heredados para que el orden de carga
        // de los módulos no pueda volver a mostrar un diálogo limitado a CBZ o imágenes.
        OpenImageButton.Click -= OpenImageButton_Click;
        OpenImageButton.Click -= OpenComicFilesButton_Click;
        OpenImageButton.Click -= OpenStandaloneDocumentsButton_Click;
        OpenImageButton.Click -= OpenComicArchiveFilesButton_Click;
        OpenImageButton.Click -= OpenComicFilesButton_Click_Multi;
        OpenImageButton.Click += OpenComicFilesButton_Click_Multi;
        OpenImageButton.Content = "Abrir cómic";
        OpenImageButton.ToolTip =
            "Abrir un proyecto TintaES, un cómic CBZ/CBR/RAR o una o varias páginas";

        InstallProjectCommands();
        InstallClassicMenu();
        InstallPsdExportCommand();
        InstallComicReaderCommand();
        InstallPageSelectionPanel();

        if (_pageSelectorComboBox is not null
            || _pageCounterText?.Parent is not StackPanel previewPanel)
        {
            UpdateProjectCommandAvailability();
            UpdateClassicMenuAvailability();
            SyncPageSelectionPanel();
            return;
        }

        _pageSelectorComboBox = new ComboBox
        {
            Width = 112,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Ir directamente a una página"
        };
        _pageSelectorComboBox.SelectionChanged += PageSelectorComboBox_SelectionChanged;

        int counterIndex = previewPanel.Children.IndexOf(_pageCounterText);
        previewPanel.Children.Insert(Math.Min(previewPanel.Children.Count, counterIndex + 1), _pageSelectorComboBox);

        // La navegación y las cargas llaman explícitamente a SyncDirectPageSelector. Escucharlo
        // en LayoutUpdated provocaba cascadas continuas de estado, selección y 179 filas.
        SyncDirectPageSelector();
        UpdateProjectCommandAvailability();
        UpdateClassicMenuAvailability();
        SyncPageSelectionPanel();
    }

    private async void OpenComicFilesButton_Click_Multi(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir proyecto, cómic o seleccionar varias páginas",
            Filter =
                "TintaES, cómics o páginas (*.tinta;*.cbz;*.cbr;*.rar;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff)|" +
                "*.tinta;*.cbz;*.cbr;*.rar;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|" +
                "Proyecto TintaES (*.tinta)|*.tinta|" +
                "Cómics CBZ, CBR o RAR (*.cbz;*.cbr;*.rar)|*.cbz;*.cbr;*.rar|" +
                "Imágenes (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff)|" +
                "*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|" +
                "Todos los archivos (*.*)|*.*",
            FilterIndex = 1,
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string[] selected = dialog.FileNames;
            string[] projectFiles = selected
                .Where(path => string.Equals(
                    Path.GetExtension(path),
                    ".tinta",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (projectFiles.Length > 0)
            {
                if (selected.Length != 1 || projectFiles.Length != 1)
                {
                    MessageBox.Show(
                        this,
                        "Abre un proyecto .tinta por separado.",
                        "Tinta ES",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                await AwaitCurrentDocumentReadyForOpenAsync();
                await LoadTintaProjectAsync(projectFiles[0]);
                return;
            }

            string[] archiveFiles = selected
                .Where(IsSupportedComicArchive)
                .ToArray();
            if (archiveFiles.Length > 0)
            {
                if (selected.Length != 1 || archiveFiles.Length != 1)
                {
                    MessageBox.Show(
                        this,
                        "Abre un único CBZ, CBR o RAR cada vez. No mezcles un archivo de cómic con páginas sueltas en la misma selección.",
                        "Tinta ES",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                await AwaitCurrentDocumentReadyForOpenAsync();
                await LoadComicFromArchiveAsync(archiveFiles[0]);
                UpdateProjectCommandAvailability();
                UpdateClassicMenuAvailability();
                SyncPageSelectionPanel();
                return;
            }

            string[] images = selected
                .Where(IsSupportedComicImage)
                .OrderBy(path => path, NaturalPageComparer.Instance)
                .ToArray();
            if (images.Length == 0)
            {
                MessageBox.Show(
                    this,
                    "Selecciona un proyecto .tinta, un cómic CBZ/CBR/RAR o una o varias imágenes.",
                    "Tinta ES",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string title = images.Length == 1
                ? Path.GetFileNameWithoutExtension(images[0])
                : new DirectoryInfo(Path.GetDirectoryName(images[0]) ?? string.Empty).Name;
            await AwaitCurrentDocumentReadyForOpenAsync();
            LoadComicSession(images, title);
            UpdateProjectCommandAvailability();
            UpdateClassicMenuAvailability();
            SyncPageSelectionPanel();
        }
        catch (Exception exception)
        {
            string selectedPath = dialog.FileNames.Length == 1
                ? dialog.FileName
                : string.Empty;
            ShowComicOpenError(CreateFriendlyArchiveException(exception, selectedPath));
        }
    }

    private void SyncDirectPageSelector()
    {
        if (_pageSelectorComboBox is null)
        {
            return;
        }

        _syncingPageSelector = true;
        try
        {
            if (_pageSelectorComboBox.Items.Count != _comicPages.Count)
            {
                _pageSelectorComboBox.Items.Clear();
                for (int index = 0; index < _comicPages.Count; index++)
                {
                    _pageSelectorComboBox.Items.Add($"Página {index + 1}");
                }
            }

            int expected = _comicPageIndex >= 0 && _comicPageIndex < _comicPages.Count
                ? _comicPageIndex
                : -1;
            if (_pageSelectorComboBox.SelectedIndex != expected)
            {
                _pageSelectorComboBox.SelectedIndex = expected;
            }

            _pageSelectorComboBox.IsEnabled = _comicPages.Count > 0 && !_comicBatchBusy && !_pageNavigationBusy;
        }
        finally
        {
            _syncingPageSelector = false;
        }

        UpdateProjectCommandAvailability();
        UpdateClassicMenuAvailability();
        SyncPageSelectionPanel();
    }

    private void PageSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPageSelector
            || _pageSelectorComboBox is null
            || _comicBatchBusy
            || _pageNavigationBusy)
        {
            return;
        }

        int index = _pageSelectorComboBox.SelectedIndex;
        if (index >= 0 && index < _comicPages.Count && index != _comicPageIndex)
        {
            _ = ShowComicPageFastAsync(index);
        }
    }
}
