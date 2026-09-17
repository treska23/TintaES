using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Procesa únicamente las páginas seleccionadas, conserva las traducciones válidas y traduce
/// solo texto asociado a un bocadillo, pensamiento o cartucho verificado.
/// </summary>
public partial class MainWindow
{
    private const int ComicPageAutomaticAttempts = 2;
    private const int ComicTranslationAutomaticAttempts = 3;

    private async Task AnalyzeSelectedComicPagesReliablyAsync(
        IReadOnlyList<int> selectedIndices,
        string model)
    {
        if (_comicBatchBusy || _comicPages.Count == 0)
        {
            return;
        }

        PersistVisibleComicPageRegions();
        _visibleComicPageIndex = -1;

        int[] pending = selectedIndices
            .Where(index => index >= 0 && index < _comicPages.Count)
            .Distinct()
            .Where(index => PageNeedsTranslation(_comicPages[index]))
            .ToArray();

        if (pending.Length == 0)
        {
            SetFooterStatus("Las páginas seleccionadas ya están procesadas.", "#58A77D");
            return;
        }

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _analysisCancellation.Token;

        var stopwatch = Stopwatch.StartNew();
        var preparation = new PreparedPageWindow<ComicAnalysis>(pending.Length);
        var failures = new List<ComicPageFailure>();
        var partialPages = new List<ComicPagePartial>();
        var deferredRetries = new List<DeferredComicPageRetry>();
        int finalizedPages = 0;
        bool cancelled = false;

        _comicBatchBusy = true;
        SetBusy(true);
        UpdateComicControls();
        BusyProgressBar.IsIndeterminate = false;
        FooterProgressBar.IsIndeterminate = false;
        BusyProgressBar.Value = 0;
        FooterProgressBar.Value = 0;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            // La primera ventana contiene una sola página. Las siguientes se preparan juntas
            // y se consumen por coste ascendente, siempre con un único trabajador de traducción.
            while (preparation.HasRemaining)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<PreparedComicPageWorkItem> preparedWindow =
                    await TakePreparedComicPageWindowAsync(
                        preparation,
                        pending,
                        finalizedPages,
                        model,
                        cancellationToken);

                foreach (PreparedComicPageWorkItem workItem in preparedWindow)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int pageIndex = workItem.PageIndex;
                    int humanPage = pageIndex + 1;
                    ComicBookPageState page = _comicPages[pageIndex];
                    Exception? firstError = workItem.Error
                        ?? (workItem.Analysis is null
                            ? new InvalidOperationException(
                                "La preparación de la página no devolvió un análisis.")
                            : null);

                    if (firstError is null && workItem.Analysis is not null)
                    {
                        try
                        {
                            ComicAnalysis preparedAnalysis = workItem.Analysis;
                            await ProcessComicPageReliablyAsync(
                                page,
                                pageIndex,
                                humanPage,
                                finalizedPages,
                                pending.Length,
                                model,
                                cancellationToken,
                                attempt: 1,
                                _ => Task.FromResult(preparedAnalysis));
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            firstError = exception;
                        }
                    }

                    if (firstError is not null)
                    {
                        // El segundo intento se aplaza hasta que todas las demás páginas hayan
                        // tenido su primera oportunidad. Nunca se reutiliza el análisis mutado.
                        DiscardPreparedComicPageArtifacts(pageIndex);
                        deferredRetries.Add(new DeferredComicPageRetry(
                            workItem.PreparationPosition,
                            pageIndex,
                            firstError));
                        FooterStatusText.Text =
                            $"Página {humanPage} pendiente de reintento · continúa el resto del lote";
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        continue;
                    }

                    finalizedPages++;
                    RecordReliableComicPageOutcome(
                        pageIndex,
                        completed: true,
                        finalError: null,
                        finalizedPages,
                        pending.Length,
                        stopwatch,
                        failures,
                        partialPages);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                }
            }

            // Segunda y última oportunidad, fuera de la ruta crítica de las páginas sanas.
            foreach (DeferredComicPageRetry retry in deferredRetries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int pageIndex = retry.PageIndex;
                int humanPage = pageIndex + 1;
                ComicBookPageState page = _comicPages[pageIndex];
                Exception? finalError = retry.FirstError;
                bool completed = false;

                BusyTitleText.Text =
                    $"Página {humanPage}/{_comicPages.Count} · reintento final…";
                FooterStatusText.Text =
                    $"Reintentando la página {humanPage} tras completar la primera pasada…";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(700, cancellationToken);

                try
                {
                    await ProcessComicPageReliablyAsync(
                        page,
                        pageIndex,
                        humanPage,
                        finalizedPages,
                        pending.Length,
                        model,
                        cancellationToken,
                        attempt: ComicPageAutomaticAttempts);
                    completed = true;
                    finalError = null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    finalError = exception;
                    DiscardPreparedComicPageArtifacts(pageIndex);
                }

                finalizedPages++;
                RecordReliableComicPageOutcome(
                    pageIndex,
                    completed,
                    finalError,
                    finalizedPages,
                    pending.Length,
                    stopwatch,
                    failures,
                    partialPages);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            // Borra los fondos/máscaras de una ventana que quedase sin consumir al cancelar.
            // Los de las páginas terminadas ya fueron transferidos por el autoguardado.
            DiscardAllPreparedComicPageArtifacts();
            stopwatch.Stop();
            _comicBatchBusy = false;
            SetBusy(false);
            UpdateComicControls();
            SynchronizeActiveDocumentState();
        }

        if (_documentOpenPending)
        {
            return;
        }

        await ShowComicPageFastAsync(Math.Clamp(_comicPageIndex, 0, _comicPages.Count - 1));

        if (cancelled)
        {
            SetFooterStatus(
                "Traducción cancelada. Las páginas y zonas terminadas se conservan.",
                "#C99A35");
            return;
        }

        if (failures.Count == 0 && partialPages.Count == 0)
        {
            SetFooterStatus(
                $"Cómic traducido · {pending.Length} páginas · {FormatDuration(stopwatch.Elapsed.TotalSeconds)}",
                "#58A77D");
            return;
        }

        SetFooterStatus(
            $"Resultado parcial · {partialPages.Count} página(s) incompleta(s) y " +
            $"{failures.Count} sin procesar",
            "#C99A35");

        var detailLines = new List<string>();
        detailLines.AddRange(partialPages
            .OrderBy(partial => partial.PageNumber)
            .Take(12)
            .Select(partial =>
                $"Página {partial.PageNumber} · {partial.DisplayName}: " +
                $"{partial.Translated}/{partial.Total} zonas traducidas."));
        detailLines.AddRange(failures
            .OrderBy(failure => failure.PageNumber)
            .Take(Math.Max(0, 12 - detailLines.Count))
            .Select(failure =>
                $"Página {failure.PageNumber} · {failure.DisplayName}: " +
                CompactFailureMessage(failure.Message)));

        int omitted = partialPages.Count + failures.Count - detailLines.Count;
        if (omitted > 0)
        {
            detailLines.Add($"…y {omitted} página(s) más.");
        }

        string introduction = partialPages.Count > 0
            ? "Se han conservado y colocado todas las traducciones válidas. Las zonas que Ollama " +
              "no pudo resolver se han dejado vacías, sin borrar el trabajo correcto.\n\n"
            : string.Empty;

        MessageBox.Show(
            this,
            introduction +
            "Las páginas parciales o fallidas se han desmarcado para que no se exporten como " +
            "terminadas. Puedes volver a marcarlas y pulsar Traducir cómic para reintentar solo " +
            "lo que falta.\n\n" + string.Join(Environment.NewLine, detailLines),
            "Traducción parcial",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void RecordReliableComicPageOutcome(
        int pageIndex,
        bool completed,
        Exception? finalError,
        int finalizedPages,
        int totalPages,
        Stopwatch stopwatch,
        ICollection<ComicPageFailure> failures,
        ICollection<ComicPagePartial> partialPages)
    {
        int humanPage = pageIndex + 1;
        ComicBookPageState page = _comicPages[pageIndex];

        if (!completed)
        {
            string message = finalError?.Message ?? "Error desconocido durante el procesamiento.";
            page.Processed = false;
            page.Error = message;
            failures.Add(new ComicPageFailure(humanPage, page.DisplayName, message));
            _selectedComicPageIndices.Remove(pageIndex);
            _exportedComicPageIndices.Remove(pageIndex);
        }
        else if (!string.IsNullOrWhiteSpace(page.Error))
        {
            int total = page.Regions.Count(region => region.IsEnabled);
            int translated = page.Regions.Count(region =>
                region.IsEnabled && region.HasRenderableTranslation);
            partialPages.Add(new ComicPagePartial(
                humanPage,
                page.DisplayName,
                translated,
                total,
                page.Error));
            _selectedComicPageIndices.Remove(pageIndex);
            _exportedComicPageIndices.Remove(pageIndex);
        }

        SyncPageSelectionCheckBoxes();
        RefreshPageSelectionVisuals();
        UpdatePageSelectionSummary();

        double completedPercent = finalizedPages / (double)totalPages * 100;
        BusyProgressBar.Value = Math.Max(BusyProgressBar.Value, completedPercent);
        FooterProgressBar.Value = Math.Max(FooterProgressBar.Value, completedPercent);

        int remainingPages = Math.Max(0, totalPages - finalizedPages);
        if (!completed)
        {
            FooterStatusText.Text =
                $"Página {humanPage} sin traducir · {remainingPages} página(s) pendientes";
        }
        else if (!string.IsNullOrWhiteSpace(page.Error))
        {
            int total = page.Regions.Count(region => region.IsEnabled);
            int translated = page.Regions.Count(region =>
                region.IsEnabled && region.HasRenderableTranslation);
            FooterStatusText.Text =
                $"Página {humanPage} parcial · {translated}/{total} zonas · " +
                $"{remainingPages} página(s) pendientes";
        }
        else
        {
            FooterStatusText.Text = remainingPages > 0
                ? $"Página {humanPage} terminada · {remainingPages} página(s) pendientes"
                : $"Página {humanPage} terminada · {FormatDuration(stopwatch.Elapsed.TotalSeconds)}";
        }
    }

    private async Task ProcessComicPageReliablyAsync(
        ComicBookPageState page,
        int pageIndex,
        int humanPage,
        int completedBeforePage,
        int pendingCount,
        string model,
        CancellationToken cancellationToken,
        int attempt,
        Func<CancellationToken, Task<ComicAnalysis>>? takePreparedAnalysis = null)
    {
        BusyTitleText.Text = attempt == 1
            ? $"Página {humanPage}/{_comicPages.Count} · localizando bocadillos…"
            : $"Página {humanPage}/{_comicPages.Count} · reintento {attempt}/{ComicPageAutomaticAttempts}…";
        FooterStatusText.Text = $"Procesando página {humanPage} de {_comicPages.Count}…";
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        // Reporte inmediato y serializado en el Dispatcher: una notificación tardía de una
        // página no puede pisar el estado de la siguiente cuando el orden ya no es lineal.
        var progress = new ImmediateProgress<AnalysisProgress>(value =>
            Dispatcher.Invoke(() =>
            {
                double withinPage = Math.Clamp(value.Percentage / 100d, 0, 1);
                double calculated = (completedBeforePage + withinPage * 0.9) / pendingCount * 100;
                double overall = Math.Max(BusyProgressBar.Value, calculated);
                BusyProgressBar.Value = overall;
                FooterProgressBar.Value = Math.Max(FooterProgressBar.Value, overall);
                BusyTitleText.Text = $"Página {humanPage}/{_comicPages.Count} · {value.Message}";
                FooterStatusText.Text = value.Message;
            }));

        ComicAnalysis analysis;
        if (takePreparedAnalysis is null)
        {
            try
            {
                analysis = await PrepareComicPageAnalysisAsync(
                    pageIndex, model, progress, cancellationToken);
            }
            finally
            {
                // Los reintentos no pasan por la preparación por ventanas. Paddle debe salir
                // igualmente de la VRAM antes de volver a cargar TranslateGemma.
                await PaddleOcrResidentControl.ReleaseAsync();
            }
        }
        else
        {
            analysis = await takePreparedAnalysis(cancellationToken);
        }
        int totalEnabled = analysis.Regions.Count(region => region.IsEnabled);
        Exception? lastTranslationError = null;

        if (totalEnabled > 0)
        {
            List<ComicRegion> remaining = analysis.Regions
                .Where(region => region.IsEnabled && !region.HasRenderableTranslation)
                .ToList();

            for (int translationAttempt = 1;
                 translationAttempt <= ComicTranslationAutomaticAttempts && remaining.Count > 0;
                 translationAttempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    BusyTitleText.Text =
                        $"Página {humanPage}/{_comicPages.Count} · traduciendo " +
                        $"{remaining.Count} texto(s) pendiente(s)…";
                    FooterStatusText.Text = translationAttempt == 1
                        ? $"Traduciendo página {humanPage} con {model}…"
                        : $"Reintentando solo {remaining.Count} zona(s) de la página {humanPage} " +
                          $"({translationAttempt}/{ComicTranslationAutomaticAttempts})…";
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                    await RunLongOperationWithPromptAsync(
                        token => TranslatePageRegionsFastAsync(
                            remaining,
                            analysis.Regions,
                            model,
                            token,
                            progress),
                        $"La traducción de la página {humanPage}",
                        () => $"Traduciendo {remaining.Count} textos con {model}",
                        cancellationToken);
                    lastTranslationError = null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (IncompleteTranslationException exception)
                {
                    // El traductor rápido ya hizo un segundo pase agrupado y un rescate
                    // individual de lo que seguía dudoso. No repetimos toda la página aquí.
                    lastTranslationError = exception;
                    break;
                }
                catch (Exception exception)
                {
                    lastTranslationError = exception;
                }

                remaining = analysis.Regions
                    .Where(region => region.IsEnabled && !region.HasRenderableTranslation)
                    .ToList();

                if (remaining.Count > 0 && translationAttempt < ComicTranslationAutomaticAttempts)
                {
                    await Task.Delay(450, cancellationToken);
                }
            }
        }

        // Corrige lecturas locales conocidas después del pase contextual: así no se pierde
        // una palabra enfatizada por color ni una onomatopeya que el OCR haya separado mal.
        TranslationRecoveryService.ApplyKnownLocalTranslations(analysis.Regions);

        List<ComicRegion> finalRecovery = analysis.Regions
            .Where(region => region.IsEnabled && !region.HasRenderableTranslation)
            .ToList();
        if (finalRecovery.Count > 0)
        {
            BusyTitleText.Text =
                $"Página {humanPage}/{_comicPages.Count} · recuperando " +
                $"{finalRecovery.Count} bocadillo(s) pendiente(s)…";
            FooterStatusText.Text =
                $"Último pase individual para {finalRecovery.Count} bocadillo(s)…";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await _translationRecoveryService.RecoverAsync(
                finalRecovery,
                model,
                cancellationToken,
                progress);
        }

        int translatedCount = analysis.Regions.Count(region =>
            region.IsEnabled && region.HasRenderableTranslation);
        int incompleteCount = Math.Max(0, totalEnabled - translatedCount);

        if (totalEnabled > 0 && translatedCount == 0)
        {
            throw new InvalidOperationException(
                lastTranslationError?.Message ??
                "Ollama no devolvió ninguna traducción utilizable para esta página.",
                lastTranslationError);
        }

        foreach (ComicRegion region in analysis.Regions)
        {
            // En el lector solo se conserva la geometría necesaria para pulsar el bocadillo.
            // No se modifica RenderBox ni el estilo porque nada se rotula sobre la página.
            region.CleanupMode = "none";
        }

        page.Regions.Clear();
        page.Regions.AddRange(analysis.Regions);
        page.SourceLanguage = analysis.SourceLanguage;
        page.CleanedPath = null;
        page.MaskPath = null;
        page.Processed = true;
        page.Error = incompleteCount > 0
            ? $"Traducción parcial: {translatedCount} de {totalEnabled} zonas traducidas. " +
              CompactFailureMessage(lastTranslationError?.Message ?? string.Empty)
            : null;
        MarkActiveDocumentDirty(pageIndex);

        // Una página ya terminada es una unidad de trabajo cerrada. Se persiste antes de permitir
        // que el coordinador pase a la siguiente; cancelar o cerrar después de este punto no puede
        // hacer perder esta traducción.
        await AutoSaveCompletedProjectTaskAsync(pageIndex);
    }

    internal static bool IsReadableLetteringCandidate(ComicRegion region)
    {
        if (!region.IsEnabled
            || region.Confidence < 0.05
            || string.IsNullOrWhiteSpace(region.Original)
            || !region.Original.Any(char.IsLetter))
        {
            return false;
        }

        region.Type = NormalizeReaderTextType(region.Type);
        return true;

        // Un SFX solo se rescata si el detector está muy seguro de que vive dentro de un
        // contenedor amplio. Una palabra o rótulo exterior nunca se convierte por su cuenta.
    }

    private static string NormalizeReaderTextType(string? type) =>
        type?.Trim().ToLowerInvariant() switch
        {
            "dialogue" or "speech" or "balloon" => "dialogue",
            "thought" => "thought",
            "narration" or "caption" => "caption",
            "sfx" or "sound_effect" or "sound-effect" or "onomatopoeia" => "sfx",
            "sign" or "label" or "title" => "sign",
            _ => "text"
        };

    private static string CompactFailureMessage(string message)
    {
        string compact = string.Join(
            " ",
            (message ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim()));
        if (compact.Length <= 180)
        {
            return compact;
        }
        return compact[..177] + "…";
    }

    private sealed record ComicPageFailure(int PageNumber, string DisplayName, string Message);

    private sealed record ComicPagePartial(
        int PageNumber,
        string DisplayName,
        int Translated,
        int Total,
        string Message);

    private sealed record DeferredComicPageRetry(
        int PreparationPosition,
        int PageIndex,
        Exception FirstError);

    private sealed record PreparedComicPageWorkItem(
        int PreparationPosition,
        int PageIndex,
        ComicAnalysis? Analysis,
        Exception? Error,
        long Cost);
}
