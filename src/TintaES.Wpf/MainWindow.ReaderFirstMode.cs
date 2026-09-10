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

    private void InstallReaderFirstMode()
    {
        Title = $"Tinta ES · Traductor local de cómics · {CurrentUiBuildStamp}";
        AnalyzeButton.Content = "✦  Detectar y traducir";

        if (!ReaderFirstModeEnabled)
        {
            AnalyzeButton.ToolTip =
                "Detectar el texto original, borrarlo y colocar únicamente la traducción española";
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
}
