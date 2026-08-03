using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TintaES.Wpf;

/// <summary>
/// Panel vertical de páginas. La selección visual de miniaturas y el estado de sus checkbox son
/// independientes: se pueden seleccionar varias miniaturas y aplicarles después un único cambio
/// de check, sin confundir la página visible con las páginas incluidas en una acción o exportación.
/// </summary>
public partial class MainWindow
{
    private const int SafeExportBatchSize = 20;

    // Páginas con el checkbox activado. Este conjunto sigue siendo la selección que consumen
    // traducción, revisión y exportación para no romper las rutas existentes.
    private readonly HashSet<int> _selectedComicPageIndices = [];

    // Miniaturas seleccionadas visualmente. No implica que sus checkbox estén activados.
    private readonly HashSet<int> _highlightedComicPageIndices = [];
    private readonly Dictionary<string, ThumbnailSelectionState> _thumbnailSelectionStates =
        new(StringComparer.OrdinalIgnoreCase);

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
    private string? _thumbnailSelectionSessionKey;
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
            Text = "PÁGINAS",
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
            navigationPanel.Children.Insert(
                Math.Min(navigationPanel.Children.Count, index + 1),
                _pageSelectionToggleButton);
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

        string key = BuildActiveDocumentSessionKey();
        SynchronizeThumbnailSelectionSession(key);

