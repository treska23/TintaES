using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Distribución compacta inspirada en editores gráficos: las acciones de documento e historial
/// permanecen fuera del lienzo; la paleta flotante y arrastrable contiene únicamente herramientas
/// que actúan directamente sobre la página.
/// </summary>
public partial class MainWindow
{
    private static readonly bool FloatingEditorPaletteRegistered = RegisterFloatingEditorPalette();

    private Border? _floatingEditorPalette;
    private Button? _selectCanvasToolButton;
    private StackPanel? _maskBrushOptionsPanel;
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
            MoveHistoryAndSaveOutsideCanvas();
            ApplyCompactCanvasToolIcons();
            ClampFloatingEditorPalette();
            return;
        }

        if (_undoEditorButton is null
            || _redoEditorButton is null
            || _saveCurrentPageButton is null
            || _maskPaintButton is null
            || _maskEraseButton is null
            || ImageScrollViewer.Parent is not Grid viewport)
        {
            Dispatcher.BeginInvoke(TryInstallFloatingEditorPalette, DispatcherPriority.ContextIdle);
            return;
        }

        _floatingEditorPaletteInstalled = true;
        MoveHistoryAndSaveOutsideCanvas();

        _selectCanvasToolButton = CreateCompactToolButton(
            "↖",
            "Seleccionar y mover textos (Esc)",
            SelectCanvasTool_Click);

        Button[] canvasTools = [AddRegionButton, _maskPaintButton, _maskEraseButton];
        foreach (Button button in canvasTools)
        {
            DetachFloatingPaletteControl(button);
            ConfigureCompactToolButton(button);
        }

        // El panel derecho conserva únicamente la opción contextual de tamaño. Los botones de
        // herramienta viven en la paleta, donde resultan accesibles sin ocupar el inspector.
        if (_maskPaintButton.Parent is StackPanel oldButtons)
        {
            oldButtons.Visibility = Visibility.Collapsed;
        }
        _maskBrushOptionsPanel = _maskBrushSizeSlider?.Parent as StackPanel;
        if (_maskBrushOptionsPanel is not null)
        {
            _maskBrushOptionsPanel.Visibility = Visibility.Collapsed;
        }

        var grip = new TextBlock
        {
            Text = "⠿",
            FontSize = 16,
            Foreground = FindResource("MutedBrush") as Brush ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
            Cursor = Cursors.SizeAll,
            ToolTip = "Mover la paleta"
        };

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        tools.Children.Add(grip);
        tools.Children.Add(_selectCanvasToolButton);
        tools.Children.Add(AddRegionButton);
        tools.Children.Add(_maskPaintButton);
        tools.Children.Add(_maskEraseButton);

        _floatingEditorPalette = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(236, 17, 19, 21)),
            BorderBrush = FindResource("LineBrush") as Brush ?? Brushes.DimGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5),
            Margin = new Thickness(12, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = tools,
            Effect = new DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.34,
                Color = Colors.Black
            },
            ToolTip = "Arrastra el asa para mover las herramientas"
        };

        _floatingEditorPalette.PreviewMouseLeftButtonDown += FloatingEditorPalette_MouseDown;
        _floatingEditorPalette.PreviewMouseMove += FloatingEditorPalette_MouseMove;
        _floatingEditorPalette.PreviewMouseLeftButtonUp += FloatingEditorPalette_MouseUp;
        _floatingEditorPalette.LostMouseCapture += (_, _) => _floatingPaletteDragging = false;

        AddRegionButton.Click += (_, _) => Dispatcher.BeginInvoke(
            ApplyCompactCanvasToolIcons,
            DispatcherPriority.Input);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Dispatcher.BeginInvoke(ApplyCompactCanvasToolIcons, DispatcherPriority.Input);
            }
        };

        Panel.SetZIndex(_floatingEditorPalette, 9_000);
        viewport.Children.Add(_floatingEditorPalette);
        ImageScrollViewer.SizeChanged += (_, _) => ClampFloatingEditorPalette();
        ApplyCompactCanvasToolIcons();
        ClampFloatingEditorPalette();
    }

    private void MoveHistoryAndSaveOutsideCanvas()
    {
        if (_undoEditorButton is null
            || _redoEditorButton is null
            || _saveCurrentPageButton is null
            || ExportButton.Parent is not StackPanel documentToolbar)
        {
            return;
        }

        Button[] documentButtons = [_undoEditorButton, _redoEditorButton, _saveCurrentPageButton];
        foreach (Button button in documentButtons)
        {
            DetachFloatingPaletteControl(button);
            ConfigureCompactDocumentButton(button);
        }

        _undoEditorButton.Content = "↶";
        _undoEditorButton.ToolTip = "Deshacer (Ctrl+Z)";
        _redoEditorButton.Content = "↷";
        _redoEditorButton.ToolTip = "Rehacer (Ctrl+Y)";
        _saveCurrentPageButton.Content = new TextBlock
        {
            Text = "\uE74E",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _saveCurrentPageButton.ToolTip = "Guardar página actual (Ctrl+S)";

        int anchor = _saveProjectButton is not null && documentToolbar.Children.Contains(_saveProjectButton)
            ? documentToolbar.Children.IndexOf(_saveProjectButton)
            : Math.Max(0, documentToolbar.Children.IndexOf(ExportButton));

        InsertDocumentButton(documentToolbar, _undoEditorButton, anchor++);
        InsertDocumentButton(documentToolbar, _redoEditorButton, anchor++);
        InsertDocumentButton(documentToolbar, _saveCurrentPageButton, anchor);
    }

    private static void InsertDocumentButton(StackPanel toolbar, Button button, int index)
    {
        if (!toolbar.Children.Contains(button))
        {
            toolbar.Children.Insert(Math.Clamp(index, 0, toolbar.Children.Count), button);
        }
    }

    private static void ConfigureCompactDocumentButton(Button button)
    {
        button.Width = 36;
        button.Height = 34;
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(0, 0, 5, 0);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.FontSize = 17;
    }

    private Button CreateCompactToolButton(string glyph, string toolTip, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = glyph,
            ToolTip = toolTip,
            Style = FindResource("ToolbarButton") as Style
        };
        ConfigureCompactToolButton(button);
        button.Click += click;
        return button;
    }

    private static void ConfigureCompactToolButton(Button button)
    {
        button.Width = 34;
        button.Height = 32;
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(2, 0, 0, 0);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.FontSize = 16;
    }

    private void SelectCanvasTool_Click(object sender, RoutedEventArgs e)
    {
        if (_drawingRegion)
        {
            SetDrawingRegionMode(false);
        }
        if (_manualMaskTool != ManualMaskTool.None)
        {
            LeaveManualMaskEditingOverPage();
        }
        OverlayCanvas.Cursor = Cursors.Arrow;
        ApplyCompactCanvasToolIcons();
        SetFooterStatus("Herramienta de selección activa.", "#6C747A");
    }

    private void ApplyCompactCanvasToolIcons()
    {
        if (AddRegionButton is not null)
        {
            AddRegionButton.Content = "▭";
            AddRegionButton.ToolTip = _drawingRegion
                ? "Dibujar zona activo · pulsa de nuevo o Esc para cancelar"
                : "Dibujar una zona de texto";
            AddRegionButton.BorderBrush = _drawingRegion
                ? FindResource("AccentBrush") as Brush
                : FindResource("LineBrush") as Brush;
        }
        if (_maskPaintButton is not null)
        {
            _maskPaintButton.Content = "✎";
            _maskPaintButton.ToolTip = "Pincel: borrar el texto original";
            _maskPaintButton.BorderBrush = _manualMaskTool == ManualMaskTool.Paint
                ? FindResource("AccentBrush") as Brush
                : FindResource("LineBrush") as Brush;
        }
        if (_maskEraseButton is not null)
        {
            _maskEraseButton.Content = "⌫";
            _maskEraseButton.ToolTip = "Borrador: recuperar la imagen original";
            _maskEraseButton.BorderBrush = _manualMaskTool == ManualMaskTool.Erase
                ? FindResource("AccentBrush") as Brush
                : FindResource("LineBrush") as Brush;
        }
        if (_selectCanvasToolButton is not null)
        {
            bool active = !_drawingRegion && _manualMaskTool == ManualMaskTool.None;
            _selectCanvasToolButton.BorderBrush = active
                ? FindResource("AccentBrush") as Brush
                : FindResource("LineBrush") as Brush;
        }
        if (_maskBrushOptionsPanel is not null)
        {
            _maskBrushOptionsPanel.Visibility = _manualMaskTool == ManualMaskTool.None
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void FloatingEditorPalette_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_floatingEditorPalette is null
            || FindButtonAncestor(e.OriginalSource as DependencyObject) is not null
            || _floatingEditorPalette.Parent is not IInputElement host)
        {
            return;
        }

        _floatingPaletteDragging = true;
        _floatingPalettePointerStart = e.GetPosition(host);
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

        double width = _floatingEditorPalette.ActualWidth > 0 ? _floatingEditorPalette.ActualWidth : 190;
        double height = _floatingEditorPalette.ActualHeight > 0 ? _floatingEditorPalette.ActualHeight : 46;
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