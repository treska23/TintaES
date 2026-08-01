using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Añade miniaturas reales al selector de páginas sin bloquear la interfaz. El panel se
/// muestra para cómics y proyectos multipágina; las imágenes sueltas de una sola página
/// utilizan su propia pestaña y no duplican la navegación a la izquierda.
/// </summary>
public partial class MainWindow
{
    private static readonly bool PageThumbnailSidebarRegistered = RegisterPageThumbnailSidebar();

    private readonly Dictionary<int, Image> _pageThumbnailImages = [];
    private readonly HashSet<int> _pageThumbnailLoads = [];
    private bool _pageThumbnailSidebarInstalled;
    private bool _pageThumbnailRefreshQueued;
    private string? _pageThumbnailSessionKey;
    private string? _pageThumbnailPageSignature;

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
            _pageThumbnailPageSignature = null;
            _pageThumbnailImages.Clear();
            _pageThumbnailLoads.Clear();
            return;
        }

        string sessionKey = BuildActiveDocumentSessionKey();
        string pageSignature = BuildPageThumbnailPageSignature();
        bool sessionChanged = !string.Equals(
            sessionKey,
            _pageThumbnailSessionKey,
            StringComparison.OrdinalIgnoreCase);
        bool pageSetChanged = !string.Equals(
            pageSignature,
            _pageThumbnailPageSignature,
            StringComparison.OrdinalIgnoreCase);

        if (sessionChanged || pageSetChanged)
        {
            _pageThumbnailSessionKey = sessionKey;
            _pageThumbnailPageSignature = pageSignature;
            _pageThumbnailImages.Clear();
            _pageThumbnailLoads.Clear();

            // Al abrir un .tinta la pestaña ya existe con el título «Cargando…». El Id de la
            // sesión no cambia cuando después se incorporan las páginas, por lo que el selector
            // antiguo no reconstruía sus filas. La firma de páginas detecta ese cambio real.
            _pageSelectionSessionKey = null;
            SyncPageSelectionPanel();

            bool shouldShow = _comicPages.Count > 1
                || !string.IsNullOrWhiteSpace(_currentProjectPath);
            SetPageSelectionPanelVisible(shouldShow);
            RenamePageSelectionHeader();
        }

        // Si otro componente reconstruyó las filas después del cambio de sesión, repetimos el
        // sincronizado una sola vez. No se crean miniaturas sobre filas pertenecientes al documento
        // anterior ni se deja vacío el lateral de un proyecto recién cargado.
        if (_pageSelectionRows.Count != _comicPages.Count)
        {
            _pageSelectionSessionKey = null;
            SyncPageSelectionPanel();
            if (_pageSelectionRows.Count != _comicPages.Count)
            {
                return;
            }
        }

        for (int index = 0; index < _comicPages.Count; index++)
        {
            if (!_pageSelectionRows.TryGetValue(index, out Border? row)
                || row.Child is not Grid rowGrid)
            {
                continue;
            }

            EnsurePageThumbnailRow(index, row, rowGrid, pageSignature);
        }
    }

    private string BuildPageThumbnailPageSignature()
    {
        string first = _comicPages.Count > 0 ? _comicPages[0].SourcePath : string.Empty;
        string last = _comicPages.Count > 0 ? _comicPages[^1].SourcePath : string.Empty;
        return $"{_currentProjectPath}|{_comicWorkspace}|{_comicPages.Count}|{first}|{last}";
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
        string pageSignature)
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
            LoadPageThumbnailAsync(index, image, _comicPages[index].SourcePath, pageSignature);
        }
    }

    private async void LoadPageThumbnailAsync(
        int index,
        Image target,
        string path,
        string pageSignature)
    {
        try
        {
            BitmapSource thumbnail = await Task.Run(() => LoadPageThumbnail(path));
            if (!string.Equals(
                    pageSignature,
                    _pageThumbnailPageSignature,
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
