using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Menú de trabajo rápido sobre la página. Reutiliza las mismas rutas de traducción,
/// navegación y edición de la interfaz principal para que el botón derecho no cree un
/// segundo comportamiento distinto.
/// </summary>
public partial class MainWindow
{
    private static readonly bool PageContextMenuRegistered = RegisterPageContextMenu();

    private bool _pageContextMenuInstalled;
    private ContextMenu? _pageContextMenu;
    private MenuItem? _contextTranslatePageItem;
    private MenuItem? _contextPreviousPageItem;
    private MenuItem? _contextNextPageItem;
    private MenuItem? _contextManualTextItem;
    private MenuItem? _contextFitWidthItem;
    private MenuItem? _contextFitHeightItem;
    private MenuItem? _contextActualSizeItem;
    private MenuItem? _contextZoomInItem;
    private MenuItem? _contextZoomOutItem;
    private MenuItem? _contextCenterViewItem;
    private double _contextPageX = 500;
    private double _contextPageY = 500;

    private static bool RegisterPageContextMenu()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_PageContextMenuLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_PageContextMenuLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallPageContextMenu,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallPageContextMenu()
    {
        if (_pageContextMenuInstalled || ImageScrollViewer is null)
        {
            return;
        }

        _pageContextMenuInstalled = true;
        _pageContextMenu = new ContextMenu
        {
            PlacementTarget = ImageScrollViewer
        };

        _contextTranslatePageItem = CreatePageMenuItem(
            "Traducir esta página",
            PageContextTranslateCurrentPage_Click);
        _contextPreviousPageItem = CreatePageMenuItem(
            "Página anterior",
            (_, _) => ShowComicPage(_comicPageIndex - 1));
        _contextNextPageItem = CreatePageMenuItem(
            "Página siguiente",
            (_, _) => ShowComicPage(_comicPageIndex + 1));
        _contextManualTextItem = CreatePageMenuItem(
            "Traducir texto manualmente…",
            PageContextAddManualText_Click);
        _contextFitWidthItem = CreatePageMenuItem(
            "Ajustar al ancho",
            (_, _) => FitCurrentPageToWidth());
        _contextFitHeightItem = CreatePageMenuItem(
            "Ajustar al alto",
            (_, _) => _ = FitCurrentPageVerticallyAsync());
        _contextActualSizeItem = CreatePageMenuItem(
            "Tamaño real (100 %)",
            (_, _) => SetPageZoom(100));
        _contextZoomInItem = CreatePageMenuItem(
            "Acercar",
            (_, _) => SetPageZoom(ZoomSlider.Value + 10));
        _contextZoomOutItem = CreatePageMenuItem(
            "Alejar",
            (_, _) => SetPageZoom(ZoomSlider.Value - 10));
        _contextCenterViewItem = CreatePageMenuItem(
            "Centrar vista",
            (_, _) => CenterCurrentPageView());

        _pageContextMenu.Items.Add(_contextTranslatePageItem);
        _pageContextMenu.Items.Add(new Separator());
        _pageContextMenu.Items.Add(_contextPreviousPageItem);
        _pageContextMenu.Items.Add(_contextNextPageItem);
        _pageContextMenu.Items.Add(new Separator());
        _pageContextMenu.Items.Add(_contextManualTextItem);
        _pageContextMenu.Items.Add(new Separator());
        _pageContextMenu.Items.Add(_contextFitWidthItem);
        _pageContextMenu.Items.Add(_contextFitHeightItem);
        _pageContextMenu.Items.Add(_contextActualSizeItem);
        _pageContextMenu.Items.Add(_contextZoomInItem);
        _pageContextMenu.Items.Add(_contextZoomOutItem);
        _pageContextMenu.Items.Add(_contextCenterViewItem);

        _pageContextMenu.Opened += (_, _) => RefreshPageContextMenu();
        ImageScrollViewer.PreviewMouseRightButtonDown += PageSurface_PreviewMouseRightButtonDown;
        ImageScrollViewer.ContextMenu = _pageContextMenu;
    }

    private static MenuItem CreatePageMenuItem(
        string header,
        RoutedEventHandler handler)
    {
        var item = new MenuItem
        {
            Header = header,
            Padding = new Thickness(12, 5, 18, 5)
        };
        item.Click += handler;
        return item;
    }

