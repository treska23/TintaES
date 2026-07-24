using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Sustituye la eliminación síncrona del editor. La zona se recompone en memoria usando solo
/// el rectángulo afectado y los PNG se comprimen fuera del hilo de la interfaz.
/// </summary>
public partial class MainWindow
{
    private const int FastDeletionVisibleBudgetSeconds = 15;
    private static readonly bool FastRegionDeletionRegistered = RegisterFastRegionDeletion();

    private bool _fastRegionDeletionInstalled;
    private bool _fastRegionDeletionBusy;

    private static bool RegisterFastRegionDeletion()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_FastRegionDeletionLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_FastRegionDeletionLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            // EditorTools se instala en ContextIdle. ApplicationIdle garantiza que retiramos
            // sus controladores síncronos después de que hayan sido añadidos.
            window.Dispatcher.BeginInvoke(
                window.InstallFastRegionDeletion,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallFastRegionDeletion()
    {
        if (_fastRegionDeletionInstalled)
        {
            return;
        }

        _fastRegionDeletionInstalled = true;
        DeleteRegionButton.Click -= DeleteSelectedRegionCompletely_Click;
        DeleteRegionButton.Click += DeleteSelectedRegionFast_Click;
        DeleteRegionButton.ToolTip = "Eliminar traducción, máscara y caja sin bloquear la aplicación";

        PreviewKeyDown -= MainWindow_EditorToolsPreviewKeyDown;
        PreviewKeyDown += MainWindow_EditorToolsPreviewKeyDown_WithFastDelete;
    }

    private async void DeleteSelectedRegionFast_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedRegionFastAsync();
    }

    private void MainWindow_EditorToolsPreviewKeyDown_WithFastDelete(object sender, KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.Z)
        {
            UndoEditorChange();
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.Y)
        {
            RedoEditorChange();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _drawingRegion)
        {
            SetDrawingRegionMode(false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete
            && Keyboard.FocusedElement is not TextBoxBase
            && Keyboard.FocusedElement is not ComboBox)
        {
            _ = DeleteSelectedRegionFastAsync();
            e.Handled = true;
        }
    }

    private async Task DeleteSelectedRegionFastAsync()
    {
        if (_fastRegionDeletionBusy
            || _selectedRegion is null
            || _originalBitmap is null
            || _comicPageIndex < 0
            || _comicPageIndex >= _comicPages.Count)
        {
            return;
        }

        _fastRegionDeletionBusy = true;
        _pageNavigationBusy = true;
        ComicRegion removed = _selectedRegion;
        int pageIndex = _comicPageIndex;
        int oldIndex = _regions.IndexOf(removed);
        NormalizedRect maskBounds = GetRegionMaskBounds(removed);
        BitmapSource original = _originalBitmap;
        BitmapSource cleaned = _cleanedBaseBitmap ?? original;
        BitmapSource? mask = _maskBitmap;
        bool keepMask = _regions.Any(region => !ReferenceEquals(region, removed));
        EditorSnapshot undoSnapshot = CaptureEditorSnapshot();
        var stopwatch = Stopwatch.StartNew();

        BeginFastDeletionProgress();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            BusyTitleText.Text = "Restaurando la zona original…";
            BusyProgressBar.Value = 18;
            FooterProgressBar.Value = 18;

            (BitmapSource restored, BitmapSource? updatedMask) = await Task.Run(() =>
            {
                BitmapSource restoredBitmap = RestoreOriginalAreaFast(cleaned, original, maskBounds);
                BitmapSource? maskBitmap = ClearMaskAreaFast(mask, original, maskBounds, keepMask);
                return (restoredBitmap, maskBitmap);
            });

            BusyTitleText.Text = "Actualizando la página…";
            BusyProgressBar.Value = 62;
            FooterProgressBar.Value = 62;

            CommitFastDeletionUndo(undoSnapshot);
            removed.PropertyChanged -= Region_PropertyChanged;
            _regions.Remove(removed);
            RenumberEditorRegions();
            _selectedRegion = null;
            _cleanedBaseBitmap = restored;
            _cleanedBitmap = restored;
            _maskBitmap = updatedMask;

            ComicBookPageState page = _comicPages[pageIndex];
            page.Processed = true;
            page.Error = null;
            page.Regions.Clear();
            page.Regions.AddRange(_regions);
            PrepareFastDeletionPaths(pageIndex, page, updatedMask is not null);
            UpdateFastDeletionBitmapCache(pageIndex, page, original, restored, updatedMask);

            PageImage.Source = _previewMode switch
            {
                "original" => original,
                "mask" when updatedMask is not null => updatedMask,
                _ => restored
            };
            MaskPreviewButton.IsEnabled = updatedMask is not null;
            CleanPreviewButton.IsEnabled = true;
            ResultPreviewButton.IsEnabled = true;
            RebuildOverlay();
            UpdateRegionCount();
            RegionListBox.SelectedIndex = _regions.Count == 0
                ? -1
                : Math.Clamp(oldIndex, 0, _regions.Count - 1);

            // La eliminación ya es visible. Quitamos el velo grande y dejamos únicamente la
            // barra inferior mientras se comprimen los archivos en segundo plano.
            BusyOverlay.Visibility = Visibility.Collapsed;
            BusyProgressBar.Value = 75;
            FooterProgressBar.Value = 75;
            FooterStatusText.Text = "Zona eliminada. Guardando el cambio en segundo plano…";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            Task persistence = SaveFastDeletionBitmapsAsync(page, restored, updatedMask);
            Task visibleBudget = Task.Delay(TimeSpan.FromSeconds(FastDeletionVisibleBudgetSeconds));
            Task first = await Task.WhenAny(persistence, visibleBudget);
            if (first == visibleBudget)
            {
                FooterProgressBar.IsIndeterminate = true;
                FooterStatusText.Text = "La zona ya está eliminada; terminando de guardar sin bloquear la aplicación…";
            }

            await persistence;
            FooterProgressBar.IsIndeterminate = false;
            FooterProgressBar.Value = 100;
            SetFooterStatus(
                $"Zona eliminada en {stopwatch.Elapsed.TotalSeconds:0.#} s · máscara, texto y caja retirados.",
                "#58A77D");
        }
        catch (Exception exception)
        {
            SetFooterStatus("No se pudo completar la eliminación de la zona.", "#EE594B");
            MessageBox.Show(
                this,
                $"No se pudo eliminar la zona.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _fastRegionDeletionBusy = false;
            _pageNavigationBusy = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
            BusyProgressBar.IsIndeterminate = false;
            FooterProgressBar.IsIndeterminate = false;
            FooterProgressBar.Visibility = Visibility.Collapsed;
            UpdateComicControls();
            UpdateProjectCommandAvailability();
            UpdatePsdExportAvailability();
            RefreshEditorToolAvailability();
        }
    }

