using System.Windows;

namespace TintaES.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// El botón principal ejecuta siempre el pipeline completo sobre los checkbox seleccionados:
    /// detección, OCR y traducción. Repasar traducción tiene su propio botón y nunca sustituye
    /// esta acción, incluso cuando el documento ya contiene trabajo traducido.
    /// </summary>
    private async void AnalyzeComicButton_Click(object sender, RoutedEventArgs e)
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

        // Se copia el estado real de los checkbox una sola vez. A partir de aquí el lote trabaja
        // con esta instantánea inmutable y ningún refresco posterior puede ampliarlo al cómic entero.
        int[] selected = OrderSelectedPagesFromCurrent(CaptureCheckedComicPageIndices());
        if (selected.Length == 0)
        {
            SetFooterStatus("Marca al menos una página en la columna izquierda.", "#C99A35");
            return;
        }

        SetFooterStatus(
            $"Selección fijada · {selected.Length} de {_comicPages.Count} páginas: " +
            FormatCheckedPageScope(selected),
            "#4CB2BB");

        bool replacesExistingWork = selected.Any(index =>
            _comicPages[index].Processed
            || _comicPages[index].Regions.Count > 0
            || PageHasReviewableText(_comicPages[index]));

        if (replacesExistingWork)
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                $"Se volverán a detectar, leer y traducir desde cero {selected.Length} página(s) marcada(s).\n\n" +
                "Las páginas que ya estaban traducidas conservarán su versión anterior si el nuevo " +
                "análisis falla o se cancela.\n\n¿Continuar?",
                "Detectar y traducir",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                SetFooterStatus("Detección y traducción canceladas antes de comenzar.", "#C99A35");
                return;
            }
        }

        if (!await EnsureComicResearchContextAsync())
        {
            return;
        }

        if (replacesExistingWork)
        {
            await RetranslateSelectedPagesFromScratchAsync(selected, model);
            return;
        }

        await AnalyzeSelectedComicPagesReliablyAsync(selected, model);
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
