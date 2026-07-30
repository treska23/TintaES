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
/// </summary>
public partial class MainWindow
{
    private static readonly bool ResizableSidePanelsRegistered = RegisterResizableSidePanels();

    private bool _resizableSidePanelsInstalled;
    private bool _resizableSidePanelsQueued;
    private int _resizableSidePanelsAttempts;
    private Grid? _sidePanelsGrid;
    private Border? _sidePanelsInspector;
    private ColumnDefinition? _sidePanelsInspectorColumn;
    private GridSplitter? _pagePanelSplitter;
    private GridSplitter? _inspectorSplitter;
    private Button? _collapseInspectorButton;
    private Button? _restoreInspectorButton;
    private double _savedPagePanelWidth = 210;
    private double _savedInspectorWidth = 330;
    private bool _pagePanelWidthChosen;
    private bool _inspectorWidthChosen;
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
            return;
        }

        if (_pageSelectionPanel is null
            || _pageSelectionColumn is null
            || RegionListBox.Parent is not Grid inspectorGrid
            || inspectorGrid.Parent is not Border inspectorBorder
            || inspectorBorder.Parent is not Grid contentGrid
            || contentGrid.ColumnDefinitions.Count < 3)
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

        _sidePanelsInspectorColumn.MinWidth = 285;
        _sidePanelsInspectorColumn.MaxWidth = 650;
        _sidePanelsInspectorColumn.Width = new GridLength(_savedInspectorWidth);

        int canvasColumn = contentGrid.ColumnDefinitions.Count - 2;
        int inspectorColumn = contentGrid.ColumnDefinitions.Count - 1;

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
            "Arrastra para cambiar el ancho del panel de textos");
        Grid.SetColumn(_inspectorSplitter, canvasColumn);
        _inspectorSplitter.DragCompleted += (_, _) =>
        {
            if (_sidePanelsInspectorColumn.ActualWidth >= 285)
            {
                _savedInspectorWidth = Math.Clamp(_sidePanelsInspectorColumn.ActualWidth, 285, 650);
                _inspectorWidthChosen = true;
                _sidePanelsInspectorColumn.Width = new GridLength(_savedInspectorWidth);
            }
        };
        contentGrid.Children.Add(_inspectorSplitter);

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
    }

    private GridSplitter CreateSidePanelSplitter(
        HorizontalAlignment alignment,
        GridResizeBehavior behavior,
        string tooltip)
    {
        return new GridSplitter
        {
            Width = 7,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = behavior,
            ShowsPreview = true,
            Background = new SolidColorBrush(Color.FromArgb(80, 112, 121, 128)),
            Cursor = Cursors.SizeWE,
            ToolTip = tooltip,
            Focusable = false
        };
    }

    private void QueueRestoreChosenSidePanelWidths()
    {
        Dispatcher.BeginInvoke(
            RestoreChosenSidePanelWidths,
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

        _sidePanelsInspectorColumn.MinWidth = _inspectorPanelVisible ? 285 : 0;
        if (_inspectorPanelVisible)
        {
            double width = _inspectorWidthChosen
                ? _savedInspectorWidth
                : Math.Min(330, Math.Max(285, _sidePanelsInspectorColumn.ActualWidth));
            _sidePanelsInspectorColumn.Width = new GridLength(Math.Clamp(width, 285, 650));
        }
        else
        {
            _sidePanelsInspectorColumn.Width = new GridLength(0);
        }
    }

    private void SetInspectorPanelVisible(bool visible)
    {
        if (_sidePanelsInspector is null || _sidePanelsInspectorColumn is null)
        {
            return;
        }

        if (!visible && _sidePanelsInspectorColumn.ActualWidth >= 285)
        {
            _savedInspectorWidth = Math.Clamp(_sidePanelsInspectorColumn.ActualWidth, 285, 650);
        }

        _inspectorPanelVisible = visible;
        _sidePanelsInspector.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _sidePanelsInspectorColumn.MinWidth = visible ? 285 : 0;
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
