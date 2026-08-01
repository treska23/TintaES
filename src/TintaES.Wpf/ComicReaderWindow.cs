using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TintaES.Wpf;

/// <summary>
/// Lector CBZ independiente del editor. Carga las páginas bajo demanda y conserva únicamente
/// una caché pequeña, por lo que puede abrir cómics largos sin extraerlos ni mantenerlos enteros
/// en memoria.
/// </summary>
public sealed partial class ComicReaderWindow : Window
{
    private static readonly string[] SupportedExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"];

    private readonly Image _pageImage;
    private readonly Grid _pageStage;
    private readonly Canvas _translationHitCanvas;
    private readonly Grid _viewerHost;
    private readonly ScrollViewer _scrollViewer;
    private readonly ScaleTransform _zoomTransform;
    private readonly Slider _zoomSlider;
    private readonly TextBlock _zoomText;
    private readonly TextBlock _statusText;
    private readonly ComboBox _pageSelector;
    private readonly Button _previousButton;
    private readonly Button _nextButton;
    private readonly Button _directionButton;
    private readonly Border _loadingOverlay;
    private readonly TextBlock _loadingText;
    private readonly SemaphoreSlim _archiveGate = new(1, 1);
    private readonly Dictionary<int, BitmapSource> _pageCache = [];
    private readonly LinkedList<int> _cacheOrder = [];

    private FileStream? _archiveStream;
    private ZipArchive? _archive;
    private List<ZipArchiveEntry> _pages = [];
    private string? _archivePath;
    private int _pageIndex = -1;
    private int _loadRevision;
    private bool _syncingPageSelector;
    private bool _syncingZoom;
    private bool _rightToLeft;
    private ReaderFitMode _fitMode = ReaderFitMode.Page;

    private int PageCount => _readerDocument?.Pages.Count ?? _pages.Count;

    private bool _dragging;
    private MouseButton _dragButton;
    private Point _dragStart;
    private DateTime _dragStartedUtc;
    private double _dragHorizontalOffset;
    private double _dragVerticalOffset;

