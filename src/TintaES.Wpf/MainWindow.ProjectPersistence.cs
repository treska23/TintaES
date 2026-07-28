using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using TintaES.Core;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private static readonly JsonSerializerOptions ProjectJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private Button? _saveProjectButton;
    private string? _currentProjectPath;

    private void InstallProjectCommands()
    {
        if (_saveProjectButton is not null || ExportButton.Parent is not StackPanel exportPanel)
        {
            return;
        }

        Style? toolbarStyle = FindResource("ToolbarButton") as Style;
        _saveProjectButton = new Button
        {
            Content = "Guardar proyecto",
            Style = toolbarStyle,
            Margin = new Thickness(0, 0, 7, 0),
            IsEnabled = false,
            ToolTip = "Guardar el trabajo editable de TintaES"
        };
        _saveProjectButton.Click += SaveProjectButton_Click;

        int insertIndex = _exportComicButton is not null
            ? exportPanel.Children.IndexOf(_exportComicButton)
            : exportPanel.Children.IndexOf(ExportButton);
        exportPanel.Children.Insert(Math.Max(0, insertIndex), _saveProjectButton);
        UpdateProjectCommandAvailability();
    }

    private void UpdateProjectCommandAvailability()
    {
        if (_saveProjectButton is not null)
        {
            _saveProjectButton.IsEnabled = _comicPages.Count > 0 && !_comicBatchBusy && !_pageNavigationBusy;
        }
    }

    private async void SaveProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0)
        {
            return;
        }

        PersistVisibleComicPageRegions();
        string? targetPath = _currentProjectPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            var dialog = new SaveFileDialog
            {
                Title = "Guardar proyecto de TintaES",
                FileName = MakeSafeFileName(_comicTitle ?? "comic") + ".tinta",
                DefaultExt = ".tinta",
                Filter = "Proyecto TintaES|*.tinta"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            targetPath = dialog.FileName;
        }

        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitleText.Text = "Guardando proyecto…";
        BusyProgressBar.IsIndeterminate = true;
        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = true;
        UpdateProjectCommandAvailability();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            string finalPath = targetPath;
            await Task.Run(() => WriteTintaProject(finalPath));
            _currentProjectPath = finalPath;
            MarkActiveDocumentSaved();
            SetFooterStatus($"Proyecto guardado · {Path.GetFileName(finalPath)}", "#58A77D");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"No se pudo guardar el proyecto.\n\n{exception.Message}", "Tinta ES",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetFooterStatus("No se pudo guardar el proyecto.", "#EE594B");
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            BusyProgressBar.IsIndeterminate = false;
            FooterProgressBar.Visibility = Visibility.Collapsed;
            FooterProgressBar.IsIndeterminate = false;
            UpdateProjectCommandAvailability();
        }
    }

    private void WriteTintaProject(string targetPath)
    {
        string temporaryPath = targetPath + ".tmp";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        var manifest = new TintaProjectManifest
        {
            Version = 1,
            Title = _comicTitle ?? "comic",
            CurrentPageIndex = Math.Clamp(_comicPageIndex, 0, Math.Max(0, _comicPages.Count - 1))
        };

        using (FileStream output = File.Create(temporaryPath))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
        {
            for (int index = 0; index < _comicPages.Count; index++)
            {
                ComicBookPageState page = _comicPages[index];
                string sourceExtension = Path.GetExtension(page.SourcePath);
                string sourceEntry = $"source/{index + 1:D4}{sourceExtension}";
                AddFileToArchive(archive, page.SourcePath, sourceEntry);

                string? cleanedEntry = null;
                if (!string.IsNullOrWhiteSpace(page.CleanedPath) && File.Exists(page.CleanedPath))
                {
                    cleanedEntry = $"processed/{index + 1:D4}-clean.png";
                    AddFileToArchive(archive, page.CleanedPath, cleanedEntry);
                }

                string? maskEntry = null;
                if (!string.IsNullOrWhiteSpace(page.MaskPath) && File.Exists(page.MaskPath))
                {
                    maskEntry = $"processed/{index + 1:D4}-mask.png";
                    AddFileToArchive(archive, page.MaskPath, maskEntry);
                }

                manifest.Pages.Add(new TintaProjectPage
                {
                    DisplayName = page.DisplayName,
                    SourceFile = sourceEntry,
                    CleanedFile = cleanedEntry,
                    MaskFile = maskEntry,
                    SourceLanguage = page.SourceLanguage,
                    Processed = page.Processed,
                    Error = page.Error,
                    Regions = page.Regions.ToList()
                });
            }

            ZipArchiveEntry manifestEntry = archive.CreateEntry("project.json", CompressionLevel.Optimal);
            using Stream stream = manifestEntry.Open();
            JsonSerializer.Serialize(stream, manifest, ProjectJsonOptions);
        }

        File.Move(temporaryPath, targetPath, overwrite: true);
    }

    private static void AddFileToArchive(ZipArchive archive, string filePath, string entryName)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        using Stream input = File.OpenRead(filePath);
        using Stream output = entry.Open();
        input.CopyTo(output);
    }

    private async Task LoadTintaProjectAsync(string projectPath)
    {
        await AwaitCurrentDocumentReadyForOpenAsync();
        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitleText.Text = "Abriendo proyecto de TintaES…";
        BusyProgressBar.IsIndeterminate = true;
        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            PrepareNewComicWorkspace();
            string workspace = _comicWorkspace ?? throw new InvalidOperationException("No se pudo preparar el espacio del proyecto.");
            TintaProjectManifest manifest = await Task.Run(() => ExtractTintaProject(projectPath, workspace));

            _comicPages.Clear();
            foreach (TintaProjectPage storedPage in manifest.Pages)
            {
                string sourcePath = Path.Combine(workspace, storedPage.SourceFile.Replace('/', Path.DirectorySeparatorChar));
                var page = new ComicBookPageState(sourcePath, storedPage.DisplayName)
                {
                    SourceLanguage = storedPage.SourceLanguage,
                    CleanedPath = string.IsNullOrWhiteSpace(storedPage.CleanedFile)
                        ? null
                        : Path.Combine(workspace, storedPage.CleanedFile.Replace('/', Path.DirectorySeparatorChar)),
                    MaskPath = string.IsNullOrWhiteSpace(storedPage.MaskFile)
                        ? null
                        : Path.Combine(workspace, storedPage.MaskFile.Replace('/', Path.DirectorySeparatorChar)),
                    Processed = storedPage.Processed,
                    Error = storedPage.Error
                };
                page.Regions.AddRange(storedPage.Regions ?? []);
                _comicPages.Add(page);
            }

            if (_comicPages.Count == 0)
            {
                throw new InvalidOperationException("El proyecto no contiene páginas.");
            }

            _comicTitle = manifest.Title;
            _currentProjectPath = projectPath;
            _comicPageIndex = Math.Clamp(manifest.CurrentPageIndex, 0, _comicPages.Count - 1);
            _visibleComicPageIndex = -1;
            SynchronizeActiveDocumentState();
            ClearComicPageBitmapCache();
            UpdateComicControls();
            SyncDirectPageSelector();
            await ShowComicPageFastAsync(_comicPageIndex);
            SetFooterStatus($"Proyecto abierto · {_comicPages.Count} páginas", "#58A77D");
        }
        catch (Exception exception)
        {
            AbandonEmptyDocumentAfterOpenFailure();
            MessageBox.Show(this, $"No se pudo abrir el proyecto.\n\n{exception.Message}", "Tinta ES",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetFooterStatus("No se pudo abrir el proyecto.", "#EE594B");
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            BusyProgressBar.IsIndeterminate = false;
            FooterProgressBar.Visibility = Visibility.Collapsed;
            FooterProgressBar.IsIndeterminate = false;
            UpdateProjectCommandAvailability();
        }
    }

    private static TintaProjectManifest ExtractTintaProject(string projectPath, string workspace)
    {
        using FileStream input = File.OpenRead(projectPath);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry manifestEntry = archive.GetEntry("project.json")
            ?? throw new InvalidOperationException("El archivo no contiene un manifiesto de proyecto válido.");

        TintaProjectManifest manifest;
        using (Stream manifestStream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<TintaProjectManifest>(manifestStream, ProjectJsonOptions)
                ?? throw new InvalidOperationException("No se pudo leer el manifiesto del proyecto.");
        }

        string workspaceRoot = Path.GetFullPath(workspace) + Path.DirectorySeparatorChar;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || string.Equals(entry.FullName, "project.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string target = Path.GetFullPath(Path.Combine(workspace, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("El proyecto contiene una ruta no válida.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream entryStream = entry.Open();
            using FileStream output = File.Create(target);
            entryStream.CopyTo(output);
        }

        return manifest;
    }

    private sealed class TintaProjectManifest
    {
        public int Version { get; set; }
        public string Title { get; set; } = "comic";
        public int CurrentPageIndex { get; set; }
        public List<TintaProjectPage> Pages { get; set; } = [];
    }

    private sealed class TintaProjectPage
    {
        public string DisplayName { get; set; } = string.Empty;
        public string SourceFile { get; set; } = string.Empty;
        public string? CleanedFile { get; set; }
        public string? MaskFile { get; set; }
        public string SourceLanguage { get; set; } = "en";
        public bool Processed { get; set; }
        public string? Error { get; set; }
        public List<ComicRegion>? Regions { get; set; }
    }
}
