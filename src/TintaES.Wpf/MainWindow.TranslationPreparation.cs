using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private Task<ComicAnalysis> TakePreparedComicPageAsync(
        PreparedPageWindow<ComicAnalysis> preparation,
        IReadOnlyList<int> pageIndices,
        int position,
        string model,
        CancellationToken cancellationToken) =>
        preparation.TakeAsync(position, async (preparationPosition, token) =>
        {
            int pageIndex = pageIndices[preparationPosition];
            int humanPage = pageIndex + 1;
            // La barra cuenta páginas terminadas. Preparar páginas futuras no debe
            // adelantarla hasta ellas y hacerla retroceder al empezar su traducción.
            BusyProgressBar.Value = position / (double)pageIndices.Count * 100;
            FooterProgressBar.Value = BusyProgressBar.Value;
            BusyTitleText.Text = $"Página {humanPage}/{_comicPages.Count} · preparando el texto…";
            FooterStatusText.Text = $"{position}/{pageIndices.Count} páginas terminadas · preparando página {humanPage}";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            // Inmediato: ningún callback pendiente de una página preparada puede
            // sobrescribir el estado de la página que ya se está traduciendo.
            var progress = new ImmediateProgress<AnalysisProgress>(value =>
                Dispatcher.Invoke(() =>
                {
                    BusyTitleText.Text = $"Página {humanPage}/{_comicPages.Count} · {value.Message}";
                    FooterStatusText.Text =
                        $"{position}/{pageIndices.Count} páginas terminadas · página {humanPage}: {value.Message}";
                }));
            return await PrepareComicPageAnalysisAsync(
                _comicPages[pageIndex].SourcePath, model, progress, token);
        }, cancellationToken);

    private async Task<ComicAnalysis> PrepareComicPageAnalysisAsync(
        string sourcePath,
        string model,
        IProgress<AnalysisProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!await _organicEngine.HasReusableAnalysisAsync(sourcePath, cancellationToken))
        {
            await _ollama.UnloadModelAsync(model, cancellationToken);
        }

        OrganicAnalysisResult organic = await AnalyzePageWithWatchdogAsync(
            sourcePath, progress, cancellationToken);
        ComicRegion[] readableCandidates = organic.Analysis.Regions
            .Where(IsReadableLetteringCandidate)
            .ToArray();
        if (readableCandidates.Length == 0)
        {
            throw new InvalidOperationException(
                "No se ha detectado ningún texto pulsable. La página queda pendiente para poder reintentarla.");
        }

        // Se conservan los objetos originales y toda la evidencia OCR. Los bitmaps
        // del análisis no se retienen mientras se traduce la ventana de páginas.
        // El documento solo se modifica al completar ProcessComicPageReliablyAsync.
        return new ComicAnalysis(organic.Analysis.SourceLanguage, readableCandidates);
    }
}
