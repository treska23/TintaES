using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene separadas las dos operaciones: el botón principal repite detección, OCR y traducción;
/// este segundo botón repasa únicamente el español ya guardado en las páginas marcadas.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ProjectRetranslationRegistered =
        RegisterProjectRetranslation();

    // Nombre histórico conservado para no romper otros módulos parciales. El botón representa
    // ahora exclusivamente la revisión lingüística, no una segunda ruta de detección.
    private Button? _retranslateProjectButton;
    private bool _projectRetranslationInstalled;
    private bool _refreshingProjectRetranslation;

    private static bool RegisterProjectRetranslation()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ProjectRetranslationLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ProjectRetranslationLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallProjectRetranslation,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallProjectRetranslation()
    {
        if (_projectRetranslationInstalled || AnalyzeButton is null)
        {
            return;
        }

        _projectRetranslationInstalled = true;
        if (AnalyzeButton.Parent is StackPanel actionPanel)
        {
            _retranslateProjectButton = new Button
            {
                Content = "✦  Repasar traducción",
                Style = FindResource("ToolbarButton") as Style,
                Margin = new Thickness(7, 0, 0, 0),
                Visibility = Visibility.Collapsed,
                ToolTip =
                    "Revisar únicamente el español de las páginas marcadas, sin repetir detección ni OCR"
            };
            _retranslateProjectButton.Click += RetranslateProjectButton_Click;

            int analyzeIndex = actionPanel.Children.IndexOf(AnalyzeButton);
            int insertIndex = analyzeIndex >= 0
                ? Math.Min(actionPanel.Children.Count, analyzeIndex + 1)
                : actionPanel.Children.Count;
            actionPanel.Children.Insert(insertIndex, _retranslateProjectButton);
        }

        AnalyzeButton.LayoutUpdated += (_, _) => RefreshProjectRetranslationAction();
        PreviewMouseUp += (_, _) => Dispatcher.BeginInvoke(
            RefreshProjectRetranslationAction,
            DispatcherPriority.Background);
        PreviewKeyUp += (_, _) => Dispatcher.BeginInvoke(
            RefreshProjectRetranslationAction,
            DispatcherPriority.Background);
        RefreshProjectRetranslationAction();
    }

    private void RefreshProjectRetranslationAction()
    {
        if (_refreshingProjectRetranslation || _retranslateProjectButton is null)
        {
            return;
        }

        _refreshingProjectRetranslation = true;
        try
        {
            bool containsReviewableWork = _comicPages.Any(PageHasReviewableText);
            _retranslateProjectButton.Visibility = containsReviewableWork
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!containsReviewableWork)
            {
                _retranslateProjectButton.IsEnabled = false;
                return;
            }

            int[] selected = GetSelectedComicPageIndices()
                .Where(index => index >= 0 && index < _comicPages.Count)
                .ToArray();
            int reviewableCount = selected.Count(index =>
                PageHasReviewableText(_comicPages[index]));
            bool busy = _comicBatchBusy
                        || _pageNavigationBusy
                        || BusyOverlay.Visibility == Visibility.Visible;
            bool hasModel = ModelComboBox.SelectedItem is not null;
            _retranslateProjectButton.IsEnabled = reviewableCount > 0 && hasModel && !busy;
            _retranslateProjectButton.ToolTip = selected.Length == 0
                ? "Marca al menos una página en la columna izquierda"
                : reviewableCount == 0
                    ? "Ninguna página marcada contiene todavía texto que se pueda repasar"
                    : $"Repasar el español de {reviewableCount} página(s) marcada(s), sin repetir OCR ni detección";
        }
        finally
        {
            _refreshingProjectRetranslation = false;
        }
    }

    private async void RetranslateProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModelComboBox.SelectedValue is not string model
            || string.IsNullOrWhiteSpace(model))
        {
            SetFooterStatus("Selecciona un modelo de traducción antes de continuar.", "#C99A35");
            return;
        }

        int[] selected = OrderSelectedPagesFromCurrent(
            CaptureCheckedComicPageIndices()
                .Where(index => index >= 0 && index < _comicPages.Count)
                .Where(index => PageHasReviewableText(_comicPages[index])));
        if (selected.Length == 0)
        {
            SetFooterStatus(
                "Las páginas marcadas todavía no contienen texto detectado para repasar.",
                "#C99A35");
            MessageBox.Show(
                this,
                "No hay texto guardado en las páginas marcadas. Usa Detectar y traducir para " +
                "crear primero las zonas y sus traducciones.",
                "Repasar traducción",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            await ReviewSelectedTranslationsAsync(selected, model);
        }
        catch (Exception exception)
        {
            _comicBatchBusy = false;
            SetBusy(false);
            SetFooterStatus("El repaso terminó con un error inesperado.", "#EE594B");
            MessageBox.Show(
                this,
                "El repaso de traducción no pudo completar su informe.\n\n" + exception.Message,
                "Resultado del repaso de traducción",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Pipeline completo utilizado por Detectar y traducir cuando alguna página marcada ya
    /// contiene trabajo. Cada página conserva su versión anterior si el reemplazo falla.
    /// </summary>
    private async Task RetranslateSelectedPagesFromScratchAsync(
        IReadOnlyList<int> selectedIndices,
        string model)
    {
        if (_comicBatchBusy || _comicPages.Count == 0)
        {
            return;
        }

        PersistVisibleComicPageRegions();
        _visibleComicPageIndex = -1;

        int[] selected = selectedIndices
            .Where(index => index >= 0 && index < _comicPages.Count)
            .Distinct()
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _analysisCancellation.Token;

        var stopwatch = Stopwatch.StartNew();
        var preparation = new PreparedPageWindow<ComicAnalysis>(selected.Length);
        var failures = new List<ComicPageRetranslationFailure>();
        var deferredPages = new List<DeferredComicPageRetranslation>();
        Dictionary<int, ComicPageRetranslationSnapshot> originalSnapshots = selected
            .ToDictionary(
                pageIndex => pageIndex,
                pageIndex => CaptureRetranslationSnapshot(_comicPages[pageIndex]));
        int finalizedPages = 0;
        int updatedPages = 0;
        int partialPages = 0;
        bool cancelled = false;
        ComicPageRetranslationSnapshot? activeSnapshot = null;
        int activePageIndex = -1;

        _comicBatchBusy = true;
        SetBusy(true);
        BusyProgressBar.IsIndeterminate = false;
        FooterProgressBar.IsIndeterminate = false;
        BusyProgressBar.Value = 0;
        FooterProgressBar.Value = 0;
        UpdateComicControls();
        RefreshProjectRetranslationAction();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            // Primera fase: prepara ventanas completas y consume cada una de menor a mayor
            // coste. Un fallo no bloquea las páginas siguientes: se conserva su estado
            // original y se aplaza su único reintento hasta terminar esta primera pasada.
            while (preparation.HasRemaining)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<PreparedComicPageWorkItem> window =
                    await TakePreparedComicPageWindowAsync(
                        preparation,
                        selected,
                        finalizedPages,
                        model,
                        cancellationToken);

                foreach (PreparedComicPageWorkItem workItem in window)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int pageIndex = workItem.PageIndex;
                    ComicBookPageState page = _comicPages[pageIndex];
                    int humanPage = pageIndex + 1;
                    ComicPageRetranslationSnapshot originalSnapshot =
                        originalSnapshots[pageIndex];

                    if (workItem.Error is not null || workItem.Analysis is null)
                    {
                        Exception preparationError = workItem.Error
                            ?? new InvalidOperationException(
                                "La preparación de la página no devolvió un análisis.");
                        RestoreRetranslationSnapshot(page, originalSnapshot);
                        DiscardPreparedComicPageArtifacts(pageIndex);
                        deferredPages.Add(new DeferredComicPageRetranslation(
                            workItem.PreparationPosition,
                            pageIndex,
                            preparationError));
                        FooterStatusText.Text =
                            $"Página {humanPage}: preparación aplazada; continúa el resto del lote";
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        continue;
                    }

                    activePageIndex = pageIndex;
                    activeSnapshot = originalSnapshot;
                    try
                    {
                        ComicAnalysis preparedAnalysis = workItem.Analysis;
                        await ProcessComicPageReliablyAsync(
                            page,
                            pageIndex,
                            humanPage,
                            finalizedPages,
                            selected.Length,
                            model,
                            cancellationToken,
                            attempt: 1,
                            token =>
                            {
                                token.ThrowIfCancellationRequested();
                                return Task.FromResult(preparedAnalysis);
                            });

                        finalizedPages++;
                        updatedPages++;
                        if (!string.IsNullOrWhiteSpace(page.Error))
                        {
                            partialPages++;
                        }

                        double completedPercent =
                            finalizedPages / (double)selected.Length * 100;
                        BusyProgressBar.Value = Math.Max(
                            BusyProgressBar.Value,
                            completedPercent);
                        FooterProgressBar.Value = Math.Max(
                            FooterProgressBar.Value,
                            completedPercent);
                        FooterStatusText.Text =
                            $"Página {humanPage} detectada y traducida desde cero";
                    }
                    catch (OperationCanceledException)
                    {
                        RestoreRetranslationSnapshot(page, originalSnapshot);
                        DiscardPreparedComicPageArtifacts(pageIndex);
                        throw;
                    }
                    catch (Exception exception)
                    {
                        RestoreRetranslationSnapshot(page, originalSnapshot);
                        DiscardPreparedComicPageArtifacts(pageIndex);
                        deferredPages.Add(new DeferredComicPageRetranslation(
                            workItem.PreparationPosition,
                            pageIndex,
                            exception));
                        FooterStatusText.Text =
                            $"Página {humanPage}: primer intento aplazado; continúa el resto del lote";
                    }
                    finally
                    {
                        activeSnapshot = null;
                        activePageIndex = -1;
                    }

                    SyncPageSelectionCheckBoxes();
                    RefreshPageSelectionVisuals();
                    UpdatePageSelectionSummary();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                }
            }

            // Segunda fase: solo ahora se reintentan las páginas aplazadas. Se fuerza una
            // preparación fresca (takePreparedAnalysis = null) y no se reutiliza ningún
            // análisis que pudiera haber quedado mutado por el primer intento.
            foreach (DeferredComicPageRetranslation deferred in deferredPages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int pageIndex = deferred.PageIndex;
                ComicBookPageState page = _comicPages[pageIndex];
                int humanPage = pageIndex + 1;
                ComicPageRetranslationSnapshot originalSnapshot =
                    originalSnapshots[pageIndex];
                RestoreRetranslationSnapshot(page, originalSnapshot);
                DiscardPreparedComicPageArtifacts(pageIndex);

                activePageIndex = pageIndex;
                activeSnapshot = originalSnapshot;
                bool completed = false;
                Exception finalError = deferred.FirstError;
                try
                {
                    BusyTitleText.Text =
                        $"Página {humanPage}/{_comicPages.Count} · reintento final desde cero…";
                    FooterStatusText.Text =
                        $"Reintentando la página {humanPage} después de completar la primera pasada…";
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                    await ProcessComicPageReliablyAsync(
                        page,
                        pageIndex,
                        humanPage,
                        finalizedPages,
                        selected.Length,
                        model,
                        cancellationToken,
                        attempt: ComicPageAutomaticAttempts,
                        takePreparedAnalysis: null);
                    completed = true;
                    updatedPages++;
                    if (!string.IsNullOrWhiteSpace(page.Error))
                    {
                        partialPages++;
                    }
                }
                catch (OperationCanceledException)
                {
                    RestoreRetranslationSnapshot(page, originalSnapshot);
                    DiscardPreparedComicPageArtifacts(pageIndex);
                    throw;
                }
                catch (Exception exception)
                {
                    finalError = exception;
                    RestoreRetranslationSnapshot(page, originalSnapshot);
                    DiscardPreparedComicPageArtifacts(pageIndex);
                    failures.Add(new ComicPageRetranslationFailure(
                        humanPage,
                        page.DisplayName,
                        finalError.Message));
                }
                finally
                {
                    if (!completed)
                    {
                        RestoreRetranslationSnapshot(page, originalSnapshot);
                    }

                    activeSnapshot = null;
                    activePageIndex = -1;
                }

                finalizedPages++;
                double completedPercent =
                    finalizedPages / (double)selected.Length * 100;
                BusyProgressBar.Value = Math.Max(
                    BusyProgressBar.Value,
                    completedPercent);
                FooterProgressBar.Value = Math.Max(
                    FooterProgressBar.Value,
                    completedPercent);
                FooterStatusText.Text = completed
                    ? $"Página {humanPage} detectada y traducida desde cero"
                    : $"Página {humanPage}: se conserva la traducción anterior";

                SyncPageSelectionCheckBoxes();
                RefreshPageSelectionVisuals();
                UpdatePageSelectionSummary();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            if (activeSnapshot is not null
                && activePageIndex >= 0
                && activePageIndex < _comicPages.Count)
            {
                RestoreRetranslationSnapshot(
                    _comicPages[activePageIndex],
                    activeSnapshot);
                DiscardPreparedComicPageArtifacts(activePageIndex);
            }
        }
        finally
        {
            DiscardAllPreparedComicPageArtifacts();
            stopwatch.Stop();
            _comicBatchBusy = false;
            SetBusy(false);
            UpdateComicControls();
            RefreshProjectRetranslationAction();
            SyncPageSelectionCheckBoxes();
            RefreshPageSelectionVisuals();
            UpdatePageSelectionSummary();
            SynchronizeActiveDocumentState();
        }

        if (!_documentOpenPending && _comicPages.Count > 0)
        {
            await ShowComicPageFastAsync(
                Math.Clamp(_comicPageIndex, 0, _comicPages.Count - 1));
        }

        if (cancelled)
        {
            SetFooterStatus(
                $"Detección y traducción canceladas · {updatedPages} página(s) actualizada(s); " +
                "la página en curso conserva su versión anterior.",
                "#C99A35");
            return;
        }

        if (failures.Count == 0)
        {
            string partialText = partialPages > 0
                ? $" · {partialPages} parcial(es)"
                : string.Empty;
            SetFooterStatus(
                $"Detección y traducción completas · {updatedPages} página(s){partialText} · " +
                FormatDuration(stopwatch.Elapsed.TotalSeconds),
                partialPages == 0 ? "#58A77D" : "#C99A35");
            return;
        }

        SetFooterStatus(
            $"Detección y traducción terminadas · {updatedPages} página(s) actualizada(s) · " +
            $"{failures.Count} conservaron su versión anterior",
            "#C99A35");

        string details = string.Join(
            Environment.NewLine,
            failures.Take(12).Select(failure =>
                $"Página {failure.PageNumber} · {failure.DisplayName}: " +
                CompactFailureMessage(failure.Message)));
        MessageBox.Show(
            this,
            "No se pudo completar el nuevo análisis de algunas páginas. Sus zonas y traducciones " +
            "anteriores se han conservado.\n\n" + details,
            "Detección y traducción parciales",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static ComicPageRetranslationSnapshot CaptureRetranslationSnapshot(
        ComicBookPageState page) =>
        new(
            page.SourceLanguage,
            page.CleanedPath,
            page.MaskPath,
            page.Processed,
            page.SuppressBatchProcessing,
            page.Error,
            page.Regions.ToArray());

    private static void RestoreRetranslationSnapshot(
        ComicBookPageState page,
        ComicPageRetranslationSnapshot snapshot)
    {
        DeleteReplacedPreparedArtifact(page.CleanedPath, snapshot.CleanedPath);
        DeleteReplacedPreparedArtifact(page.MaskPath, snapshot.MaskPath);
        page.SourceLanguage = snapshot.SourceLanguage;
        page.CleanedPath = snapshot.CleanedPath;
        page.MaskPath = snapshot.MaskPath;
        page.Processed = snapshot.Processed;
        page.SuppressBatchProcessing = snapshot.SuppressBatchProcessing;
        page.Error = snapshot.Error;
        page.Regions.Clear();
        page.Regions.AddRange(snapshot.Regions);
    }

    private static void DeleteReplacedPreparedArtifact(string? currentPath, string? restoredPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath)
            || string.Equals(currentPath, restoredPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string preparedSegment =
            $"{Path.DirectorySeparatorChar}processed{Path.DirectorySeparatorChar}.prepared" +
            Path.DirectorySeparatorChar;
        if (currentPath.Contains(preparedSegment, StringComparison.OrdinalIgnoreCase))
        {
            DeleteFileQuietly(currentPath);
        }
    }

    private sealed record ComicPageRetranslationSnapshot(
        string SourceLanguage,
        string? CleanedPath,
        string? MaskPath,
        bool Processed,
        bool SuppressBatchProcessing,
        string? Error,
        ComicRegion[] Regions);

    private sealed record ComicPageRetranslationFailure(
        int PageNumber,
        string DisplayName,
        string Message);

    private sealed record DeferredComicPageRetranslation(
        int PreparationPosition,
        int PageIndex,
        Exception FirstError);
}
