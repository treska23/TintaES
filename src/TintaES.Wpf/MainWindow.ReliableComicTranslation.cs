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
            .OrderBy(index => index)
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
        var failures = new List<ComicPageFailure>();
        var partialPages = new List<ComicPagePartial>();
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
            for (int pendingPosition = 0; pendingPosition < pending.Length; pendingPosition++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int pageIndex = pending[pendingPosition];
                int humanPage = pageIndex + 1;
                ComicBookPageState page = _comicPages[pageIndex];
                Exception? finalError = null;
                bool completed = false;

                for (int attempt = 1; attempt <= ComicPageAutomaticAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await ProcessComicPageReliablyAsync(
                            page,
                            pageIndex,
                            humanPage,
                            pendingPosition,
                            pending.Length,
                            model,
                            cancellationToken,
                            attempt);
                        completed = true;
                        finalError = null;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        finalError = exception;
                        if (attempt >= ComicPageAutomaticAttempts)
                        {
                            break;
                        }

                        BusyTitleText.Text =
                            $"Página {humanPage}/{_comicPages.Count} · el intento falló; reintentando…";
                        FooterStatusText.Text =
                            $"Reintentando la página {humanPage} sin perder las páginas ya terminadas…";
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        await Task.Delay(700, cancellationToken);
                    }
                }

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

                double completedPercent = (pendingPosition + 1d) / pending.Length * 100;
                BusyProgressBar.Value = completedPercent;
                FooterProgressBar.Value = completedPercent;

                if (!completed)
                {
                    FooterStatusText.Text =
                        $"Página {humanPage} sin traducir · continúa el resto del lote";
                }
                else if (!string.IsNullOrWhiteSpace(page.Error))
                {
                    int total = page.Regions.Count(region => region.IsEnabled);
                    int translated = page.Regions.Count(region =>
                        region.IsEnabled && region.HasRenderableTranslation);
                    FooterStatusText.Text =
                        $"Página {humanPage} parcial · {translated}/{total} zonas traducidas";
                }
                else
                {
                    double secondsPerPage = stopwatch.Elapsed.TotalSeconds / Math.Max(1, pendingPosition + 1);
                    double remainingSeconds = secondsPerPage * Math.Max(0, pending.Length - pendingPosition - 1);
                    FooterStatusText.Text = remainingSeconds > 1
                        ? $"Página {humanPage} terminada · quedan aproximadamente {FormatDuration(remainingSeconds)}"
                        : $"Página {humanPage} terminada";
                }

                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
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
        detailLines.AddRange(partialPages.Take(12).Select(partial =>
            $"Página {partial.PageNumber} · {partial.DisplayName}: " +
            $"{partial.Translated}/{partial.Total} zonas traducidas."));
        detailLines.AddRange(failures.Take(Math.Max(0, 12 - detailLines.Count)).Select(failure =>
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

    private async Task ProcessComicPageReliablyAsync(
        ComicBookPageState page,
        int pageIndex,
        int humanPage,
        int pendingPosition,
        int pendingCount,
        string model,
        CancellationToken cancellationToken,
        int attempt)
    {
        BusyTitleText.Text = attempt == 1
            ? $"Página {humanPage}/{_comicPages.Count} · localizando bocadillos…"
            : $"Página {humanPage}/{_comicPages.Count} · reintento {attempt}/{ComicPageAutomaticAttempts}…";
        FooterStatusText.Text = $"Procesando página {humanPage} de {_comicPages.Count}…";
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        var progress = new Progress<AnalysisProgress>(value =>
        {
            double withinPage = Math.Clamp(value.Percentage / 100d, 0, 1);
            double overall = (pendingPosition + withinPage * 0.9) / pendingCount * 100;
            BusyProgressBar.Value = overall;
            FooterProgressBar.Value = overall;
            BusyTitleText.Text = $"Página {humanPage}/{_comicPages.Count} · {value.Message}";
            FooterStatusText.Text = value.Message;
        });

        if (!await _organicEngine.HasReusableAnalysisAsync(page.SourcePath, cancellationToken))
        {
            await _ollama.UnloadModelAsync(model, cancellationToken);
        }

        OrganicAnalysisResult organic = await AnalyzePageWithWatchdogAsync(
            page.SourcePath,
            progress,
            cancellationToken);

        ComicRegion[] readableCandidates = organic.Analysis.Regions
            .Where(IsReadableLetteringCandidate)
            .ToArray();

        if (readableCandidates.Length == 0)
        {
            page.Processed = false;
            page.Error = "No se ha detectado ningún texto pulsable. La página queda pendiente para poder reintentarla.";
            throw new InvalidOperationException(page.Error);
        }

        var analysis = new ComicAnalysis(organic.Analysis.SourceLanguage, readableCandidates);
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
                        token => _ollama.TranslateRegionsAsync(
                            remaining,
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
}