    public ComicReaderWindow(string? cbzPath = null)
    {
        Title = $"Visor CBZ · Tinta ES · {MainWindow.CurrentUiBuildStamp}";
        Width = 1240;
        Height = 900;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(23, 26, 29));

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var toolbar = new DockPanel
        {
            LastChildFill = true,
            Height = 54,
            Background = new SolidColorBrush(Color.FromRgb(18, 21, 24)),
            Margin = new Thickness(0)
        };
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        var toolbarItems = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0)
        };
        toolbar.Children.Add(toolbarItems);

        Button openButton = CreateToolbarButton("Abrir…", 82);
        openButton.Click += OpenButton_Click;
        toolbarItems.Children.Add(openButton);

        _previousButton = CreateToolbarButton("‹", 42);
        _previousButton.Margin = new Thickness(12, 0, 5, 0);
        _previousButton.Click += (_, _) => Navigate(-1);
        toolbarItems.Children.Add(_previousButton);

        _pageSelector = new ComboBox
        {
            Width = 150,
            Height = 30,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = false,
            ToolTip = "Ir directamente a una página"
        };
        _pageSelector.SelectionChanged += PageSelector_SelectionChanged;
        toolbarItems.Children.Add(_pageSelector);

        _nextButton = CreateToolbarButton("›", 42);
        _nextButton.Click += (_, _) => Navigate(1);
        toolbarItems.Children.Add(_nextButton);

        _directionButton = CreateToolbarButton("Occidental →", 104);
        _directionButton.Margin = new Thickness(12, 0, 5, 0);
        _directionButton.ToolTip = "Cambiar entre lectura occidental y lectura manga";
        _directionButton.Click += (_, _) => ToggleReadingDirection();
        toolbarItems.Children.Add(_directionButton);

        Button fitPageButton = CreateToolbarButton("Página", 76);
        fitPageButton.Margin = new Thickness(12, 0, 5, 0);
        fitPageButton.Click += (_, _) => FitToViewport(ReaderFitMode.Page);
        toolbarItems.Children.Add(fitPageButton);

        Button fitWidthButton = CreateToolbarButton("Ancho", 72);
        fitWidthButton.Margin = new Thickness(0, 0, 12, 0);
        fitWidthButton.Click += (_, _) => FitToViewport(ReaderFitMode.Width);
        toolbarItems.Children.Add(fitWidthButton);

        toolbarItems.Children.Add(new TextBlock
        {
            Text = "ZOOM",
            Foreground = new SolidColorBrush(Color.FromRgb(179, 186, 192)),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        _zoomTransform = new ScaleTransform(1, 1);
        _zoomSlider = new Slider
        {
            Minimum = 5,
            Maximum = 400,
            Value = 100,
            Width = 125,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = false,
            ToolTip = "La rueda del ratón cambia el zoom directamente"
        };
        _zoomSlider.ValueChanged += ZoomSlider_ValueChanged;
        toolbarItems.Children.Add(_zoomSlider);

        _zoomText = new TextBlock
        {
            Text = "100 %",
            Width = 58,
            TextAlignment = TextAlignment.Right,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        };
        toolbarItems.Children.Add(_zoomText);

        _viewerHost = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(38, 42, 45))
        };
        Grid.SetRow(_viewerHost, 1);
        root.Children.Add(_viewerHost);

        _pageImage = new Image
        {
            // Los JPG de cómic suelen declarar 72 DPI. Stretch=None hace que WPF los
            // dibuje mayores que su caja en píxeles y recorta la derecha y el final.
            // Fill mantiene imagen y zonas táctiles en las mismas dimensiones.
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(_pageImage, BitmapScalingMode.HighQuality);

        _translationHitCanvas = new Canvas
        {
            Background = Brushes.Transparent,
            // La selección compara todas las regiones. Si cada rectángulo recibe
            // el clic por separado, el último dibujado gana en los solapes.
            IsHitTestVisible = false
        };
        _pageStage = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = _zoomTransform,
            Background = Brushes.Transparent
        };
        _pageStage.Children.Add(_pageImage);
        _pageStage.Children.Add(_translationHitCanvas);

        _scrollViewer = new ScrollViewer
        {
            Content = _pageStage,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            PanningMode = PanningMode.Both,
            CanContentScroll = false,
            Padding = new Thickness(16),
            Cursor = Cursors.Hand
        };
        _scrollViewer.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
        _scrollViewer.PreviewMouseDown += ScrollViewer_PreviewMouseDown;
        _scrollViewer.PreviewMouseMove += ScrollViewer_PreviewMouseMove;
        _scrollViewer.PreviewMouseUp += ScrollViewer_PreviewMouseUp;
        _scrollViewer.LostMouseCapture += (_, _) => EndDrag();
        _viewerHost.Children.Add(_scrollViewer);

        _loadingText = new TextBlock
        {
            Text = "Abre un archivo CBZ para empezar.",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520
        };
        _loadingOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 23, 26, 29)),
            Child = _loadingText,
            Visibility = Visibility.Visible
        };
        Panel.SetZIndex(_loadingOverlay, 1000);
        _viewerHost.Children.Add(_loadingOverlay);

        _statusText = new TextBlock
        {
            Text = "Sin cómic abierto",
            Height = 28,
            Padding = new Thickness(12, 6, 12, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(186, 193, 198)),
            Background = new SolidColorBrush(Color.FromRgb(18, 21, 24)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(_statusText, 2);
        root.Children.Add(_statusText);

        InstallTranslationReaderExperience(root, toolbar, _statusText);

        PreviewKeyDown += ComicReaderWindow_PreviewKeyDown;
        SizeChanged += (_, _) =>
        {
            if (_fitMode != ReaderFitMode.None && _pageImage.Source is not null)
            {
                Dispatcher.BeginInvoke(() => FitToViewport(_fitMode));
            }
        };
        Closed += (_, _) => DisposeArchive();

        if (!string.IsNullOrWhiteSpace(cbzPath))
        {
            Loaded += async (_, _) => await OpenArchiveAsync(cbzPath);
        }
    }

    internal ComicReaderWindow(ReaderComicDocument document)
        : this()
    {
        _readerDocument = document ?? throw new ArgumentNullException(nameof(document));
        Loaded += async (_, _) => await OpenDocumentAsync();
    }

    private static Button CreateToolbarButton(string text, double width)
    {
        return new Button
        {
            Content = text,
            Width = width,
            Height = 31,
            Padding = new Thickness(8, 2, 8, 2),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(43, 48, 52)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(73, 79, 84)),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir cómic CBZ",
            Filter = "Comic Book ZIP|*.cbz|Todos los archivos|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await OpenArchiveAsync(dialog.FileName);
        }
    }

    private async Task OpenArchiveAsync(string path)
    {
        ShowLoading("Abriendo el cómic…");
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        try
        {
            DisposeArchive();
            _readerDocument = null;
            _archiveStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _archive = new ZipArchive(_archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            _pages = _archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name) && IsSupportedImage(entry.FullName))
                .OrderBy(entry => entry.FullName, NaturalStringComparer.Instance)
                .ToList();
            if (_pages.Count == 0)
            {
                throw new InvalidOperationException("El CBZ no contiene páginas de imagen compatibles.");
            }

            _archivePath = path;
            Title = $"{Path.GetFileNameWithoutExtension(path)} · Visor CBZ · Tinta ES · " +
                    MainWindow.CurrentUiBuildStamp;
            PopulatePageSelector();
            _pageCache.Clear();
            _cacheOrder.Clear();
            _fitMode = ReaderFitMode.Page;
            await ShowPageAsync(0);
        }
        catch (Exception exception)
        {
            DisposeArchive();
            ShowLoading("No se pudo abrir el cómic.");
            MessageBox.Show(
                this,
                $"No se pudo abrir el CBZ.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void PopulatePageSelector()
    {
        _syncingPageSelector = true;
        try
        {
            _pageSelector.Items.Clear();
            for (int index = 0; index < PageCount; index++)
            {
                _pageSelector.Items.Add($"Página {index + 1} · {GetPageDisplayName(index)}");
            }
            _pageSelector.IsEnabled = PageCount > 0;
            _zoomSlider.IsEnabled = PageCount > 0;
        }
        finally
        {
            _syncingPageSelector = false;
        }
    }

    private async Task ShowPageAsync(int index)
    {
        if (index < 0 || index >= PageCount)
        {
            return;
        }

        int revision = ++_loadRevision;
        HideTranslationCard();
        ShowLoading($"Cargando página {index + 1} de {PageCount}…");
        UpdateNavigationButtons(index);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        try
        {
            BitmapSource bitmap = await GetPageBitmapAsync(index);
            if (revision != _loadRevision)
            {
                return;
            }

            _pageIndex = index;
            _pageImage.Source = bitmap;
            _pageStage.Width = bitmap.PixelWidth;
            _pageStage.Height = bitmap.PixelHeight;
            _pageImage.Width = bitmap.PixelWidth;
            _pageImage.Height = bitmap.PixelHeight;
            _translationHitCanvas.Width = bitmap.PixelWidth;
            _translationHitCanvas.Height = bitmap.PixelHeight;
            RebuildTranslationHitAreas(index, bitmap.PixelWidth, bitmap.PixelHeight);
            _syncingPageSelector = true;
            try
            {
                _pageSelector.SelectedIndex = index;
            }
            finally
            {
                _syncingPageSelector = false;
            }

            UpdateNavigationButtons(index);
            _statusText.Text = $"{GetReaderTitle()} · Página {index + 1} de {PageCount} · " +
                               $"{GetPageRegionSummary(index)} · {bitmap.PixelWidth} × {bitmap.PixelHeight} px";
            _loadingOverlay.Visibility = Visibility.Collapsed;
            FitToViewport(_fitMode == ReaderFitMode.None ? ReaderFitMode.Page : _fitMode);
            _scrollViewer.ScrollToTop();
            _scrollViewer.ScrollToHorizontalOffset(0);
            _ = PreloadNearbyPagesAsync(index);
        }
        catch (Exception exception)
        {
            if (revision != _loadRevision)
            {
                return;
            }
            ShowLoading($"No se pudo cargar la página {index + 1}.");
            MessageBox.Show(
                this,
                $"No se pudo cargar la página {index + 1}.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<BitmapSource> GetPageBitmapAsync(int index)
    {
        if (_pageCache.TryGetValue(index, out BitmapSource? cached))
        {
            TouchCache(index);
            return cached;
        }

        await _archiveGate.WaitAsync();
        try
        {
            if (_pageCache.TryGetValue(index, out cached))
            {
                TouchCache(index);
                return cached;
            }

            if (index < 0 || index >= PageCount)
            {
                throw new InvalidOperationException("El cómic ya no está abierto.");
            }

            BitmapSource bitmap;
            if (_readerDocument is not null)
            {
                string sourcePath = _readerDocument.Pages[index].SourcePath;
                bitmap = await Task.Run(() => LoadFileBitmap(sourcePath));
            }
            else
            {
                if (_archive is null)
                {
                    throw new InvalidOperationException("El cómic ya no está abierto.");
                }
                ZipArchiveEntry entry = _pages[index];
                bitmap = await Task.Run(() => LoadEntryBitmap(entry));
            }
            _pageCache[index] = bitmap;
            TouchCache(index);
            TrimCache();
            return bitmap;
        }
        finally
        {
            _archiveGate.Release();
        }
    }

    private static BitmapSource LoadEntryBitmap(ZipArchiveEntry entry)
    {
        using Stream entryStream = entry.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        memory.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = memory;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource LoadFileBitmap(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No se encuentra la página del cómic.", path);
        }

        using FileStream input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        memory.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = memory;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async Task PreloadNearbyPagesAsync(int centerIndex)
    {
        foreach (int index in new[] { centerIndex - 1, centerIndex + 1 })
        {
            if (index < 0 || index >= PageCount || _pageCache.ContainsKey(index))
            {
                continue;
            }
            try
            {
                await GetPageBitmapAsync(index);
            }
            catch
            {
                // La precarga es opcional; la carga visible mostrará cualquier error real.
            }
        }
    }

    private void TouchCache(int index)
    {
        LinkedListNode<int>? node = _cacheOrder.Find(index);
        if (node is not null)
        {
            _cacheOrder.Remove(node);
        }
        _cacheOrder.AddLast(index);
    }

    private void TrimCache()
    {
        const int maximumCachedPages = 5;
        while (_cacheOrder.Count > maximumCachedPages)
        {
            int index = _cacheOrder.First!.Value;
            _cacheOrder.RemoveFirst();
            if (index != _pageIndex)
            {
                _pageCache.Remove(index);
            }
        }
    }

    private void PageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPageSelector)
        {
            return;
        }
        int index = _pageSelector.SelectedIndex;
        if (index >= 0 && index < PageCount && index != _pageIndex)
        {
            _ = ShowPageAsync(index);
        }
    }

    private void Navigate(int delta)
    {
        int target = _pageIndex + delta;
        if (target >= 0 && target < PageCount)
        {
            _ = ShowPageAsync(target);
        }
    }

    private void NavigateByArrow(bool rightArrow)
    {
        bool advance = _rightToLeft ? !rightArrow : rightArrow;
        Navigate(advance ? 1 : -1);
    }

    private void ToggleReadingDirection()
    {
        _rightToLeft = !_rightToLeft;
        _directionButton.Content = _rightToLeft ? "Manga ←" : "Occidental →";
        _previousButton.Content = _rightToLeft ? "›" : "‹";
        _nextButton.Content = _rightToLeft ? "‹" : "›";
        _statusText.Text = _rightToLeft
            ? "Modo manga: la flecha izquierda avanza de página."
            : "Modo occidental: la flecha derecha avanza de página.";
        UpdateReaderEdgeButtons(_pageIndex);
    }

    private void UpdateNavigationButtons(int index)
    {
        bool canGoBack = index > 0;
        bool canGoForward = index >= 0 && index < PageCount - 1;
        _previousButton.IsEnabled = true;
        _previousButton.IsHitTestVisible = canGoBack;
        _previousButton.Opacity = canGoBack ? 1 : 0.28;
        _nextButton.IsEnabled = true;
        _nextButton.IsHitTestVisible = canGoForward;
        _nextButton.Opacity = canGoForward ? 1 : 0.28;
        UpdateReaderEdgeButtons(index);
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingZoom)
        {
            return;
        }
        _fitMode = ReaderFitMode.None;
        ApplyZoom(e.NewValue);
    }

    private void ApplyZoom(double percent)
    {
        percent = Math.Clamp(percent, _zoomSlider.Minimum, _zoomSlider.Maximum);
        double scale = percent / 100d;
        _zoomTransform.ScaleX = scale;
        _zoomTransform.ScaleY = scale;
        _zoomText.Text = $"{percent:0} %";
    }

    private void SetZoom(double percent, ReaderFitMode fitMode)
    {
        _syncingZoom = true;
        try
        {
            percent = Math.Clamp(percent, _zoomSlider.Minimum, _zoomSlider.Maximum);
            _zoomSlider.Value = percent;
            ApplyZoom(percent);
            _fitMode = fitMode;
        }
        finally
        {
            _syncingZoom = false;
        }
    }

    private void FitToViewport(ReaderFitMode mode)
    {
        if (_pageImage.Source is not BitmapSource bitmap)
        {
            return;
        }

        double viewportWidth = _scrollViewer.ViewportWidth > 1
            ? _scrollViewer.ViewportWidth - 34
            : Math.Max(100, ActualWidth - 70);
        double viewportHeight = _scrollViewer.ViewportHeight > 1
            ? _scrollViewer.ViewportHeight - 34
            : Math.Max(100, ActualHeight - 130);

        double widthScale = viewportWidth / Math.Max(1, bitmap.PixelWidth);
        double heightScale = viewportHeight / Math.Max(1, bitmap.PixelHeight);
        double scale = mode == ReaderFitMode.Width ? widthScale : Math.Min(widthScale, heightScale);
        SetZoom(scale * 100, mode);
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point pointer = e.GetPosition(_scrollViewer);
        double step = e.Delta > 0 ? 8 : -8;
        ZoomReaderAroundPoint(_zoomSlider.Value + step, pointer);
        e.Handled = true;
    }

    private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool canDrag = e.ChangedButton == MouseButton.Middle
            || e.ChangedButton == MouseButton.Left;
        if (!canDrag)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            if (DateTime.UtcNow < _ignoreSyntheticMouseUntilUtc)
            {
                e.Handled = true;
                return;
            }

            // Los bocadillos tienen prioridad absoluta sobre el paneo y el cambio
            // de página. Así un clic destinado a traducir nunca inicia un swipe.
            if (ResolveReaderRegionAt(e.GetPosition(_pageStage)) is { } region)
            {
                BeginMouseTranslationHold(region);
                e.Handled = true;
                return;
            }

            HideTranslationCard();
        }

        _dragging = true;
        _dragButton = e.ChangedButton;
        _dragStart = e.GetPosition(_scrollViewer);
        _dragStartedUtc = DateTime.UtcNow;
        _dragHorizontalOffset = _scrollViewer.HorizontalOffset;
        _dragVerticalOffset = _scrollViewer.VerticalOffset;
        _scrollViewer.Cursor = Cursors.Hand;
        _scrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void ScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        bool stillPressed = _dragButton == MouseButton.Middle
            ? e.MiddleButton == MouseButtonState.Pressed
            : e.LeftButton == MouseButtonState.Pressed;
        if (!stillPressed)
        {
            EndDrag();
            return;
        }

        Point current = e.GetPosition(_scrollViewer);
        Vector delta = current - _dragStart;
        _scrollViewer.ScrollToHorizontalOffset(_dragHorizontalOffset - delta.X);
        _scrollViewer.ScrollToVerticalOffset(_dragVerticalOffset - delta.Y);
        e.Handled = true;
    }

    private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_translationMouseHeld && e.ChangedButton == MouseButton.Left)
        {
            EndMouseTranslationHold();
            e.Handled = true;
            return;
        }

        if (_dragging && e.ChangedButton == _dragButton)
        {
            Point end = e.GetPosition(_scrollViewer);
            Vector gesture = end - _dragStart;
            TimeSpan elapsed = DateTime.UtcNow - _dragStartedUtc;
            bool changedPage = TryNavigateFromSwipe(gesture, elapsed, scaledGesture: false);
            EndDrag();
            e.Handled = changedPage || gesture.Length > 3;
        }
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        _scrollViewer.Cursor = Cursors.Hand;
        if (_scrollViewer.IsMouseCaptured)
        {
            _scrollViewer.ReleaseMouseCapture();
        }
    }

    private void ComicReaderWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_translationCard?.Visibility == Visibility.Visible)
            {
                HideTranslationCard();
            }
            else if (_isFullscreen)
            {
                ToggleFullscreen();
            }
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                NavigateByArrow(rightArrow: false);
                e.Handled = true;
                break;
            case Key.Right:
                NavigateByArrow(rightArrow: true);
                e.Handled = true;
                break;
            case Key.PageUp:
                Navigate(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                Navigate(1);
                e.Handled = true;
                break;
            case Key.Home:
                _ = ShowPageAsync(0);
                e.Handled = true;
                break;
            case Key.End:
                if (PageCount > 0)
                {
                    _ = ShowPageAsync(PageCount - 1);
                }
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                SetZoom(_zoomSlider.Value + 5, ReaderFitMode.None);
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                SetZoom(_zoomSlider.Value - 5, ReaderFitMode.None);
                e.Handled = true;
                break;
        }
    }

    private void ShowLoading(string message)
    {
        _loadingText.Text = message;
        _loadingOverlay.Visibility = Visibility.Visible;
        _statusText.Text = message;
    }

    private void DisposeArchive()
    {
        _loadRevision++;
        HideTranslationCard();
        _pageImage.Source = null;
        _translationHitCanvas.Children.Clear();
        _pages.Clear();
        _pageCache.Clear();
        _cacheOrder.Clear();
        _pageIndex = -1;
        _archive?.Dispose();
        _archive = null;
        _archiveStream?.Dispose();
        _archiveStream = null;
        _archivePath = null;
        _syncingPageSelector = true;
        try
        {
            _pageSelector.Items.Clear();
            _pageSelector.IsEnabled = false;
            _zoomSlider.IsEnabled = false;
        }
        finally
        {
            _syncingPageSelector = false;
        }
        UpdateNavigationButtons(-1);
    }

    private static bool IsSupportedImage(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private enum ReaderFitMode
    {
        None,
        Page,
        Width
    }

    private sealed class NaturalStringComparer : IComparer<string>
    {
        public static NaturalStringComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            int leftIndex = 0;
            int rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                char leftCharacter = left[leftIndex];
                char rightCharacter = right[rightIndex];
                if (char.IsDigit(leftCharacter) && char.IsDigit(rightCharacter))
                {
                    int leftStart = leftIndex;
                    int rightStart = rightIndex;
                    while (leftIndex < left.Length && char.IsDigit(left[leftIndex])) leftIndex++;
                    while (rightIndex < right.Length && char.IsDigit(right[rightIndex])) rightIndex++;

                    string leftNumber = left[leftStart..leftIndex].TrimStart('0');
                    string rightNumber = right[rightStart..rightIndex].TrimStart('0');
                    if (leftNumber.Length == 0) leftNumber = "0";
                    if (rightNumber.Length == 0) rightNumber = "0";

                    int lengthComparison = leftNumber.Length.CompareTo(rightNumber.Length);
                    if (lengthComparison != 0) return lengthComparison;
                    int numberComparison = string.Compare(leftNumber, rightNumber, StringComparison.Ordinal);
                    if (numberComparison != 0) return numberComparison;
                    continue;
                }

                int characterComparison = char.ToUpperInvariant(leftCharacter)
                    .CompareTo(char.ToUpperInvariant(rightCharacter));
                if (characterComparison != 0) return characterComparison;
                leftIndex++;
                rightIndex++;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
