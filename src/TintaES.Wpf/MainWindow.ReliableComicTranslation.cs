using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Procesa únicamente las páginas seleccionadas, reintenta fallos transitorios y nunca presenta
/// un lote incompleto como si hubiera terminado correctamente. Las páginas que finalmente fallen
/// quedan marcadas, desmarcadas y resumidas al usuario al terminar el resto del cómic.
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

                SyncPageSelectionCheckBoxes();
                RefreshPageSelectionVisuals();
                UpdatePageSelectionSummary();

                double completedPercent = (pendingPosition + 1d) / pending.Length * 100;
                BusyProgressBar.Value = completedPercent;
                FooterProgressBar.Value = completedPercent;

                if (completed)
                {
                    double secondsPerPage = stopwatch.Elapsed.TotalSeconds / Math.Max(1, pendingPosition + 1);
                    double remainingSeconds = secondsPerPage * Math.Max(0, pending.Length - pendingPosition - 1);
                    FooterStatusText.Text = remainingSeconds > 1
                        ? $"Página {humanPage} terminada · quedan aproximadamente {FormatDuration(remainingSeconds)}"
                        : $"Página {humanPage} terminada";
                }
                else
                {
                    FooterStatusText.Text =
                        $"Página {humanPage} sin traducir · continúa el resto del lote";
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
                "Traducción cancelada. Las páginas terminadas se conservan.",
                "#C99A35");
            return;
        }

        if (failures.Count == 0)
        {
            SetFooterStatus(
                $"Cómic traducido · {pending.Length} páginas · {FormatDuration(stopwatch.Elapsed.TotalSeconds)}",
                "#58A77D");
            return;
        }

        string failedPages = string.Join(", ", failures.Select(failure => failure.PageNumber));
        SetFooterStatus(
            $"Traducción incompleta · {failures.Count} página(s) sin traducir: {failedPages}",
            "#EE594B");

        string details = string.Join(
            Environment.NewLine,
            failures.Take(12).Select(failure =>
                $"Página {failure.PageNumber} · {failure.DisplayName}: {CompactFailureMessage(failure.Message)}"));
        if (failures.Count > 12)
        {
            details += Environment.NewLine + $"…y {failures.Count - 12} página(s) más.";
        }

        MessageBox.Show(
            this,
            $"El proceso terminó, pero {failures.Count} página(s) no pudieron traducirse.\n\n" +
            "Se han marcado como error y se les ha quitado el checkbox para que no se exporten " +
            "como si estuvieran terminadas. Puedes volver a marcarlas y pulsar Traducir cómic " +
            "para reintentarlas.\n\n" + details,
            "Traducción incompleta",
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
            ? $"Página {humanPage}/{_comicPages.Count} · localizando y limpiando texto…"
            : $"Página {humanPage}/{_comicPages.Count} · reintento {attempt}/{ComicPageAutomaticAttempts}…";
        FooterStatusText.Text = $"Procesando página {humanPage} de {_comicPages.Count}…";
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        BitmapSource original = LoadBitmapSource(page.SourcePath);
        var progress = new Progress<AnalysisProgress>(value =>
        {
            double withinPage = Math.Clamp(value.Percentage / 100d, 0, 1);
            double overall = (pendingPosition + withinPage * 0.9) / pendingCount * 100;
            BusyProgressBar.Value = overall;
            FooterProgressBar.Value = overall;
            BusyTitleText.Text = $"Página {humanPage}/{_comicPages.Count} · {value.Message}";
            FooterStatusText.Text = value.Message;
        });

        OrganicAnalysisResult organic = await AnalyzePageWithWatchdogAsync(
            page.SourcePath,
            progress,
            cancellationToken);

        DialogueOnlyResult filtered = await Task.Run(
            () => _dialogueOnlyResultService.Build(
                original,
                organic.CleanedBitmap,
                organic.MaskBitmap,
                organic.Analysis.Regions,
                includeAllDetectedText: true),
            cancellationToken);

        var analysis = new ComicAnalysis(organic.Analysis.SourceLanguage, filtered.Regions);

        if (analysis.Regions.Count > 0)
        {
            Exception? translationError = null;
            bool translated = false;
            for (int translationAttempt = 1;
                 translationAttempt <= ComicTranslationAutomaticAttempts;
                 translationAttempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    BusyTitleText.Text =
                        $"Página {humanPage}/{_comicPages.Count} · traduciendo " +
                        $"{analysis.Regions.Count} textos…";
                    FooterStatusText.Text = translationAttempt == 1
                        ? $"Traduciendo página {humanPage} con {model}…"
                        : $"Reintentando los textos de la página {humanPage} " +
                          $"({translationAttempt}/{ComicTranslationAutomaticAttempts})…";
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                    await RunLongOperationWithPromptAsync(
                        token => _ollama.TranslateRegionsAsync(
                            analysis.Regions,
                            model,
                            token,
                            progress),
                        $"La traducción de la página {humanPage}",
                        () => $"Traduciendo {analysis.Regions.Count} textos con {model}",
                        cancellationToken);

                    EnsureTranslationsAreComplete(analysis.Regions);
                    translated = true;
                    translationError = null;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    translationError = exception;
                    if (translationAttempt < ComicTranslationAutomaticAttempts)
                    {
                        await Task.Delay(450, cancellationToken);
                    }
                }
            }

            if (!translated)
            {
                throw new InvalidOperationException(
                    translationError?.Message ??
                    "La traducción no devolvió un resultado completo para esta página.",
                    translationError);
            }
        }

        string processedDirectory = Path.Combine(_comicWorkspace!, "processed");
        Directory.CreateDirectory(processedDirectory);
        string cleanedPath = Path.Combine(processedDirectory, $"{pageIndex + 1:D4}-clean.png");
        string maskPath = Path.Combine(processedDirectory, $"{pageIndex + 1:D4}-mask.png");

        await Task.WhenAll(
            Task.Run(() => SaveBitmap(filtered.CleanedBitmap, cleanedPath), cancellationToken),
            Task.Run(() => SaveBitmap(filtered.MaskBitmap, maskPath), cancellationToken));

        foreach (ComicRegion region in analysis.Regions)
        {
            // El renderizador canónico calcula el mayor tamaño que cabe partiendo de 100 %.
            // No se aplican multiplicadores ni migraciones posteriores.
            region.FontScale = 1;
            region.ManualFontScale = 1;
            region.IsManual = false;
            region.ManualBaseFontSize = 0;
            region.ManualLayoutSeedText = string.Empty;
        }

        page.Regions.Clear();
        page.Regions.AddRange(analysis.Regions);
        page.SourceLanguage = analysis.SourceLanguage;
        page.CleanedPath = cleanedPath;
        page.MaskPath = maskPath;
        page.Processed = true;
        page.Error = null;
        MarkActiveDocumentDirty(pageIndex);
    }

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
}
