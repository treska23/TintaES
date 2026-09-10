using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Orquesta la traducción de página. TranslateGemma usa exclusivamente la ruta anclada al OCR:
/// el contexto ayuda a interpretar texto que sí existe, pero nunca puede fabricar un bocadillo.
/// Los demás modelos conservan la ruta histórica.
/// </summary>
public partial class MainWindow
{
    private async Task TranslatePageRegionsFastAsync(
        IReadOnlyList<ComicRegion> targets,
        IReadOnlyList<ComicRegion> fullContext,
        string model,
        CancellationToken cancellationToken,
        IProgress<AnalysisProgress>? progress)
    {
        if (!model.StartsWith("translategemma", StringComparison.OrdinalIgnoreCase))
        {
            await _ollama.TranslateRegionsAsync(targets, model, cancellationToken, progress);
            return;
        }

        bool recoveringSubset = targets.Count < fullContext.Count;
        if (!recoveringSubset)
        {
            // PaddleOCR ya ha entregado sus lecturas. Se libera antes de cargar TranslateGemma
            // para no hacer competir ambos modelos por VRAM.
            await PaddleOcrResidentControl.ReleaseAsync();
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _ollama.TranslateGroundedRegionsAsync(
            targets,
            fullContext,
            model,
            cancellationToken,
            progress);
    }
}
