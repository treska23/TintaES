using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        BusyTitleText.Text = "Preparando exportación reanudable…";
        BusyProgressBar.IsIndeterminate = false;
        FooterProgressBar.IsIndeterminate = false;
        BusyProgressBar.Value = 0;
        FooterProgressBar.Value = 0;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        var fallbackPages = new List<string>();
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
                    ? $"Reutilizando una página preparada de una exportación interrumpida…"
                    : $"Preparando página {pageIndex + 1} de {_comicPages.Count}…";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                if (!reusable)
                {
                    BitmapSource image = RenderComicPageForCbz(pageIndex, page, fallbackPages);
                    await Task.Run(
                        () => SavePngAtomically(image, stagePath, cancellationToken),
                        cancellationToken);

                    stageManifest.Pages[entryName] = fingerprint;
                    await Task.Run(
                        () => SaveCbzStageManifest(stagingManifestPath, stageManifest),
                        cancellationToken);
                }

                stagedPages[pageIndex] = stagePath;
                double progress = (position + 1d) / selectedPages.Count * 88;
                BusyProgressBar.Value = progress;
                FooterProgressBar.Value = progress;
                FooterStatusText.Text = $"Preparadas {position + 1} de {selectedPages.Count} páginas.";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            cancellationToken.ThrowIfCancellationRequested();
            BusyTitleText.Text = "Montando el CBZ una sola vez…";
            FooterStatusText.Text = "Conservando páginas anteriores y añadiendo las seleccionadas…";
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
            MarkComicPagesExported(selectedPages);
            CleanupCommittedCbzStaging(stagingDirectory, stagingManifestPath, selectedPages);

            BusyProgressBar.Value = 100;
            FooterProgressBar.Value = 100;
            if (fallbackPages.Count == 0)
            {
                SetFooterStatus(
                    $"CBZ actualizado · {selectedPages.Count} página(s) · {Path.GetFileName(outputPath)}",
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
            TryDeleteTemporaryCbz(buildTemporaryPath);
            MessageBox.Show(
                this,
                "La exportación se ha cancelado.\n\nEl CBZ anterior sigue intacto y las páginas ya preparadas se reutilizarán cuando vuelvas a exportar al mismo archivo.",
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

    private BitmapSource RenderComicPageForCbz(
        int index,
        ComicBookPageState page,
        List<string> fallbackPages)
    {
        try
        {
            if (page.Processed
                && page.Error is null
                && !string.IsNullOrWhiteSpace(page.CleanedPath)
                && File.Exists(page.CleanedPath))
            {
                BitmapSource background = LoadBitmapSource(page.CleanedPath);
                return _exportService.Render(background, page.Regions);
            }

            return LoadBitmapSource(page.SourcePath);
        }
        catch (Exception exception)
        {
            fallbackPages.Add($"Página {index + 1}: {exception.Message}");
            return LoadBitmapSource(page.SourcePath);
        }
    }

    private static void SavePngAtomically(BitmapSource image, string targetPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string temporaryPath = targetPath + ".tmp";
        TryDeleteTemporaryCbz(temporaryPath);
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 1024 * 1024,
                       FileOptions.SequentialScan))
            {
                encoder.Save(stream);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDeleteTemporaryCbz(temporaryPath);
            throw;
        }
    }

    private static void BuildFinalCbz(
        string? existingCbzPath,
        string temporaryPath,
        IReadOnlyDictionary<int, string> stagedPages,
        CancellationToken cancellationToken)
    {
        var replacementEntries = new HashSet<string>(
            stagedPages.Keys.Select(GetCbzPageEntryName),
            StringComparer.OrdinalIgnoreCase);

        using FileStream output = new(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        using var destinationArchive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);

        if (!string.IsNullOrWhiteSpace(existingCbzPath) && File.Exists(existingCbzPath))
        {
            using FileStream existingStream = new(
                existingCbzPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
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
                    CompressionLevel.NoCompression);
                copiedEntry.LastWriteTime = existingEntry.LastWriteTime;
                if (string.IsNullOrEmpty(existingEntry.Name))
                {
                    continue;
                }

                using Stream source = existingEntry.Open();
                using Stream target = copiedEntry.Open();
                source.CopyTo(target, 1024 * 1024);
            }
        }

        foreach ((int pageIndex, string stagePath) in stagedPages.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = destinationArchive.CreateEntry(
                GetCbzPageEntryName(pageIndex),
                CompressionLevel.NoCompression);
            using FileStream source = new(
                stagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
            using Stream target = entry.Open();
            source.CopyTo(target, 1024 * 1024);
        }
    }

    private string CreateCbzPageFingerprint(int pageIndex, ComicBookPageState page)
    {
        var identity = new StringBuilder();
        identity.Append("cbz-stage-v2|").Append(pageIndex).Append('|');
        AppendFileIdentity(identity, page.SourcePath);
        AppendFileIdentity(identity, page.CleanedPath);
        identity.Append('|').Append(page.Processed).Append('|').Append(page.Error);
        identity.Append('|').Append(JsonSerializer.Serialize(page.Regions, ProjectJsonOptions));
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()));
        return Convert.ToHexString(digest);
    }

    private static void AppendFileIdentity(StringBuilder identity, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            identity.Append("missing|");
            return;
        }

        var file = new FileInfo(path);
        identity.Append(file.FullName)
            .Append('|')
            .Append(file.Length)
            .Append('|')
            .Append(file.LastWriteTimeUtc.Ticks)
            .Append('|');
    }

    private static string GetCbzStagingDirectory(string outputPath)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(outputPath).ToUpperInvariant()));
        string key = Convert.ToHexString(digest)[..24].ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TintaES",
            "ExportStaging",
            key);
    }

    private static CbzStageManifest LoadCbzStageManifest(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new CbzStageManifest();
            }

            return JsonSerializer.Deserialize<CbzStageManifest>(File.ReadAllText(path, Encoding.UTF8))
                   ?? new CbzStageManifest();
        }
        catch (JsonException)
        {
            return new CbzStageManifest();
        }
    }

    private static void SaveCbzStageManifest(string path, CbzStageManifest manifest)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(manifest),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void CleanupCommittedCbzStaging(
        string stagingDirectory,
        string manifestPath,
        IEnumerable<int> committedPages)
    {
        CbzStageManifest manifest = LoadCbzStageManifest(manifestPath);
        foreach (int pageIndex in committedPages)
        {
            string entryName = GetCbzPageEntryName(pageIndex);
            manifest.Pages.Remove(entryName);
            TryDeleteTemporaryCbz(Path.Combine(stagingDirectory, entryName));
        }

        if (manifest.Pages.Count == 0)
        {
            TryDeleteTemporaryCbz(manifestPath);
            try
            {
                if (Directory.Exists(stagingDirectory)
                    && !Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
                {
                    Directory.Delete(stagingDirectory);
                }
            }
            catch
            {
            }
        }
        else
        {
            SaveCbzStageManifest(manifestPath, manifest);
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

    private sealed class CbzStageManifest
    {
        public Dictionary<string, string> Pages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
