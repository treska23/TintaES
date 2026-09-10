using System.IO;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private readonly object _preparedPageArtifactsLock = new();
    private readonly Dictionary<int, PreparedPageArtifacts> _preparedPageArtifacts = [];

    private async Task<ComicAnalysis> TakePreparedComicPageAsync(
        PreparedPageWindow<ComicAnalysis> preparation,
        IReadOnlyList<int> pageIndices,
        int position,
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            return await preparation.TakeAsync(position, async (preparationPosition, token) =>
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
        }
        finally
        {
            // PaddleOCR mantiene un worker residente para preparar varias páginas de una ventana.
            // Antes de cargar TranslateGemma hay que liberarlo: si ambos modelos quedan en VRAM,
            // Ollama puede descargar parte de TranslateGemma a CPU y una traducción de segundos
            // pasa a tardar varios minutos.
            await PaddleOcrResidentControl.ReleaseAsync();
        }
    }

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

        int pageIndex = ResolveComicPageIndex(sourcePath);
        if (pageIndex >= 0)
        {
            await StagePreparedPageArtifactsAsync(
                pageIndex,
                organic.CleanedBitmap,
                organic.MaskBitmap,
                cancellationToken);
        }

        // Las imágenes reconstruidas se escriben en el workspace y no se mantienen en memoria
        // mientras TranslateGemma procesa la ventana. Al cerrar la página se enlazan al estado
        // y el autoguardado las copia al .tinta principal.
        return new ComicAnalysis(organic.Analysis.SourceLanguage, readableCandidates);
    }

    private int ResolveComicPageIndex(string sourcePath)
    {
        for (int index = 0; index < _comicPages.Count; index++)
        {
            if (string.Equals(
                    _comicPages[index].SourcePath,
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private async Task StagePreparedPageArtifactsAsync(
        int pageIndex,
        System.Windows.Media.Imaging.BitmapSource cleaned,
        System.Windows.Media.Imaging.BitmapSource mask,
        CancellationToken cancellationToken)
    {
        string workspace = _comicWorkspace
                           ?? Path.Combine(Path.GetTempPath(), "TintaES", "prepared-pages");
        string stagingDirectory = Path.Combine(workspace, "processed", ".prepared");
        Directory.CreateDirectory(stagingDirectory);

        string stamp = $"{pageIndex + 1:D4}-{Guid.NewGuid():N}";
        string cleanedPath = Path.Combine(stagingDirectory, $"{stamp}-clean.png");
        string maskPath = Path.Combine(stagingDirectory, $"{stamp}-mask.png");

        try
        {
            await Task.WhenAll(
                Task.Run(() => SaveBitmapAtomicallyFast(cleaned, cleanedPath), cancellationToken),
                Task.Run(() => SaveBitmapAtomicallyFast(mask, maskPath), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            DeleteFileQuietly(cleanedPath);
            DeleteFileQuietly(maskPath);
            throw;
        }

        lock (_preparedPageArtifactsLock)
        {
            // No se elimina aquí el artefacto anterior: puede ser todavía el fondo válido de
            // una retraducción previa y debe sobrevivir si el nuevo intento termina fallando.
            _preparedPageArtifacts[pageIndex] = new PreparedPageArtifacts(cleanedPath, maskPath);
        }
    }

    private void CommitPreparedComicPageArtifacts(int pageIndex)
    {
        PreparedPageArtifacts? artifacts;
        lock (_preparedPageArtifactsLock)
        {
            if (!_preparedPageArtifacts.Remove(pageIndex, out artifacts))
            {
                return;
            }
        }

        if (!File.Exists(artifacts.CleanedPath))
        {
            throw new FileNotFoundException(
                "El fondo limpio preparado para la página ha desaparecido.",
                artifacts.CleanedPath);
        }
        if (!File.Exists(artifacts.MaskPath))
        {
            throw new FileNotFoundException(
                "La máscara preparada para la página ha desaparecido.",
                artifacts.MaskPath);
        }

        ComicBookPageState page = _comicPages[pageIndex];
        page.CleanedPath = artifacts.CleanedPath;
        page.MaskPath = artifacts.MaskPath;
    }

    private sealed record PreparedPageArtifacts(string CleanedPath, string MaskPath);
}
