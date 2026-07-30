using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Recupera altura útil del lienzo. En pantallas de escritorio todas las acciones del documento
/// caben en una sola fila; la distribución anterior dejaba grandes huecos y empujaba guardar y
/// exportar a una segunda línea innecesaria.
/// </summary>
public partial class MainWindow
{
    private static readonly bool CompactWorkspaceHeaderRegistered = RegisterCompactWorkspaceHeader();

    private bool _compactWorkspaceHeaderInstalled;
    private bool _compactWorkspaceHeaderPending;
    private int _compactWorkspaceHeaderAttempts;

    private static bool RegisterCompactWorkspaceHeader()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_CompactWorkspaceHeaderLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_CompactWorkspaceHeaderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.QueueCompactWorkspaceHeader();
        }
    }

    private void QueueCompactWorkspaceHeader()
    {
        if (_compactWorkspaceHeaderPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _compactWorkspaceHeaderPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _compactWorkspaceHeaderPending = false;
                InstallOrApplyCompactWorkspaceHeader();
            },
            DispatcherPriority.ApplicationIdle);
    }

    private void InstallOrApplyCompactWorkspaceHeader()
    {
        if (_responsiveDocumentToolbarGrid is null
            || _responsiveDocumentToolbarBorder is null
            || _responsiveOpenActionsPanel is null
            || _responsiveZoomPanel is null
            || _responsiveDocumentActionsPanel is null
            || _responsiveHeaderGrid is null)
        {
            if (++_compactWorkspaceHeaderAttempts < 8)
            {
                QueueCompactWorkspaceHeader();
            }
            return;
        }

        if (!_compactWorkspaceHeaderInstalled)
        {
            _compactWorkspaceHeaderInstalled = true;
            SizeChanged += (_, _) => QueueCompactWorkspaceHeader();
        }

        ApplyCompactWorkspaceHeader();
    }

    private void ApplyCompactWorkspaceHeader()
    {
        double width = ActualWidth > 0 ? ActualWidth : Width;
        bool desktop = width >= 1540;
        bool medium = width >= 1120;

        Grid? root = (_responsiveHeaderGrid?.Parent as Border)?.Parent as Grid;
        if (root is not null && root.RowDefinitions.Count >= 2)
        {
            root.RowDefinitions[0].Height = new GridLength(52);
        }

        Border? headerBorder = _responsiveHeaderGrid?.Parent as Border;
        if (headerBorder is not null)
        {
            headerBorder.MinHeight = 0;
            headerBorder.Padding = new Thickness(0);
        }
        _responsiveHeaderGrid!.Margin = new Thickness(14, 0, 14, 0);

        CompactBrandAndModel(width);
        CompactCommandButtons(_responsiveOpenActionsPanel!);
        CompactCommandButtons(_responsiveDocumentActionsPanel!);

        if (desktop)
        {
            // Una sola fila real: abrir/analizar a la izquierda, zoom en el hueco central y
            // guardar/exportar a la derecha. Se elimina la fila vacía que robaba lienzo.
            ConfigureToolbarRows(1);
            SetToolbarPanel(
                _responsiveOpenActionsPanel!,
                0,
                0,
                1,
                HorizontalAlignment.Left,
                new Thickness(0));
            SetToolbarPanel(
                _responsiveZoomPanel!,
                0,
                1,
                1,
                HorizontalAlignment.Center,
                new Thickness(8, 0, 8, 0));
            SetToolbarPanel(
                _responsiveDocumentActionsPanel!,
                0,
                2,
                1,
                HorizontalAlignment.Right,
                new Thickness(0));

            _responsiveDocumentToolbarGrid!.Margin = new Thickness(12, 0, 12, 0);
            _responsiveDocumentToolbarBorder!.MinHeight = 0;
            if (_responsiveDocumentHostRow is not null)
            {
                _responsiveDocumentHostRow.Height = new GridLength(50);
            }
            SetZoomPresentation(showLabel: false, sliderWidth: 112);
            ZoomText.Width = 43;
        }
        else if (medium)
        {
            // Dos filas compactas sin la separación de 100 px de la disposición anterior.
            ConfigureToolbarRows(2);
            SetToolbarPanel(
                _responsiveOpenActionsPanel!,
                0,
                0,
                2,
                HorizontalAlignment.Left,
                new Thickness(0, 2, 0, 1));
            SetToolbarPanel(
                _responsiveZoomPanel!,
                0,
                2,
                1,
                HorizontalAlignment.Right,
                new Thickness(8, 2, 0, 1));
            SetToolbarPanel(
                _responsiveDocumentActionsPanel!,
                1,
                0,
                3,
                HorizontalAlignment.Right,
                new Thickness(0, 1, 0, 2));

            _responsiveDocumentToolbarGrid!.Margin = new Thickness(12, 1, 12, 1);
            _responsiveDocumentToolbarBorder!.MinHeight = 0;
            if (_responsiveDocumentHostRow is not null)
            {
                _responsiveDocumentHostRow.Height = new GridLength(76);
            }
            SetZoomPresentation(showLabel: false, sliderWidth: 108);
            ZoomText.Width = 43;
        }
        else
        {
            // En una ventana realmente estrecha se conservan tres grupos, pero sin las enormes
            // franjas de aire de la versión anterior.
            ConfigureToolbarRows(3);
            SetToolbarPanel(_responsiveOpenActionsPanel!, 0, 0, 3, HorizontalAlignment.Left, new Thickness(0, 1, 0, 1));
            SetToolbarPanel(_responsiveDocumentActionsPanel!, 1, 0, 3, HorizontalAlignment.Left, new Thickness(0, 1, 0, 1));
            SetToolbarPanel(_responsiveZoomPanel!, 2, 0, 3, HorizontalAlignment.Left, new Thickness(0, 1, 0, 1));
            _responsiveDocumentToolbarGrid!.Margin = new Thickness(10, 1, 10, 1);
            _responsiveDocumentToolbarBorder!.MinHeight = 0;
            if (_responsiveDocumentHostRow is not null)
            {
                _responsiveDocumentHostRow.Height = new GridLength(112);
            }
            SetZoomPresentation(showLabel: false, sliderWidth: 105);
        }
    }

    private void CompactBrandAndModel(double width)
    {
        StackPanel? brand = _responsiveHeaderGrid?
            .Children
            .OfType<StackPanel>()
            .FirstOrDefault(panel => Grid.GetColumn(panel) == 0);
        Border? logo = brand?.Children.OfType<Border>().FirstOrDefault();
        if (logo is not null)
        {
            logo.Width = 36;
            logo.Height = 38;
        }

        if (_responsiveOllamaBadge is not null)
        {
            _responsiveOllamaBadge.Padding = new Thickness(10, 5, 10, 5);
            _responsiveOllamaBadge.Margin = new Thickness(0, 0, 12, 0);
            _responsiveOllamaBadge.Visibility = width >= 1420
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_responsiveModelLabel is not null)
        {
            _responsiveModelLabel.Visibility = width >= 1180
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        ModelComboBox.Width = width >= 1420 ? 170 : 145;
        ModelComboBox.Height = 32;
        RefreshModelsButton.Height = 32;
    }

    private static void CompactCommandButtons(Panel panel)
    {
        Button[] buttons = panel.Children.OfType<Button>().ToArray();
        for (int index = 0; index < buttons.Length; index++)
        {
            Button button = buttons[index];
            button.Height = 34;
            button.MinHeight = 0;
            button.Padding = button.Width > 0 && button.Width <= 44
                ? new Thickness(0)
                : new Thickness(10, 4, 10, 4);
            button.Margin = index == buttons.Length - 1
                ? new Thickness(0)
                : new Thickness(0, 0, 5, 0);
        }
    }
}
