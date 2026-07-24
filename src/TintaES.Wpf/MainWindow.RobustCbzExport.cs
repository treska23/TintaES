using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private bool _robustCbzExportInstalled;

    private void InstallRobustCbzExport()
    {
        if (_robustCbzExportInstalled || _exportComicButton is null)
        {
            return;
        }

        _robustCbzExportInstalled = true;
        _exportComicButton.Click -= ExportComicButton_Click;
        _exportComicButton.Click += ExportComicButton_Click_Robust;
    }

    private async void ExportComicButton_Click_Robust(object sender, RoutedEventArgs e)
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
        BusyTitleText.Text = "Preparando exportación segura…";
        BusyProgressBar.IsIndeterminate = false;
        FooterProgressBar.IsIndeterminate = false;

        var fallbackPages = new List<string>();
        var committedPages = new List<int>();
        string batchTemporaryPath = outputPath + ".tinta-batch.tmp";

        try
        {
            int[][] batches = selectedPages
                .Chunk(SafeExportBatchSize)
                .Select(chunk => chunk.ToArray())
                .ToArray();

            for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int[] batch = batches[batchIndex];
                TryDeleteTemporaryCbz(batchTemporaryPath);

                BusyTitleText.Text = $"Lote {batchIndex + 1}/{batches.Length} · páginas {batch[0] + 1}–{batch[^1] + 1}";
                FooterStatusText.Text = $"Creando punto de control {batchIndex + 1} de {batches.Length}…";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                await BuildCbzCheckpointAsync(
                    File.Exists(outputPath) ? outputPath : null,
                    batchTemporaryPath,
                    batch,
                    selectedPages.Count,
                    committedPages.Count,
                    fallbackPages,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                CommitCbzCheckpoint(batchTemporaryPath, outputPath);
                committedPages.AddRange(batch);
                MarkComicPagesExported(batch);

                double committedProgress = committedPages.Count / (double)selectedPages.Count * 100;
                BusyProgressBar.Value = committedProgress;
                FooterProgressBar.Value = committedProgress;
                FooterStatusText.Text = $"Guardadas {committedPages.Count} de {selectedPages.Count} páginas seleccionadas.";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                // Liberamos imágenes grandes entre puntos de control. No es necesario conservar
                // ningún RenderTargetBitmap del lote anterior para continuar el CBZ.
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
            }

            BusyProgressBar.Value = 100;
            FooterProgressBar.Value = 100;
            if (fallbackPages.Count == 0)
            {
                SetFooterStatus(
                    $"CBZ actualizado · {committedPages.Count} página(s) · {Path.GetFileName(outputPath)}",
                    "#58A77D");
            }
            else
            {
                SetFooterStatus(
                    $"CBZ actualizado con {fallbackPages.Count} página(s) de respaldo.",
                    "#C99A35");
                MessageBox.Show(
                    this,
                    "El CBZ se ha actualizado, pero algunas páginas no pudieron renderizar la rotulación y se incluyeron con su imagen original:\n\n" +
                    string.Join("\n", fallbackPages.Take(8)) +
                    (fallbackPages.Count > 8 ? $"\n… y {fallbackPages.Count - 8} más." : string.Empty),
                    "Tinta ES",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            SelectNextComicPageBatchAfter(selectedPages[^1]);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemporaryCbz(batchTemporaryPath);
            string detail = committedPages.Count == 0
                ? "No se llegó a confirmar ninguna página nueva."
                : $"Las {committedPages.Count} páginas ya confirmadas siguen guardadas correctamente en el CBZ.";
            MessageBox.Show(
                this,
                $"La exportación se ha cancelado.\n\n{detail}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            SetFooterStatus("Exportación CBZ cancelada sin dañar el archivo anterior.", "#C99A35");
        }
        catch (Exception exception)
        {
            TryDeleteTemporaryCbz(batchTemporaryPath);
            string detail = committedPages.Count == 0
                ? "El CBZ anterior no se ha modificado."
                : $"Las {committedPages.Count} páginas de los lotes anteriores permanecen guardadas.";
            MessageBox.Show(
                this,
                $"No se pudo terminar la exportación CBZ.\n\n{exception.Message}\n\n{detail}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetFooterStatus("La exportación CBZ se detuvo en un punto de control seguro.", "#EE594B");
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

    private async Task BuildCbzCheckpointAsync(
        string? existingCbzPath,
        string temporaryPath,
        IReadOnlyCollection<int> batchPages,
        int totalSelected,
        int alreadyCommitted,
        List<string> fallbackPages,
        CancellationToken cancellationToken)
    {
        var replacementEntries = new HashSet<string>(
            batchPages.Select(index => GetCbzPageEntryName(index)),
            StringComparer.OrdinalIgnoreCase);

        using FileStream output = File.Create(temporaryPath);
        using var destinationArchive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);

        if (!string.IsNullOrWhiteSpace(existingCbzPath) && File.Exists(existingCbzPath))
        {
            using FileStream existingStream = File.OpenRead(existingCbzPath);
            using var existingArchive = new ZipArchive(existingStream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (ZipArchiveEntry existingEntry in existingArchive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (replacementEntries.Contains(existingEntry.FullName))
                {
                    continue;
                }

                ZipArchiveEntry copiedEntry = destinationArchive.CreateEntry(
                    existingEntry.FullName,
                    CompressionLevel.Fastest);
                copiedEntry.LastWriteTime = existingEntry.LastWriteTime;
                if (string.IsNullOrEmpty(existingEntry.Name))
                {
                    continue;
                }

                using Stream source = existingEntry.Open();
                using Stream target = copiedEntry.Open();
                await source.CopyToAsync(target, cancellationToken);
            }
        }

        int completedInsideBatch = 0;
        foreach (int index in batchPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComicBookPageState page = _comicPages[index];
            int globalCompleted = alreadyCommitted + completedInsideBatch;
            double progress = globalCompleted / (double)Math.Max(1, totalSelected) * 100;
            BusyProgressBar.Value = progress;
            FooterProgressBar.Value = progress;
            BusyTitleText.Text = $"Exportando página {index + 1} · {globalCompleted + 1}/{totalSelected}";
            FooterStatusText.Text = $"Renderizando página {index + 1} de {_comicPages.Count}…";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            byte[] encoded = RenderComicPageForCbz(index, page, fallbackPages);
            ZipArchiveEntry entry = destinationArchive.CreateEntry(
                GetCbzPageEntryName(index),
                CompressionLevel.Fastest);
            using Stream entryStream = entry.Open();
            await entryStream.WriteAsync(encoded, cancellationToken);
            completedInsideBatch++;

            // Cedemos el hilo entre páginas para que Cancelar y el progreso sigan respondiendo.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private byte[] RenderComicPageForCbz(int index, ComicBookPageState page, List<string> fallbackPages)
    {
        BitmapSource image;
        try
        {
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
        }
        catch (Exception exception)
        {
            fallbackPages.Add($"Página {index + 1}: {exception.Message}");
            image = LoadBitmapSource(page.SourcePath);
        }

        try
        {
            return EncodePng(image);
        }
        catch (Exception exception)
        {
            fallbackPages.Add($"Página {index + 1} (codificación): {exception.Message}");
            return EncodePng(LoadBitmapSource(page.SourcePath));
        }
    }

    private static string GetCbzPageEntryName(int pageIndex) => $"{pageIndex + 1:D4}.png";

    private static void CommitCbzCheckpoint(string temporaryPath, string outputPath)
    {
        string backupPath = outputPath + ".tinta-backup";
        TryDeleteTemporaryCbz(backupPath);

        if (!File.Exists(outputPath))
        {
            File.Move(temporaryPath, outputPath);
            return;
        }

        try
        {
            File.Replace(temporaryPath, outputPath, backupPath, ignoreMetadataErrors: true);
            TryDeleteTemporaryCbz(backupPath);
        }
        catch
        {
            // Recuperación para unidades o sistemas de archivos que no admiten File.Replace.
            try
            {
                File.Move(outputPath, backupPath, overwrite: true);
                File.Move(temporaryPath, outputPath);
                TryDeleteTemporaryCbz(backupPath);
            }
            catch
            {
                if (!File.Exists(outputPath) && File.Exists(backupPath))
                {
                    File.Move(backupPath, outputPath);
                }
                throw;
            }
        }
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    private static void TryDeleteTemporaryCbz(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
