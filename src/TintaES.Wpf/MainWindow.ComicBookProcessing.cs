using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private async void AnalyzeComicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0)
        {
            AnalyzeButton_Click_Responsive(sender, e);
            return;
        }
        if (ModelComboBox.SelectedValue is not string model || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        PersistVisibleComicPageRegions();
        _visibleComicPageIndex = -1;

        int[] pending = _comicPages
            .Select((page, index) => (page, index))
            .Where(item => !item.page.Processed)
            .Select(item => item.index)
            .ToArray();
        if (pending.Length == 0)
        {
            SetFooterStatus("Todas las páginas del cómic ya están procesadas.", "#58A77D");
            return;
        }

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _analysisCancellation.Token;
        var stopwatch = Stopwatch.StartNew();
        int failures = 0;
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
                ComicBookPageState page = _comicPages[pageIndex];
                int humanPage = pageIndex + 1;

                BusyTitleText.Text = $"Página {humanPage}/{_comicPages.Count} · localizando y limpiando texto…";
                FooterStatusText.Text = $"Procesando página {humanPage} de {_comicPages.Count}…";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                try
                {
                    BitmapSource original = LoadBitmapSource(page.SourcePath);
                    int capturedPendingPosition = pendingPosition;
                    var progress = new Progress<AnalysisProgress>(value =>
                    {
                        double withinPage = Math.Clamp(value.Percentage / 100d, 0, 1);
                        double overall = (capturedPendingPosition + withinPage * 0.9) / pending.Length * 100;
                        BusyProgressBar.Value = overall;
                        FooterProgressBar.Value = overall;
                        BusyTitleText.Text = $"Página {humanPage}/{_comicPages.Count} · {value.Message}";
                    });

                    OrganicAnalysisResult organic = await _organicEngine.AnalyzeAsync(
                        page.SourcePath,
                        progress,
                        cancellationToken);

                    DialogueOnlyResult filtered = await Task.Run(
                        () => _dialogueOnlyResultService.Build(
                            original,
                            organic.CleanedBitmap,
                            organic.MaskBitmap,
                            organic.Analysis.Regions),
                        cancellationToken);

                    var analysis = new ComicAnalysis(organic.Analysis.SourceLanguage, filtered.Regions);
                    if (analysis.Regions.Count > 0)
                    {
                        BusyTitleText.Text = $"Página {humanPage}/{_comicPages.Count} · traduciendo {analysis.Regions.Count} bocadillos…";
                        FooterStatusText.Text = $"Traduciendo página {humanPage} con {model}…";
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        await _ollama.TranslateRegionsAsync(analysis.Regions, model, cancellationToken);
                    }

                    foreach (ComicRegion region in analysis.Regions)
                    {
                        region.FontScale = 1;
                        region.ManualFontScale = 1;
                        region.IsManual = false;
                        region.ManualBaseFontSize = 0;
                        region.ManualLayoutSeedText = string.Empty;
                    }

                    string processedDirectory = Path.Combine(_comicWorkspace!, "processed");
                    string cleanedPath = Path.Combine(processedDirectory, $"{pageIndex + 1:D4}-clean.png");
                    string maskPath = Path.Combine(processedDirectory, $"{pageIndex + 1:D4}-mask.png");
                    SaveBitmap(filtered.CleanedBitmap, cleanedPath);
                    SaveBitmap(filtered.MaskBitmap, maskPath);

                    page.Regions.Clear();
                    page.Regions.AddRange(analysis.Regions);
                    page.SourceLanguage = analysis.SourceLanguage;
                    page.CleanedPath = cleanedPath;
                    page.MaskPath = maskPath;
                    page.Processed = true;
                    page.Error = null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    page.Error = exception.Message;
                    failures++;
                }

                double completed = (pendingPosition + 1d) / pending.Length * 100;
                BusyProgressBar.Value = completed;
                FooterProgressBar.Value = completed;
                double secondsPerPage = stopwatch.Elapsed.TotalSeconds / Math.Max(1, pendingPosition + 1);
                double remainingSeconds = secondsPerPage * Math.Max(0, pending.Length - pendingPosition - 1);
                FooterStatusText.Text = remainingSeconds > 1
                    ? $"Página {humanPage} terminada · tiempo restante estimado {FormatDuration(remainingSeconds)}"
                    : $"Página {humanPage} terminada";
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
        }

        ShowComicPage(Math.Clamp(_comicPageIndex, 0, _comicPages.Count - 1));
        if (cancelled)
        {
            SetFooterStatus("Traducción del cómic cancelada. Las páginas ya terminadas se conservan.", "#C99A35");
        }
        else if (failures > 0)
        {
            SetFooterStatus($"Proceso terminado en {FormatDuration(stopwatch.Elapsed.TotalSeconds)} · {failures} página(s) con error.", "#C99A35");
        }
        else
        {
            SetFooterStatus($"Cómic traducido · {_comicPages.Count} páginas · {FormatDuration(stopwatch.Elapsed.TotalSeconds)}", "#58A77D");
        }
    }

    private async void ExportComicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0)
        {
            return;
        }

        PersistVisibleComicPageRegions();

        var dialog = new SaveFileDialog
        {
            Title = "Exportar cómic traducido",
            FileName = MakeSafeFileName(_comicTitle ?? "comic") + "-es.cbz",
            DefaultExt = ".cbz",
            Filter = "Comic Book ZIP|*.cbz"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _analysisCancellation.Token;
        _comicBatchBusy = true;
        SetBusy(true);
        UpdateComicControls();
        BusyTitleText.Text = "Preparando CBZ…";
        BusyProgressBar.IsIndeterminate = false;
        FooterProgressBar.IsIndeterminate = false;

        try
        {
            using FileStream output = File.Create(dialog.FileName);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
            for (int index = 0; index < _comicPages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ComicBookPageState page = _comicPages[index];
                BusyTitleText.Text = $"Exportando página {index + 1}/{_comicPages.Count}…";
                double progress = index / (double)_comicPages.Count * 100;
                BusyProgressBar.Value = progress;
                FooterProgressBar.Value = progress;

                BitmapSource image;
                if (page.Processed
                    && page.Error is null
                    && !string.IsNullOrWhiteSpace(page.CleanedPath)
                    && File.Exists(page.CleanedPath))
                {
                    BitmapSource background = LoadBitmapSource(page.CleanedPath);
                    image = _exportService.Render(background, page.Regions);
                }
                else
                {
                    image = LoadBitmapSource(page.SourcePath);
                }

                ZipArchiveEntry entry = archive.CreateEntry($"{index + 1:D4}.png", CompressionLevel.Fastest);
                using Stream entryStream = entry.Open();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(entryStream);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            BusyProgressBar.Value = 100;
            FooterProgressBar.Value = 100;
            SetFooterStatus($"CBZ exportado · {Path.GetFileName(dialog.FileName)}", "#58A77D");
        }
        catch (OperationCanceledException)
        {
            SetFooterStatus("Exportación CBZ cancelada.", "#C99A35");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"No se pudo exportar el CBZ.\n\n{exception.Message}", "Tinta ES",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetFooterStatus("La exportación CBZ ha fallado.", "#EE594B");
        }
        finally
        {
            _comicBatchBusy = false;
            SetBusy(false);
            UpdateComicControls();
        }
    }
}
