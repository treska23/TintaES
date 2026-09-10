using System.Windows;

namespace TintaES.Wpf;

/// <summary>
/// El cargador rápido de páginas reconstruye las capas de texto mientras la página sigue en modo
/// "original". Ese modo deja OverlayCanvas oculto, por lo que las cajas existen pero no se ven.
/// Restauramos una sola vez el modo resultado al terminar una navegación de una página procesada,
/// sin impedir que el usuario cambie después manualmente a Original/Máscara/Limpio.
/// </summary>
public partial class MainWindow
{
    private static readonly bool EditorOverlayVisibilityFixRegistered =
        RegisterEditorOverlayVisibilityFix();

    private bool _editorOverlayVisibilityFixInstalled;
    private bool _editorOverlaySawNavigationBusy;

    private static bool RegisterEditorOverlayVisibilityFix()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_EditorOverlayVisibilityLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_EditorOverlayVisibilityLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || window._editorOverlayVisibilityFixInstalled)
        {
            return;
        }

        window._editorOverlayVisibilityFixInstalled = true;
        window.LayoutUpdated += window.MainWindow_EditorOverlayVisibilityLayoutUpdated;
    }

    private void MainWindow_EditorOverlayVisibilityLayoutUpdated(object? sender, EventArgs e)
    {
        if (ReaderFirstModeEnabled)
        {
            return;
        }

        if (_pageNavigationBusy)
        {
            _editorOverlaySawNavigationBusy = true;
            return;
        }

        if (!_editorOverlaySawNavigationBusy)
        {
            return;
        }
        _editorOverlaySawNavigationBusy = false;

        if (_comicPageIndex < 0 || _comicPageIndex >= _comicPages.Count)
        {
            return;
        }

        ComicBookPageState page = _comicPages[_comicPageIndex];
        if (!page.Processed || _cleanedBitmap is null)
        {
            return;
        }

        // ApplyComicPageAsync ha terminado. A partir de aquí la vista editable debe enseñar
        // el fondo limpio más las traducciones, no dejar las capas escondidas en modo Original.
        ShowPreviewMode("result");
        RebuildOverlay();
    }
}
