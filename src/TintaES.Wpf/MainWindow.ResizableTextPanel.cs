using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TintaES.Wpf;

/// <summary>
/// Hace redimensionable el panel derecho del editor. El usuario puede decidir cuánto ancho
/// dedica a las traducciones y cuánto alto dedica a la lista de tarjetas frente al editor.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ResizableTextPanelRegistered = RegisterResizableTextPanel();
    private bool _resizableTextPanelInstalled;

    private static bool RegisterResizableTextPanel()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ResizableTextPanelLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ResizableTextPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.InstallResizableTextPanel();
        }
    }

    private void InstallResizableTextPanel()
    {
        if (_resizableTextPanelInstalled || RegionListBox is null)
        {
            return;
        }

        if (FindAncestor<Border>(RegionListBox) is not { } rightBorder
            || rightBorder.Parent is not Grid mainContentGrid
            || rightBorder.Child is not Grid rightGrid
            || mainContentGrid.ColumnDefinitions.Count < 2
            || rightGrid.RowDefinitions.Count < 3)
        {
            return;
        }

        _resizableTextPanelInstalled = true;

        // La columna original estaba clavada a 390 px. Conservamos un tamaño inicial cómodo,
        // pero desde aquí el usuario puede arrastrar libremente el borde izquierdo.
        ColumnDefinition editorColumn = mainContentGrid.ColumnDefinitions[0];
        ColumnDefinition textColumn = mainContentGrid.ColumnDefinitions[1];
        editorColumn.MinWidth = 520;
        textColumn.Width = new GridLength(470);
        textColumn.MinWidth = 315;
        textColumn.MaxWidth = 760;

        var widthSplitter = new GridSplitter
        {
            Width = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeWE,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndCurrent,
            ShowsPreview = false,
            ToolTip = "Arrastra para cambiar el ancho del panel de textos"
        };
        Grid.SetColumn(widthSplitter, 1);
        Grid.SetRowSpan(widthSplitter, Math.Max(1, mainContentGrid.RowDefinitions.Count));
        Panel.SetZIndex(widthSplitter, 5000);
        mainContentGrid.Children.Add(widthSplitter);

        // La lista de tarjetas deja de tener una altura rígida de 190 px. Añadimos un divisor
        // horizontal para repartir a voluntad el espacio entre la lista y el editor de la zona.
        rightGrid.RowDefinitions[1].Height = new GridLength(255);
        rightGrid.RowDefinitions[1].MinHeight = 120;
        rightGrid.RowDefinitions[2].MinHeight = 180;
        rightGrid.RowDefinitions.Insert(2, new RowDefinition { Height = new GridLength(7) });

        UIElement[] existingChildren = rightGrid.Children.Cast<UIElement>().ToArray();
        foreach (UIElement child in existingChildren)
        {
            if (Grid.GetRow(child) == 2)
            {
                Grid.SetRow(child, 3);
            }
        }

        var heightSplitter = new GridSplitter
        {
            Height = 7,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(75, 125, 132, 138)),
            Cursor = Cursors.SizeNS,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ShowsPreview = false,
            ToolTip = "Arrastra para cambiar la altura de la lista de textos"
        };
        Grid.SetRow(heightSplitter, 2);
        Panel.SetZIndex(heightSplitter, 5000);
        rightGrid.Children.Add(heightSplitter);

        // Un poco más de aire en cada tarjeta para que los textos largos sean más legibles.
        RegionListBox.FontSize = Math.Max(RegionListBox.FontSize, 13);
        if (RegionListBox.ItemContainerStyle is { } currentStyle)
        {
            var roomierStyle = new Style(typeof(ListBoxItem), currentStyle);
            roomierStyle.Setters.Add(new Setter(MinHeightProperty, 62d));
            RegionListBox.ItemContainerStyle = roomierStyle;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is T match)
            {
                return match;
            }
        }
        return null;
    }
}
