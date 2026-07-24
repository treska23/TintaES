using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Guardado rápido de la página actual y atajos de edición convencionales. El guardado de
/// página sustituye únicamente sus entradas procesadas y el manifiesto del .tinta; no vuelve
/// a comprimir las imágenes fuente del resto del cómic.
/// </summary>
public partial class MainWindow
{
    private static readonly bool PageSaveAndShortcutsRegistered = RegisterPageSaveAndShortcuts();

    private Button? _saveCurrentPageButton;
    private MenuItem? _menuSaveCurrentPage;
    private bool _pageSaveAndShortcutsInstalled;
    private bool _pageSaveBusy;

    private static bool RegisterPageSaveAndShortcuts()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_PageSaveAndShortcutsLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_PageSaveAndShortcutsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallPageSaveAndShortcuts,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallPageSaveAndShortcuts()
    {
        if (_pageSaveAndShortcutsInstalled)
        {
            RefreshPageSaveAvailability();
            return;
        }

        _pageSaveAndShortcutsInstalled = true;
        PreviewKeyDown += MainWindow_PageSaveAndShortcutsPreviewKeyDown;
        LayoutUpdated += MainWindow_PageSaveLayoutUpdated;

        if (AddRegionButton.Parent is StackPanel toolbar)
        {
            Style? toolbarStyle = FindResource("ToolbarButton") as Style;
            _saveCurrentPageButton = new Button
            {
                Content = "Guardar página",
                Style = toolbarStyle,
                Margin = new Thickness(0, 0, 7, 0),
                ToolTip = "Guardar únicamente la página actual (Ctrl+S)"
            };
            _saveCurrentPageButton.Click += SaveCurrentPageButton_Click;

            int insertionIndex = toolbar.Children.IndexOf(AddRegionButton);
            toolbar.Children.Insert(Math.Max(0, insertionIndex), _saveCurrentPageButton);
        }

        TryInstallPageSaveMenuCommand();
        RefreshPageSaveAvailability();
    }

    private void MainWindow_PageSaveLayoutUpdated(object? sender, EventArgs e)
    {
        TryInstallPageSaveMenuCommand();
        RefreshPageSaveAvailability();
    }

    private void TryInstallPageSaveMenuCommand()
    {
        if (_menuSaveCurrentPage is not null || _classicMenu is null)
        {
            return;
        }

        MenuItem? fileMenu = _classicMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => item.Header?.ToString()?.Contains("Archivo", StringComparison.OrdinalIgnoreCase) == true);
        if (fileMenu is null)
        {
            return;
        }

        _menuSaveCurrentPage = CreateMenuItem("Guardar _página", "Ctrl+S", SaveCurrentPageButton_Click);
        int projectIndex = _menuSaveProject is null ? -1 : fileMenu.Items.IndexOf(_menuSaveProject);
        fileMenu.Items.Insert(projectIndex >= 0 ? projectIndex : Math.Min(3, fileMenu.Items.Count), _menuSaveCurrentPage);

