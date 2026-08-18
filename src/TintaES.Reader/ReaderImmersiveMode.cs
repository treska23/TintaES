using System.Windows;

namespace TintaES.Wpf;

/// <summary>
/// Comportamiento exclusivo del ejecutable ligero: la biblioteca sirve para elegir el cómic,
/// pero desaparece en cuanto empieza la lectura. La página pasa a ser la interfaz principal.
/// </summary>
public sealed partial class ComicReaderWindow
{
    partial void OnStandaloneReaderContentOpened()
    {
        if (_libraryInstalled && _libraryVisible)
        {
            ToggleLibraryPanel();
        }

        // El lector independiente no reserva márgenes decorativos alrededor de la página.
        _scrollViewer.Padding = new Thickness(0);

        if (!_isFullscreen)
        {
            ToggleFullscreen();
        }
    }
}
