using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Convierte una sola vez las regiones de un proyecto importado al modelo de tamaño directo
/// en píxeles. Redimensionar la caja después no vuelve a deducir ni modificar la fuente.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ImportedFontSizeStabilityRegistered =
        RegisterImportedFontSizeStability();

    private readonly HashSet<Guid> _stabilizedImportedFontSizes = [];
    private bool _importedFontSizeStabilityInstalled;
    private bool _stabilizingImportedFontSizes;

    private static bool RegisterImportedFontSizeStability()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ImportedFontSizeStabilityLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ImportedFontSizeStabilityLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallImportedFontSizeStability,
                DispatcherPriority.ContextIdle);
        }
    }

    private void InstallImportedFontSizeStability()
    {
        if (_importedFontSizeStabilityInstalled)
        {
            StabilizeAllImportedFontSizes();
            return;
        }

        _importedFontSizeStabilityInstalled = true;
        _regions.CollectionChanged += Regions_ImportedFontSizeCollectionChanged;
        RegionListBox.SelectionChanged += RegionListBox_ImportedFontSizeSelectionChanged;
        BusyOverlay.IsVisibleChanged += BusyOverlay_ImportedFontSizeVisibilityChanged;
        StabilizeAllImportedFontSizes();
    }

    private bool HasImportedProject =>
        !string.IsNullOrWhiteSpace(_currentProjectPath)
        && File.Exists(_currentProjectPath);

    private void Regions_ImportedFontSizeCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (!HasImportedProject)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Dispatcher.BeginInvoke(
                StabilizeAllImportedFontSizes,
                DispatcherPriority.DataBind);
            return;
        }

        if (e.NewItems is null)
        {
            return;
        }

        bool changed = false;
        foreach (object item in e.NewItems)
        {
            if (item is ComicRegion region)
            {
                changed |= StabilizeImportedFontSize(region);
            }
        }

        if (changed)
        {
            QueueFastCanvasTextRefresh(forceLayout: false);
            QueueNativeTextFrameRefresh();
        }
    }

    private void RegionListBox_ImportedFontSizeSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!HasImportedProject
            || RegionListBox.SelectedItem is not ComicRegion region)
        {
            return;
        }

        if (StabilizeImportedFontSize(region))
        {
            PersistVisibleComicPageRegions();
            QueueFastCanvasTextRefresh(forceLayout: false);
            QueueNativeTextFrameRefresh();
        }
    }

    private void BusyOverlay_ImportedFontSizeVisibilityChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (!BusyOverlay.IsVisible)
        {
            StabilizeAllImportedFontSizes();
        }
    }

    private void StabilizeAllImportedFontSizes()
    {
        if (!HasImportedProject
            || _stabilizingImportedFontSizes
            || _regions.Count == 0)
        {
            return;
        }

        _stabilizingImportedFontSizes = true;
        try
        {
            bool changed = false;
            foreach (ComicRegion region in _regions)
            {
                changed |= StabilizeImportedFontSize(region);
            }

            if (!changed)
            {
                return;
            }

            PersistVisibleComicPageRegions();
            RegionListBox.Items.Refresh();
            QueueFastCanvasTextRefresh(forceLayout: false);
            QueueNativeTextFrameRefresh();
        }
        finally
        {
            _stabilizingImportedFontSizes = false;
        }
    }

    private bool StabilizeImportedFontSize(ComicRegion region)
    {
        if (_stabilizedImportedFontSizes.Contains(region.Id))
        {
            _validatedNativeBaseSizes.Add(region.Id);
            return false;
        }

        double manualScale = ValidImportedFontScale(region.ManualFontScale);
        double legacyScale = ValidImportedFontScale(region.FontScale);
        bool hasExplicitBase = double.IsFinite(region.ManualBaseFontSize)
            && region.ManualBaseFontSize >= 2;

        double effectiveSize = hasExplicitBase
            ? region.ManualBaseFontSize * manualScale
            : ResolveImportedDetectedFontSize(region) * legacyScale * manualScale;

        effectiveSize = Math.Clamp(effectiveSize, 2, 512);
        bool changed = !ImportedFontValuesEqual(region.ManualBaseFontSize, effectiveSize)
            || !ImportedFontValuesEqual(region.ManualFontScale, 1)
            || !ImportedFontValuesEqual(region.FontScale, 1);

        if (changed)
        {
            region.ManualBaseFontSize = effectiveSize;
            region.ManualFontScale = 1;
            region.FontScale = 1;

            if (string.IsNullOrEmpty(region.ManualLayoutSeedText))
            {
                region.ManualLayoutSeedText = region.Translation;
            }
        }

        _validatedNativeBaseSizes.Add(region.Id);
        _stabilizedImportedFontSizes.Add(region.Id);
        return changed;
    }

    private double ResolveImportedDetectedFontSize(ComicRegion region)
    {
        double detectedSize = region.Style.FontSize;
        if (double.IsFinite(detectedSize) && detectedSize >= 2)
        {
            return detectedSize;
        }

        double pageHeight = _originalBitmap?.PixelHeight ?? 1000;
        double boxHeight = Math.Max(
            1,
            region.RenderBox.Height / 1000 * pageHeight);

        int explicitLines = Math.Max(
            1,
            (region.Translation ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n').Length);
        int detectedLines = Math.Max(1, region.Style.OriginalLineCount);
        int lineCount = Math.Max(explicitLines, detectedLines);

        double lineHeight = double.IsFinite(region.Style.LineHeightRatio)
            && region.Style.LineHeightRatio >= 0.6
            && region.Style.LineHeightRatio <= 3
                ? region.Style.LineHeightRatio
                : 1.12;

        return Math.Clamp(
            boxHeight / (lineCount * lineHeight),
            6,
            180);
    }

    private static double ValidImportedFontScale(double value) =>
        double.IsFinite(value) && value >= 0.05 && value <= 20
            ? value
            : 1;

    private static bool ImportedFontValuesEqual(double left, double right) =>
        Math.Abs(left - right) < 0.0001;
}
