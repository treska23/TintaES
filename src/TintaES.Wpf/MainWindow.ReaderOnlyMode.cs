using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TintaES.Wpf;

/// <summary>
/// Modo de lectura del ejecutable independiente. No implementa un segundo visor: reutiliza
/// MainWindow, LoadTintaProjectAsync, ImageStage, _regions y la interacción de traducción de
/// la aplicación madre. Únicamente retira de la interfaz las herramientas de autoría.
/// </summary>
public partial class MainWindow
{
    private bool _readerOnlyMode;
    private bool _readerOnlyInputInstalled;
    private Slider? _readerOnlyPageSlider;
    private TextBlock? _readerOnlyPageText;
    private bool _readerOnlySyncingSlider;
    private int _readerOnlyLastSyncedPage = int.MinValue;
    private TouchDevice? _readerOnlySwipeTouch;
    private Point _readerOnlySwipeStart;
    private Point _readerOnlySwipeLast;
    private DateTime _readerOnlySwipeStartedUtc;

    public MainWindow(bool readerOnly)
        : this()
    {
        _readerOnlyMode = readerOnly;
        if (!readerOnly)
        {
            return;
        }

        // El Reader no inicia Ollama ni calienta OCR/Python. El resto de la MainWindow se carga
        // normalmente para conservar exactamente la misma ruta de proyecto y lectura.
        Loaded -= MainWindow_Loaded;
        Loaded += MainWindow_ReaderOnlyLoaded;
    }

    public async Task OpenReaderProjectAsync(string projectPath)
    {
        if (!_readerOnlyMode)
        {
            throw new InvalidOperationException("Esta ventana no está en modo Reader.");
        }
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            throw new FileNotFoundException("No se encuentra el proyecto TintaES.", projectPath);
        }
        if (!string.Equals(Path.GetExtension(projectPath), ".tinta", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("El Reader abre proyectos .tinta.");
        }

        InstallComicBookHandlers();
        InstallDirectPageSelector();
        InstallDirectReaderInput();
        InstallMainTranslationInteraction();
        ApplyReaderOnlyShell();

        await LoadTintaProjectAsync(projectPath);
        ApplyReaderOnlyShell();
        SyncReaderOnlyNavigation(force: true);
    }

    private void MainWindow_ReaderOnlyLoaded(object sender, RoutedEventArgs e)
    {
        InstallComicBookHandlers();
        InstallDirectReaderInput();
        InstallMainTranslationInteraction();
        InstallReaderOnlyInput();

        // Los instaladores de la aplicación madre terminan en ApplicationIdle. Aplicamos el
        // recorte después para que ningún módulo tardío vuelva a enseñar controles de edición.
        Dispatcher.BeginInvoke(ApplyReaderOnlyShell, DispatcherPriority.SystemIdle);
    }

    private void ApplyReaderOnlyShell()
    {
        if (!_readerOnlyMode)
        {
            return;
        }

        Title = "Tinta ES · Reader";
        MinWidth = 420;
        MinHeight = 520;
        WindowState = WindowState.Maximized;

        InstallDirectReaderInput();
        InstallMainTranslationInteraction();
        InstallReaderOnlyInput();

        // Cabecera de Ollama/modelo fuera. Se mantiene una única barra con Abrir .tinta y zoom.
        if (Content is Grid root && root.RowDefinitions.Count >= 5)
        {
            root.RowDefinitions[0].Height = new GridLength(0);
            root.RowDefinitions[1].Height = new GridLength(58);
            root.RowDefinitions[2].Height = new GridLength(0);
            root.RowDefinitions[4].Height = new GridLength(52);

            Grid? workspace = root.Children
                .OfType<Grid>()
                .FirstOrDefault(child => Grid.GetRow(child) == 3 && child.ColumnDefinitions.Count >= 2);
            if (workspace is not null)
            {
                workspace.ColumnDefinitions[1].Width = new GridLength(0);
                foreach (UIElement child in workspace.Children.Cast<UIElement>()
                             .Where(child => Grid.GetColumn(child) == 1))
                {
                    child.Visibility = Visibility.Collapsed;
                }
            }

            EnsureReaderOnlyFooterNavigation(root);
        }

        OpenImageButton.Click -= OpenImageButton_Click;
        OpenImageButton.Click -= OpenComicFilesButton_Click;
        OpenImageButton.Click -= OpenStandaloneDocumentsButton_Click;
        OpenImageButton.Click -= OpenComicArchiveFilesButton_Click;
        OpenImageButton.Click -= OpenComicFilesButton_Click_Multi;
        OpenImageButton.Click -= ReaderOnlyOpenProjectButton_Click;
        OpenImageButton.Click += ReaderOnlyOpenProjectButton_Click;
        OpenImageButton.Content = "Abrir .tinta";
        OpenImageButton.ToolTip = "Abrir un proyecto TintaES para leerlo";
        OpenImageButton.Visibility = Visibility.Visible;

        AnalyzeButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        AddRegionButton.Visibility = Visibility.Collapsed;
        ExportButton.Visibility = Visibility.Collapsed;
        OriginalPreviewButton.Visibility = Visibility.Collapsed;
        MaskPreviewButton.Visibility = Visibility.Collapsed;
        CleanPreviewButton.Visibility = Visibility.Collapsed;
        ResultPreviewButton.Visibility = Visibility.Collapsed;

        if (_openFolderButton is not null) _openFolderButton.Visibility = Visibility.Collapsed;
        if (_exportComicButton is not null) _exportComicButton.Visibility = Visibility.Collapsed;
        if (_exportPsdButton is not null) _exportPsdButton.Visibility = Visibility.Collapsed;
        if (_saveProjectButton is not null) _saveProjectButton.Visibility = Visibility.Collapsed;
        if (_comicReaderButton is not null) _comicReaderButton.Visibility = Visibility.Collapsed;
        if (_floatingEditorPalette is not null) _floatingEditorPalette.Visibility = Visibility.Collapsed;
        if (_pageSelectionPanel is not null) _pageSelectionPanel.Visibility = Visibility.Collapsed;
        if (_pageSelectionColumn is not null) _pageSelectionColumn.Width = new GridLength(0);
        if (_pageSelectionToggleButton is not null) _pageSelectionToggleButton.Visibility = Visibility.Collapsed;

        // El inspector ya está fuera de layout; la página original y sus regiones son las de la madre.
        OverlayCanvas.Children.Clear();
        ImageScrollViewer.Padding = new Thickness(14);
        SyncReaderOnlyNavigation(force: true);
    }

