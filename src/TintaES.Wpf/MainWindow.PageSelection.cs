using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TintaES.Wpf;

/// <summary>
/// Panel vertical para visualizar páginas y elegir cuáles se exportan. El texto navega a la
/// página y únicamente el CheckBox modifica la selección de exportación.
/// </summary>
public partial class MainWindow
{
    private const int SafeExportBatchSize = 20;

    private readonly HashSet<int> _selectedComicPageIndices = [];
    private readonly Dictionary<int, CheckBox> _pageSelectionCheckBoxes = [];
    private readonly Dictionary<int, TextBlock> _pageSelectionLabels = [];
    private readonly Dictionary<int, Border> _pageSelectionRows = [];
    private readonly HashSet<int> _exportedComicPageIndices = [];

    private Border? _pageSelectionPanel;
    private ColumnDefinition? _pageSelectionColumn;
    private StackPanel? _pageSelectionItemsPanel;
    private TextBlock? _pageSelectionSummary;
    private Button? _pageSelectionToggleButton;
    private string? _pageSelectionSessionKey;
    private int _lastPageSelectionVisualIndex = -2;
    private int _pageSelectionAnchorIndex = -1;
    private bool _syncingPageSelection;

    private void InstallPageSelectionPanel()
    {
        if (_pageSelectionPanel is not null)
        {
            SyncPageSelectionPanel();
            return;
        }

        if (ImageScrollViewer.Parent is not Grid imageViewportGrid
            || imageViewportGrid.Parent is not Grid pageAreaGrid
            || pageAreaGrid.Parent is not Border pageBorder
            || pageBorder.Parent is not Grid contentGrid)
        {
            return;
        }

        UIElement[] existingChildren = contentGrid.Children.Cast<UIElement>().ToArray();
        _pageSelectionColumn = new ColumnDefinition { Width = new GridLength(252) };
        contentGrid.ColumnDefinitions.Insert(0, _pageSelectionColumn);
        foreach (UIElement child in existingChildren)
        {
            Grid.SetColumn(child, Grid.GetColumn(child) + 1);
        }

        _pageSelectionItemsPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var pageScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _pageSelectionItemsPanel
        };

        _pageSelectionSummary = new TextBlock
        {
            Margin = new Thickness(12, 7, 12, 9),
            FontSize = 10,
            Foreground = FindResource("MutedBrush") as Brush ?? Brushes.Gray,
            TextWrapping = TextWrapping.Wrap
        };

        var allButton = CreatePageSelectionButton("Todas", (_, _) => SelectAllComicPages());
        var noneButton = CreatePageSelectionButton("Ninguna", (_, _) => SelectNoComicPages());
        var firstButton = CreatePageSelectionButton("20 primeras", (_, _) => SelectFirstComicPageBatch());
        var nextButton = CreatePageSelectionButton("20 siguientes", (_, _) => SelectNextComicPageBatch());

        var actionPanel = new WrapPanel { Margin = new Thickness(10, 7, 8, 5) };
        actionPanel.Children.Add(allButton);
        actionPanel.Children.Add(noneButton);
        actionPanel.Children.Add(firstButton);
        actionPanel.Children.Add(nextButton);

        var header = new Grid { Height = 43, Margin = new Thickness(11, 0, 7, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "PÁGINAS A EXPORTAR",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = FindResource("AccentBrush") as Brush ?? Brushes.IndianRed
        });
        var closeButton = new Button
        {
            Content = "×",
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            ToolTip = "Ocultar selector de páginas",
            Style = FindResource("ToolbarButton") as Style
        };
        closeButton.Click += (_, _) => SetPageSelectionPanelVisible(false);
        Grid.SetColumn(closeButton, 1);
        header.Children.Add(closeButton);

        var panelGrid = new Grid();
        panelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panelGrid.RowDefinitions.Add(new RowDefinition());
        panelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panelGrid.Children.Add(header);
        Grid.SetRow(actionPanel, 1);
        panelGrid.Children.Add(actionPanel);
        Grid.SetRow(pageScroll, 2);
        panelGrid.Children.Add(pageScroll);
        Grid.SetRow(_pageSelectionSummary, 3);
        panelGrid.Children.Add(_pageSelectionSummary);

