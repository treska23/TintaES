using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Convierte cada entrada del selector de páginas en una fila completamente clicable. El
/// CheckBox queda como indicador visual y la fila entera alterna la selección, evitando que
/// estilos, padding o contenido multilínea dejen una zona diminuta difícil de pulsar.
/// </summary>
public partial class MainWindow
{
    private static readonly bool PageSelectionInteractionRegistered = RegisterPageSelectionInteraction();

    private readonly Dictionary<int, Border> _interactivePageSelectionRows = [];
    private bool _pageSelectionInteractionInstalled;

    private static bool RegisterPageSelectionInteraction()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_PageSelectionInteractionLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_PageSelectionInteractionLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.Dispatcher.BeginInvoke(
            window.InstallPageSelectionInteraction,
            DispatcherPriority.ApplicationIdle);
    }

    private void InstallPageSelectionInteraction()
    {
        if (_pageSelectionInteractionInstalled)
        {
            return;
        }

        _pageSelectionInteractionInstalled = true;
        LayoutUpdated += (_, _) => PrepareClickablePageSelectionRows();
        PrepareClickablePageSelectionRows();
    }

    private void PrepareClickablePageSelectionRows()
    {
        if (_pageSelectionItemsPanel is null)
        {
            return;
        }

        foreach ((int index, CheckBox checkBox) in _pageSelectionCheckBoxes.ToArray())
        {
            if (checkBox.Parent is not StackPanel parent || !ReferenceEquals(parent, _pageSelectionItemsPanel))
            {
                continue;
            }

            int childIndex = parent.Children.IndexOf(checkBox);
            if (childIndex < 0)
            {
                continue;
            }

            parent.Children.RemoveAt(childIndex);
            checkBox.Margin = new Thickness(7, 5, 7, 5);
            checkBox.Padding = new Thickness(0);
            checkBox.Background = Brushes.Transparent;
            checkBox.BorderThickness = new Thickness(0);
            checkBox.IsHitTestVisible = false;
            checkBox.Focusable = false;

            var row = new Border
            {
                Child = checkBox,
                Margin = new Thickness(5, 2, 5, 2),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = index,
                ToolTip = "Pulsa en cualquier punto de la fila para marcar o desmarcar esta página"
            };
            row.PreviewMouseLeftButtonDown += (_, args) => TogglePageSelectionFromRow(index, checkBox, args);
            checkBox.Checked += (_, _) => RefreshInteractivePageSelectionRow(index);
            checkBox.Unchecked += (_, _) => RefreshInteractivePageSelectionRow(index);

            _interactivePageSelectionRows[index] = row;
            parent.Children.Insert(childIndex, row);
            RefreshInteractivePageSelectionRow(index);
        }

        foreach (int staleIndex in _interactivePageSelectionRows
                     .Where(item => item.Value.Parent is null)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _interactivePageSelectionRows.Remove(staleIndex);
        }
    }

    private void TogglePageSelectionFromRow(int index, CheckBox checkBox, MouseButtonEventArgs e)
    {
        if (_comicBatchBusy
            || _pageNavigationBusy
            || index < 0
            || index >= _comicPages.Count)
        {
            return;
        }

        bool selected = !_selectedComicPageIndices.Contains(index);
        SetPageSelected(index, selected);

        _syncingPageSelection = true;
        try
        {
            checkBox.IsChecked = selected;
        }
        finally
        {
            _syncingPageSelection = false;
        }

        RefreshPageSelectionVisuals();
        RefreshInteractivePageSelectionRow(index);
        UpdatePageSelectionSummary();
        UpdateCbzExportSelectionCaption();
        e.Handled = true;
    }

    private void RefreshInteractivePageSelectionRow(int index)
    {
        if (!_interactivePageSelectionRows.TryGetValue(index, out Border? row)
            || !_pageSelectionCheckBoxes.TryGetValue(index, out CheckBox? checkBox))
        {
            return;
        }

        bool selected = _selectedComicPageIndices.Contains(index);
        bool current = index == _comicPageIndex;
        bool enabled = !_comicBatchBusy && !_pageNavigationBusy;

        _syncingPageSelection = true;
        try
        {
            if (checkBox.IsChecked != selected)
            {
                checkBox.IsChecked = selected;
            }
        }
        finally
        {
            _syncingPageSelection = false;
        }

        Brush accent = FindResource("AccentBrush") as Brush ?? Brushes.IndianRed;
        Brush teal = FindResource("TealBrush") as Brush ?? Brushes.Teal;
        row.BorderBrush = current ? accent : selected ? teal : Brushes.Transparent;
        row.Background = current
            ? new SolidColorBrush(Color.FromArgb(42, 238, 89, 75))
            : selected
                ? new SolidColorBrush(Color.FromArgb(34, 76, 178, 187))
                : Brushes.Transparent;
        row.Opacity = enabled ? 1 : 0.58;
        row.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
    }
}