        if (_menuSaveProject is not null)
        {
            _menuSaveProject.InputGestureText = "Ctrl+Mayús+S";
        }
        if (_menuSaveProjectAs is not null)
        {
            _menuSaveProjectAs.InputGestureText = "Ctrl+Alt+S";
        }
    }

    private async void SaveCurrentPageButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentPageAsync();
    }

    private async Task SaveCurrentPageAsync()
    {
        if (_pageSaveBusy
            || _comicPageIndex < 0
            || _comicPageIndex >= _comicPages.Count
            || _comicBatchBusy
            || _pageNavigationBusy)
        {
            return;
        }

        // La primera vez sí hay que crear el contenedor completo. A partir de ahí Ctrl+S es
        // incremental y solo toca la página visible.
        if (string.IsNullOrWhiteSpace(_currentProjectPath) || !File.Exists(_currentProjectPath))
        {
            SetFooterStatus("El proyecto todavía no tiene archivo. Elige dónde crearlo una sola vez.", "#C99A35");
            SaveProjectButton_Click(this, new RoutedEventArgs());
            return;
        }

        PersistVisibleComicPageRegions();
        int pageIndex = _comicPageIndex;
        ComicBookPageState page = _comicPages[pageIndex];
        TintaProjectManifest manifest = BuildIncrementalProjectManifest(pageIndex);
        var saveData = new IncrementalPageSaveData(
            _currentProjectPath,
            pageIndex,
            page.CleanedPath,
            page.MaskPath,
            manifest);

        _pageSaveBusy = true;
        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = true;
        FooterStatusText.Text = $"Guardando únicamente la página {pageIndex + 1}…";
        RefreshPageSaveAvailability();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            await Task.Run(() => WriteIncrementalPageToProject(saveData));
            SetFooterStatus($"Página {pageIndex + 1} guardada · el resto del proyecto no se ha reempaquetado.", "#58A77D");
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"No se pudo guardar la página actual.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetFooterStatus("No se pudo guardar la página actual.", "#EE594B");
        }
        finally
        {
            _pageSaveBusy = false;
            FooterProgressBar.IsIndeterminate = false;
            FooterProgressBar.Visibility = Visibility.Collapsed;
            RefreshPageSaveAvailability();
        }
    }

    private TintaProjectManifest BuildIncrementalProjectManifest(int currentPageIndex)
    {
        var manifest = new TintaProjectManifest
        {
            Version = 1,
            Title = _comicTitle ?? "comic",
            CurrentPageIndex = Math.Clamp(currentPageIndex, 0, Math.Max(0, _comicPages.Count - 1))
        };

        for (int index = 0; index < _comicPages.Count; index++)
        {
            ComicBookPageState page = _comicPages[index];
            string sourceExtension = Path.GetExtension(page.SourcePath);
            manifest.Pages.Add(new TintaProjectPage
            {
                DisplayName = page.DisplayName,
                SourceFile = $"source/{index + 1:D4}{sourceExtension}",
                CleanedFile = !string.IsNullOrWhiteSpace(page.CleanedPath) && File.Exists(page.CleanedPath)
                    ? $"processed/{index + 1:D4}-clean.png"
                    : null,
                MaskFile = !string.IsNullOrWhiteSpace(page.MaskPath) && File.Exists(page.MaskPath)
                    ? $"processed/{index + 1:D4}-mask.png"
                    : null,
                SourceLanguage = page.SourceLanguage,
                Processed = page.Processed,
                Error = page.Error,
                Regions = page.Regions.ToList()
            });
        }

        return manifest;
    }

    private static void WriteIncrementalPageToProject(IncrementalPageSaveData data)
    {
        using FileStream stream = new(
            data.ProjectPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.RandomAccess);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false);

        int number = data.PageIndex + 1;
        ReplacePageArchiveEntry(
            archive,
            $"processed/{number:D4}-clean.png",
            data.CleanedPath);
        ReplacePageArchiveEntry(
            archive,
            $"processed/{number:D4}-mask.png",
            data.MaskPath);

        archive.GetEntry("project.json")?.Delete();
        ZipArchiveEntry manifestEntry = archive.CreateEntry("project.json", CompressionLevel.Fastest);
        using Stream manifestStream = manifestEntry.Open();
        JsonSerializer.Serialize(manifestStream, data.Manifest, ProjectJsonOptions);
    }

    private static void ReplacePageArchiveEntry(ZipArchive archive, string entryName, string? filePath)
    {
        archive.GetEntry(entryName)?.Delete();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        // PNG ya está comprimido. NoCompression evita gastar CPU intentando comprimirlo otra vez.
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using Stream input = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        using Stream output = entry.Open();
        input.CopyTo(output, 1024 * 1024);
    }

    private void MainWindow_PageSaveAndShortcutsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool control = modifiers.HasFlag(ModifierKeys.Control);
        if (!control)
        {
            return;
        }

        if (e.Key == Key.S)
        {
            if (modifiers.HasFlag(ModifierKeys.Alt))
            {
                SaveProjectAsMenu_Click(this, new RoutedEventArgs());
            }
            else if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                SaveProjectButton_Click(this, new RoutedEventArgs());
            }
            else
            {
                _ = SaveCurrentPageAsync();
            }
            e.Handled = true;
            return;
        }

        if (e.Key is Key.OemPlus or Key.Add)
        {
            ChangeZoomBy(10);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.OemMinus or Key.Subtract)
        {
            ChangeZoomBy(-10);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.D0 or Key.NumPad0)
        {
            FitImageToViewport();
            e.Handled = true;
        }
    }

    private void ChangeZoomBy(double delta)
    {
        if (_originalBitmap is null)
        {
            return;
        }

        ZoomSlider.Value = Math.Clamp(
            ZoomSlider.Value + delta,
            ZoomSlider.Minimum,
            ZoomSlider.Maximum);
    }

    private void RefreshPageSaveAvailability()
    {
        bool available = _comicPages.Count > 0
            && _comicPageIndex >= 0
            && _comicPageIndex < _comicPages.Count
            && !_comicBatchBusy
            && !_pageNavigationBusy
            && !_pageSaveBusy;

        if (_saveCurrentPageButton is not null)
        {
            _saveCurrentPageButton.IsEnabled = available;
        }
        if (_menuSaveCurrentPage is not null)
        {
            _menuSaveCurrentPage.IsEnabled = available;
        }
    }

    private sealed record IncrementalPageSaveData(
        string ProjectPath,
        int PageIndex,
        string? CleanedPath,
        string? MaskPath,
        TintaProjectManifest Manifest);
}
