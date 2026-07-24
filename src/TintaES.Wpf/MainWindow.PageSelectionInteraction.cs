using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Separa claramente las dos acciones del panel izquierdo: pulsar la fila visualiza la página
/// y pulsar el CheckBox la incluye o excluye de la exportación.
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
            checkBox.IsHitTestVisible = true;
            checkBox.Focusable = true;
            checkBox.Cursor = Cursors.Arrow;
            checkBox.ToolTip = "Marcar o desmarcar esta página para la exportación CBZ";

            var row = new Border
            {
                Child = checkBox,
                Margin = new Thickness(5, 2, 5, 2),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = index,
                ToolTip = "Pulsa en el nombre o en la fila para visualizar esta página. Usa el checkbox para exportarla."
            };
            row.PreviewMouseLeftButtonDown += (_, args) => NavigateFromPageSelectionRow(index, checkBox, args);
            checkBox.Checked += (_, _) => PageSelectionCheckBoxChanged(index);
            checkBox.Unchecked += (_, _) => PageSelectionCheckBoxChanged(index);

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

    private void NavigateFromPageSelectionRow(
        int index,
        CheckBox checkBox,
        MouseButtonEventArgs e)
    {
        // El evento Preview de la fila también recibe los clics efectuados dentro del checkbox.
        // En ese caso no lo interceptamos: WPF cambiará IsChecked y los handlers originales
        // actualizarán la selección de exportación.
        if (e.OriginalSource is DependencyObject source && IsDescendantOf(source, checkBox))
        {
            return;
        }

        if (_comicBatchBusy
            || _pageNavigationBusy
            || index < 0
            || index >= _comicPages.Count)
        {
            return;
        }

        e.Handled = true;
        if (index != _comicPageIndex)
        {
            _ = ShowComicPageFastAsync(index);
        }
        else
        {
            RefreshInteractivePageSelectionRows();
        }
    }

    private void PageSelectionCheckBoxChanged(int index)
    {
        RefreshPageSelectionVisuals();
        RefreshInteractivePageSelectionRow(index);
        UpdatePageSelectionSummary();
        UpdateCbzExportSelectionCaption();
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void RefreshInteractivePageSelectionRows()
    {
        foreach (int index in _interactivePageSelectionRows.Keys.ToArray())
        {
            RefreshInteractivePageSelectionRow(index);
        }
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
                ? new SolidColorBrush(Color.FromArgb(24, 76, 178, 187))
                : Brushes.Transparent;
        row.Opacity = enabled ? 1 : 0.58;
        row.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
        checkBox.IsEnabled = enabled;
    }
}
