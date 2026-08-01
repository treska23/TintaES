using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private static readonly TimeSpan PageLoadWarningInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<int, ComicPageBitmapCache> _comicPageBitmapCache = [];
    private readonly object _comicPageBitmapCacheLock = new();
    private bool _pageNavigationBusy;

    private async Task ShowComicPageFastAsync(int index)
    {
        if (_comicBatchBusy || _pageNavigationBusy || index < 0 || index >= _comicPages.Count)
        {
            return;
        }

        PersistVisibleComicPageRegions();
        HideMainTranslation();
        _pageNavigationBusy = true;
        ComicBookPageState page = _comicPages[index];

        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitleText.Text = $"Cargando página {index + 1} de {_comicPages.Count}…";
        BusyProgressBar.IsIndeterminate = true;
        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = true;
        FooterStatusText.Text = $"Cargando página {index + 1} de {_comicPages.Count}…";
        UpdateComicControls();
        SyncDirectPageSelector();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            ComicPageBitmapCache cache = await GetComicPageBitmapCacheAsync(index, page);
            await ApplyComicPageAsync(index, page, cache);

            // Una página de 1800 x 2700 puede ocupar decenas de MB por cada bitmap. Solo
            // conservamos la visible para no disparar el consumo al recorrer un cómic entero.
            PruneComicPageBitmapCache(index);
        }
        catch (OperationCanceledException)
        {
            SetFooterStatus($"Carga de la página {index + 1} cancelada.", "#C99A35");
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"No se pudo cargar la página {index + 1}.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetFooterStatus($"No se pudo cargar la página {index + 1}.", "#EE594B");
        }
        finally
        {
            _pageNavigationBusy = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
            BusyProgressBar.IsIndeterminate = false;
            FooterProgressBar.Visibility = Visibility.Collapsed;
            FooterProgressBar.IsIndeterminate = false;
            UpdateComicControls();
            SyncDirectPageSelector();
            RefreshEditorToolAvailability();
            RefreshManualMaskAvailability();
            RefreshPageSaveAvailability();
            UpdatePsdExportAvailability();
        }
    }

    private async Task<ComicPageBitmapCache> GetComicPageBitmapCacheAsync(int index, ComicBookPageState page)
    {
        lock (_comicPageBitmapCacheLock)
        {
            if (_comicPageBitmapCache.TryGetValue(index, out ComicPageBitmapCache? cached)
                && string.Equals(cached.SourcePath, page.SourcePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(cached.CleanedPath, page.CleanedPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(cached.MaskPath, page.MaskPath, StringComparison.OrdinalIgnoreCase))
            {
                return cached;
            }
        }

        object stageLock = new();
        string currentStage = "abriendo la imagen original";
        void ReportStage(string stage)
        {
            lock (stageLock)
            {
                currentStage = stage;
            }

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_pageNavigationBusy)
                {
                    return;
                }

                BusyTitleText.Text = $"Página {index + 1}/{_comicPages.Count} · {stage}…";
                FooterStatusText.Text = $"Página {index + 1} · {stage}…";
            }, DispatcherPriority.Background);
        }

        using var loadCancellation = new CancellationTokenSource();
        Task<ComicPageBitmapCache> loadTask = Task.Run(
            () => LoadComicPageBitmapCache(page, ReportStage, loadCancellation.Token),
            loadCancellation.Token);

        while (!loadTask.IsCompleted)
        {
            Task completed = await Task.WhenAny(
                loadTask,
                Task.Delay(PageLoadWarningInterval));
            if (completed == loadTask || loadTask.IsCompleted)
            {
                break;
            }

            string stage;
            lock (stageLock)
            {
                stage = currentStage;
            }

            MessageBoxResult decision = MessageBox.Show(
                this,
                $"La página lleva 30 segundos {stage}.\n\n" +
                "Esto no debería tardar tanto. ¿Quieres seguir esperando?",
                "La carga está tardando demasiado",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (decision != MessageBoxResult.Yes)
            {
                loadCancellation.Cancel();
                throw new OperationCanceledException("El usuario canceló la recarga de la página.");
            }
        }

        ComicPageBitmapCache loaded = await loadTask;
        lock (_comicPageBitmapCacheLock)
        {
            _comicPageBitmapCache[index] = loaded;
        }
        return loaded;
    }

    private static ComicPageBitmapCache LoadComicPageBitmapCache(
        ComicBookPageState page,
        Action<string> reportStage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        reportStage("abriendo la imagen original");
        BitmapSource original = LoadBitmapSourceDetached(page.SourcePath, cancellationToken);

        BitmapSource? cleaned = null;
        if (page.Processed
            && !string.IsNullOrWhiteSpace(page.CleanedPath)
            && File.Exists(page.CleanedPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportStage("abriendo el fondo limpio");
            cleaned = LoadBitmapSourceDetached(page.CleanedPath, cancellationToken);
        }

        BitmapSource? mask = null;
        if (page.Processed
            && !string.IsNullOrWhiteSpace(page.MaskPath)
            && File.Exists(page.MaskPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportStage("abriendo la máscara de texto");
            mask = LoadBitmapSourceDetached(page.MaskPath, cancellationToken);
        }

        return new ComicPageBitmapCache(
            page.SourcePath,
            page.CleanedPath,
            page.MaskPath,
            original,
            cleaned,
            mask);
    }

    private static BitmapSource LoadBitmapSourceDetached(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No se encuentra una de las imágenes de la página.", path);
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException($"La imagen «{Path.GetFileName(path)}» no contiene ningún fotograma.");
        }

        BitmapSource bitmap = decoder.Frames[0];
        if (!bitmap.IsFrozen && bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }

    private void StoreComicPageBitmapCache(
        int index,
        ComicBookPageState page,
        BitmapSource original,
        BitmapSource cleaned,
        BitmapSource mask)
    {
        FreezeForPageCache(original);
        FreezeForPageCache(cleaned);
        FreezeForPageCache(mask);

        var cache = new ComicPageBitmapCache(
            page.SourcePath,
            page.CleanedPath,
            page.MaskPath,
            original,
            cleaned,
            mask);
        lock (_comicPageBitmapCacheLock)
        {
            _comicPageBitmapCache[index] = cache;
        }
    }

    private static void FreezeForPageCache(BitmapSource bitmap)
    {
        if (!bitmap.IsFrozen && bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }
    }

    private async Task ApplyComicPageAsync(int index, ComicBookPageState page, ComicPageBitmapCache cache)
    {
        _comicPageIndex = index;
        _visibleComicPageIndex = -1;
        _sourcePath = page.SourcePath;
        _originalBitmap = cache.Original;
        _cleanedBaseBitmap = page.Processed ? cache.Cleaned ?? cache.Original : cache.Original;
        _cleanedBitmap = _cleanedBaseBitmap;
        _maskBitmap = page.Processed ? cache.Mask : null;
        _selectedRegion = null;

        IReadOnlyList<ComicRegion> groupedRegions = BalloonRegionGrouper.Group(page.Regions);
        if (groupedRegions.Count != page.Regions.Count
            || !groupedRegions.SequenceEqual(page.Regions))
        {
            page.Regions.Clear();
            page.Regions.AddRange(groupedRegions);
        }

        PageImage.Source = _originalBitmap;
        ImageStage.Width = _originalBitmap.PixelWidth;
        ImageStage.Height = _originalBitmap.PixelHeight;
        PageImage.Width = _originalBitmap.PixelWidth;
        PageImage.Height = _originalBitmap.PixelHeight;
        OverlayCanvas.Width = _originalBitmap.PixelWidth;
        OverlayCanvas.Height = _originalBitmap.PixelHeight;
        EmptyState.Visibility = Visibility.Collapsed;
        ImageScrollViewer.Visibility = Visibility.Visible;
        OverlayCanvas.Visibility = Visibility.Visible;

        foreach (ComicRegion current in _regions)
        {
            current.PropertyChanged -= Region_PropertyChanged;
        }
        _regions.Clear();
        if (page.Processed)
        {
            foreach (ComicRegion region in page.Regions)
            {
                region.PropertyChanged -= Region_PropertyChanged;
                region.PropertyChanged += Region_PropertyChanged;
                _regions.Add(region);
            }
        }

        RegionListBox.SelectedItem = null;
        ShowRegionEditor(null);
        _previewMode = "original";
        OriginalPreviewButton.IsEnabled = true;
        MaskPreviewButton.IsEnabled = _maskBitmap is not null;
        CleanPreviewButton.IsEnabled = page.Processed;
        ResultPreviewButton.IsEnabled = page.Processed;
        LanguageText.Text = page.Processed ? $"{page.SourceLanguage.ToUpperInvariant()} → ES" : "— → ES";

        ShowPreviewMode("original");
        OverlayCanvas.Children.Clear();
        UpdateRegionCount();

        _visibleComicPageIndex = index;
        PageNameText.Text = page.DisplayName;
        PageInfoText.Text = $"{_originalBitmap.PixelWidth} × {_originalBitmap.PixelHeight} px · Página {index + 1} de {_comicPages.Count}";
        UpdateComicControls();
        SyncDirectPageSelector();

        BusyOverlay.Visibility = Visibility.Collapsed;
        BusyProgressBar.IsIndeterminate = false;
        FooterProgressBar.IsIndeterminate = false;
        FooterProgressBar.Value = 45;
        FooterStatusText.Text = page.Processed && _regions.Count > 0
            ? $"Preparando {_regions.Count} textos…"
            : $"Mostrando página {index + 1}…";
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        ComicRegion[] enabledRegions = ReaderFirstModeEnabled
            ? []
            : _regions.Where(region => region.IsEnabled).ToArray();
        for (int regionIndex = 0; regionIndex < enabledRegions.Length; regionIndex++)
        {
            AddRegionVisual(enabledRegions[regionIndex]);
            if ((regionIndex + 1) % 4 == 0 || regionIndex + 1 == enabledRegions.Length)
            {
                double fraction = (regionIndex + 1d) / Math.Max(1, enabledRegions.Length);
                FooterProgressBar.Value = 45 + fraction * 45;
                FooterStatusText.Text =
                    $"Colocando textos · {regionIndex + 1}/{enabledRegions.Length}";
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        FooterStatusText.Text = "Finalizando la página…";
        FinalizeProgressiveOverlayTextLayout(finalPass: false);
        await Dispatcher.Yield(DispatcherPriority.Render);

        if (_regions.Count > 0)
        {
            _suppressSelectionRebuild = true;
            try
            {
                _selectedRegion = _regions[0];
                RegionListBox.SelectedIndex = 0;
                ShowRegionEditor(_selectedRegion);
            }
            finally
            {
                _suppressSelectionRebuild = false;
            }
        }

        FooterProgressBar.Value = 100;
        SynchronizeActiveDocumentState();
        string state = page.Error is not null ? "con error" : page.Processed ? "traducida" : "pendiente";
        SetFooterStatus($"Página {index + 1}/{_comicPages.Count} · {state}", page.Error is null ? "#58A77D" : "#C99A35");
    }

    private void PruneComicPageBitmapCache(int centerIndex)
    {
        lock (_comicPageBitmapCacheLock)
        {
            int[] removable = _comicPageBitmapCache.Keys
                .Where(index => index != centerIndex)
                .ToArray();
            foreach (int index in removable)
            {
                _comicPageBitmapCache.Remove(index);
            }
        }
    }

    private void ClearComicPageBitmapCache()
    {
        lock (_comicPageBitmapCacheLock)
        {
            _comicPageBitmapCache.Clear();
        }
    }

    private sealed record ComicPageBitmapCache(
        string SourcePath,
        string? CleanedPath,
        string? MaskPath,
        BitmapSource Original,
        BitmapSource? Cleaned,
        BitmapSource? Mask);
}
