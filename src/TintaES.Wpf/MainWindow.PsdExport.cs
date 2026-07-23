using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private Button? _exportPsdButton;

    private void InstallPsdExportCommand()
    {
        if (_exportPsdButton is not null || ExportButton.Parent is not StackPanel exportPanel)
        {
            return;
        }

        Style? toolbarStyle = FindResource("ToolbarButton") as Style;
        _exportPsdButton = new Button
        {
            Content = "Exportar PSD",
            Style = toolbarStyle,
            Margin = new Thickness(0, 0, 7, 0),
            ToolTip = "Exportar la página actual con el fondo limpio y textos editables",
            IsEnabled = false
        };
        _exportPsdButton.Click += ExportPsdButton_Click;

        int exportPngIndex = exportPanel.Children.IndexOf(ExportButton);
        exportPanel.Children.Insert(Math.Max(0, exportPngIndex), _exportPsdButton);
        if (_pageCounterText is not null)
        {
            _pageCounterText.LayoutUpdated += (_, _) => UpdatePsdExportAvailability();
        }
        InstallRobustCbzExport();
        UpdatePsdExportAvailability();
    }

    private void UpdatePsdExportAvailability()
    {
        if (_exportPsdButton is null)
        {
            return;
        }

        bool processed = _comicPageIndex >= 0
            && _comicPageIndex < _comicPages.Count
            && _comicPages[_comicPageIndex].Processed
            && _comicPages[_comicPageIndex].Error is null;
        _exportPsdButton.IsEnabled = processed && !_comicBatchBusy && !_pageNavigationBusy;
    }

    private async void ExportPsdButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPageIndex < 0 || _comicPageIndex >= _comicPages.Count)
        {
            return;
        }

        PersistVisibleComicPageRegions();
        ComicBookPageState page = _comicPages[_comicPageIndex];
        if (!page.Processed || page.Error is not null || _cleanedBitmap is null)
        {
            MessageBox.Show(this, "Esta página todavía no tiene un fondo limpio procesado.", "Tinta ES",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar página a Photoshop PSD",
            FileName = Path.GetFileNameWithoutExtension(page.DisplayName) + "-es.psd",
            DefaultExt = ".psd",
            Filter = "Adobe Photoshop|*.psd"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitleText.Text = "Preparando PSD editable…";
        BusyProgressBar.IsIndeterminate = true;
        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = true;
        UpdatePsdExportAvailability();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        string? temporaryBackground = null;
        string? temporaryRegions = null;
        try
        {
            string projectRoot = FindPsdProjectRoot();
            string pythonPath = Path.Combine(projectRoot, "engine", "manga-image-translator", ".venv", "Scripts", "python.exe");
            string exporterPath = Path.Combine(projectRoot, "engine", "export_psd.py");
            if (!File.Exists(pythonPath))
            {
                throw new InvalidOperationException("No se encuentra el Python local del motor de TintaES.");
            }
            if (!File.Exists(exporterPath))
            {
                throw new InvalidOperationException("No se encuentra el exportador PSD de TintaES.");
            }

            BusyTitleText.Text = "Comprobando soporte PSD…";
            await EnsurePhotoshopApiAsync(pythonPath);

            string workspace = _comicWorkspace ?? Path.GetTempPath();
            string psdTemp = Path.Combine(workspace, "psd-export");
            Directory.CreateDirectory(psdTemp);
            temporaryBackground = Path.Combine(psdTemp, $"page-{_comicPageIndex + 1:D4}-background.png");
            temporaryRegions = Path.Combine(psdTemp, $"page-{_comicPageIndex + 1:D4}-regions.json");
            SaveBitmap(_cleanedBitmap, temporaryBackground);
            await File.WriteAllTextAsync(
                temporaryRegions,
                JsonSerializer.Serialize(page.Regions, ProjectJsonOptions),
                Encoding.UTF8);

            BusyTitleText.Text = "Creando capas editables de Photoshop…";
            FooterStatusText.Text = "Exportando PSD con una capa de texto por bocadillo…";
            ProcessResult result = await RunPythonAsync(
                pythonPath,
                exporterPath,
                "--background", temporaryBackground,
                "--regions", temporaryRegions,
                "--output", dialog.FileName);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
                    ? "El exportador PSD terminó con un error desconocido."
                    : result.StandardError.Trim());
            }

            SetFooterStatus($"PSD exportado · {Path.GetFileName(dialog.FileName)}", "#58A77D");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"No se pudo exportar el PSD.\n\n{exception.Message}", "Tinta ES",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetFooterStatus("La exportación PSD ha fallado.", "#EE594B");
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryBackground);
            TryDeleteTemporaryFile(temporaryRegions);
            BusyOverlay.Visibility = Visibility.Collapsed;
            BusyProgressBar.IsIndeterminate = false;
            FooterProgressBar.Visibility = Visibility.Collapsed;
            FooterProgressBar.IsIndeterminate = false;
            UpdatePsdExportAvailability();
        }
    }

    private async Task EnsurePhotoshopApiAsync(string pythonPath)
    {
        ProcessResult probe = await RunPythonAsync(pythonPath, "-c", "import photoshopapi");
        if (probe.ExitCode == 0)
        {
            return;
        }

        BusyTitleText.Text = "Instalando soporte PSD por primera vez…";
        FooterStatusText.Text = "Preparando PhotoshopAPI en el entorno local…";
        ProcessResult install = await RunPythonAsync(pythonPath, "-m", "pip", "install", "PhotoshopAPI");
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "No se pudo instalar PhotoshopAPI automáticamente. Comprueba la conexión a Internet y vuelve a intentar.\n" +
                install.StandardError.Trim());
        }
    }

    private static async Task<ProcessResult> RunPythonAsync(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("No se pudo iniciar Python.");
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static string FindPsdProjectRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(start);
            for (int depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "engine", "export_psd.py")))
                {
                    return directory.FullName;
                }
            }
        }
        throw new InvalidOperationException("No se pudo localizar la carpeta engine de TintaES.");
    }

    private static void TryDeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
