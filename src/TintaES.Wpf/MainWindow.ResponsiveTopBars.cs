using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene toda la barra de trabajo en una sola fila. La cabecera corporativa grande se oculta:
/// el nombre de la aplicación ya está en la barra de título y no debe robar altura al lienzo.
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
    private Border? _responsiveHeaderBorder;
    private RowDefinition? _responsiveHeaderHostRow;
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
            || modelPanel.Parent is not Grid headerGrid
            || headerGrid.Parent is not Border headerBorder)
        {
            Dispatcher.BeginInvoke(InstallResponsiveTopBars, DispatcherPriority.ContextIdle);
            return;
        }

        _responsiveOpenActionsPanel = openActions;
        _responsiveZoomPanel = zoomPanel;
        _responsiveDocumentActionsPanel = documentActions;
        _responsiveDocumentToolbarGrid = toolbarGrid;
        _responsiveDocumentToolbarBorder = toolbarBorder;
        _responsiveHeaderGrid = headerGrid;
        _responsiveHeaderBorder = headerBorder;
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

        if (toolbarBorder.Parent is Grid rootGrid)
        {
            int toolbarRow = Grid.GetRow(toolbarBorder);
            if (toolbarRow >= 0 && toolbarRow < rootGrid.RowDefinitions.Count)
            {
                _responsiveDocumentHostRow = rootGrid.RowDefinitions[toolbarRow];
            }
        }

        if (headerBorder.Parent is Grid headerRoot)
        {
            int headerRow = Grid.GetRow(headerBorder);
            if (headerRow >= 0 && headerRow < headerRoot.RowDefinitions.Count)
            {
                _responsiveHeaderHostRow = headerRoot.RowDefinitions[headerRow];
            }
        }

        // El selector de modelo deja la cabecera grande y pasa a la barra única de comandos.
        headerGrid.Children.Remove(modelPanel);
        toolbarGrid.Children.Add(modelPanel);

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
            || _responsiveDocumentActionsPanel is null
            || _responsiveModelPanel is null)
        {
            return;
        }

        _applyingResponsiveTopBars = true;
        try
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;

            // La marca de 66 px desaparece por completo. El título de Windows ya identifica la app.
            if (_responsiveHeaderBorder is not null)
            {
                _responsiveHeaderBorder.Visibility = Visibility.Collapsed;
                _responsiveHeaderBorder.Height = 0;
                _responsiveHeaderBorder.MinHeight = 0;
            }
            if (_responsiveHeaderHostRow is not null)
            {
                _responsiveHeaderHostRow.Height = new GridLength(0);
            }
            if (_responsiveOllamaBadge is not null)
            {
                _responsiveOllamaBadge.Visibility = Visibility.Collapsed;
            }
            if (_responsiveBrandTextPanel is not null)
            {
                _responsiveBrandTextPanel.Visibility = Visibility.Collapsed;
            }
            if (_responsiveModelLabel is not null)
            {
                _responsiveModelLabel.Visibility = Visibility.Collapsed;
            }

            ConfigureSingleToolbarGrid();

            SetToolbarPanel(
                _responsiveOpenActionsPanel,
                0,
                0,
                1,
                HorizontalAlignment.Left,
                new Thickness(0));
            SetToolbarPanel(
                _responsiveZoomPanel,
                0,
                2,
                1,
                HorizontalAlignment.Right,
                new Thickness(8, 0, 8, 0));
            SetToolbarPanel(
                _responsiveModelPanel,
                0,
                3,
                1,
                HorizontalAlignment.Right,
                new Thickness(0, 0, 7, 0));
            SetToolbarPanel(
                _responsiveDocumentActionsPanel,
                0,
                4,
                1,
                HorizontalAlignment.Right,
                new Thickness(0));

            _responsiveDocumentToolbarGrid.Margin = new Thickness(10, 0, 10, 0);
            _responsiveDocumentToolbarBorder.MinHeight = 0;
            _responsiveDocumentToolbarBorder.Height = 48;
            if (_responsiveDocumentHostRow is not null)
            {
                _responsiveDocumentHostRow.Height = new GridLength(48);
            }

            SetZoomPresentation(showLabel: false, sliderWidth: width < 1250 ? 78 : 96);
            ZoomText.Width = 38;
            ZoomText.FontSize = 10;

            ModelComboBox.Width = width < 1250 ? 116 : 138;
            ModelComboBox.Height = 32;
            RefreshModelsButton.Width = 32;
            RefreshModelsButton.Height = 32;
            RefreshModelsButton.Margin = new Thickness(4, 0, 0, 0);

            CompactCommandButtons(_responsiveOpenActionsPanel);
            CompactCommandButtons(_responsiveDocumentActionsPanel);
            CompactToolbarLabels();
        }
        finally
        {
            _applyingResponsiveTopBars = false;
        }
    }

    private void ConfigureSingleToolbarGrid()
    {
        Grid grid = _responsiveDocumentToolbarGrid!;

        if (grid.RowDefinitions.Count != 1)
        {
            grid.RowDefinitions.Clear();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        }

        if (grid.ColumnDefinitions.Count != 5)
        {
            grid.ColumnDefinitions.Clear();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
    }

    private void CompactToolbarLabels()
    {
        SetButtonLabel(OpenImageButton, "Abrir", "Abrir cómic o páginas");
        SetButtonLabel(_openFolderButton, "Carpeta", "Abrir una carpeta de páginas");
        SetButtonLabel(AnalyzeButton, "✦ Traducir", "Detectar los bocadillos y traducir las páginas seleccionadas");
        SetButtonLabel(_saveProjectButton, "Guardar", "Guardar proyecto editable");
        SetButtonLabel(_exportComicButton, "CBZ", "Exportar páginas seleccionadas a CBZ");
        SetButtonLabel(_exportPsdButton, "PSD", "Exportar la página actual a PSD");
        SetButtonLabel(ExportButton, "Imagen", "Exportar la página actual como imagen");

        foreach (Button button in _responsiveOpenActionsPanel!.Children.OfType<Button>())
        {
            string current = button.Content?.ToString() ?? string.Empty;
            if (current.Contains("Visualizar", StringComparison.OrdinalIgnoreCase)
                || current.Contains("Leer", StringComparison.OrdinalIgnoreCase))
            {
                SetButtonLabel(button, "Leer", "Leer el cómic y pulsar los bocadillos para traducirlos");
            }
            else if (current.Contains("Páginas", StringComparison.OrdinalIgnoreCase))
            {
                SetButtonLabel(button, "+ Páginas", "Añadir páginas al cómic");
            }
        }
    }

    private static void SetButtonLabel(Button? button, string label, string tooltip)
    {
        if (button is null)
        {
            return;
        }

        button.Content = label;
        button.ToolTip = tooltip;
    }

    private static void CompactCommandButtons(Panel panel)
    {
        Button[] buttons = panel.Children.OfType<Button>().ToArray();
        for (int index = 0; index < buttons.Length; index++)
        {
            Button button = buttons[index];
            button.Height = 32;
            button.MinHeight = 0;
            button.Padding = button.Width > 0 && button.Width <= 44
                ? new Thickness(0)
                : new Thickness(8, 3, 8, 3);
            button.Margin = index == buttons.Length - 1
                ? new Thickness(0)
                : new Thickness(0, 0, 4, 0);
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
        panel.VerticalAlignment = VerticalAlignment.Center;
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
