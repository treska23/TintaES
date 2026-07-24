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

        // Abrir una carpeta inicia un cómic nuevo. Limpiamos únicamente el estado asociado al
        // proyecto anterior; el handler normal del botón seguirá abriendo la carpeta.
        if (ReferenceEquals(button, window._openFolderButton))
        {
            window._currentProjectPath = null;
            window.ClearComicPageBitmapCache();
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

        // Los handlers antiguos son síncronos y vuelven a decodificar todo desde disco. Marcamos
        // el evento como atendido y usamos la ruta asíncrona con caché y precarga de vecinos.
        e.Handled = true;
        _ = window.ShowComicPageFastAsync(targetIndex);
    }

    private void InstallDirectPageSelector()
    {
        InstallComicBookHandlers();

        OpenImageButton.Click -= OpenImageButton_Click;
        OpenImageButton.Click -= OpenComicFilesButton_Click;
        OpenImageButton.Click -= OpenComicFilesButton_Click_Multi;
        OpenImageButton.Click += OpenComicFilesButton_Click_Multi;
        OpenImageButton.Content = "Abrir cómic";

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
        _pageCounterText.LayoutUpdated += (_, _) => SyncDirectPageSelector();
        SyncDirectPageSelector();
        UpdateProjectCommandAvailability();
        UpdateClassicMenuAvailability();
        SyncPageSelectionPanel();
    }

    private void OpenComicFilesButton_Click_Multi(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir proyecto, cómic o seleccionar varias páginas",
            Filter = "TintaES, CBZ o páginas|*.tinta;*.cbz;*.png;*.jpg;*.jpeg;*.webp;*.bmp|Proyecto TintaES|*.tinta|Cómic CBZ|*.cbz|Imágenes|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Todos los archivos|*.*",
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
                .Where(path => string.Equals(Path.GetExtension(path), ".tinta", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (projectFiles.Length > 0)
            {
                if (selected.Length != 1 || projectFiles.Length != 1)
                {
                    MessageBox.Show(this, "Abre un proyecto .tinta por separado.", "Tinta ES",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _ = LoadTintaProjectAsync(projectFiles[0]);
                return;
            }

            string[] cbzFiles = selected
                .Where(path => string.Equals(Path.GetExtension(path), ".cbz", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (cbzFiles.Length > 0)
            {
                if (selected.Length != 1 || cbzFiles.Length != 1)
                {
                    MessageBox.Show(
                        this,
                        "Abre un CBZ por separado o selecciona varias imágenes. No mezcles un CBZ con páginas sueltas en la misma selección.",
                        "Tinta ES",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                _currentProjectPath = null;
                ClearComicPageBitmapCache();
                LoadComicFromCbz(cbzFiles[0]);
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
                    "Selecciona un proyecto .tinta, un archivo CBZ o una o varias imágenes.",
                    "Tinta ES",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string title = images.Length == 1
                ? Path.GetFileNameWithoutExtension(images[0])
                : new DirectoryInfo(Path.GetDirectoryName(images[0]) ?? string.Empty).Name;
            _currentProjectPath = null;
            ClearComicPageBitmapCache();
            LoadComicSession(images, title);
            UpdateProjectCommandAvailability();
            UpdateClassicMenuAvailability();
            SyncPageSelectionPanel();
        }
        catch (Exception exception)
        {
            ShowComicOpenError(exception);
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
