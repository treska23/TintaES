using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Repara al cargar y al terminar un análisis los cartuchos planos cuyo color haya sido sustituido
/// por otro durante el inpainting. La corrección se guarda en clean.png, de modo que vista previa y
/// exportación comparten exactamente el mismo fondo.
/// </summary>
public partial class MainWindow
{
    private static readonly bool FlatRegionBackgroundRepairRegistered =
        RegisterFlatRegionBackgroundRepair();

    private readonly FlatRegionBackgroundRepairService _flatRegionBackgroundRepairService = new();
    private bool _flatRegionBackgroundRepairInstalled;
    private bool _flatRegionBackgroundRepairPending;
    private bool _flatRegionBackgroundRepairBusy;
    private string? _lastFlatRegionBackgroundRepairSignature;

    private static bool RegisterFlatRegionBackgroundRepair()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_FlatRegionBackgroundRepairLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_FlatRegionBackgroundRepairLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallFlatRegionBackgroundRepair,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallFlatRegionBackgroundRepair()
    {
        if (_flatRegionBackgroundRepairInstalled)
        {
            QueueFlatRegionBackgroundRepair(force: true);
            return;
        }

        _flatRegionBackgroundRepairInstalled = true;
        _regions.CollectionChanged += Regions_FlatRegionBackgroundRepairCollectionChanged;
        BusyOverlay.IsVisibleChanged += BusyOverlay_FlatRegionBackgroundRepairVisibilityChanged;
        ResultPreviewButton.Click += (_, _) => QueueFlatRegionBackgroundRepair(force: false);
        CleanPreviewButton.Click += (_, _) => QueueFlatRegionBackgroundRepair(force: false);
        OverlayCanvas.LayoutUpdated += (_, _) => QueueFlatRegionBackgroundRepair(force: false);
        QueueFlatRegionBackgroundRepair(force: true);
    }

    private void Regions_FlatRegionBackgroundRepairCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e) =>
        QueueFlatRegionBackgroundRepair(force: true);

    private void BusyOverlay_FlatRegionBackgroundRepairVisibilityChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (!BusyOverlay.IsVisible)
        {
            QueueFlatRegionBackgroundRepair(force: true);
        }
    }

    private void QueueFlatRegionBackgroundRepair(bool force)
    {
        if (force)
        {
            _lastFlatRegionBackgroundRepairSignature = null;
        }

        if (_flatRegionBackgroundRepairPending
            || _flatRegionBackgroundRepairBusy
            || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        string? signature = BuildFlatRegionBackgroundRepairSignature();
        if (!force
            && signature is not null
            && string.Equals(
                signature,
                _lastFlatRegionBackgroundRepairSignature,
                StringComparison.Ordinal))
        {
            return;
        }

        _flatRegionBackgroundRepairPending = true;
        Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                _flatRegionBackgroundRepairPending = false;
                await RepairVisibleFlatRegionBackgroundAsync();
            }),
            DispatcherPriority.ContextIdle);
    }

    private async Task RepairVisibleFlatRegionBackgroundAsync()
    {
        if (_flatRegionBackgroundRepairBusy
            || BusyOverlay.IsVisible
            || _originalBitmap is null
            || _cleanedBaseBitmap is null
            || _maskBitmap is null
            || _regions.Count == 0)
        {
            return;
        }

        string? signature = BuildFlatRegionBackgroundRepairSignature();
        if (signature is null
            || string.Equals(
                signature,
                _lastFlatRegionBackgroundRepairSignature,
                StringComparison.Ordinal))
        {
            return;
        }

        _flatRegionBackgroundRepairBusy = true;
        int pageIndex = _comicPageIndex;
        var regions = _regions.ToArray();
        var original = _originalBitmap;
        var cleaned = _cleanedBaseBitmap;
        var mask = _maskBitmap;

        try
        {
            var repaired = await Task.Run(() =>
                _flatRegionBackgroundRepairService.Repair(
                    original,
                    cleaned,
                    mask,
                    regions));

            if (pageIndex != _comicPageIndex
                || _originalBitmap is null
                || _cleanedBaseBitmap is null)
            {
                return;
            }

            _cleanedBaseBitmap = repaired;
            _cleanedBitmap = _processingService.CleanText(repaired, _regions);

            if (string.Equals(_previewMode, "clean", StringComparison.Ordinal))
            {
                PageImage.Source = _cleanedBaseBitmap;
            }
            else if (string.Equals(_previewMode, "result", StringComparison.Ordinal))
            {
                PageImage.Source = _cleanedBitmap;
                RebuildOverlay();
            }

            if (pageIndex >= 0 && pageIndex < _comicPages.Count)
            {
                ComicBookPageState page = _comicPages[pageIndex];
                if (!string.IsNullOrWhiteSpace(page.CleanedPath))
                {
                    string path = page.CleanedPath;
                    await Task.Run(() => SaveBitmap(repaired, path));
                }
            }

            _lastFlatRegionBackgroundRepairSignature =
                BuildFlatRegionBackgroundRepairSignature();
        }
        finally
        {
            _flatRegionBackgroundRepairBusy = false;
        }
    }

    private string? BuildFlatRegionBackgroundRepairSignature()
    {
        if (_comicPageIndex < 0
            || _comicPageIndex >= _comicPages.Count
            || _originalBitmap is null
            || _cleanedBaseBitmap is null
            || _maskBitmap is null)
        {
            return null;
        }

        ComicBookPageState page = _comicPages[_comicPageIndex];
        long written = !string.IsNullOrWhiteSpace(page.CleanedPath)
                       && File.Exists(page.CleanedPath)
            ? File.GetLastWriteTimeUtc(page.CleanedPath).Ticks
            : 0;
        return $"{BuildActiveDocumentSessionKey()}|{_comicPageIndex}|{written}|{_regions.Count}";
    }
}
