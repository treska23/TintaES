using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Reorganiza las dos barras superiores según el ancho real de la ventana. En lugar de comprimir
/// controles hasta volverlos ilegibles, la barra de documento pasa a dos o tres filas conservando
/// los mismos StackPanel que utilizan los instaladores de comandos.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ResponsiveTopBarsRegistered = RegisterResponsiveTopBars();

    private bool _responsiveTopBarsInstalled;
    private bool _applyingResponsiveTopBars;
    private Grid? _responsiveDocumentToolbarGrid;
    private Border? _responsiveDocumentToolbarBorder;
    private StackPanel? _responsiveOpenActionsPanel;
    private StackPanel? _responsiveZoomPanel;
    private StackPanel? _responsiveDocumentActionsPanel;
    private RowDefinition? _responsiveDocumentHostRow;

    private Grid? _responsiveHeaderGrid;
    private Border? _responsiveOllamaBadge;
    private StackPanel? _responsiveModelPanel;
    private TextBlock? _responsiveModelLabel;
    private StackPanel? _responsiveBrandTextPanel;

    private static bool RegisterResponsiveTopBars()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ResponsiveTopBarsLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ResponsiveTopBarsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallResponsiveTopBars,
                DispatcherPriority.ContextIdle);
        }
    }

    private void InstallResponsiveTopBars()
    {
        if (_responsiveTopBarsInstalled)
        {
            ApplyResponsiveTopBars();
            return;
        }

        if (OpenImageButton.Parent is not StackPanel openActions
            || ZoomSlider.Parent is not StackPanel zoomPanel
            || ExportButton.Parent is not StackPanel documentActions
            || openActions.Parent is not Grid toolbarGrid
            || !ReferenceEquals(zoomPanel.Parent, toolbarGrid)
            || !ReferenceEquals(documentActions.Parent, toolbarGrid)
            || toolbarGrid.Parent is not Border toolbarBorder
            || ModelComboBox.Parent is not StackPanel modelPanel
            || modelPanel.Parent is not Grid headerGrid)
        {
            Dispatcher.BeginInvoke(InstallResponsiveTopBars, DispatcherPriority.ContextIdle);
            return;
        }

        _responsiveOpenActionsPanel = openActions;
        _responsiveZoomPanel = zoomPanel;
        _responsiveDocumentActionsPanel = documentActions;
        _responsiveDocumentToolbarGrid = toolbarGrid;
        _responsiveDocumentToolbarBorder = toolbarBorder;

        if (toolbarBorder.Parent is Grid rootGrid)
        {
            int toolbarRow = Grid.GetRow(toolbarBorder);
            if (toolbarRow >= 0 && toolbarRow < rootGrid.RowDefinitions.Count)
            {
                _responsiveDocumentHostRow = rootGrid.RowDefinitions[toolbarRow];
            }
        }

        _responsiveHeaderGrid = headerGrid;
        _responsiveModelPanel = modelPanel;
        _responsiveModelLabel = modelPanel.Children.OfType<TextBlock>().FirstOrDefault();
        _responsiveOllamaBadge = headerGrid.Children
            .OfType<Border>()
            .FirstOrDefault(border => Grid.GetColumn(border) == 1);

        StackPanel? brandRoot = headerGrid.Children
            .OfType<StackPanel>()
            .FirstOrDefault(panel => Grid.GetColumn(panel) == 0);
        _responsiveBrandTextPanel = brandRoot?.Children
            .OfType<StackPanel>()
            .FirstOrDefault();

        _responsiveTopBarsInstalled = true;
        SizeChanged += MainWindow_ResponsiveTopBarsSizeChanged;
        ApplyResponsiveTopBars();
    }

    private void MainWindow_ResponsiveTopBarsSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveTopBars();

    private void ApplyResponsiveTopBars()
    {
        if (!_responsiveTopBarsInstalled
            || _applyingResponsiveTopBars
            || _responsiveDocumentToolbarGrid is null
            || _responsiveDocumentToolbarBorder is null
            || _responsiveOpenActionsPanel is null
            || _responsiveZoomPanel is null
            || _responsiveDocumentActionsPanel is null)
        {
            return;
        }

        _applyingResponsiveTopBars = true;
        try
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;
            ApplyResponsiveHeader(width);

            if (width >= 1900)
            {
                ApplyWideDocumentToolbar();
            }
            else if (width >= 1250)
            {
                ApplyTwoRowDocumentToolbar();
            }
            else
            {
                ApplyThreeRowDocumentToolbar();
            }
        }
        finally
        {
            _applyingResponsiveTopBars = false;
        }
    }

    private void ApplyResponsiveHeader(double width)
    {
        if (_responsiveHeaderGrid is null || _responsiveModelPanel is null)
        {
            return;
        }

        bool compact = width < 1350;
        bool veryCompact = width < 1050;

        _responsiveHeaderGrid.Margin = compact
            ? new Thickness(12, 0, 12, 0)
            : new Thickness(22, 0, 22, 0);

        if (_responsiveOllamaBadge is not null)
        {
            _responsiveOllamaBadge.Visibility = compact
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        if (_responsiveModelLabel is not null)
        {
            _responsiveModelLabel.Visibility = veryCompact
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        if (_responsiveBrandTextPanel is not null)
        {
            _responsiveBrandTextPanel.Visibility = width < 920
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        ModelComboBox.Width = veryCompact ? 132 : compact ? 154 : 178;
    }

    private void ApplyWideDocumentToolbar()
    {
        ConfigureToolbarRows(1);
        SetToolbarPanel(_responsiveOpenActionsPanel!, 0, 0, 1, HorizontalAlignment.Left, new Thickness(0));
        SetToolbarPanel(_responsiveZoomPanel!, 0, 1, 1, HorizontalAlignment.Center, new Thickness(0));
        SetToolbarPanel(_responsiveDocumentActionsPanel!, 0, 2, 1, HorizontalAlignment.Right, new Thickness(0));

        _responsiveDocumentToolbarGrid!.Margin = new Thickness(18, 0, 18, 0);
        _responsiveDocumentToolbarBorder!.MinHeight = 58;
        if (_responsiveDocumentHostRow is not null)
        {
            _responsiveDocumentHostRow.Height = new GridLength(58);
        }

        SetZoomPresentation(showLabel: true, sliderWidth: 170);
    }

    private void ApplyTwoRowDocumentToolbar()
    {
        ConfigureToolbarRows(2);
        SetToolbarPanel(_responsiveOpenActionsPanel!, 0, 0, 2, HorizontalAlignment.Left, new Thickness(0, 2, 0, 2));
        SetToolbarPanel(_responsiveZoomPanel!, 0, 2, 1, HorizontalAlignment.Right, new Thickness(12, 2, 0, 2));
        SetToolbarPanel(_responsiveDocumentActionsPanel!, 1, 0, 3, HorizontalAlignment.Right, new Thickness(0, 2, 0, 2));

        _responsiveDocumentToolbarGrid!.Margin = new Thickness(18, 4, 18, 4);
        _responsiveDocumentToolbarBorder!.MinHeight = 100;
        if (_responsiveDocumentHostRow is not null)
        {
            _responsiveDocumentHostRow.Height = GridLength.Auto;
        }

        SetZoomPresentation(showLabel: true, sliderWidth: 145);
    }

    private void ApplyThreeRowDocumentToolbar()
    {
        ConfigureToolbarRows(3);
        SetToolbarPanel(_responsiveOpenActionsPanel!, 0, 0, 3, HorizontalAlignment.Left, new Thickness(0, 2, 0, 2));
        SetToolbarPanel(_responsiveDocumentActionsPanel!, 1, 0, 3, HorizontalAlignment.Left, new Thickness(0, 2, 0, 2));
        SetToolbarPanel(_responsiveZoomPanel!, 2, 0, 3, HorizontalAlignment.Left, new Thickness(0, 2, 0, 2));

        _responsiveDocumentToolbarGrid!.Margin = new Thickness(12, 4, 12, 4);
        _responsiveDocumentToolbarBorder!.MinHeight = 146;
        if (_responsiveDocumentHostRow is not null)
        {
            _responsiveDocumentHostRow.Height = GridLength.Auto;
        }

        SetZoomPresentation(showLabel: false, sliderWidth: 128);
    }

    private void ConfigureToolbarRows(int count)
    {
        Grid grid = _responsiveDocumentToolbarGrid!;
        if (grid.RowDefinitions.Count != count)
        {
            grid.RowDefinitions.Clear();
            for (int index = 0; index < count; index++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
        }
    }

    private static void SetToolbarPanel(
        FrameworkElement panel,
        int row,
        int column,
        int columnSpan,
        HorizontalAlignment alignment,
        Thickness margin)
    {
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        Grid.SetColumnSpan(panel, columnSpan);
        panel.HorizontalAlignment = alignment;
        panel.Margin = margin;
    }

    private void SetZoomPresentation(bool showLabel, double sliderWidth)
    {
        TextBlock? zoomLabel = _responsiveZoomPanel?
            .Children
            .OfType<TextBlock>()
            .FirstOrDefault(text => !ReferenceEquals(text, ZoomText));
        if (zoomLabel is not null)
        {
            zoomLabel.Visibility = showLabel ? Visibility.Visible : Visibility.Collapsed;
        }
        ZoomSlider.Width = sliderWidth;
    }
}
