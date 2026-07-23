using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

public partial class MainWindow
{
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
            ApplyComicPage(index, page, cache);
            PruneComicPageBitmapCache(index);
            _ = PreloadComicPageNeighborsAsync(index);
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

        ComicPageBitmapCache loaded = await Task.Run(() => LoadComicPageBitmapCache(page));
        lock (_comicPageBitmapCacheLock)
        {
            _comicPageBitmapCache[index] = loaded;
        }
        return loaded;
    }

    private static ComicPageBitmapCache LoadComicPageBitmapCache(ComicBookPageState page)
    {
        BitmapSource original = LoadBitmapSource(page.SourcePath);
        BitmapSource? cleaned = page.Processed
            && !string.IsNullOrWhiteSpace(page.CleanedPath)
            && File.Exists(page.CleanedPath)
                ? LoadBitmapSource(page.CleanedPath)
                : null;
        BitmapSource? mask = page.Processed
            && !string.IsNullOrWhiteSpace(page.MaskPath)
            && File.Exists(page.MaskPath)
                ? LoadBitmapSource(page.MaskPath)
                : null;

        return new ComicPageBitmapCache(
            page.SourcePath,
            page.CleanedPath,
            page.MaskPath,
            original,
            cleaned,
            mask);
    }

    private void ApplyComicPage(int index, ComicBookPageState page, ComicPageBitmapCache cache)
    {
        _comicPageIndex = index;
        _visibleComicPageIndex = -1;
        _sourcePath = page.SourcePath;
        _originalBitmap = cache.Original;
        _cleanedBaseBitmap = page.Processed ? cache.Cleaned ?? cache.Original : cache.Original;
        _cleanedBitmap = _cleanedBaseBitmap;
        _maskBitmap = page.Processed ? cache.Mask : null;
        _selectedRegion = null;

        PageImage.Source = page.Processed ? _cleanedBitmap : _originalBitmap;
        ImageStage.Width = _originalBitmap.PixelWidth;
        ImageStage.Height = _originalBitmap.PixelHeight;
        PageImage.Width = _originalBitmap.PixelWidth;
        PageImage.Height = _originalBitmap.PixelHeight;
        OverlayCanvas.Width = _originalBitmap.PixelWidth;
        OverlayCanvas.Height = _originalBitmap.PixelHeight;
        EmptyState.Visibility = Visibility.Collapsed;
        ImageScrollViewer.Visibility = Visibility.Visible;
        OverlayCanvas.Visibility = Visibility.Visible;

        _regions.Clear();
        if (page.Processed)
        {
            foreach (ComicRegion region in page.Regions)
            {
                _regions.Add(region);
            }
        }

        RegionListBox.SelectedItem = null;
        ShowRegionEditor(null);
        _previewMode = "result";
        OriginalPreviewButton.IsEnabled = true;
        MaskPreviewButton.IsEnabled = _maskBitmap is not null;
        CleanPreviewButton.IsEnabled = page.Processed;
        ResultPreviewButton.IsEnabled = page.Processed;
        LanguageText.Text = page.Processed ? $"{page.SourceLanguage.ToUpperInvariant()} → ES" : "— → ES";

        ShowPreviewMode("result");
        RebuildOverlay();
        UpdateRegionCount();
        if (_regions.Count > 0)
        {
            RegionListBox.SelectedIndex = 0;
        }

        _visibleComicPageIndex = index;
        PageNameText.Text = page.DisplayName;
        PageInfoText.Text = $"{_originalBitmap.PixelWidth} × {_originalBitmap.PixelHeight} px · Página {index + 1} de {_comicPages.Count}";
        UpdateComicControls();
        SyncDirectPageSelector();

        string state = page.Error is not null ? "con error" : page.Processed ? "traducida" : "pendiente";
        SetFooterStatus($"Página {index + 1}/{_comicPages.Count} · {state}", page.Error is null ? "#58A77D" : "#C99A35");
    }

    private async Task PreloadComicPageNeighborsAsync(int centerIndex)
    {
        foreach (int index in new[] { centerIndex - 1, centerIndex + 1 })
        {
            if (index < 0 || index >= _comicPages.Count)
            {
                continue;
            }

            lock (_comicPageBitmapCacheLock)
            {
                if (_comicPageBitmapCache.ContainsKey(index))
                {
                    continue;
                }
            }

            try
            {
                ComicBookPageState page = _comicPages[index];
                ComicPageBitmapCache cache = await Task.Run(() => LoadComicPageBitmapCache(page));
                lock (_comicPageBitmapCacheLock)
                {
                    _comicPageBitmapCache[index] = cache;
                }
            }
            catch
            {
                // La precarga es una optimización. Si falla, la carga normal mostrará el error.
            }
        }
    }

    private void PruneComicPageBitmapCache(int centerIndex)
    {
        lock (_comicPageBitmapCacheLock)
        {
            int[] removable = _comicPageBitmapCache.Keys
                .Where(index => Math.Abs(index - centerIndex) > 1)
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