    private async void ReaderOnlyOpenProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir proyecto TintaES",
            Filter = "Proyecto TintaES (*.tinta)|*.tinta",
            DefaultExt = ".tinta",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await OpenReaderProjectAsync(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"No se pudo abrir el proyecto.\n\n{exception.Message}", "Tinta ES Reader",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EnsureReaderOnlyFooterNavigation(Grid root)
    {
        Border? footer = root.Children
            .OfType<Border>()
            .FirstOrDefault(child => Grid.GetRow(child) == 4);
        if (footer?.Child is not Grid footerGrid)
        {
            return;
        }

        if (_readerOnlyPageSlider is not null)
        {
            return;
        }

        footerGrid.Children.Clear();
        footerGrid.ColumnDefinitions.Clear();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Style? toolbarStyle = FindResource("ToolbarButton") as Style;
        var previous = new Button
        {
            Content = "‹",
            Width = 40,
            Height = 30,
            Style = toolbarStyle,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Página anterior"
        };
        previous.Click += async (_, _) => await NavigateReaderOnlyAsync(-1);
        footerGrid.Children.Add(previous);

        _readerOnlyPageSlider = new Slider
        {
            Minimum = 1,
            Maximum = 1,
            Value = 1,
            Margin = new Thickness(8, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
            ToolTip = "Arrastra para ir a otra página"
        };
        Grid.SetColumn(_readerOnlyPageSlider, 1);
        _readerOnlyPageSlider.ValueChanged += (_, _) => UpdateReaderOnlyPageTextFromSlider();
        _readerOnlyPageSlider.PreviewMouseLeftButtonUp += async (_, _) => await CommitReaderOnlySliderAsync();
        _readerOnlyPageSlider.PreviewTouchUp += async (_, _) => await CommitReaderOnlySliderAsync();
        _readerOnlyPageSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(async (_, _) => await CommitReaderOnlySliderAsync()));
        footerGrid.Children.Add(_readerOnlyPageSlider);

        var next = new Button
        {
            Content = "›",
            Width = 40,
            Height = 30,
            Style = toolbarStyle,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Página siguiente"
        };
        next.Click += async (_, _) => await NavigateReaderOnlyAsync(1);
        Grid.SetColumn(next, 2);
        footerGrid.Children.Add(next);

        _readerOnlyPageText = new TextBlock
        {
            Text = "— / —",
            MinWidth = 78,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Foreground = FindResource("MutedBrush") as Brush ?? Brushes.Gray,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(_readerOnlyPageText, 2);
        _readerOnlyPageText.Margin = new Thickness(48, 0, 0, 0);
        footerGrid.Children.Add(_readerOnlyPageText);

        LayoutUpdated += (_, _) => SyncReaderOnlyNavigation(force: false);
    }

    private void InstallReaderOnlyInput()
    {
        if (_readerOnlyInputInstalled)
        {
            return;
        }
        _readerOnlyInputInstalled = true;

        PreviewKeyDown += ReaderOnly_PreviewKeyDown;
        AddHandler(UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(ReaderOnly_PreviewTouchDown), handledEventsToo: true);
        AddHandler(UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>(ReaderOnly_PreviewTouchMove), handledEventsToo: true);
        AddHandler(UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>(ReaderOnly_PreviewTouchUp), handledEventsToo: true);
    }

    private async void ReaderOnly_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_readerOnlyMode || e.OriginalSource is TextBoxBase or ComboBox)
        {
            return;
        }

        int delta = e.Key switch
        {
            Key.Right or Key.PageDown => 1,
            Key.Left or Key.PageUp => -1,
            _ => 0
        };
        if (delta == 0)
        {
            return;
        }
        e.Handled = true;
        await NavigateReaderOnlyAsync(delta);
    }

