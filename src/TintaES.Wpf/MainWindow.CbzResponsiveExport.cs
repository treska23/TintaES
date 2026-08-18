using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TintaES.Wpf;

/// <summary>
/// Exportación CBZ resistente a páginas que bloquean el render de WPF. Cada página se procesa
/// en un hilo STA independiente. A los treinta segundos se pregunta si debe seguir esperando; si el
/// usuario no responde en treinta segundos, la página se omite. Los checkboxes actúan como lista
/// de pendientes: una página se desmarca al quedar preparada.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan CbzPageReviewInterval = TimeSpan.FromSeconds(30);
    private static readonly bool ResponsiveCbzExportRegistered = RegisterResponsiveCbzExport();

    private bool _responsiveCbzExportInstalled;

    private static bool RegisterResponsiveCbzExport()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ResponsiveCbzExportLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ResponsiveCbzExportLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.LayoutUpdated -= window.MainWindow_TryInstallResponsiveCbzExport;
        window.LayoutUpdated += window.MainWindow_TryInstallResponsiveCbzExport;
        window.Dispatcher.BeginInvoke(
            window.TryInstallResponsiveCbzExport,
            DispatcherPriority.ContextIdle);
    }

    private void MainWindow_TryInstallResponsiveCbzExport(object? sender, EventArgs e) =>
        TryInstallResponsiveCbzExport();

    private void TryInstallResponsiveCbzExport()
    {
        if (_responsiveCbzExportInstalled
            || !_robustCbzExportInstalled
            || _exportComicButton is null)
        {
            return;
        }

        _responsiveCbzExportInstalled = true;
        LayoutUpdated -= MainWindow_TryInstallResponsiveCbzExport;
        _exportComicButton.Click -= ExportComicButton_Click_Robust;
        _exportComicButton.Click += ExportComicButton_Click_Responsive;
    }

    private async void ExportComicButton_Click_Responsive(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0)
        {
            return;
        }

        PersistVisibleComicPageRegions();
        IReadOnlyList<int> selectedPages = GetSelectedComicPageIndices();
        if (selectedPages.Count == 0)
        {
            MessageBox.Show(
                this,
                "No hay ninguna página marcada en el selector vertical.",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar páginas seleccionadas al cómic",
            FileName = MakeSafeFileName(_comicTitle ?? "comic") + "-es.cbz",
            DefaultExt = ".cbz",
            Filter = "Comic Book ZIP|*.cbz",
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string outputPath = dialog.FileName;
        if (File.Exists(outputPath))
        {
            MessageBoxResult append = MessageBox.Show(
                this,
                $"El CBZ ya existe.\n\nSe conservarán las páginas que ya contiene y se añadirán o reemplazarán únicamente las {selectedPages.Count} páginas marcadas.\n\n¿Continuar?",
                "Continuar exportación CBZ",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (append != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _analysisCancellation.Token;

        _comicBatchBusy = true;
        SetBusy(true);
        UpdateComicControls();
        UpdateProjectCommandAvailability();
        UpdatePsdExportAvailability();
        RefreshPageSelectionVisuals();
        BusyTitleText.Text = "Preparando exportación reanudable…";
        BusyProgressBar.IsIndeterminate = false;
        FooterProgressBar.IsIndeterminate = false;
        BusyProgressBar.Value = 0;
        FooterProgressBar.Value = 0;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        var fallbackPages = new List<string>();
        var failedPages = new List<CbzPageFailure>();
        var stagedPages = new Dictionary<int, string>();
        string stagingDirectory = GetCbzStagingDirectory(outputPath);
        string stagingManifestPath = Path.Combine(stagingDirectory, "stage.json");
        string buildTemporaryPath = outputPath + ".tinta-build.tmp";

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            CbzStageManifest stageManifest = await Task.Run(
                () => LoadCbzStageManifest(stagingManifestPath),
                cancellationToken);

            for (int position = 0; position < selectedPages.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int pageIndex = selectedPages[position];
                ComicBookPageState page = _comicPages[pageIndex];
                string entryName = GetCbzPageEntryName(pageIndex);
                string stagePath = Path.Combine(stagingDirectory, entryName);
                string fingerprint = CreateCbzPageFingerprint(pageIndex, page);

                bool reusable = File.Exists(stagePath)
                    && stageManifest.Pages.TryGetValue(entryName, out string? savedFingerprint)
                    && string.Equals(savedFingerprint, fingerprint, StringComparison.Ordinal);

                BusyTitleText.Text = reusable
                    ? $"Recuperando página {pageIndex + 1} · {position + 1}/{selectedPages.Count}"
                    : $"Renderizando página {pageIndex + 1} · {position + 1}/{selectedPages.Count}";
                FooterStatusText.Text = reusable
                    ? "Reutilizando una página preparada de una exportación interrumpida…"
                    : $"Preparando página {pageIndex + 1} de {_comicPages.Count} · aviso si tarda más de 30 segundos…";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                if (!reusable)
                {
                    try
                    {
                        CbzStaPageResult result = await RenderAndStageCbzPageOnStaAsync(
                            pageIndex,
                            page,
                            stagePath,
                            cancellationToken);
                        fallbackPages.AddRange(result.FallbackMessages);

                        stageManifest.Pages[entryName] = fingerprint;
                        await Task.Run(
                            () => SaveCbzStageManifest(stagingManifestPath, stageManifest),
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failedPages.Add(new CbzPageFailure(pageIndex, exception.Message));
                        stageManifest.Pages.Remove(entryName);
                        TryDeleteTemporaryCbz(stagePath);
                        TryDeleteTemporaryCbz(stagePath + ".tmp");
                        await Task.Run(
                            () => SaveCbzStageManifest(stagingManifestPath, stageManifest),
                            cancellationToken);

                        SetCbzPagePendingInSelector(pageIndex, pending: true);
                        UpdateResponsiveCbzProgress(position, selectedPages.Count, failedPages.Count);
                        FooterStatusText.Text =
                            $"Página {pageIndex + 1} omitida. Continúa marcada y se pasa a la siguiente.";
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                        continue;
                    }
                }

                stagedPages[pageIndex] = stagePath;

                // El checkbox se convierte en un indicador de trabajo: desmarcado significa
                // que la página ya quedó preparada y puede reutilizarse aunque se cancele después.
                SetCbzPagePendingInSelector(pageIndex, pending: false);

                UpdateResponsiveCbzProgress(position, selectedPages.Count, failedPages.Count);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (stagedPages.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "No se pudo preparar ninguna de las páginas seleccionadas.\n\nLas páginas problemáticas continúan marcadas para volver a intentarlo.",
                    "Exportación CBZ incompleta",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                SetFooterStatus("No se preparó ninguna página. El CBZ anterior sigue intacto.", "#C99A35");
                return;
            }

            BusyTitleText.Text = "Montando el CBZ una sola vez…";
            FooterStatusText.Text = "Conservando páginas anteriores y añadiendo las terminadas…";
            BusyProgressBar.Value = 90;
            FooterProgressBar.Value = 90;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            TryDeleteTemporaryCbz(buildTemporaryPath);
            await Task.Run(
                () => BuildFinalCbz(
                    File.Exists(outputPath) ? outputPath : null,
                    buildTemporaryPath,
                    stagedPages,
                    cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            CommitCbzCheckpoint(buildTemporaryPath, outputPath);

            int[] committedPages = stagedPages.Keys.OrderBy(index => index).ToArray();
            MarkComicPagesExported(committedPages);
            CleanupCommittedCbzStaging(
                stagingDirectory,
                stagingManifestPath,
                committedPages);

            BusyProgressBar.Value = 100;
            FooterProgressBar.Value = 100;

            if (failedPages.Count > 0)
            {
                SetFooterStatus(
                    $"CBZ actualizado · {committedPages.Length} añadidas · {failedPages.Count} pendientes",
                    "#C99A35");
                MessageBox.Show(
                    this,
                    $"El CBZ se ha actualizado con las páginas que terminaron.\n\nSe omitieron {failedPages.Count} página(s):\n\n" +
                    string.Join("\n", failedPages.Take(12).Select(failure =>
                        $"Página {failure.PageIndex + 1}: {failure.Reason}")) +
                    (failedPages.Count > 12 ? $"\n… y {failedPages.Count - 12} más." : string.Empty) +
                    "\n\nLas omitidas siguen marcadas. Vuelve a exportar seleccionando el mismo CBZ para añadirlas sin rehacer las anteriores.",
                    "CBZ exportado con páginas pendientes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else if (fallbackPages.Count > 0)
            {
                SetFooterStatus(
                    $"CBZ actualizado con {fallbackPages.Count} página(s) de respaldo.",
                    "#C99A35");
                MessageBox.Show(
                    this,
                    "El CBZ se ha actualizado, pero algunas páginas se incluyeron con su imagen original:\n\n" +
                    string.Join("\n", fallbackPages.Take(8)) +
                    (fallbackPages.Count > 8 ? $"\n… y {fallbackPages.Count - 8} más." : string.Empty),
                    "Tinta ES",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                SetFooterStatus(
                    $"CBZ actualizado · {committedPages.Length} página(s) · {Path.GetFileName(outputPath)}",
                    "#58A77D");
            }
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemporaryCbz(buildTemporaryPath);
            MessageBox.Show(
                this,
                "La exportación se ha cancelado.\n\nEl CBZ anterior sigue intacto. Las páginas ya desmarcadas quedaron preparadas y se reutilizarán al volver a exportar al mismo archivo.",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            SetFooterStatus("Exportación CBZ pausada sin perder las páginas preparadas.", "#C99A35");
        }
        catch (Exception exception)
        {
            TryDeleteTemporaryCbz(buildTemporaryPath);
            MessageBox.Show(
                this,
                $"No se pudo terminar la exportación CBZ.\n\n{exception.Message}\n\nEl CBZ anterior no se ha dañado. Las páginas preparadas se conservarán para reanudar.",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetFooterStatus("La exportación CBZ se detuvo sin dañar el archivo anterior.", "#EE594B");
        }
        finally
        {
            _comicBatchBusy = false;
            SetBusy(false);
            UpdateComicControls();
            UpdateProjectCommandAvailability();
            UpdatePsdExportAvailability();
            RefreshPageSelectionVisuals();
            UpdatePageSelectionSummary();
        }
    }

    private void SetCbzPagePendingInSelector(int pageIndex, bool pending)
    {
        if (pageIndex < 0 || pageIndex >= _comicPages.Count)
        {
            return;
        }

        if (pending)
        {
            _selectedComicPageIndices.Add(pageIndex);
        }
        else
        {
            _selectedComicPageIndices.Remove(pageIndex);
        }

        if (_pageSelectionCheckBoxes.TryGetValue(pageIndex, out System.Windows.Controls.CheckBox? checkBox))
        {
            checkBox.IsChecked = pending;
        }

        UpdatePageSelectionSummary();
        UpdateCbzExportSelectionCaption();
    }

    private void UpdateResponsiveCbzProgress(int position, int total, int failed)
    {
        double progress = (position + 1d) / total * 88;
        BusyProgressBar.Value = progress;
        FooterProgressBar.Value = progress;
        FooterStatusText.Text = failed == 0
            ? $"Revisadas {position + 1} de {total} páginas."
            : $"Revisadas {position + 1} de {total} páginas · {failed} omitida(s).";
    }

    private async Task<CbzStaPageResult> RenderAndStageCbzPageOnStaAsync(
        int pageIndex,
        ComicBookPageState page,
        string stagePath,
        CancellationToken cancellationToken)
    {
        string attemptPath = stagePath + ".attempt-" + Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<CbzStaPageResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            var localFallbacks = new List<string>();
            try
            {
                var image = RenderComicPageForCbz(pageIndex, page, localFallbacks);
                SavePngAtomically(image, attemptPath, CancellationToken.None);
                var result = new CbzStaPageResult(attemptPath, localFallbacks);

                if (!completion.TrySetResult(result))
                {
                    TryDeleteTemporaryCbz(attemptPath);
                    TryDeleteTemporaryCbz(attemptPath + ".tmp");
                }
            }
            catch (Exception exception)
            {
                TryDeleteTemporaryCbz(attemptPath);
                TryDeleteTemporaryCbz(attemptPath + ".tmp");
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = $"TintaES CBZ página {pageIndex + 1}"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        while (!completion.Task.IsCompleted)
        {
            Task reviewDelay = Task.Delay(CbzPageReviewInterval, cancellationToken);
            Task completed = await Task.WhenAny(completion.Task, reviewDelay).ConfigureAwait(false);
            if (ReferenceEquals(completed, completion.Task))
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            bool continueWaiting = await Dispatcher.InvokeAsync(() =>
            {
                var prompt = new CbzPageWaitPromptWindow(pageIndex + 1)
                {
                    Owner = this
                };
                return prompt.ShowDialog() == true;
            });

            // La página puede haber terminado durante los treinta segundos de la pregunta.
            if (completion.Task.IsCompleted)
            {
                break;
            }

            if (!continueWaiting)
            {
                completion.TrySetException(new TimeoutException(
                    "omitida porque se eligió saltarla o no hubo respuesta durante 30 segundos"));
                break;
            }
        }

        CbzStaPageResult completedResult = await completion.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(
            () => File.Move(completedResult.AttemptPath, stagePath, overwrite: true),
            cancellationToken).ConfigureAwait(false);

        return completedResult;
    }

    private sealed record CbzStaPageResult(
        string AttemptPath,
        IReadOnlyList<string> FallbackMessages);

    private sealed record CbzPageFailure(int PageIndex, string Reason);
}
