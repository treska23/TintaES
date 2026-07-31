using System.Windows;

namespace TintaES.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// Punto de entrada heredado del botón principal. La lógica real del lote vive únicamente
    /// en AnalyzeSelectedComicPagesReliablyAsync.
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

        int[] pending = _comicPages
            .Select((page, index) => (page, index))
            .Where(item => !item.page.SuppressBatchProcessing && PageNeedsTranslation(item.page))
            .Select(item => item.index)
            .ToArray();

        if (pending.Length == 0)
        {
            SetFooterStatus("Todas las páginas del cómic ya están procesadas.", "#58A77D");
            return;
        }

        _ = AnalyzeSelectedComicPagesReliablyAsync(pending, model);
    }
}