    private void ReaderOnly_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        if (!_readerOnlyMode || IsReaderOnlyNavigationElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        Point point = e.GetTouchPoint(ImageStage).Position;
        if (point.X < 0 || point.Y < 0 || point.X > ImageStage.ActualWidth || point.Y > ImageStage.ActualHeight)
        {
            return;
        }

        _readerOnlySwipeTouch = e.TouchDevice;
        _readerOnlySwipeStart = point;
        _readerOnlySwipeLast = point;
        _readerOnlySwipeStartedUtc = DateTime.UtcNow;
    }

    private void ReaderOnly_PreviewTouchMove(object? sender, TouchEventArgs e)
    {
        if (_readerOnlySwipeTouch == e.TouchDevice)
        {
            _readerOnlySwipeLast = e.GetTouchPoint(ImageStage).Position;
        }
    }

    private async void ReaderOnly_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (_readerOnlySwipeTouch != e.TouchDevice)
        {
            return;
        }

        _readerOnlySwipeLast = e.GetTouchPoint(ImageStage).Position;
        _readerOnlySwipeTouch = null;
        Vector gesture = _readerOnlySwipeLast - _readerOnlySwipeStart;
        TimeSpan elapsed = DateTime.UtcNow - _readerOnlySwipeStartedUtc;

        double horizontal = Math.Abs(gesture.X);
        double required = Math.Max(90d, ImageScrollViewer.ViewportWidth * 0.12d);
        if (elapsed.TotalMilliseconds > 1_500
            || horizontal < required
            || Math.Abs(gesture.Y) > horizontal * 0.62d)
        {
            return;
        }

        e.Handled = true;
        await NavigateReaderOnlyAsync(gesture.X < 0 ? 1 : -1);
    }

    private static bool IsReaderOnlyNavigationElement(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Slider or Button or ComboBox)
            {
                return true;
            }
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private async Task NavigateReaderOnlyAsync(int delta)
    {
        int target = _comicPageIndex + delta;
        if (target < 0 || target >= _comicPages.Count || _pageNavigationBusy || _comicBatchBusy)
        {
            return;
        }

        await ShowComicPageFastAsync(target);
        SyncReaderOnlyNavigation(force: true);
    }

    private async Task CommitReaderOnlySliderAsync()
    {
        if (_readerOnlyPageSlider is null || _readerOnlySyncingSlider || _comicPages.Count == 0)
        {
            return;
        }
        int target = Math.Clamp((int)Math.Round(_readerOnlyPageSlider.Value) - 1, 0, _comicPages.Count - 1);
        if (target != _comicPageIndex && !_pageNavigationBusy && !_comicBatchBusy)
        {
            await ShowComicPageFastAsync(target);
        }
        SyncReaderOnlyNavigation(force: true);
    }

    private void SyncReaderOnlyNavigation(bool force)
    {
        if (!_readerOnlyMode || _readerOnlyPageSlider is null)
        {
            return;
        }
        if (!force && _readerOnlyLastSyncedPage == _comicPageIndex)
        {
            return;
        }

        _readerOnlyLastSyncedPage = _comicPageIndex;
        _readerOnlySyncingSlider = true;
        try
        {
            int count = Math.Max(1, _comicPages.Count);
            _readerOnlyPageSlider.Minimum = 1;
            _readerOnlyPageSlider.Maximum = count;
            _readerOnlyPageSlider.IsEnabled = _comicPages.Count > 1;
            _readerOnlyPageSlider.Value = _comicPageIndex >= 0 ? _comicPageIndex + 1 : 1;
            if (_readerOnlyPageText is not null)
            {
                _readerOnlyPageText.Text = _comicPageIndex >= 0
                    ? $"{_comicPageIndex + 1} / {_comicPages.Count}"
                    : "— / —";
            }
        }
        finally
        {
            _readerOnlySyncingSlider = false;
        }
    }

    private void UpdateReaderOnlyPageTextFromSlider()
    {
        if (_readerOnlyPageText is null || _readerOnlyPageSlider is null || _readerOnlySyncingSlider)
        {
            return;
        }
        _readerOnlyPageText.Text = _comicPages.Count == 0
            ? "— / —"
            : $"{Math.Clamp((int)Math.Round(_readerOnlyPageSlider.Value), 1, _comicPages.Count)} / {_comicPages.Count}";
    }
}
