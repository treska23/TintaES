using System.Windows;

namespace TintaES.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// El botón principal obedece siempre a los checkbox del selector izquierdo. Cuando todas
    /// las páginas marcadas ya conservan texto detectado, ejecuta solo la revisión lingüística;
    /// si alguna todavía no tiene zonas, procesa únicamente esas páginas seleccionadas.
    /// </summary>
    private void AnalyzeComicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0)
        {
            AnalyzeButton_Click(sender, e);
            return;
        }

        if (ModelComboBox.SelectedValue is not string model || string.IsNullOrWhiteSpace(model))
        {
            SetFooterStatus("Selecciona un modelo de traducción antes de continuar.", "#C99A35");
            return;
        }

        int[] selected = GetSelectedComicPageIndices()
            .Where(index => index >= 0 && index < _comicPages.Count)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
        if (selected.Length == 0)
        {
            SetFooterStatus("Marca al menos una página en la columna izquierda.", "#C99A35");
            return;
        }

        if (SelectedPagesCanBeReviewed(selected))
        {
            _ = ReviewSelectedTranslationsAsync(selected, model);
            return;
        }

        int[] pendingDetection = selected
            .Where(index => !_comicPages[index].SuppressBatchProcessing)
            .Where(index => !PageHasReviewableText(_comicPages[index]))
            .Where(index => PageNeedsTranslation(_comicPages[index]))
            .ToArray();

        if (pendingDetection.Length == 0)
        {
            SetFooterStatus(
                "Las páginas marcadas no necesitan detección. Selecciona solo páginas con texto para revisarlas.",
                "#C99A35");
            return;
        }

        _ = AnalyzeSelectedComicPagesReliablyAsync(pendingDetection, model);
    }

    private static bool PageNeedsTranslation(ComicBookPageState page) =>
        !page.Processed
        || page.Regions.Count == 0
        || page.Regions.Any(region =>
            !region.IsEnabled
            && region.Confidence >= 0.05
            && !string.IsNullOrWhiteSpace(region.Original))
        || page.Regions.Any(region => region.IsEnabled && !region.HasRenderableTranslation);

    /// <summary>
    /// Nombre heredado que todavía desconectan algunos instaladores. Toda exportación CBZ pasa
    /// directamente por la implementación robusta; no existe ya una segunda ruta de exportación.
    /// </summary>
    private void ExportComicButton_Click(object sender, RoutedEventArgs e) =>
        ExportComicButton_Click_Robust(sender, e);
}
