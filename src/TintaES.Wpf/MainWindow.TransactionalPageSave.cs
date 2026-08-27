using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Protege el guardado incremental de página frente a cortes, excepciones o cierres inesperados.
/// El guardado antiguo abría el .tinta real con ZipArchiveMode.Update; si la operación no llegaba
/// a cerrar correctamente el ZIP, podía perderse el End Of Central Directory y el proyecto entero
/// quedaba ilegible. Esta capa intercepta únicamente "Guardar página" y trabaja siempre sobre una
/// copia temporal validada antes de sustituir el proyecto original.
/// </summary>
public partial class MainWindow
{
    private static readonly bool TransactionalPageSaveRegistered = RegisterTransactionalPageSave();

    private static bool RegisterTransactionalPageSave()
    {
        EventManager.RegisterClassHandler(
            typeof(Button),
            ButtonBase.ClickEvent,
            new RoutedEventHandler(TransactionalPageSaveButton_Click),
            handledEventsToo: true);

        EventManager.RegisterClassHandler(
            typeof(MenuItem),
            MenuItem.ClickEvent,
            new RoutedEventHandler(TransactionalPageSaveMenuItem_Click),
            handledEventsToo: true);

        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(TransactionalPageSave_PreviewKeyDown),
            handledEventsToo: true);

        return true;
    }

    private static void TransactionalPageSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || Window.GetWindow(button) is not MainWindow window
            || !ReferenceEquals(button, window._saveCurrentPageButton))
        {
            return;
        }

        e.Handled = true;
        _ = window.SaveCurrentPageTransactionallyAsync();
    }

    private static void TransactionalPageSaveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || Application.Current is null)
        {
            return;
        }

        MainWindow? window = Application.Current.Windows
            .OfType<MainWindow>()
            .FirstOrDefault(candidate => ReferenceEquals(item, candidate._menuSaveCurrentPage));
        if (window is null)
        {
            return;
        }

        e.Handled = true;
        _ = window.SaveCurrentPageTransactionallyAsync();
    }

    private static void TransactionalPageSave_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not MainWindow window || e.Key != Key.S)
        {
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if (!modifiers.HasFlag(ModifierKeys.Control)
            || modifiers.HasFlag(ModifierKeys.Shift)
            || modifiers.HasFlag(ModifierKeys.Alt))
        {
            return;
        }

        e.Handled = true;
        _ = window.SaveCurrentPageTransactionallyAsync();
    }

    private async Task SaveCurrentPageTransactionallyAsync()
    {
        if (_pageSaveBusy
            || _comicPageIndex < 0
            || _comicPageIndex >= _comicPages.Count
            || _comicBatchBusy
            || _pageNavigationBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentProjectPath) || !File.Exists(_currentProjectPath))
        {
            SetFooterStatus(
                "El proyecto todavía no tiene archivo. Elige dónde crearlo una sola vez.",
                "#C99A35");
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
            await Task.Run(() => WriteIncrementalPageToProjectTransactionally(saveData));
            MarkActiveDocumentPageSaved(pageIndex);
            SetFooterStatus(
                $"Página {pageIndex + 1} guardada · copia de seguridad actualizada.",
                "#58A77D");
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(
                this,
                "No se ha tocado el proyecto original porque el archivo no es un .tinta válido o ya estaba dañado.\n\n" +
                exception.Message +
                "\n\nPuedes recuperar una copia automática desde %LOCALAPPDATA%\\TintaES\\Backups.",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetFooterStatus("Guardado cancelado · el proyecto original se ha conservado.", "#EE594B");
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "No se pudo guardar la página actual. El proyecto original no se ha modificado.\n\n" +
                exception.Message,
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

    private static void WriteIncrementalPageToProjectTransactionally(IncrementalPageSaveData data)
    {
        string temporaryPath = data.ProjectPath + ".pagesave.tmp";
        string backupPath = data.ProjectPath + ".bak";
        TryDeleteTransactionalPageSaveFile(temporaryPath);

        try
        {
            // Nunca se abre el proyecto real en modo Update. Toda modificación ocurre sobre una copia.
            File.Copy(data.ProjectPath, temporaryPath, overwrite: true);

            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       bufferSize: 1024 * 1024,
                       FileOptions.RandomAccess))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false))
            {
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
                ZipArchiveEntry manifestEntry = archive.CreateEntry(
                    "project.json",
                    CompressionLevel.Fastest);
                using Stream manifestStream = manifestEntry.Open();
                JsonSerializer.Serialize(manifestStream, data.Manifest, ProjectJsonOptions);
            }

            // Fuerza una reapertura completa del ZIP. Si falta el directorio central o cualquier
            // entrada esencial, se aborta antes de tocar el archivo que el usuario ya tenía.
            ValidateTransactionalPageSaveArchive(temporaryPath, data.Manifest);

            // File.Replace trabaja en el mismo directorio/volumen: sustituye el proyecto de forma
            // atómica y deja la versión anterior como .bak para recuperación manual inmediata.
            TryDeleteTransactionalPageSaveFile(backupPath);
            File.Replace(temporaryPath, data.ProjectPath, backupPath, ignoreMetadataErrors: true);
        }
        catch
        {
            TryDeleteTransactionalPageSaveFile(temporaryPath);
            throw;
        }
    }

    private static void ValidateTransactionalPageSaveArchive(
        string projectPath,
        TintaProjectManifest expectedManifest)
    {
        using FileStream input = new(
            projectPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 256 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);

        ZipArchiveEntry manifestEntry = archive.GetEntry("project.json")
            ?? throw new InvalidDataException("El ZIP temporal no contiene project.json.");

        TintaProjectManifest manifest;
        using (Stream manifestStream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<TintaProjectManifest>(manifestStream, ProjectJsonOptions)
                ?? throw new InvalidDataException("No se puede leer project.json después del guardado.");
        }

        if (manifest.Pages.Count != expectedManifest.Pages.Count)
        {
            throw new InvalidDataException("El guardado temporal no conserva todas las páginas del proyecto.");
        }

        foreach (TintaProjectPage page in manifest.Pages)
        {
            EnsureTransactionalArchiveEntryExists(archive, page.SourceFile, "imagen fuente");

            if (!string.IsNullOrWhiteSpace(page.CleanedFile))
            {
                EnsureTransactionalArchiveEntryExists(archive, page.CleanedFile, "imagen procesada");
            }

            if (!string.IsNullOrWhiteSpace(page.MaskFile))
            {
                EnsureTransactionalArchiveEntryExists(archive, page.MaskFile, "máscara");
            }
        }
    }

    private static void EnsureTransactionalArchiveEntryExists(
        ZipArchive archive,
        string entryName,
        string description)
    {
        if (string.IsNullOrWhiteSpace(entryName) || archive.GetEntry(entryName) is null)
        {
            throw new InvalidDataException(
                $"El guardado temporal ha perdido una {description}: {entryName}.");
        }
    }

    private static void TryDeleteTransactionalPageSaveFile(string path)
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
            // La limpieza no debe ocultar la excepción real del guardado.
        }
    }
}
