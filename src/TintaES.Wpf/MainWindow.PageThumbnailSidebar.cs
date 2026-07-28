using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene visible el selector de páginas también con una sola página y añade miniaturas reales
/// sin bloquear el hilo de interfaz. El usuario puede ocultarlo manualmente; solo se abre de nuevo
/// al comenzar una sesión distinta.
/// </summary>
public partial class MainWindow
{
    private static readonly bool PageThumbnailSidebarRegistered = RegisterPageThumbnailSidebar();

    private readonly Dictionary<int, Image> _pageThumbnailImages = [];
    private readonly HashSet<int> _pageThumbnailLoads = [];
    private bool _pageThumbnailSidebarInstalled;
    private bool _pageThumbnailRefreshQueued;
    private string? _pageThumbnailSessionKey;

    private static bool RegisterPageThumbnailSidebar()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_PageThumbnailSidebarLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_PageThumbnailSidebarLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallPageThumbnailSidebar,
                DispatcherPriority.ContextIdle);
        }
    }

    private void InstallPageThumbnailSidebar()
    {
        if (_pageThumbnailSidebarInstalled)
        {
            QueuePageThumbnailRefresh();
            return;
        }

        if (_pageSelectionPanel is null || _pageSelectionItemsPanel is null)
        {
            Dispatcher.BeginInvoke(InstallPageThumbnailSidebar, DispatcherPriority.ApplicationIdle);
            return;
        }

        _pageThumbnailSidebarInstalled = true;
        LayoutUpdated += (_, _) => QueuePageThumbnailRefresh();
        QueuePageThumbnailRefresh();
    }

    private void QueuePageThumbnailRefresh()
    {
        if (_pageThumbnailRefreshQueued || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _pageThumbnailRefreshQueued = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _pageThumbnailRefreshQueued = false;
                RefreshPageThumbnailSidebar();
            },
            DispatcherPriority.Background);
    }

    private void RefreshPageThumbnailSidebar()
    {
        if (_pageSelectionPanel is null || _pageSelectionItemsPanel is null)
        {
            return;
        }

        if (_comicPages.Count == 0)
        {
            _pageThumbnailSessionKey = null;
            _pageThumbnailImages.Clear();
            _pageThumbnailLoads.Clear();
            return;
        }

        string sessionKey = BuildActiveDocumentSessionKey();
        bool newSession = !string.Equals(
            sessionKey,
            _pageThumbnailSessionKey,
            StringComparison.OrdinalIgnoreCase);
        if (newSession)
        {
            _pageThumbnailSessionKey = sessionKey;
            _pageThumbnailImages.Clear();
            _pageThumbnailLoads.Clear();

            // SyncPageSelectionPanel ocultaba expresamente el panel cuando Count == 1.
            // Lo abrimos una vez por sesión; si el usuario pulsa cerrar, respetamos su decisión.
            SetPageSelectionPanelVisible(true);
            RenamePageSelectionHeader();
        }

        for (int index = 0; index < _comicPages.Count; index++)
        {
            if (!_pageSelectionRows.TryGetValue(index, out Border? row)
                || row.Child is not Grid rowGrid)
            {
                continue;
            }

            EnsurePageThumbnailRow(index, row, rowGrid, sessionKey);
        }
    }

    private void RenamePageSelectionHeader()
    {
        if (_pageSelectionPanel is null)
        {
            return;
        }

        foreach (TextBlock text in FindVisualChildren<TextBlock>(_pageSelectionPanel))
        {
            if (string.Equals(text.Text, "PÁGINAS A EXPORTAR", StringComparison.Ordinal))
            {
                text.Text = "PÁGINAS";
                break;
            }
        }
    }

    private void EnsurePageThumbnailRow(
        int index,
        Border row,
        Grid rowGrid,
        string sessionKey)
    {
        row.MinHeight = 94;

        Image? image = rowGrid.Children
            .OfType<Border>()
            .Select(border => border.Child)
            .OfType<Image>()
            .FirstOrDefault(candidate => Equals(candidate.Tag, "tinta-page-thumbnail"));

        if (image is null)
        {
            if (rowGrid.ColumnDefinitions.Count == 2)
            {
                rowGrid.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = new GridLength(72) });
                foreach (UIElement child in rowGrid.Children)
                {
                    if (Grid.GetColumn(child) == 1)
                    {
                        Grid.SetColumn(child, 2);
                    }
                }
            }

            image = new Image
            {
                Tag = "tinta-page-thumbnail",
                Width = 62,
                Height = 82,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            var thumbnailBorder = new Border
            {
                Width = 66,
                Height = 86,
                Margin = new Thickness(2, 4, 4, 4),
                Padding = new Thickness(2),
                Background = new SolidColorBrush(Color.FromRgb(17, 20, 22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(66, 72, 78)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Child = image
            };
            Grid.SetColumn(thumbnailBorder, 1);
            rowGrid.Children.Add(thumbnailBorder);
        }

        _pageThumbnailImages[index] = image;
        if (_pageSelectionCheckBoxes.TryGetValue(index, out CheckBox? checkBox))
        {
            checkBox.ToolTip = "Incluir esta página en el siguiente proceso y en la exportación";
        }

        if (image.Source is null && _pageThumbnailLoads.Add(index))
        {
            LoadPageThumbnailAsync(index, image, _comicPages[index].SourcePath, sessionKey);
        }
    }

    private async void LoadPageThumbnailAsync(
        int index,
        Image target,
        string path,
        string sessionKey)
    {
        try
        {
            BitmapSource thumbnail = await Task.Run(() => LoadPageThumbnail(path));
            if (!string.Equals(
                    sessionKey,
                    _pageThumbnailSessionKey,
                    StringComparison.OrdinalIgnoreCase)
                || !_pageThumbnailImages.TryGetValue(index, out Image? current)
                || !ReferenceEquals(current, target))
            {
                return;
            }

            target.Source = thumbnail;
            target.ToolTip = Path.GetFileName(path);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or FileFormatException)
        {
            target.ToolTip = $"No se pudo crear la miniatura: {exception.Message}";
        }
    }

    private static BitmapSource LoadPageThumbnail(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.DecodePixelWidth = 160;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
}