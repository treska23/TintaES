using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Permite dedicar el ancho de la ventana al lienzo: los paneles de páginas y textos pueden
/// estrecharse arrastrando sus bordes y el inspector derecho puede plegarse por completo.
/// La lista superior de tarjetas también puede ganar o ceder altura frente al editor de zona.
/// </summary>
public partial class MainWindow
{
    private const double InspectorMinWidth = 315;
    private const double InspectorMaxWidth = 780;
    private const double InspectorDefaultWidth = 430;
    private const double RegionListMinHeight = 120;
    private const double RegionListMaxHeight = 520;
    private const double RegionListDefaultHeight = 260;

    private static readonly bool ResizableSidePanelsRegistered = RegisterResizableSidePanels();

    private bool _resizableSidePanelsInstalled;
    private bool _resizableSidePanelsQueued;
    private int _resizableSidePanelsAttempts;
    private Grid? _sidePanelsGrid;
    private Border? _sidePanelsInspector;
    private ColumnDefinition? _sidePanelsInspectorColumn;
    private GridSplitter? _pagePanelSplitter;
    private GridSplitter? _inspectorSplitter;
    private GridSplitter? _regionListSplitter;
    private Button? _collapseInspectorButton;
    private Button? _restoreInspectorButton;
    private double _savedPagePanelWidth = 210;
    private double _savedInspectorWidth = InspectorDefaultWidth;
    private double _savedRegionListHeight = RegionListDefaultHeight;
    private bool _pagePanelWidthChosen;
    private bool _inspectorWidthChosen;
    private bool _regionListHeightChosen;
    private bool _inspectorPanelVisible = true;