        if (!string.Equals(key, _pageSelectionSessionKey, StringComparison.OrdinalIgnoreCase))
        {
            _pageSelectionSessionKey = key;
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

    private void SynchronizeThumbnailSelectionSession(string key)
    {
        if (string.Equals(
                key,
                _thumbnailSelectionSessionKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _thumbnailSelectionSessionKey = key;
        _highlightedComicPageIndices.Clear();
        _pageSelectionAnchorIndex = -1;

        if (!string.IsNullOrWhiteSpace(key)
            && _thumbnailSelectionStates.TryGetValue(key, out ThumbnailSelectionState? stored))
        {
            foreach (int index in stored.Indices.Where(index =>
                         index >= 0 && index < _comicPages.Count))
            {
                _highlightedComicPageIndices.Add(index);
            }
            _pageSelectionAnchorIndex = stored.AnchorIndex >= 0
                                        && stored.AnchorIndex < _comicPages.Count
                ? stored.AnchorIndex
                : -1;
            return;
        }

        if (_comicPageIndex >= 0 && _comicPageIndex < _comicPages.Count)
        {
            _highlightedComicPageIndices.Add(_comicPageIndex);
            _pageSelectionAnchorIndex = _comicPageIndex;
        }
        SaveThumbnailSelectionState();
    }

    private void SaveThumbnailSelectionState()
    {
        if (string.IsNullOrWhiteSpace(_thumbnailSelectionSessionKey))
        {
            return;
        }

        _thumbnailSelectionStates[_thumbnailSelectionSessionKey] = new ThumbnailSelectionState(
            _highlightedComicPageIndices.ToHashSet(),
            _pageSelectionAnchorIndex);
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
                    ToolTip =
                        "Clic: aplicar el check a la selección de miniaturas. " +
                        "Shift + clic: dejar marcada únicamente esta página."
                };
                checkBox.PreviewMouseLeftButtonDown += (_, e) =>
                    PageSelectionCheckBox_PreviewMouseLeftButtonDown(capturedIndex, e);
                checkBox.PreviewKeyDown += (_, e) =>
                    PageSelectionCheckBox_PreviewKeyDown(capturedIndex, e);

                var label = new TextBlock
                {
                    Margin = new Thickness(1, 5, 7, 5),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    ToolTip =
                        "Clic: seleccionar y abrir. Ctrl + clic: añadir o quitar. " +
                        "Shift + clic: seleccionar un rango."
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
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3)
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

    private void PageSelectionCheckBox_PreviewMouseLeftButtonDown(
        int index,
        MouseButtonEventArgs e)
    {
        if (index < 0 || index >= _comicPages.Count)
        {
            return;
        }

        e.Handled = true;
        TogglePageChecksFromThumbnail(index, Keyboard.Modifiers);
    }

    private void PageSelectionCheckBox_PreviewKeyDown(int index, KeyEventArgs e)
    {
        if (e.Key != Key.Space || index < 0 || index >= _comicPages.Count)
        {
            return;
        }

        e.Handled = true;
        TogglePageChecksFromThumbnail(index, Keyboard.Modifiers);
    }

    private void TogglePageChecksFromThumbnail(int index, ModifierKeys modifiers)
    {
        // Shift sobre un checkbox es el acceso rápido exclusivo solicitado: esta página queda
        // marcada y todas las demás se desmarcan, sin depender de la selección visual.
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            _selectedComicPageIndices.Clear();
            _selectedComicPageIndices.Add(index);
            SynchronizePageChecksAfterChange();
            return;
        }

        int[] targets = _highlightedComicPageIndices.Contains(index)
            ? _highlightedComicPageIndices
                .Where(item => item >= 0 && item < _comicPages.Count)
                .Distinct()
                .OrderBy(item => item)
                .ToArray()
            : [index];

        if (targets.Length == 0)
        {
            targets = [index];
        }

        // Solo se desmarca el grupo cuando ya estaba completamente marcado. Tanto una mezcla
        // como un grupo completamente desmarcado se convierten en un grupo marcado.
        bool allChecked = targets.All(item => _selectedComicPageIndices.Contains(item));
        ApplyPageCheckState(targets, isChecked: !allChecked);
    }

    private void ApplyPageCheckState(IEnumerable<int> indices, bool isChecked)
    {
        foreach (int index in indices
                     .Where(index => index >= 0 && index < _comicPages.Count)
                     .Distinct())
        {
            if (isChecked)
            {
                _selectedComicPageIndices.Add(index);
            }
            else
            {
                _selectedComicPageIndices.Remove(index);
            }
        }

        SynchronizePageChecksAfterChange();
    }

    private void SynchronizePageChecksAfterChange()
    {
        SyncPageSelectionCheckBoxes();
        UpdatePageSelectionSummary();
        UpdateCbzExportSelectionCaption();
        RefreshPrimaryTranslationAction();
    }

    private void PageSelectionLabel_PreviewMouseLeftButtonDown(
        int index,
        MouseButtonEventArgs e)
    {
        if (index < 0 || index >= _comicPages.Count)
        {
            return;
        }

        e.Handled = true;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            if (_pageSelectionAnchorIndex < 0
                || _pageSelectionAnchorIndex >= _comicPages.Count)
            {
                _pageSelectionAnchorIndex = index;
            }
            SelectThumbnailRange(_pageSelectionAnchorIndex, index);
        }
        else if ((modifiers & ModifierKeys.Control) != 0)
        {
            if (!_highlightedComicPageIndices.Add(index))
            {
                _highlightedComicPageIndices.Remove(index);
            }
            _pageSelectionAnchorIndex = index;
        }
        else
        {
            _highlightedComicPageIndices.Clear();
            _highlightedComicPageIndices.Add(index);
            _pageSelectionAnchorIndex = index;
        }

        SaveThumbnailSelectionState();
        RefreshPageSelectionVisuals();
        UpdatePageSelectionSummary();

        if (_comicBatchBusy || _pageNavigationBusy)
        {
            return;
        }

        if (index != _comicPageIndex)
        {
            _ = ShowComicPageFastAsync(index);
        }
    }

    private void SelectThumbnailRange(int anchorIndex, int targetIndex)
    {
        int first = Math.Max(0, Math.Min(anchorIndex, targetIndex));
        int last = Math.Min(_comicPages.Count - 1, Math.Max(anchorIndex, targetIndex));
        if (last < first)
        {
            return;
        }

        _highlightedComicPageIndices.Clear();
        for (int index = first; index <= last; index++)
        {
            _highlightedComicPageIndices.Add(index);
        }
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
        Brush accent = FindResource("AccentBrush") as Brush ?? Brushes.IndianRed;
        Brush ink = FindResource("InkBrush") as Brush ?? Brushes.White;
        var selectedBackground = new SolidColorBrush(Color.FromArgb(52, 76, 178, 187));

        foreach ((int index, CheckBox checkBox) in _pageSelectionCheckBoxes)
        {
            string state = _comicPages[index].Error is not null
                ? "error"
                : _comicPages[index].Processed ? "traducida" : "pendiente";
            string exported = _exportedComicPageIndices.Contains(index)
                ? " · exportada"
                : string.Empty;
            bool thumbnailSelected = _highlightedComicPageIndices.Contains(index);

            if (_pageSelectionLabels.TryGetValue(index, out TextBlock? label))
            {
                label.Text =
                    $"{index + 1:D3} · {_comicPages[index].DisplayName}\n      {state}{exported}";
                label.FontWeight = index == _comicPageIndex
                    ? FontWeights.Bold
                    : FontWeights.Normal;
                label.Foreground = index == _comicPageIndex ? accent : ink;
                label.Opacity = 1;
                label.Cursor = _comicBatchBusy || _pageNavigationBusy
                    ? Cursors.Arrow
                    : Cursors.Hand;
            }

            // La selección y los checkbox pueden prepararse mientras otra operación trabaja; la
            // lista consumida por esa operación ya se copió al comenzar.
            checkBox.IsEnabled = true;
            checkBox.Opacity = 1;
            if (_pageSelectionRows.TryGetValue(index, out Border? row))
            {
                row.Opacity = 1;
                row.Background = thumbnailSelected
                    ? selectedBackground
                    : Brushes.Transparent;
                row.BorderBrush = thumbnailSelected
                    ? accent
                    : Brushes.Transparent;
            }
        }
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
        int start = _selectedComicPageIndices.Count == 0
            ? 0
            : _selectedComicPageIndices.Max() + 1;
        if (start >= _comicPages.Count)
        {
            start = 0;
        }
        ApplyPageSelection(
            Enumerable.Range(start, Math.Min(SafeExportBatchSize, _comicPages.Count - start)));
    }

    private void SelectNextComicPageBatchAfter(int lastExportedIndex)
    {
        int start = Math.Min(_comicPages.Count, lastExportedIndex + 1);
        if (start >= _comicPages.Count)
        {
            ApplyPageSelection([]);
            return;
        }
        ApplyPageSelection(
            Enumerable.Range(start, Math.Min(SafeExportBatchSize, _comicPages.Count - start)));
    }

    private void ApplyPageSelection(IEnumerable<int> indices)
    {
        _selectedComicPageIndices.Clear();
        foreach (int index in indices.Where(index =>
                     index >= 0 && index < _comicPages.Count))
        {
            _selectedComicPageIndices.Add(index);
        }

        SynchronizePageChecksAfterChange();
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

        if (_comicPages.Count == 0)
        {
            _pageSelectionSummary.Text = "No hay páginas cargadas.";
            return;
        }

        int highlighted = _highlightedComicPageIndices.Count(index =>
            index >= 0 && index < _comicPages.Count);
        _pageSelectionSummary.Text =
            $"{_selectedComicPageIndices.Count} de {_comicPages.Count} marcadas · " +
            $"{highlighted} miniatura(s) seleccionada(s). " +
            "Ctrl + clic añade o quita; Shift + clic selecciona un rango. " +
            "Un clic en un check se aplica a toda la selección; Shift + clic deja solo esa página marcada.";
    }

    private void SetPageSelectionPanelVisible(bool visible)
    {
        if (_pageSelectionPanel is null || _pageSelectionColumn is null)
        {
            return;
        }
        _pageSelectionPanel.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        _pageSelectionColumn.Width = visible
            ? new GridLength(252)
            : new GridLength(0);
        if (_pageSelectionToggleButton is not null)
        {
            _pageSelectionToggleButton.Content = visible ? "☷" : "☰";
        }
    }

    private sealed record ThumbnailSelectionState(
        HashSet<int> Indices,
        int AnchorIndex);
}
