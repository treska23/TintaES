using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TintaES.Wpf;

/// <summary>
/// Añade navegación directa a cualquier página del cómic sin llenar la interfaz de pestañas.
/// También fuerza el selector de apertura multipágina para que no quede enganchado el flujo
/// antiguo de una sola imagen.
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
        if (index >= 0 && index < _comicPages.Count && index != _comicPageIndex)
        {
            ShowComicPage(index);
        }
    }
}
