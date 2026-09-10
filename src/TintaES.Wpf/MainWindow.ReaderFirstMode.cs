using System.Windows;

namespace TintaES.Wpf;

/// <summary>
/// El resultado normal de Tinta ES sustituye la rotulación original: fondo limpio más español.
/// El antiguo modo lector, que conservaba el inglés en la página y enseñaba la traducción aparte,
/// queda desactivado.
/// </summary>
public partial class MainWindow
{
    private const bool ReaderFirstModeEnabled = false;
    private bool _translatedResultModeInstalled;
    private int _lastTranslatedResultPageIndex = -1;
    private string? _lastTranslatedResultCleanedPath;

    private void InstallReaderFirstMode()
    {
        Title = $"Tinta ES · Traductor local de cómics · {CurrentUiBuildStamp}";
        AnalyzeButton.Content = "✦  Detectar y traducir";

        if (!ReaderFirstModeEnabled)
        {
            AnalyzeButton.ToolTip =
                "Detectar el texto original, borrarlo y colocar únicamente la traducción española";
            InstallTranslatedResultMode();
            return;
        }

        AnalyzeButton.ToolTip = "Detectar y traducir todos los textos sin modificar la página";
        InstallDirectReaderInput();
        InstallMainTranslationInteraction();

        OriginalPreviewButton.Visibility = Visibility.Collapsed;
        MaskPreviewButton.Visibility = Visibility.Collapsed;
        CleanPreviewButton.Visibility = Visibility.Collapsed;
        ResultPreviewButton.Visibility = Visibility.Collapsed;
        AddRegionButton.Visibility = Visibility.Collapsed;
        ExportButton.Visibility = Visibility.Collapsed;
        if (_exportComicButton is not null) _exportComicButton.Visibility = Visibility.Collapsed;
        if (_exportPsdButton is not null) _exportPsdButton.Visibility = Visibility.Collapsed;

        if (_comicReaderButton is not null)
        {
            _comicReaderButton.Content = "Leer cómic";
            _comicReaderButton.Style = FindResource("AccentButton") as Style;
            _comicReaderButton.ToolTip = "Leer a pantalla completa y pulsar cualquier texto para traducirlo";
        }

        if (_originalBitmap is not null)
        {
            ShowPreviewMode("original");
        }
    }

    private void InstallTranslatedResultMode()
    {
        if (_translatedResultModeInstalled)
        {
            return;
        }

        _translatedResultModeInstalled = true;
        LayoutUpdated += MainWindow_TranslatedResultLayoutUpdated;
    }

    private void MainWindow_TranslatedResultLayoutUpdated(object? sender, EventArgs e)
    {
        if (ReaderFirstModeEnabled
            || _comicBatchBusy
            || _pageNavigationBusy
            || _comicPageIndex < 0
            || _comicPageIndex >= _comicPages.Count)
        {
            return;
        }

        ComicBookPageState page = _comicPages[_comicPageIndex];
        if (!page.Processed
            || string.IsNullOrWhiteSpace(page.CleanedPath)
            || _cleanedBitmap is null)
        {
            return;
        }

        bool alreadyApplied = _lastTranslatedResultPageIndex == _comicPageIndex
                              && string.Equals(
                                  _lastTranslatedResultCleanedPath,
                                  page.CleanedPath,
                                  StringComparison.OrdinalIgnoreCase);
        if (alreadyApplied)
        {
            return;
        }

        _lastTranslatedResultPageIndex = _comicPageIndex;
        _lastTranslatedResultCleanedPath = page.CleanedPath;
        ShowPreviewMode("result");
        RebuildOverlay();
    }
}
