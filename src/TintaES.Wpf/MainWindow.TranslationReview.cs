using System.Windows;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private readonly TranslationReviewService _translationReviewService = new();

    /// <summary>
    /// Una página es revisable cuando ya conserva el texto original y sus zonas. La traducción
    /// puede estar completa, ser antigua o contener huecos: la revisión los corrige sin repetir
    /// detección, OCR, limpieza ni reconstrucción visual.
    /// </summary>
    private static bool PageHasReviewableText(ComicBookPageState page) =>
        page.Regions.Any(region =>
            region.IsEnabled && !string.IsNullOrWhiteSpace(region.Original));

    private bool SelectedPagesCanBeReviewed(IReadOnlyCollection<int> selectedIndices) =>
        selectedIndices.Count > 0
        && selectedIndices.All(index =>
            index >= 0
            && index < _comicPages.Count
            && PageHasReviewableText(_comicPages[index]));

    private async Task ReviewSelectedTranslationsAsync(
        IReadOnlyCollection<int> selectedIndices,
        string model)
    {
        if (_comicBatchBusy || _pageNavigationBusy)
        {
            return;
        }

        int[] selected = selectedIndices
            .Where(index => index >= 0 && index < _comicPages.Count)
            .Distinct()
            .OrderBy(index => index)
            .Where(index => PageHasReviewableText(_comicPages[index]))
            .ToArray();
        if (selected.Length == 0)
        {
            SetFooterStatus(
                "Las páginas seleccionadas todavía no contienen texto detectado para revisar.",
                "#C99A35");
            return;
        }

        PersistVisibleComicPageRegions();
        await EnsureComicResearchContextAsync(forceInteractive: false);

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _analysisCancellation.Token;

        int reviewed = 0;
        int changed = 0;
        int unresolved = 0;
        int failedPages = 0;
        bool cancelled = false;

        _comicBatchBusy = true;
        SetBusy(true);
        BusyProgressBar.IsIndeterminate = false;
        FooterProgressBar.IsIndeterminate = false;
        BusyProgressBar.Value = 0;
        FooterProgressBar.Value = 0;
        BusyTitleText.Text = "Revisando las traducciones seleccionadas…";
        FooterStatusText.Text = $"Revisión rápida · 0/{selected.Length} páginas";
        UpdateComicControls();
        UpdateProjectCommandAvailability();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            for (int position = 0; position < selected.Length; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pageIndex = selected[position];
                ComicBookPageState page = _comicPages[pageIndex];
                bool wasProcessed = page.Processed;
                string? previousError = page.Error;

                BusyTitleText.Text =
                    $"Página {pageIndex + 1}/{_comicPages.Count} · revisando español…";
                FooterStatusText.Text =
                    $"Revisando página {position + 1} de {selected.Length} sin repetir OCR…";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                var pageProgress = new Progress<AnalysisProgress>(value =>
                {
                    double withinPage = Math.Clamp(value.Percentage / 100d, 0, 1);
                    double overall = (position + withinPage) / selected.Length * 100;
                    BusyProgressBar.Value = overall;
                    FooterProgressBar.Value = overall;
                    FooterStatusText.Text =
                        $"Página {pageIndex + 1} · {value.Message}";
                });

                try
                {
                    TranslationReviewResult result = await _translationReviewService.ReviewPageAsync(
                        page.Regions,
                        model,
                        cancellationToken,
                        pageProgress);
                    reviewed += result.Reviewed;
                    changed += result.Changed;
                    unresolved += result.Unresolved;

                    ComicRegion[] expected = page.Regions
                        .Where(region => region.IsEnabled
                                         && !string.IsNullOrWhiteSpace(region.Original))
                        .ToArray();
                    int missing = expected.Count(region => !region.HasRenderableTranslation);
                    page.Processed = expected.Length > 0 && missing == 0;
                    page.Error = missing == 0
                        ? null
                        : $"Revisión parcial: quedan {missing} texto(s) sin traducción válida.";

                    if (result.Changed > 0
                        || page.Processed != wasProcessed
                        || !string.Equals(page.Error, previousError, StringComparison.Ordinal))
                    {
                        MarkActiveDocumentDirty(pageIndex);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    failedPages++;
                }

                double completed = (position + 1d) / selected.Length * 100;
                BusyProgressBar.Value = completed;
                FooterProgressBar.Value = completed;
                RefreshPageSelectionVisuals();
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            _comicBatchBusy = false;
            SetBusy(false);
            UpdateComicControls();
            UpdateProjectCommandAvailability();
            RefreshPageSelectionVisuals();
            UpdatePageSelectionSummary();
            SynchronizeActiveDocumentState();
        }

        if (_comicPageIndex >= 0 && _comicPageIndex < _comicPages.Count)
        {
            RegionListBox.Items.Refresh();
            RebuildOverlay();
            ShowRegionEditor(_selectedRegion);
        }

        if (cancelled)
        {
            SetFooterStatus(
                $"Revisión cancelada · {changed} traducción(es) corregida(s) conservadas.",
                "#C99A35");
            return;
        }

        string unresolvedText = unresolved > 0
            ? $" · {unresolved} pendiente(s)"
            : string.Empty;
        string failureText = failedPages > 0
            ? $" · {failedPages} página(s) con error"
            : string.Empty;
        SetFooterStatus(
            $"Revisión terminada · {reviewed} textos repasados · {changed} corrección(es)" +
            unresolvedText + failureText,
            failedPages == 0 && unresolved == 0 ? "#58A77D" : "#C99A35");
    }
}
