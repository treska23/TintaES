using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Añade una segunda acción para proyectos guardados: repasar conserva detección y geometría;
/// volver a traducir ejecuta de nuevo el pipeline completo sobre las páginas marcadas.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ProjectRetranslationRegistered =
        RegisterProjectRetranslation();

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
                Content = "↻  Volver a traducir",
                Style = FindResource("ToolbarButton") as Style,
                Margin = new Thickness(7, 0, 0, 0),
                Visibility = Visibility.Collapsed,
                ToolTip =
                    "Repetir detección, OCR y traducción desde cero únicamente en las páginas marcadas"
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
            bool openedProject = !string.IsNullOrWhiteSpace(_currentProjectPath);
            bool containsTranslatedWork = _comicPages.Any(PageHasReviewableText);
            bool visible = openedProject && containsTranslatedWork;
            _retranslateProjectButton.Visibility = visible
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!visible)
            {
                _retranslateProjectButton.IsEnabled = false;
                return;
            }

            int selectedCount = GetSelectedComicPageIndices()
                .Count(index => index >= 0 && index < _comicPages.Count);
            bool busy = _comicBatchBusy
                        || _pageNavigationBusy
                        || BusyOverlay.Visibility == Visibility.Visible;
            bool hasModel = ModelComboBox.SelectedItem is not null;
            _retranslateProjectButton.IsEnabled = selectedCount > 0 && hasModel && !busy;
            _retranslateProjectButton.ToolTip = selectedCount == 0
                ? "Marca al menos una página en la columna izquierda"
                : "Repetir detección, OCR y traducción desde cero en las páginas marcadas; " +
                  "la versión anterior se conserva si una página falla";
        }
        finally
        {
            _refreshingProjectRetranslation = false;
        }
    }

    private async void RetranslateProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentProjectPath)
            || ModelComboBox.SelectedValue is not string model
            || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        int[] selected = OrderSelectedPagesFromCurrent(
            GetSelectedComicPageIndices()
                .Where(index => index >= 0 && index < _comicPages.Count));
        if (selected.Length == 0)
        {
            SetFooterStatus("Marca al menos una página en la columna izquierda.", "#C99A35");
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Se volverán a detectar y traducir desde cero {selected.Length} página(s) marcada(s).\n\n" +
            "Esto tardará bastante más que Repasar traducción. Las zonas y traducciones actuales " +
            "solo se sustituirán cuando la nueva versión de cada página termine correctamente.\n\n" +
            "¿Continuar?",
            "Volver a traducir",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RetranslateSelectedPagesFromScratchAsync(selected, model);
    }

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
                    ? $"Página {humanPage} retraducida desde cero"
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
                $"Retraducción cancelada · {completedPages} página(s) actualizada(s); " +
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
                $"Retraducción completa · {completedPages} página(s){partialText} · " +
                FormatDuration(stopwatch.Elapsed.TotalSeconds),
                partialPages == 0 ? "#58A77D" : "#C99A35");
            return;
        }

        SetFooterStatus(
            $"Retraducción terminada · {completedPages} página(s) actualizada(s) · " +
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
            "Retraducción parcial",
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
