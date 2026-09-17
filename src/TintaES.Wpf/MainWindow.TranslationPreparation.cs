using System.IO;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private readonly object _preparedPageArtifactsLock = new();
    private readonly Dictionary<int, PreparedPageArtifacts> _preparedPageArtifacts = [];

    private async Task<IReadOnlyList<PreparedComicPageWorkItem>> TakePreparedComicPageWindowAsync(
        PreparedPageWindow<ComicAnalysis> preparation,
        IReadOnlyList<int> pageIndices,
        int completedPages,
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<PreparedPageWindowItem<ComicAnalysis>> scheduled =
                await preparation.TakeNextWindowAsync(
                    async (preparationPosition, token) =>
                    {
                        int pageIndex = pageIndices[preparationPosition];
                        int humanPage = pageIndex + 1;
                        // Preparar páginas futuras no cuenta como finalizarlas ni puede hacer
                        // retroceder una barra que ya avanzó por otra página de la ventana.
                        double floor = completedPages / (double)pageIndices.Count * 100;
                        BusyProgressBar.Value = Math.Max(BusyProgressBar.Value, floor);
                        FooterProgressBar.Value = Math.Max(FooterProgressBar.Value, floor);
                        BusyTitleText.Text =
                            $"Página {humanPage}/{_comicPages.Count} · preparando el texto…";
                        FooterStatusText.Text =
                            $"{completedPages}/{pageIndices.Count} páginas terminadas · " +
                            $"preparando página {humanPage}";
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                        var progress = new ImmediateProgress<AnalysisProgress>(value =>
                            Dispatcher.Invoke(() =>
                            {
                                BusyTitleText.Text =
                                    $"Página {humanPage}/{_comicPages.Count} · {value.Message}";
                                FooterStatusText.Text =
                                    $"{completedPages}/{pageIndices.Count} páginas terminadas · " +
                                    $"página {humanPage}: {value.Message}";
                            }));
                        return await PrepareComicPageAnalysisAsync(
                            pageIndex, model, progress, token);
                    },
                    EstimateComicTranslationCost,
                    cancellationToken);

            return scheduled
                .Select(item => new PreparedComicPageWorkItem(
                    item.Position,
                    pageIndices[item.Position],
                    item.Result,
                    item.Error,
                    item.Cost))
                .ToArray();
        }
        finally
        {
            // Toda la ventana comparte PaddleOCR. Se libera una sola vez antes de cargar
            // TranslateGemma y consumir sus páginas por prioridad.
            await PaddleOcrResidentControl.ReleaseAsync();
        }
    }

    private static long EstimateComicTranslationCost(ComicAnalysis analysis)
    {
        ComicRegion[] regions = analysis.Regions
            .Where(region => region.IsEnabled)
            .ToArray();
        long contextCharacters = regions.Sum(region =>
            (long)(region.Original?.Trim().Length ?? 0)
            + region.StoredOcrAlternatives
                .Take(3)
                .Sum(alternative => (long)(alternative?.Trim().Length ?? 0))
            + 32L);
        int chunkSize = contextCharacters switch
        {
            <= 6_500 => 30,
            <= 9_000 => 24,
            <= 12_500 => 18,
            <= 17_000 => 12,
            _ => 8
        };
        long chunks = Math.Max(1, (regions.Length + chunkSize - 1L) / chunkSize);
        long uncertainRegions = regions.Count(region => region.Confidence < 0.70);
        long alternatives = regions.Sum(region => (long)Math.Min(3, region.StoredOcrAlternatives.Count));
        long layoutComplexity = regions.Count(region => region.Vertical || Math.Abs(region.Rotation) >= 8);

        // Los bloques dominan el coste; caracteres, incertidumbre y geometría solo ordenan
        // páginas comparables. Saturar evita que datos corruptos desborden la puntuación.
        const long MaximumCost = long.MaxValue - 1;
        try
        {
            return checked(
                chunks * 1_000_000L
                + contextCharacters * 100L
                + regions.Length * 10_000L
                + uncertainRegions * 2_000L
                + alternatives * 1_000L
                + layoutComplexity * 500L);
        }
        catch (OverflowException)
        {
            return MaximumCost;
        }
    }

    private async Task<ComicAnalysis> PrepareComicPageAnalysisAsync(
        int pageIndex,
        string model,
        IProgress<AnalysisProgress> progress,
        CancellationToken cancellationToken)
    {
        if (pageIndex < 0 || pageIndex >= _comicPages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        string sourcePath = _comicPages[pageIndex].SourcePath;
        if (!await _organicEngine.HasReusableAnalysisAsync(sourcePath, cancellationToken))
        {
            await _ollama.UnloadModelAsync(model, cancellationToken);
        }

        OrganicAnalysisResult organic = await AnalyzePageWithWatchdogAsync(
            sourcePath, progress, cancellationToken);

        ComicRegion[] detectorCandidates = organic.Analysis.Regions
            .Where(IsReadableLetteringCandidate)
            .ToArray();
        var readableCandidates = new List<ComicRegion>(detectorCandidates.Length);
        foreach (ComicRegion candidate in detectorCandidates)
        {
            // Si la lectura principal es ruido pero Paddle/otra pasada dejó una alternativa OCR
            // real y legible, esa alternativa se convierte en Original. Las alternativas que
            // también sean ruido se eliminan antes de construir TARGETS y CONTEXT.
            if (TranslationSourceGuard.NormalizeEvidence(candidate))
            {
                readableCandidates.Add(candidate);
            }
        }

        int rejectedNoise = detectorCandidates.Length - readableCandidates.Count;
        if (rejectedNoise > 0)
        {
            progress.Report(new AnalysisProgress(
                99,
                100,
                $"Descartadas {rejectedNoise} lectura(s) OCR sin evidencia textual suficiente; no se inventará diálogo."));
        }

        if (readableCandidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No se ha detectado ningún texto legible. Las lecturas OCR dudosas se descartan en vez de inventar diálogo.");
        }

        await StagePreparedPageArtifactsAsync(
            pageIndex,
            organic.CleanedBitmap,
            organic.MaskBitmap,
            cancellationToken);

        // El análisis textual sigue siendo ligero. El fondo limpio y la máscara quedan en
        // ficheros temporales hasta que la página completa termina; así no se conserva una
        // colección de bitmaps enormes mientras TranslateGemma procesa varias páginas.
        return new ComicAnalysis(organic.Analysis.SourceLanguage, readableCandidates);
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

        PreparedPageArtifacts? previous = null;
        lock (_preparedPageArtifactsLock)
        {
            _preparedPageArtifacts.Remove(pageIndex, out previous);
            _preparedPageArtifacts[pageIndex] = new PreparedPageArtifacts(cleanedPath, maskPath);
        }

        if (previous is not null)
        {
            DeleteFileQuietly(previous.CleanedPath);
            DeleteFileQuietly(previous.MaskPath);
        }
    }

    private void CommitPreparedComicPageArtifacts(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _comicPages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        PreparedPageArtifacts? artifacts;
        lock (_preparedPageArtifactsLock)
        {
            if (!_preparedPageArtifacts.Remove(pageIndex, out artifacts))
            {
                return;
            }
        }

        try
        {
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
        }
        catch
        {
            DeleteFileQuietly(artifacts.CleanedPath);
            DeleteFileQuietly(artifacts.MaskPath);
            throw;
        }

        ComicBookPageState page = _comicPages[pageIndex];
        page.CleanedPath = artifacts.CleanedPath;
        page.MaskPath = artifacts.MaskPath;
    }

    private void DiscardPreparedComicPageArtifacts(int pageIndex)
    {
        PreparedPageArtifacts? artifacts;
        lock (_preparedPageArtifactsLock)
        {
            _preparedPageArtifacts.Remove(pageIndex, out artifacts);
        }

        if (artifacts is null)
        {
            return;
        }

        DeleteFileQuietly(artifacts.CleanedPath);
        DeleteFileQuietly(artifacts.MaskPath);
    }

    private void DiscardAllPreparedComicPageArtifacts()
    {
        PreparedPageArtifacts[] artifacts;
        lock (_preparedPageArtifactsLock)
        {
            artifacts = _preparedPageArtifacts.Values.ToArray();
            _preparedPageArtifacts.Clear();
        }

        foreach (PreparedPageArtifacts prepared in artifacts)
        {
            DeleteFileQuietly(prepared.CleanedPath);
            DeleteFileQuietly(prepared.MaskPath);
        }
    }

    private sealed record PreparedPageArtifacts(string CleanedPath, string MaskPath);
}
