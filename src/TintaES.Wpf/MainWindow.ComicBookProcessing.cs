using System.Windows;

namespace TintaES.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// El botón principal obedece siempre a los checkbox del selector izquierdo. Los proyectos
    /// .tinta ejecutan una revisión rápida de las páginas marcadas; un cómic nuevo detecta y
    /// traduce únicamente la selección que todavía no contiene texto guardado.
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

        int[] selected = OrderSelectedPagesFromCurrent(
            GetSelectedComicPageIndices()
                .Where(index => index >= 0 && index < _comicPages.Count));
        if (selected.Length == 0)
        {
            SetFooterStatus("Marca al menos una página en la columna izquierda.", "#C99A35");
            return;
        }

        // Al abrir o guardar un proyecto .tinta, el trabajo de detección ya pertenece al
        // proyecto editable. El botón repasa solo las traducciones marcadas y nunca relanza OCR.
        if (!string.IsNullOrWhiteSpace(_currentProjectPath)
            || SelectedPagesCanBeReviewed(selected))
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
                "Las páginas marcadas no necesitan detección. Selecciona solo páginas con texto para repasarlas.",
                "#C99A35");
            return;
        }

        _ = AnalyzeSelectedComicPagesReliablyAsync(pendingDetection, model);
    }

    /// <summary>
    /// Mantiene el conjunto decidido por los checkbox, pero hace que el trabajo empiece en la
    /// página que el usuario está viendo y continúe hacia delante. Al llegar al final, envuelve
    /// al principio del cómic. Si la página visible no está marcada, comienza en la siguiente
    /// página marcada posterior.
    /// </summary>
    private int[] OrderSelectedPagesFromCurrent(IEnumerable<int> indices)
    {
        int[] selected = indices
            .Where(index => index >= 0 && index < _comicPages.Count)
            .Distinct()
            .ToArray();
        if (selected.Length <= 1 || _comicPages.Count == 0)
        {
            return selected;
        }

        int startIndex = _comicPageIndex >= 0 && _comicPageIndex < _comicPages.Count
            ? _comicPageIndex
            : 0;
        return selected
            .OrderBy(index => (index - startIndex + _comicPages.Count) % _comicPages.Count)
            .ToArray();
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
