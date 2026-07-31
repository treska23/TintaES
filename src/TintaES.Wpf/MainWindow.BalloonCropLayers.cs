using System.Windows.Media.Imaging;

namespace TintaES.Wpf;

/// <summary>
/// Fuente estable para las capas locales de bocadillo. La vista ya no modifica RenderBox ni
/// reconstruye el lienzo al resolver un recorte; el recorte es un dato de render inmutable.
/// </summary>
public partial class MainWindow
{
    internal BitmapSource? CurrentBalloonSourceBitmap =>
        _cleanedBaseBitmap ?? _cleanedBitmap ?? _originalBitmap;
}
