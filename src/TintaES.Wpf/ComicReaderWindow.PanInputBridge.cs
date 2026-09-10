using System.Windows.Input;
using TintaES.Core;

namespace TintaES.Wpf;

public sealed partial class ComicReaderWindow
{
    // ScrollViewer_PreviewMouseDown sigue siendo responsable del paneo. Si recibe un clic sobre
    // un bocadillo, lo entrega a la única implementación de tarjeta, que siempre la posiciona.
    // No existe aquí lógica de presentación ni un segundo estado visual.
    private void BeginMouseTranslationHold(ComicRegion region) =>
        BeginMouseTranslationHold(region, Mouse.GetPosition(_viewerHost));
}
