using System.Windows;
using System.Windows.Controls;

namespace TintaES.Wpf;

/// <summary>
/// El Reader se usa también en tablets y pantallas giradas. La barra original era una fila fija
/// de más de 900 DIPs y la ventana imponía 760 DIPs de ancho mínimo, por lo que en vertical los
/// últimos botones quedaban literalmente fuera de la pantalla. El ejecutable ligero sustituye
/// esa fila por un WrapPanel y admite ventanas estrechas sin tocar el visor del programa madre.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private bool _standaloneResponsiveLayoutInstalled;

    internal void EnsureStandaloneResponsiveLayoutInstalled()
    {
        if (_standaloneResponsiveLayoutInstalled)
        {
            return;
        }

        _standaloneResponsiveLayoutInstalled = true;
        MinWidth = 360;
        MinHeight = 420;

        if (_readerToolbar is null
            || _readerToolbar.Children.OfType<StackPanel>().FirstOrDefault() is not { } oldRow)
        {
            return;
        }

        var wrapped = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 6, 8, 6)
        };

        UIElement[] controls = oldRow.Children.Cast<UIElement>().ToArray();
        oldRow.Children.Clear();
        foreach (UIElement control in controls)
        {
            wrapped.Children.Add(control);
        }

        _readerToolbar.Children.Remove(oldRow);
        _readerToolbar.Children.Add(wrapped);
        _readerToolbar.Height = double.NaN;
        _readerToolbar.MinHeight = 54;

        // Al girar la pantalla, WPF recalcula automáticamente las filas del WrapPanel. Cuando el
        // lector no está en pantalla completa, reajustamos después la página al área restante.
        SizeChanged += (_, _) =>
        {
            if (!_isFullscreen && _pageImage.Source is not null)
            {
                Dispatcher.BeginInvoke(
                    () => FitToViewport(_fitMode == ReaderFitMode.None
                        ? ReaderFitMode.Page
                        : _fitMode),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };
    }
}
