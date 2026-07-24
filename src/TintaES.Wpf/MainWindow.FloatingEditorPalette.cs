using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene las operaciones propias de la página dentro del área de trabajo. La barra superior
/// queda reservada para abrir, procesar, guardar el proyecto y exportar, evitando que el zoom y
/// los botones se aplasten cuando la ventana o el monitor tienen menos ancho.
/// </summary>
public partial class MainWindow
{
    private static readonly bool FloatingEditorPaletteRegistered = RegisterFloatingEditorPalette();

    private Border? _floatingEditorPalette;
    private bool _floatingEditorPaletteInstalled;

    private static bool RegisterFloatingEditorPalette()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_FloatingEditorPaletteLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_FloatingEditorPaletteLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.LayoutUpdated -= window.MainWindow_TryInstallFloatingEditorPalette;
        window.LayoutUpdated += window.MainWindow_TryInstallFloatingEditorPalette;
        window.Dispatcher.BeginInvoke(
            window.TryInstallFloatingEditorPalette,
            DispatcherPriority.SystemIdle);
    }

    private void MainWindow_TryInstallFloatingEditorPalette(object? sender, EventArgs e) =>
        TryInstallFloatingEditorPalette();

    private void TryInstallFloatingEditorPalette()
    {
        if (_floatingEditorPaletteInstalled)
        {
            UpdateFloatingEditorPaletteWidth();
            return;
        }

        if (_undoEditorButton is null
            || _redoEditorButton is null
            || _saveCurrentPageButton is null
            || ImageScrollViewer.Parent is not Grid viewport)
        {
            return;
        }

        _floatingEditorPaletteInstalled = true;
        LayoutUpdated -= MainWindow_TryInstallFloatingEditorPalette;

        Button[] controls =
        [
            _undoEditorButton,
            _redoEditorButton,
            _saveCurrentPageButton,
            AddRegionButton
        ];

        foreach (Button button in controls)
        {
            DetachFloatingPaletteControl(button);
            button.VerticalAlignment = VerticalAlignment.Center;
        }

        _undoEditorButton.Margin = new Thickness(0, 0, 5, 0);
        _redoEditorButton.Margin = new Thickness(0, 0, 10, 0);
        _saveCurrentPageButton.Margin = new Thickness(0, 0, 7, 0);
        AddRegionButton.Margin = new Thickness(0);

        var tools = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (Button button in controls)
        {
            tools.Children.Add(button);
        }

        _floatingEditorPalette = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(238, 17, 19, 21)),
            BorderBrush = FindResource("LineBrush") as Brush ?? Brushes.DimGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = tools,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 3,
                Opacity = 0.42,
                Color = Colors.Black
            },
            ToolTip = "Herramientas de la página actual"
        };

        Panel.SetZIndex(_floatingEditorPalette, 9_000);
        viewport.Children.Add(_floatingEditorPalette);
        ImageScrollViewer.SizeChanged += (_, _) => UpdateFloatingEditorPaletteWidth();
        UpdateFloatingEditorPaletteWidth();
    }

    private void UpdateFloatingEditorPaletteWidth()
    {
        if (_floatingEditorPalette is null)
        {
            return;
        }

        double viewportWidth = ImageScrollViewer.ActualWidth;
        _floatingEditorPalette.MaxWidth = viewportWidth > 0
            ? Math.Max(250, viewportWidth - 32)
            : 520;
    }

    private static void DetachFloatingPaletteControl(UIElement control)
    {
        if (control is not FrameworkElement element)
        {
            return;
        }

        switch (element.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, control):
                decorator.Child = null;
                break;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, control):
                contentControl.Content = null;
                break;
        }
    }
}