        _pageSelectionPanel = new Border
        {
            Background = FindResource("PanelBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(27, 30, 33)),
            BorderBrush = FindResource("LineBrush") as Brush ?? Brushes.DimGray,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = panelGrid
        };
        Grid.SetColumn(_pageSelectionPanel, 0);
        Panel.SetZIndex(_pageSelectionPanel, 50);
        contentGrid.Children.Add(_pageSelectionPanel);

        if (_pageCounterText?.Parent is StackPanel navigationPanel)
        {
            _pageSelectionToggleButton = new Button
            {
                Content = "☷",
                Width = 32,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Mostrar u ocultar el selector de páginas",
                Style = FindResource("ToolbarButton") as Style
            };
            _pageSelectionToggleButton.Click += (_, _) =>
                SetPageSelectionPanelVisible(_pageSelectionPanel.Visibility != Visibility.Visible);
            int index = navigationPanel.Children.IndexOf(_pageCounterText);
            navigationPanel.Children.Insert(Math.Min(navigationPanel.Children.Count, index + 1), _pageSelectionToggleButton);
            _pageCounterText.LayoutUpdated += (_, _) => SyncPageSelectionPanel();
        }

        SyncPageSelectionPanel();
    }

    private Button CreatePageSelectionButton(string text, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 62,
            Height = 27,
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 5, 5),
            Style = FindResource("ToolbarButton") as Style
        };
        button.Click += click;
        return button;
    }

    private void SyncPageSelectionPanel()
    {
        if (_pageSelectionPanel is null || _pageSelectionItemsPanel is null)
        {
            return;
        }

        string key = _comicPages.Count == 0
            ? string.Empty
            : $"{_comicPages.Count}|{_comicPages[0].SourcePath}|{_comicPages[^1].SourcePath}";
        if (!string.Equals(key, _pageSelectionSessionKey, StringComparison.OrdinalIgnoreCase))
        {
            _pageSelectionSessionKey = key;
            _pageSelectionAnchorIndex = -1;
            _selectedComicPageIndices.Clear();
            _exportedComicPageIndices.Clear();
            foreach (int index in Enumerable.Range(0, _comicPages.Count))
            {
                _selectedComicPageIndices.Add(index);
            }
            RebuildPageSelectionItems();
            SetPageSelectionPanelVisible(_comicPages.Count > 1);
        }

        if (_lastPageSelectionVisualIndex != _comicPageIndex)
        {
            _lastPageSelectionVisualIndex = _comicPageIndex;
            RefreshPageSelectionVisuals();
        }
        UpdatePageSelectionSummary();
    }

    private void RebuildPageSelectionItems()
    {
        if (_pageSelectionItemsPanel is null)
        {
            return;
        }

        _syncingPageSelection = true;
        try
        {
            _pageSelectionItemsPanel.Children.Clear();
            _pageSelectionCheckBoxes.Clear();
            _pageSelectionLabels.Clear();
            _pageSelectionRows.Clear();

            for (int index = 0; index < _comicPages.Count; index++)
            {
                int capturedIndex = index;
                var checkBox = new CheckBox
                {
                    IsChecked = _selectedComicPageIndices.Contains(index),
                    Width = 22,
                    MinWidth = 22,
                    Margin = new Thickness(7, 8, 4, 0),
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    ToolTip = "Incluir o excluir esta página de la exportación CBZ"
                };
                checkBox.PreviewMouseLeftButtonDown += (_, e) =>
                    PageSelectionCheckBox_PreviewMouseLeftButtonDown(capturedIndex, e);
                checkBox.Checked += (_, _) => SetPageSelected(capturedIndex, true);
                checkBox.Unchecked += (_, _) => SetPageSelected(capturedIndex, false);

                var label = new TextBlock
                {
                    Margin = new Thickness(1, 5, 7, 5),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    ToolTip = "Pulsa para visualizar esta página. Shift + clic selecciona un rango."
                };
                label.PreviewMouseLeftButtonDown += (_, e) =>
                    PageSelectionLabel_PreviewMouseLeftButtonDown(capturedIndex, e);

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(checkBox, 0);
                Grid.SetColumn(label, 1);
                rowGrid.Children.Add(checkBox);
                rowGrid.Children.Add(label);

                var row = new Border
                {
                    Child = rowGrid,
                    Margin = new Thickness(5, 2, 5, 2),
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0)
                };

                _pageSelectionCheckBoxes[index] = checkBox;
                _pageSelectionLabels[index] = label;
                _pageSelectionRows[index] = row;
                _pageSelectionItemsPanel.Children.Add(row);
            }
        }
        finally
        {
            _syncingPageSelection = false;
        }

        RefreshPageSelectionVisuals();
    }

    private void PageSelectionCheckBox_PreviewMouseLeftButtonDown(int index, MouseButtonEventArgs e)
    {
        if (index < 0 || index >= _comicPages.Count)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _pageSelectionAnchorIndex >= 0)
        {
            e.Handled = true;
            bool selectRange = _selectedComicPageIndices.Contains(_pageSelectionAnchorIndex);
            ApplyPageSelectionRange(_pageSelectionAnchorIndex, index, selectRange);
            return;
        }

        _pageSelectionAnchorIndex = index;
        // No marcamos el evento como Handled: el CheckBox nativo cambia IsChecked normalmente.
    }

    private void PageSelectionLabel_PreviewMouseLeftButtonDown(int index, MouseButtonEventArgs e)
    {
        if (index < 0 || index >= _comicPages.Count)
        {
            return;
        }

        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (_pageSelectionAnchorIndex < 0)
            {
                _pageSelectionAnchorIndex = index;
            }
            ApplyPageSelectionRange(_pageSelectionAnchorIndex, index, selected: true);
            return;
        }

        _pageSelectionAnchorIndex = index;
        if (_comicBatchBusy || _pageNavigationBusy)
        {
            return;
        }

        if (index != _comicPageIndex)
        {
            _ = ShowComicPageFastAsync(index);
        }
    }

    private void ApplyPageSelectionRange(int anchorIndex, int targetIndex, bool selected)
    {
        int first = Math.Max(0, Math.Min(anchorIndex, targetIndex));
        int last = Math.Min(_comicPages.Count - 1, Math.Max(anchorIndex, targetIndex));
        if (last < first)
        {
            return;
        }

        for (int index = first; index <= last; index++)
        {
            if (selected)
            {
                _selectedComicPageIndices.Add(index);
            }
            else
            {
                _selectedComicPageIndices.Remove(index);
            }
        }

        SyncPageSelectionCheckBoxes();
        UpdatePageSelectionSummary();
        UpdateCbzExportSelectionCaption();
    }

    private void SyncPageSelectionCheckBoxes()
    {
        _syncingPageSelection = true;
        try
        {
            foreach ((int index, CheckBox checkBox) in _pageSelectionCheckBoxes)
            {
                checkBox.IsChecked = _selectedComicPageIndices.Contains(index);
            }
        }
        finally
        {
            _syncingPageSelection = false;
        }
        RefreshPageSelectionVisuals();
    }

    private void RefreshPageSelectionVisuals()
    {
        foreach ((int index, CheckBox checkBox) in _pageSelectionCheckBoxes)
        {
            string state = _comicPages[index].Error is not null
                ? "error"
                : _comicPages[index].Processed ? "traducida" : "pendiente";
            string exported = _exportedComicPageIndices.Contains(index) ? " · exportada" : string.Empty;

            if (_pageSelectionLabels.TryGetValue(index, out TextBlock? label))
            {
                label.Text = $"{index + 1:D3} · {_comicPages[index].DisplayName}\n      {state}{exported}";
                label.FontWeight = index == _comicPageIndex ? FontWeights.Bold : FontWeights.Normal;
                label.Foreground = index == _comicPageIndex
                    ? FindResource("AccentBrush") as Brush ?? Brushes.IndianRed
                    : FindResource("InkBrush") as Brush ?? Brushes.White;
                label.Opacity = 1;
                label.Cursor = _comicBatchBusy || _pageNavigationBusy ? Cursors.Arrow : Cursors.Hand;
            }

            // La selección puede prepararse para la siguiente exportación mientras la actual sigue
            // trabajando. La lista usada por la exportación ya se tomó al comenzar.
            checkBox.IsEnabled = true;
            checkBox.Opacity = 1;
            if (_pageSelectionRows.TryGetValue(index, out Border? row))
            {
                row.Opacity = 1;
            }
        }
    }

    private void SetPageSelected(int index, bool selected)
    {
        if (_syncingPageSelection)
        {
            return;
        }

        if (selected)
        {
            _selectedComicPageIndices.Add(index);
        }
        else
        {
            _selectedComicPageIndices.Remove(index);
        }

        UpdatePageSelectionSummary();
        UpdateCbzExportSelectionCaption();
    }

    private void SelectAllComicPages()
    {
        ApplyPageSelection(Enumerable.Range(0, _comicPages.Count));
    }

    private void SelectNoComicPages()
    {
        ApplyPageSelection([]);
    }

    private void SelectFirstComicPageBatch()
    {
        ApplyPageSelection(Enumerable.Range(0, Math.Min(SafeExportBatchSize, _comicPages.Count)));
    }

    private void SelectNextComicPageBatch()
    {
        int start = _selectedComicPageIndices.Count == 0 ? 0 : _selectedComicPageIndices.Max() + 1;
        if (start >= _comicPages.Count)
        {
            start = 0;
        }
        ApplyPageSelection(Enumerable.Range(start, Math.Min(SafeExportBatchSize, _comicPages.Count - start)));
    }

    private void SelectNextComicPageBatchAfter(int lastExportedIndex)
    {
        int start = Math.Min(_comicPages.Count, lastExportedIndex + 1);
        if (start >= _comicPages.Count)
        {
            ApplyPageSelection([]);
            return;
        }
        ApplyPageSelection(Enumerable.Range(start, Math.Min(SafeExportBatchSize, _comicPages.Count - start)));
    }

    private void ApplyPageSelection(IEnumerable<int> indices)
    {
        _selectedComicPageIndices.Clear();
        foreach (int index in indices.Where(index => index >= 0 && index < _comicPages.Count))
        {
            _selectedComicPageIndices.Add(index);
        }

        _pageSelectionAnchorIndex = -1;
        SyncPageSelectionCheckBoxes();
        UpdatePageSelectionSummary();
        UpdateCbzExportSelectionCaption();
    }

    private IReadOnlyList<int> GetSelectedComicPageIndices() =>
        _selectedComicPageIndices.OrderBy(index => index).ToArray();

    private void MarkComicPagesExported(IEnumerable<int> indices)
    {
        _exportedComicPageIndices.UnionWith(indices);
        RefreshPageSelectionVisuals();
        UpdatePageSelectionSummary();
    }

    private void UpdatePageSelectionSummary()
    {
        if (_pageSelectionSummary is null)
        {
            return;
        }
        _pageSelectionSummary.Text = _comicPages.Count == 0
            ? "No hay páginas cargadas."
            : $"{_selectedComicPageIndices.Count} de {_comicPages.Count} seleccionadas. " +
              "Shift + clic selecciona un rango. Una exportación interrumpida puede reanudarse sin dañar el CBZ anterior.";
    }

    private void SetPageSelectionPanelVisible(bool visible)
    {
        if (_pageSelectionPanel is null || _pageSelectionColumn is null)
        {
            return;
        }
        _pageSelectionPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _pageSelectionColumn.Width = visible ? new GridLength(252) : new GridLength(0);
        if (_pageSelectionToggleButton is not null)
        {
            _pageSelectionToggleButton.Content = visible ? "☷" : "☰";
        }
    }
}
