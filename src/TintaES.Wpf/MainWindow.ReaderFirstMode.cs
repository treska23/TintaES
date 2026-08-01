using System.Windows;

namespace TintaES.Wpf;

/// <summary>
/// La imagen ya no es un lienzo de sustitución. El área principal sirve para revisar la
/// detección y corregir traducciones; la lectura se realiza sobre la página original.
/// </summary>
public partial class MainWindow
{
    private const bool ReaderFirstModeEnabled = true;

    private void InstallReaderFirstMode()
    {
        Title = $"Tinta ES · Lector y traductor local de cómics · {CurrentUiBuildStamp}";
        AnalyzeButton.Content = "✦  Detectar y traducir";
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