    private void BeginFastDeletionProgress()
    {
        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitleText.Text = "Eliminando la zona seleccionada…";
        BusyProgressBar.IsIndeterminate = false;
        BusyProgressBar.Value = 5;
        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = false;
        FooterProgressBar.Value = 5;
        FooterStatusText.Text = "Quitando traducción, máscara y caja…";
        DeleteRegionButton.IsEnabled = false;
        AddRegionButton.IsEnabled = false;
        UpdateComicControls();
        UpdateProjectCommandAvailability();
        UpdatePsdExportAvailability();
    }

    private void CommitFastDeletionUndo(EditorSnapshot snapshot)
    {
        EditorPageHistory history = GetCurrentEditorHistory(create: true)!;
        history.Undo.Push(snapshot);
        history.Redo.Clear();
    }

    private void PrepareFastDeletionPaths(int pageIndex, ComicBookPageState page, bool hasMask)
    {
        string processedDirectory = Path.Combine(
            _comicWorkspace ?? Path.Combine(Path.GetTempPath(), "TintaES", "manual"),
            "processed");
        Directory.CreateDirectory(processedDirectory);

        page.CleanedPath ??= Path.Combine(processedDirectory, $"{pageIndex + 1:D4}-clean.png");
        if (hasMask)
        {
            page.MaskPath ??= Path.Combine(processedDirectory, $"{pageIndex + 1:D4}-mask.png");
        }
    }

    private void UpdateFastDeletionBitmapCache(
        int pageIndex,
        ComicBookPageState page,
        BitmapSource original,
        BitmapSource cleaned,
        BitmapSource? mask)
    {
        lock (_comicPageBitmapCacheLock)
        {
            _comicPageBitmapCache[pageIndex] = new ComicPageBitmapCache(
                page.SourcePath,
                page.CleanedPath,
                page.MaskPath,
                original,
                cleaned,
                mask);
        }
    }

