using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Añade navegación directa a cualquier página del cómic sin llenar la interfaz de pestañas.
/// Se instala después de que la plantilla y la barra multipágina estén listas.
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
