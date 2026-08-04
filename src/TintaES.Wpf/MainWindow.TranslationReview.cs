using System.Text.RegularExpressions;
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
            MessageBox.Show(
                this,
                "No se pudo iniciar el repaso porque hay otra operación en curso.",
                "Resultado del repaso de traducción",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Se conserva deliberadamente el orden entregado por el llamador. El botón principal
        // lo rota desde la página visible; el menú contextual entrega una sola página.
        int[] selected = selectedIndices
            .Where(index => index >= 0 && index < _comicPages.Count)
            .Distinct()
            .Where(index => PageHasReviewableText(_comicPages[index]))
            .ToArray();
        if (selected.Length == 0)
        {
            SetFooterStatus(
                "Las páginas seleccionadas todavía no contienen texto detectado para revisar.",
                "#C99A35");
            MessageBox.Show(
                this,
                "No se ha repasado ningún texto porque las páginas marcadas todavía no contienen " +
                "zonas con texto original guardado.",
                "Resultado del repaso de traducción",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        PersistVisibleComicPageRegions();
        await EnsureComicResearchContextAsync(forceInteractive: false);

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _analysisCancellation.Token;

        int reviewed = 0;
        int unresolved = 0;
        bool cancelled = false;
        var changes = new List<TranslationReviewChange>();
        var failures = new List<TranslationReviewFailure>();

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
                Dictionary<Guid, string> previousTranslations = page.Regions
                    .ToDictionary(
                        region => region.Id,
                        region => CompactReviewText(region.Translation));

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
                    unresolved += result.Unresolved;

                    foreach (ComicRegion region in page.Regions.Where(region =>
                                 region.IsEnabled
                                 && !string.IsNullOrWhiteSpace(region.Original)))
                    {
                        string before = previousTranslations.TryGetValue(region.Id, out string? value)
                            ? value
                            : string.Empty;
                        string after = CompactReviewText(region.Translation);
                        if (string.Equals(before, after, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        changes.Add(new TranslationReviewChange(
                            pageIndex + 1,
                            region.Order,
                            CompactReviewText(region.Original),
                            before,
                            after));
                    }

                    ComicRegion[] expected = page.Regions
                        .Where(region => region.IsEnabled
                                         && !string.IsNullOrWhiteSpace(region.Original))
                        .ToArray();
                    int missing = expected.Count(region => !region.HasRenderableTranslation);
                    page.Processed = expected.Length > 0 && missing == 0;
                    page.Error = missing == 0
                        ? null
                        : $"Revisión parcial: quedan {missing} texto(s) sin traducción válida.";

                    bool pageChanged = changes.Any(change => change.PageNumber == pageIndex + 1);
                    if (pageChanged
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
                catch (Exception exception)
                {
                    failures.Add(new TranslationReviewFailure(
                        pageIndex + 1,
                        page.DisplayName,
                        CompactReviewFailure(exception.Message)));
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
            try
            {
                SetBusy(false);
                UpdateComicControls();
                UpdateProjectCommandAvailability();
                RefreshPageSelectionVisuals();
                UpdatePageSelectionSummary();
                SynchronizeActiveDocumentState();
            }
            catch (Exception exception)
            {
                failures.Add(new TranslationReviewFailure(
                    _comicPageIndex + 1,
                    "Actualización de la interfaz",
                    CompactReviewFailure(exception.Message)));
            }
        }

        // La reconstrucción visual es secundaria. Cualquier error se añade al informe, pero jamás
        // puede impedir que aparezca la ventana de finalización.
        try
        {
            if (_comicPageIndex >= 0 && _comicPageIndex < _comicPages.Count)
            {
                RegionListBox.Items.Refresh();
                RebuildOverlay();
                ShowRegionEditor(_selectedRegion);
            }
        }
        catch (Exception exception)
        {
            failures.Add(new TranslationReviewFailure(
                _comicPageIndex + 1,
                "Actualización de la página visible",
                CompactReviewFailure(exception.Message)));
        }

        if (cancelled)
        {
            SetFooterStatus(
                $"Revisión cancelada · {changes.Count} traducción(es) modificada(s) conservadas.",
                "#C99A35");
        }
        else
        {
            string unresolvedText = unresolved > 0
                ? $" · {unresolved} pendiente(s)"
                : string.Empty;
            string failureText = failures.Count > 0
                ? $" · {failures.Count} error(es)"
                : string.Empty;
            SetFooterStatus(
                $"Revisión terminada · {reviewed} textos repasados · {changes.Count} cambio(s)" +
                unresolvedText + failureText,
                failures.Count == 0 && unresolved == 0 ? "#58A77D" : "#C99A35");
        }

        // Se publica a prioridad ApplicationIdle para que el overlay de carga ya haya desaparecido.
        // Esta llamada se realiza siempre: con cambios, sin cambios, con errores o al cancelar.
        await Dispatcher.InvokeAsync(
            () => ShowTranslationReviewReport(
                reviewed,
                changes,
                unresolved,
                failures,
                cancelled),
            DispatcherPriority.ApplicationIdle);
    }

    private void ShowTranslationReviewReport(
        int reviewed,
        IReadOnlyList<TranslationReviewChange> changes,
        int unresolved,
        IReadOnlyList<TranslationReviewFailure> failures,
        bool cancelled)
    {
        var lines = new List<string>
        {
            cancelled ? "Estado: repaso cancelado" : "Estado: repaso terminado",
            string.Empty,
            $"Textos examinados: {reviewed}",
            $"Traducciones modificadas: {changes.Count}",
            $"Textos pendientes: {unresolved}",
            $"Errores: {failures.Count}"
        };

        if (changes.Count == 0 && failures.Count == 0 && !cancelled)
        {
            lines.Add(string.Empty);
            lines.Add(
                "El repaso sí se ha ejecutado, pero Ollama no ha propuesto ningún cambio " +
                "válido respecto a las traducciones guardadas.");
        }

        if (cancelled)
        {
            lines.Add(string.Empty);
            lines.Add("Los cambios completados antes de la cancelación se han conservado.");
        }

        if (changes.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Cambios realizados:");
            foreach (TranslationReviewChange change in changes.Take(8))
            {
                lines.Add(string.Empty);
                lines.Add(
                    $"Página {change.PageNumber} · zona {change.RegionOrder}: " +
                    AbbreviateReviewText(change.Original, 90));
                lines.Add(
                    "Antes: " +
                    (string.IsNullOrWhiteSpace(change.Before)
                        ? "[sin traducción]"
                        : AbbreviateReviewText(change.Before, 120)));
                lines.Add(
                    "Después: " +
                    (string.IsNullOrWhiteSpace(change.After)
                        ? "[pendiente; se rechazó una traducción cruzada o inválida]"
                        : AbbreviateReviewText(change.After, 120)));
            }

            if (changes.Count > 8)
            {
                lines.Add(string.Empty);
                lines.Add($"…y {changes.Count - 8} cambio(s) más.");
            }
        }

        if (failures.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Errores:");
            foreach (TranslationReviewFailure failure in failures.Take(6))
            {
                lines.Add(
                    $"Página {failure.PageNumber} · {failure.DisplayName}: {failure.Message}");
            }
            if (failures.Count > 6)
            {
                lines.Add($"…y {failures.Count - 6} error(es) más.");
            }
        }

        Activate();
        MessageBox.Show(
            this,
            string.Join(Environment.NewLine, lines),
            "Resultado del repaso de traducción",
            MessageBoxButton.OK,
            failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static string CompactReviewText(string? value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");

    private static string CompactReviewFailure(string? value)
    {
        string compact = CompactReviewText(value);
        return compact.Length <= 220 ? compact : compact[..217] + "…";
    }

    private static string AbbreviateReviewText(string value, int maximumLength) =>
        value.Length <= maximumLength
            ? value
            : value[..Math.Max(1, maximumLength - 1)] + "…";

    private sealed record TranslationReviewChange(
        int PageNumber,
        int RegionOrder,
        string Original,
        string Before,
        string After);

    private sealed record TranslationReviewFailure(
        int PageNumber,
        string DisplayName,
        string Message);
}