    private static bool RegisterResizableSidePanels()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ResizableSidePanelsLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ResizableSidePanelsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.QueueResizableSidePanels();
        }
    }

    private void QueueResizableSidePanels()
    {
        if (_resizableSidePanelsQueued || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _resizableSidePanelsQueued = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _resizableSidePanelsQueued = false;
                InstallOrRefreshResizableSidePanels();
            },
            DispatcherPriority.ApplicationIdle);
    }

    private void InstallOrRefreshResizableSidePanels()
    {
        if (_resizableSidePanelsInstalled)
        {
            RestoreChosenSidePanelWidths();
            RestoreChosenRegionListHeight();
            return;
        }

        if (_pageSelectionPanel is null
            || _pageSelectionColumn is null
            || RegionListBox.Parent is not Grid inspectorGrid
            || inspectorGrid.Parent is not Border inspectorBorder
            || inspectorBorder.Parent is not Grid contentGrid
            || contentGrid.ColumnDefinitions.Count < 3
            || inspectorGrid.RowDefinitions.Count < 3)
        {
            if (++_resizableSidePanelsAttempts < 12)
            {
                QueueResizableSidePanels();
            }
            return;
        }

        _sidePanelsGrid = contentGrid;
        _sidePanelsInspector = inspectorBorder;
        _sidePanelsInspectorColumn = contentGrid.ColumnDefinitions[^1];

        _pageSelectionColumn.MinWidth = 145;
        _pageSelectionColumn.MaxWidth = 440;
        _pageSelectionColumn.Width = new GridLength(_savedPagePanelWidth);

        _sidePanelsInspectorColumn.MinWidth = InspectorMinWidth;
        _sidePanelsInspectorColumn.MaxWidth = InspectorMaxWidth;
        _sidePanelsInspectorColumn.Width = new GridLength(_savedInspectorWidth);

        int canvasColumn = contentGrid.ColumnDefinitions.Count - 2;

        _pagePanelSplitter = CreateSidePanelSplitter(
            HorizontalAlignment.Left,
            GridResizeBehavior.PreviousAndCurrent,
            "Arrastra para cambiar el ancho de las miniaturas");
        Grid.SetColumn(_pagePanelSplitter, canvasColumn);
        _pagePanelSplitter.DragCompleted += (_, _) =>
        {
            if (_pageSelectionColumn.ActualWidth >= 145)
            {
                _savedPagePanelWidth = Math.Clamp(_pageSelectionColumn.ActualWidth, 145, 440);
                _pagePanelWidthChosen = true;
                _pageSelectionColumn.Width = new GridLength(_savedPagePanelWidth);
            }
        };
        contentGrid.Children.Add(_pagePanelSplitter);

        _inspectorSplitter = CreateSidePanelSplitter(
            HorizontalAlignment.Right,
            GridResizeBehavior.CurrentAndNext,
            "Arrastra para hacer más ancho o más estrecho el panel de textos");
        Grid.SetColumn(_inspectorSplitter, canvasColumn);
        _inspectorSplitter.DragCompleted += (_, _) =>
        {
            if (_sidePanelsInspectorColumn.ActualWidth >= InspectorMinWidth)
            {
                _savedInspectorWidth = Math.Clamp(
                    _sidePanelsInspectorColumn.ActualWidth,
                    InspectorMinWidth,
                    InspectorMaxWidth);
                _inspectorWidthChosen = true;
                _sidePanelsInspectorColumn.Width = new GridLength(_savedInspectorWidth);
            }
        };
        contentGrid.Children.Add(_inspectorSplitter);

        InstallResizableRegionList(inspectorGrid);

        _collapseInspectorButton = new Button
        {
            Content = "›",
            Width = 27,
            Height = 27,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 9, 54, 0),
            ToolTip = "Ocultar el panel de textos",
            Style = FindResource("ToolbarButton") as Style
        };
        _collapseInspectorButton.Click += (_, _) => SetInspectorPanelVisible(false);
        Grid.SetRow(_collapseInspectorButton, 0);
        Panel.SetZIndex(_collapseInspectorButton, 30_000);
        inspectorGrid.Children.Add(_collapseInspectorButton);

        _restoreInspectorButton = new Button
        {
            Content = "‹ Textos",
            Height = 30,
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 9, 8, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "Mostrar el panel de textos",
            Style = FindResource("ToolbarButton") as Style
        };
        _restoreInspectorButton.Click += (_, _) => SetInspectorPanelVisible(true);
        Grid.SetColumn(_restoreInspectorButton, canvasColumn);
        Panel.SetZIndex(_restoreInspectorButton, 30_000);
        contentGrid.Children.Add(_restoreInspectorButton);

        _pageSelectionPanel.IsVisibleChanged += (_, _) => QueueRestoreChosenSidePanelWidths();
        SizeChanged += (_, _) => QueueRestoreChosenSidePanelWidths();

        _resizableSidePanelsInstalled = true;
        RestoreChosenSidePanelWidths();
        RestoreChosenRegionListHeight();
    }

    private void InstallResizableRegionList(Grid inspectorGrid)
    {
        RowDefinition cardsRow = inspectorGrid.RowDefinitions[1];
        RowDefinition editorRow = inspectorGrid.RowDefinitions[2];
        cardsRow.MinHeight = RegionListMinHeight;
        cardsRow.MaxHeight = RegionListMaxHeight;
        cardsRow.Height = new GridLength(_savedRegionListHeight);
        editorRow.MinHeight = 180;

        _regionListSplitter = new GridSplitter
        {
            Height = 9,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.CurrentAndNext,
            ShowsPreview = false,
            Background = new SolidColorBrush(Color.FromArgb(110, 112, 121, 128)),
            Cursor = Cursors.SizeNS,
            ToolTip = "Arrastra para dar más o menos altura a las tarjetas de texto",
            Focusable = false
        };
        Grid.SetRow(_regionListSplitter, 1);
        Panel.SetZIndex(_regionListSplitter, 30_000);
        _regionListSplitter.DragCompleted += (_, _) =>
        {
            double height = inspectorGrid.RowDefinitions[1].ActualHeight;
            if (height >= RegionListMinHeight)
            {
                _savedRegionListHeight = Math.Clamp(
                    height,
                    RegionListMinHeight,
                    RegionListMaxHeight);
                _regionListHeightChosen = true;
                inspectorGrid.RowDefinitions[1].Height = new GridLength(_savedRegionListHeight);
            }
        };
        inspectorGrid.Children.Add(_regionListSplitter);

        // Las tarjetas dejan de sentirse apretadas incluso antes de ensanchar el panel.
        RegionListBox.FontSize = Math.Max(13d, RegionListBox.FontSize);
        if (RegionListBox.ItemContainerStyle is { } existingStyle)
        {
            var roomierStyle = new Style(typeof(ListBoxItem), existingStyle);
            roomierStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 62d));
            RegionListBox.ItemContainerStyle = roomierStyle;
        }
    }

    private GridSplitter CreateSidePanelSplitter(
        HorizontalAlignment alignment,
        GridResizeBehavior behavior,
        string tooltip)
    {
        return new GridSplitter
        {
            Width = 9,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = behavior,
            ShowsPreview = false,
            Background = new SolidColorBrush(Color.FromArgb(105, 112, 121, 128)),
            Cursor = Cursors.SizeWE,
            ToolTip = tooltip,
            Focusable = false
        };
    }

    private void QueueRestoreChosenSidePanelWidths()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                RestoreChosenSidePanelWidths();
                RestoreChosenRegionListHeight();
            },
            DispatcherPriority.ApplicationIdle);
    }

    private void RestoreChosenSidePanelWidths()
    {
        if (!_resizableSidePanelsInstalled
            || _pageSelectionPanel is null
            || _pageSelectionColumn is null
            || _sidePanelsInspectorColumn is null)
        {
            return;
        }

        bool pagePanelVisible = _pageSelectionPanel.Visibility == Visibility.Visible;
        _pageSelectionColumn.MinWidth = pagePanelVisible ? 145 : 0;
        if (pagePanelVisible)
        {
            double width = _pagePanelWidthChosen
                ? _savedPagePanelWidth
                : Math.Min(210, Math.Max(145, _pageSelectionColumn.ActualWidth));
            _pageSelectionColumn.Width = new GridLength(Math.Clamp(width, 145, 440));
        }
        else
        {
            _pageSelectionColumn.Width = new GridLength(0);
        }
        if (_pagePanelSplitter is not null)
        {
            _pagePanelSplitter.Visibility = pagePanelVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        _sidePanelsInspectorColumn.MinWidth = _inspectorPanelVisible ? InspectorMinWidth : 0;
        if (_inspectorPanelVisible)
        {
            double width = _inspectorWidthChosen
                ? _savedInspectorWidth
                : InspectorDefaultWidth;
            _sidePanelsInspectorColumn.Width = new GridLength(Math.Clamp(
                width,
                InspectorMinWidth,
                InspectorMaxWidth));
        }
        else
        {
            _sidePanelsInspectorColumn.Width = new GridLength(0);
        }
    }

    private void RestoreChosenRegionListHeight()
    {
        if (!_resizableSidePanelsInstalled
            || RegionListBox.Parent is not Grid inspectorGrid
            || inspectorGrid.RowDefinitions.Count < 3)
        {
            return;
        }

        double height = _regionListHeightChosen
            ? _savedRegionListHeight
            : RegionListDefaultHeight;
        inspectorGrid.RowDefinitions[1].Height = new GridLength(Math.Clamp(
            height,
            RegionListMinHeight,
            RegionListMaxHeight));
    }

    private void SetInspectorPanelVisible(bool visible)
    {
        if (_sidePanelsInspector is null || _sidePanelsInspectorColumn is null)
        {
            return;
        }

        if (!visible && _sidePanelsInspectorColumn.ActualWidth >= InspectorMinWidth)
        {
            _savedInspectorWidth = Math.Clamp(
                _sidePanelsInspectorColumn.ActualWidth,
                InspectorMinWidth,
                InspectorMaxWidth);
        }

        _inspectorPanelVisible = visible;
        _sidePanelsInspector.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _sidePanelsInspectorColumn.MinWidth = visible ? InspectorMinWidth : 0;
        _sidePanelsInspectorColumn.Width = visible
            ? new GridLength(_savedInspectorWidth)
            : new GridLength(0);

        if (_inspectorSplitter is not null)
        {
            _inspectorSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_restoreInspectorButton is not null)
        {
            _restoreInspectorButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
