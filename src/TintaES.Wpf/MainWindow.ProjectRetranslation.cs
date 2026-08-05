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
        var failures = new List<ComicPageRetranslationFailure>();
        int completedPages = 0;
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
            for (int position = 0; position < selected.Length; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int pageIndex = selected[position];
                ComicBookPageState page = _comicPages[pageIndex];
                int humanPage = pageIndex + 1;
                activePageIndex = pageIndex;
                activeSnapshot = CaptureRetranslationSnapshot(page);

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
                            position,
                            selected.Length,
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
                        if (attempt < ComicPageAutomaticAttempts)
                        {
                            RestoreRetranslationSnapshot(page, activeSnapshot);
                            BusyTitleText.Text =
                                $"Página {humanPage}/{_comicPages.Count} · reintentando desde cero…";
                            FooterStatusText.Text =
                                $"El nuevo análisis de la página {humanPage} falló; reintentando sin perder el anterior…";
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                            await Task.Delay(700, cancellationToken);
                        }
                    }
                }

                if (!completed)
                {
                    RestoreRetranslationSnapshot(page, activeSnapshot);
                    failures.Add(new ComicPageRetranslationFailure(
                        humanPage,
                        page.DisplayName,
                        finalError?.Message ?? "Error desconocido."));
                }
                else
                {
                    completedPages++;
                    if (!string.IsNullOrWhiteSpace(page.Error))
                    {
                        partialPages++;
                    }
                }

                activeSnapshot = null;
                activePageIndex = -1;
                SyncPageSelectionCheckBoxes();
                RefreshPageSelectionVisuals();
                UpdatePageSelectionSummary();

                double completedPercent = (position + 1d) / selected.Length * 100;
                BusyProgressBar.Value = completedPercent;
                FooterProgressBar.Value = completedPercent;
                FooterStatusText.Text = completed
                    ? $"Página {humanPage} detectada y traducida desde cero"
                    : $"Página {humanPage}: se conserva la traducción anterior";
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
            }
        }
        finally
        {
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
                $"Detección y traducción canceladas · {completedPages} página(s) actualizada(s); " +
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
                $"Detección y traducción completas · {completedPages} página(s){partialText} · " +
                FormatDuration(stopwatch.Elapsed.TotalSeconds),
                partialPages == 0 ? "#58A77D" : "#C99A35");
            return;
        }

        SetFooterStatus(
            $"Detección y traducción terminadas · {completedPages} página(s) actualizada(s) · " +
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
        page.SourceLanguage = snapshot.SourceLanguage;
        page.CleanedPath = snapshot.CleanedPath;
        page.MaskPath = snapshot.MaskPath;
        page.Processed = snapshot.Processed;
        page.SuppressBatchProcessing = snapshot.SuppressBatchProcessing;
        page.Error = snapshot.Error;
        page.Regions.Clear();
        page.Regions.AddRange(snapshot.Regions);
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
}
