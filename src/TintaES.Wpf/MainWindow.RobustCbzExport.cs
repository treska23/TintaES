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
        var fallbackPages = new List<string>();
        string outputPath = dialog.FileName;

        try
        {
            using FileStream output = File.Create(outputPath);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);

            for (int index = 0; index < _comicPages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ComicBookPageState page = _comicPages[index];
                BusyTitleText.Text = $"Exportando página {index + 1}/{_comicPages.Count}…";
                double progress = index / (double)_comicPages.Count * 100;
                BusyProgressBar.Value = progress;
                FooterProgressBar.Value = progress;
                FooterStatusText.Text = $"Renderizando página {index + 1} de {_comicPages.Count}…";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

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
                    // No dejamos que una sola composición defectuosa destruya todo el CBZ.
                    fallbackPages.Add($"Página {index + 1}: {exception.Message}");
                    image = LoadBitmapSource(page.SourcePath);
                }

                byte[] encoded;
                try
                {
                    encoded = EncodePng(image);
                }
                catch (Exception exception)
                {
                    fallbackPages.Add($"Página {index + 1} (codificación): {exception.Message}");
                    encoded = EncodePng(LoadBitmapSource(page.SourcePath));
                }

                ZipArchiveEntry entry = archive.CreateEntry($"{index + 1:D4}.png", CompressionLevel.Fastest);
                using Stream entryStream = entry.Open();
                await entryStream.WriteAsync(encoded, cancellationToken);
            }

            BusyProgressBar.Value = 100;
            FooterProgressBar.Value = 100;
            if (fallbackPages.Count == 0)
            {
                SetFooterStatus($"CBZ exportado · {Path.GetFileName(outputPath)}", "#58A77D");
            }
            else
            {
                SetFooterStatus($"CBZ exportado con {fallbackPages.Count} página(s) de respaldo.", "#C99A35");
                MessageBox.Show(
                    this,
                    "El CBZ se ha creado, pero algunas páginas no pudieron renderizar la rotulación y se incluyeron con su imagen original:\n\n" +
                    string.Join("\n", fallbackPages.Take(8)) +
                    (fallbackPages.Count > 8 ? $"\n… y {fallbackPages.Count - 8} más." : string.Empty),
                    "Tinta ES",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            TryDeleteIncompleteCbz(outputPath);
            SetFooterStatus("Exportación CBZ cancelada.", "#C99A35");
        }
        catch (Exception exception)
        {
            TryDeleteIncompleteCbz(outputPath);
            MessageBox.Show(this, $"No se pudo exportar el CBZ.\n\n{exception.Message}", "Tinta ES",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetFooterStatus("La exportación CBZ ha fallado.", "#EE594B");
        }
        finally
        {
            _comicBatchBusy = false;
            SetBusy(false);
            UpdateComicControls();
            UpdateProjectCommandAvailability();
            UpdatePsdExportAvailability();
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

    private static void TryDeleteIncompleteCbz(string path)
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