    private void PageSurface_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_originalBitmap is null || OverlayCanvas is null)
        {
            return;
        }

        Point point = e.GetPosition(OverlayCanvas);
        double width = Math.Max(1, OverlayCanvas.ActualWidth);
        double height = Math.Max(1, OverlayCanvas.ActualHeight);
        _contextPageX = Math.Clamp(point.X / width * 1000, 0, 1000);
        _contextPageY = Math.Clamp(point.Y / height * 1000, 0, 1000);
    }

    private void RefreshPageContextMenu()
    {
        if (_pageContextMenu is null)
        {
            return;
        }

        bool hasPage = _originalBitmap is not null
                       && _comicPageIndex >= 0
                       && _comicPageIndex < _comicPages.Count;
        bool busy = _comicBatchBusy
                    || _pageNavigationBusy
                    || BusyOverlay.Visibility == Visibility.Visible;
        bool hasModel = ModelComboBox.SelectedItem is not null;
        bool reviewable = hasPage && PageHasReviewableText(_comicPages[_comicPageIndex]);

        if (_contextTranslatePageItem is not null)
        {
            _contextTranslatePageItem.Header = reviewable
                ? "Repasar traducción de esta página"
                : "Detectar y traducir esta página";
            _contextTranslatePageItem.IsEnabled = hasPage && hasModel && !busy;
        }
        if (_contextPreviousPageItem is not null)
        {
            _contextPreviousPageItem.IsEnabled = hasPage && !busy && _comicPageIndex > 0;
        }
        if (_contextNextPageItem is not null)
        {
            _contextNextPageItem.IsEnabled = hasPage
                                             && !busy
                                             && _comicPageIndex < _comicPages.Count - 1;
        }
        if (_contextManualTextItem is not null)
        {
            _contextManualTextItem.IsEnabled = hasPage && !busy;
        }

        foreach (MenuItem? item in new[]
                 {
                     _contextFitWidthItem,
                     _contextFitHeightItem,
                     _contextActualSizeItem,
                     _contextZoomInItem,
                     _contextZoomOutItem,
                     _contextCenterViewItem
                 })
        {
            if (item is not null)
            {
                item.IsEnabled = _originalBitmap is not null && !busy;
            }
        }
    }

    private void PageContextTranslateCurrentPage_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPageIndex < 0
            || _comicPageIndex >= _comicPages.Count
            || ModelComboBox.SelectedValue is not string model
            || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        int pageIndex = _comicPageIndex;
        if (PageHasReviewableText(_comicPages[pageIndex]))
        {
            _ = ReviewSelectedTranslationsAsync([pageIndex], model);
            return;
        }

        _ = AnalyzeSelectedComicPagesReliablyAsync([pageIndex], model);
    }

    private void PageContextAddManualText_Click(object sender, RoutedEventArgs e)
    {
        if (_originalBitmap is null)
        {
            return;
        }

        AddRegionButton_Click(sender, e);
        ComicRegion? region = _selectedRegion;
        if (region is null)
        {
            return;
        }

        const double width = 280;
        const double height = 110;
        double x = Math.Clamp(_contextPageX - width / 2, 0, 1000 - width);
        double y = Math.Clamp(_contextPageY - height / 2, 0, 1000 - height);
        var box = new NormalizedRect(x, y, width, height);

        region.Original = string.Empty;
        region.Translation = string.Empty;
        region.TextBox = box;
        region.RenderBox = box;
        region.IsManual = true;
        region.CleanupMode = "auto";
        region.NotifyVisualChange();

        UpdateCleanedPreview();
        ShowRegionEditor(region);
        OriginalTextBox.Focus();
        OriginalTextBox.SelectAll();
        SetFooterStatus(
            "Zona manual creada. Ajusta el rectángulo y usa el panel derecho para introducir y traducir el texto.",
            "#4CB2BB");
    }

    private void FitCurrentPageToWidth()
    {
        if (_originalBitmap is null)
        {
            return;
        }

        double viewportWidth = ImageScrollViewer.ViewportWidth;
        if (viewportWidth <= 1)
        {
            viewportWidth = Math.Max(
                1,
                ImageScrollViewer.ActualWidth
                - ImageScrollViewer.Padding.Left
                - ImageScrollViewer.Padding.Right);
        }

        double availableWidth = Math.Max(1, viewportWidth - 18);
        double targetPercent = availableWidth / Math.Max(1, _originalBitmap.PixelWidth) * 100;
        SetPageZoom(targetPercent);
        Dispatcher.BeginInvoke(
            () => ImageScrollViewer.ScrollToHorizontalOffset(0),
            DispatcherPriority.Render);
    }

    private void SetPageZoom(double percent)
    {
        ZoomSlider.Value = Math.Clamp(
            percent,
            ZoomSlider.Minimum,
            ZoomSlider.Maximum);
    }

    private void CenterCurrentPageView()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                double horizontal = Math.Max(
                    0,
                    (ImageScrollViewer.ExtentWidth - ImageScrollViewer.ViewportWidth) / 2);
                double vertical = Math.Max(
                    0,
                    (ImageScrollViewer.ExtentHeight - ImageScrollViewer.ViewportHeight) / 2);
                ImageScrollViewer.ScrollToHorizontalOffset(horizontal);
                ImageScrollViewer.ScrollToVerticalOffset(vertical);
            },
            DispatcherPriority.Render);
    }
}
