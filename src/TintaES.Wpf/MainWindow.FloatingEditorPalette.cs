using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Paleta de herramientas locales de la página. Solo contiene acciones de edición y puede moverse
/// libremente por el área de trabajo. Guardar página es una acción de documento y permanece fuera
/// del lienzo como un botón compacto con icono de disquete.
/// </summary>
public partial class MainWindow
{
    private static readonly bool FloatingEditorPaletteRegistered = RegisterFloatingEditorPalette();

    private Border? _floatingEditorPalette;
    private bool _floatingEditorPaletteInstalled;
    private bool _floatingPaletteDragging;
    private Point _floatingPalettePointerStart;
    private Point _floatingPalettePositionStart;

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
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.TryInstallFloatingEditorPalette,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void TryInstallFloatingEditorPalette()
    {
        if (_floatingEditorPaletteInstalled)
        {
            MovePageSaveButtonOutsideCanvas();
            ClampFloatingEditorPalette();
            return;
        }

        if (_undoEditorButton is null
            || _redoEditorButton is null
            || _saveCurrentPageButton is null
            || ImageScrollViewer.Parent is not Grid viewport)
        {
            Dispatcher.BeginInvoke(TryInstallFloatingEditorPalette, DispatcherPriority.ContextIdle);
            return;
        }

        _floatingEditorPaletteInstalled = true;
        MovePageSaveButtonOutsideCanvas();

        Button[] controls = [_undoEditorButton, _redoEditorButton, AddRegionButton];
        foreach (Button button in controls)
        {
            DetachFloatingPaletteControl(button);
            button.VerticalAlignment = VerticalAlignment.Center;
        }

        _undoEditorButton.Margin = new Thickness(0, 0, 5, 0);
        _redoEditorButton.Margin = new Thickness(0, 0, 9, 0);
        AddRegionButton.Margin = new Thickness(0);

        var grip = new TextBlock
        {
            Text = "⠿",
            FontSize = 17,
            Foreground = FindResource("MutedBrush") as Brush ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
            Cursor = Cursors.SizeAll,
            ToolTip = "Arrastra para mover la paleta"
        };

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        tools.Children.Add(grip);
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
            Margin = new Thickness(12, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = tools,
            Cursor = Cursors.SizeAll,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 3,
                Opacity = 0.42,
                Color = Colors.Black
            },
            ToolTip = "Arrastra la zona vacía o el asa para mover las herramientas"
        };

        _floatingEditorPalette.PreviewMouseLeftButtonDown += FloatingEditorPalette_MouseDown;
        _floatingEditorPalette.PreviewMouseMove += FloatingEditorPalette_MouseMove;
        _floatingEditorPalette.PreviewMouseLeftButtonUp += FloatingEditorPalette_MouseUp;
        _floatingEditorPalette.LostMouseCapture += (_, _) => _floatingPaletteDragging = false;

        Panel.SetZIndex(_floatingEditorPalette, 9_000);
        viewport.Children.Add(_floatingEditorPalette);
        ImageScrollViewer.SizeChanged += (_, _) => ClampFloatingEditorPalette();
        ClampFloatingEditorPalette();
    }

    private void MovePageSaveButtonOutsideCanvas()
    {
        if (_saveCurrentPageButton is null || ExportButton.Parent is not StackPanel documentToolbar)
        {
            return;
        }

        DetachFloatingPaletteControl(_saveCurrentPageButton);
        _saveCurrentPageButton.Content = new TextBlock
        {
            Text = "\uE74E",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _saveCurrentPageButton.Width = 40;
        _saveCurrentPageButton.Height = 34;
        _saveCurrentPageButton.Padding = new Thickness(0);
        _saveCurrentPageButton.Margin = new Thickness(0, 0, 7, 0);
        _saveCurrentPageButton.ToolTip = "Guardar página actual (Ctrl+S)";

        int index = _saveProjectButton is not null && documentToolbar.Children.Contains(_saveProjectButton)
            ? documentToolbar.Children.IndexOf(_saveProjectButton)
            : Math.Max(0, documentToolbar.Children.IndexOf(ExportButton));
        if (!documentToolbar.Children.Contains(_saveCurrentPageButton))
        {
            documentToolbar.Children.Insert(Math.Max(0, index), _saveCurrentPageButton);
        }
    }

    private void FloatingEditorPalette_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_floatingEditorPalette is null || FindButtonAncestor(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _floatingPaletteDragging = true;
        _floatingPalettePointerStart = e.GetPosition(_floatingEditorPalette.Parent as IInputElement);
        _floatingPalettePositionStart = new Point(
            _floatingEditorPalette.Margin.Left,
            _floatingEditorPalette.Margin.Top);
        _floatingEditorPalette.CaptureMouse();
        e.Handled = true;
    }

    private void FloatingEditorPalette_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_floatingPaletteDragging || _floatingEditorPalette?.Parent is not FrameworkElement host)
        {
            return;
        }

        Point current = e.GetPosition(host);
        double left = _floatingPalettePositionStart.X + current.X - _floatingPalettePointerStart.X;
        double top = _floatingPalettePositionStart.Y + current.Y - _floatingPalettePointerStart.Y;
        SetFloatingPalettePosition(left, top, host);
        e.Handled = true;
    }

    private void FloatingEditorPalette_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_floatingPaletteDragging)
        {
            return;
        }

        _floatingPaletteDragging = false;
        _floatingEditorPalette?.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ClampFloatingEditorPalette()
    {
        if (_floatingEditorPalette?.Parent is not FrameworkElement host)
        {
            return;
        }
        SetFloatingPalettePosition(_floatingEditorPalette.Margin.Left, _floatingEditorPalette.Margin.Top, host);
    }

    private void SetFloatingPalettePosition(double left, double top, FrameworkElement host)
    {
        if (_floatingEditorPalette is null)
        {
            return;
        }

        double width = _floatingEditorPalette.ActualWidth > 0 ? _floatingEditorPalette.ActualWidth : 310;
        double height = _floatingEditorPalette.ActualHeight > 0 ? _floatingEditorPalette.ActualHeight : 54;
        double maxLeft = Math.Max(0, host.ActualWidth - width - 8);
        double maxTop = Math.Max(0, host.ActualHeight - height - 8);
        _floatingEditorPalette.Margin = new Thickness(
            Math.Clamp(left, 0, maxLeft),
            Math.Clamp(top, 0, maxTop),
            0,
            0);
    }

    private static Button? FindButtonAncestor(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button button)
            {
                return button;
            }
        }
        return null;
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