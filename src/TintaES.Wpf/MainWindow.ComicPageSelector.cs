using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TintaES.Wpf;

/// <summary>
/// Añade navegación directa a cualquier página del cómic sin llenar la interfaz de pestañas.
/// También fuerza el selector de apertura multipágina y da respuesta visual inmediata durante
/// cualquier cambio de página.
/// </summary>
public partial class MainWindow
{
    private ComboBox? _pageSelectorComboBox;
    private bool _syncingPageSelector;
    private bool _navigationFeedbackHandlersInstalled;
    private bool _pageNavigationFeedbackVisible;

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ComicPageSelectorLoaded),
            handledEventsToo: true);
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

    private void InstallDirectPageSelector()
    {
        // Asegura que la navegación multipágina base exista antes de insertar el selector.
        InstallComicBookHandlers();

        // Reemplaza de forma explícita cualquier flujo antiguo de apertura de una sola página.
        // El primer filtro muestra CBZ e imágenes juntos, por lo que el usuario puede marcar
        // 001.jpg, 002.jpg, 003.jpg... directamente con Ctrl/Shift desde el primer momento.
        OpenImageButton.Click -= OpenImageButton_Click;
        OpenImageButton.Click -= OpenComicFilesButton_Click;
        OpenImageButton.Click -= OpenComicFilesButton_Click_Multi;
        OpenImageButton.Click += OpenComicFilesButton_Click_Multi;
        OpenImageButton.Content = "Abrir cómic";

        InstallNavigationFeedbackHandlers();

        if (_pageSelectorComboBox is not null
            || _pageCounterText?.Parent is not StackPanel previewPanel)
        {
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

        // El contador se actualiza en cada navegación. LayoutUpdated solo sincroniza el selector
        // cuando cambia realmente el índice o el número de páginas; no realiza trabajo pesado.
        _pageCounterText.LayoutUpdated += (_, _) => SyncDirectPageSelector();
        SyncDirectPageSelector();
    }

    private void InstallNavigationFeedbackHandlers()
    {
        if (_navigationFeedbackHandlersInstalled
            || _previousPageButton is null
            || _nextPageButton is null)
        {
            return;
        }

        _navigationFeedbackHandlersInstalled = true;

        // PreviewMouseDown ocurre antes del Click que cambia la página. De esta forma WPF puede
        // pintar el indicador de carga antes de empezar a decodificar imágenes o crear overlays.
        _previousPageButton.PreviewMouseLeftButtonDown += NavigationButton_PreviewMouseLeftButtonDown;
        _nextPageButton.PreviewMouseLeftButtonDown += NavigationButton_PreviewMouseLeftButtonDown;

        // Estos Click se registran después de los handlers que llaman a ShowComicPage, por lo
        // que ocultan el indicador una vez terminada la navegación.
        _previousPageButton.Click += NavigationButton_ClickCompleted;
        _nextPageButton.Click += NavigationButton_ClickCompleted;
    }

    private void NavigationButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        int targetIndex = ReferenceEquals(sender, _previousPageButton)
            ? _comicPageIndex - 1
            : _comicPageIndex + 1;
        BeginPageNavigationFeedback(targetIndex);
    }

    private void NavigationButton_ClickCompleted(object sender, RoutedEventArgs e)
    {
        EndPageNavigationFeedback();
    }

    private void BeginPageNavigationFeedback(int targetIndex)
    {
        if (_comicBatchBusy
            || targetIndex < 0
            || targetIndex >= _comicPages.Count
            || targetIndex == _comicPageIndex)
        {
            return;
        }

        _pageNavigationFeedbackVisible = true;
        BusyTitleText.Text = $"Cargando página {targetIndex + 1} de {_comicPages.Count}…";
        BusyProgressBar.IsIndeterminate = true;
        BusyOverlay.Visibility = Visibility.Visible;
        Panel.SetZIndex(BusyOverlay, 10_000);

        FooterStatusText.Text = $"Cargando página {targetIndex + 1} de {_comicPages.Count}…";
        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = true;
        Cursor = Cursors.Wait;

        // Fuerza un ciclo de pintura ahora, antes de entrar en el cambio de página síncrono.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private void EndPageNavigationFeedback()
    {
        if (!_pageNavigationFeedbackVisible)
        {
            return;
        }

        _pageNavigationFeedbackVisible = false;
        BusyOverlay.Visibility = Visibility.Collapsed;
        BusyProgressBar.IsIndeterminate = false;
        FooterProgressBar.IsIndeterminate = false;
        FooterProgressBar.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;
        UpdateActionAvailability();
        UpdateComicControls();
    }

    private void OpenComicFilesButton_Click_Multi(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir cómic o seleccionar varias páginas",
            Filter = "Cómic o páginas|*.cbz;*.png;*.jpg;*.jpeg;*.webp;*.bmp|Cómic CBZ|*.cbz|Imágenes|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Todos los archivos|*.*",
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

                LoadComicFromCbz(cbzFiles[0]);
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
                    "Selecciona un archivo CBZ o una o varias imágenes.",
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

            _pageSelectorComboBox.IsEnabled = _comicPages.Count > 0 && !_comicBatchBusy;
        }
        finally
        {
            _syncingPageSelector = false;
        }
    }

    private void PageSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPageSelector
            || _pageSelectorComboBox is null
            || _comicBatchBusy)
        {
            return;
        }

        int index = _pageSelectorComboBox.SelectedIndex;
        if (index < 0 || index >= _comicPages.Count || index == _comicPageIndex)
        {
            return;
        }

        BeginPageNavigationFeedback(index);
        try
        {
            ShowComicPage(index);
        }
        finally
        {
            EndPageNavigationFeedback();
        }
    }
}