    private async Task SaveFastDeletionBitmapsAsync(
        ComicBookPageState page,
        BitmapSource cleaned,
        BitmapSource? mask)
    {
        string cleanedPath = page.CleanedPath
            ?? throw new InvalidOperationException("No existe una ruta para guardar el fondo editado.");
        string? previousMaskPath = page.MaskPath;

        Task cleanedSave = Task.Run(() => SaveBitmapAtomicallyFast(cleaned, cleanedPath));
        Task maskSave;
        if (mask is not null)
        {
            string maskPath = page.MaskPath
                ?? throw new InvalidOperationException("No existe una ruta para guardar la máscara editada.");
            maskSave = Task.Run(() => SaveBitmapAtomicallyFast(mask, maskPath));
        }
        else
        {
            page.MaskPath = null;
            maskSave = Task.Run(() => DeleteFileQuietly(previousMaskPath));
        }

        await Task.WhenAll(cleanedSave, maskSave);
    }

    private static void SaveBitmapAtomicallyFast(BitmapSource bitmap, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("Ruta de imagen no válida."));
        string temporaryPath = targetPath + ".edit.tmp";
        DeleteFileQuietly(temporaryPath);

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
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
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch
        {
            DeleteFileQuietly(temporaryPath);
            throw;
        }
    }

    private static void DeleteFileQuietly(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static BitmapSource RestoreOriginalAreaFast(
        BitmapSource cleaned,
        BitmapSource original,
        NormalizedRect area)
    {
        BitmapSource cleaned32 = ConvertBitmapFormatFast(cleaned, PixelFormats.Bgra32);
        BitmapSource original32 = ConvertBitmapFormatFast(original, PixelFormats.Bgra32);
        if (cleaned32.PixelWidth != original32.PixelWidth
            || cleaned32.PixelHeight != original32.PixelHeight)
        {
            throw new InvalidOperationException("La imagen original y el fondo editado no tienen el mismo tamaño.");
        }

        int width = cleaned32.PixelWidth;
        int height = cleaned32.PixelHeight;
        int stride = width * 4;
        byte[] output = new byte[stride * height];
        cleaned32.CopyPixels(output, stride, 0);

        FastPixelArea rect = ToFastPixelArea(area, width, height);
        int areaStride = rect.Width * 4;
        byte[] originalArea = new byte[areaStride * rect.Height];
        original32.CopyPixels(
            new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height),
            originalArea,
            areaStride,
            0);

        for (int row = 0; row < rect.Height; row++)
        {
            Buffer.BlockCopy(
                originalArea,
                row * areaStride,
                output,
                (rect.Y + row) * stride + rect.X * 4,
                areaStride);
        }

        BitmapSource result = BitmapSource.Create(
            width,
            height,
            cleaned.DpiX > 0 ? cleaned.DpiX : 96,
            cleaned.DpiY > 0 ? cleaned.DpiY : 96,
            PixelFormats.Bgra32,
            null,
            output,
            stride);
        result.Freeze();
        return result;
    }

    private static BitmapSource? ClearMaskAreaFast(
        BitmapSource? mask,
        BitmapSource original,
        NormalizedRect area,
        bool keepMask)
    {
        if (mask is null || !keepMask)
        {
            return null;
        }

        BitmapSource gray = ConvertBitmapFormatFast(mask, PixelFormats.Gray8);
        int width = original.PixelWidth;
        int height = original.PixelHeight;
        if (gray.PixelWidth != width || gray.PixelHeight != height)
        {
            return null;
        }

        int stride = width;
        byte[] pixels = new byte[stride * height];
        gray.CopyPixels(pixels, stride, 0);
        FastPixelArea rect = ToFastPixelArea(area, width, height);
        for (int row = 0; row < rect.Height; row++)
        {
            Array.Clear(pixels, (rect.Y + row) * stride + rect.X, rect.Width);
        }

        BitmapSource result = BitmapSource.Create(
            width,
            height,
            original.DpiX > 0 ? original.DpiX : 96,
            original.DpiY > 0 ? original.DpiY : 96,
            PixelFormats.Gray8,
            null,
            pixels,
            stride);
        result.Freeze();
        return result;
    }

    private static BitmapSource ConvertBitmapFormatFast(BitmapSource source, PixelFormat format)
    {
        if (source.Format == format)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(source, format, null, 0);
        converted.Freeze();
        return converted;
    }

    private static FastPixelArea ToFastPixelArea(NormalizedRect area, int width, int height)
    {
        int x = Math.Clamp((int)Math.Floor(area.X / 1000 * width), 0, Math.Max(0, width - 1));
        int y = Math.Clamp((int)Math.Floor(area.Y / 1000 * height), 0, Math.Max(0, height - 1));
        int right = Math.Clamp((int)Math.Ceiling(area.Right / 1000 * width), x + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(area.Bottom / 1000 * height), y + 1, height);
        return new FastPixelArea(x, y, right - x, bottom - y);
    }

    private sealed record FastPixelArea(int X, int Y, int Width, int Height);
}
