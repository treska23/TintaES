using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TintaES.Core;

namespace TintaES.Wpf;

public sealed partial class ComicReaderWindow
{
    private ReaderComicDocument? _readerDocument;
    private Border? _translationCard;
    private TextBlock? _translationText;
    private bool _translationMouseHeld;
    private DockPanel? _readerToolbar;
    private TextBlock? _readerStatus;
    private Button? _fullscreenButton;
    private Button? _edgePreviousButton;
    private Button? _edgeNextButton;
    private Grid? _readerRoot;
    private bool _isFullscreen;
    private WindowStyle _windowStyleBeforeFullscreen;
    private WindowState _windowStateBeforeFullscreen;
    private ResizeMode _resizeModeBeforeFullscreen;
    private DateTime _ignoreSyntheticMouseUntilUtc;
    private Vector _touchGestureTranslation;
    private DateTime _touchGestureStartedUtc;
    private bool _touchGestureScaled;
    private TimeSpan _touchGestureElapsedAtRelease;
    private bool _leftEdgeNavigationAvailable;
    private bool _rightEdgeNavigationAvailable;

    private async Task OpenDocumentAsync()
    {
        if (_readerDocument is null || _readerDocument.Pages.Count == 0)
        {
            ShowLoading("El proyecto no contiene páginas para leer.");
            return;
        }

        ShowLoading("Preparando el lector…");
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        _archive?.Dispose();
        _archive = null;
        _archiveStream?.Dispose();
        _archiveStream = null;
        _archivePath = null;
        _pages.Clear();
        _pageCache.Clear();
        _cacheOrder.Clear();
        _pageIndex = -1;
        _fitMode = ReaderFitMode.Page;

        Title = $"{_readerDocument.Title} · Lector traducido · Tinta ES · " +
                MainWindow.CurrentUiBuildStamp;
        PopulatePageSelector();
        await ShowPageAsync(_readerDocument.InitialPageIndex);
    }

    private void InstallTranslationReaderExperience(Grid root, DockPanel toolbar, TextBlock statusText)
    {
        _readerToolbar = toolbar;
        _readerStatus = statusText;
        _readerRoot = root;
        _scrollViewer.PanningMode = PanningMode.None;
        _pageStage.IsManipulationEnabled = true;
        _pageStage.ManipulationStarting += PageStage_ManipulationStarting;
        _pageStage.ManipulationDelta += PageStage_ManipulationDelta;
        _pageStage.ManipulationInertiaStarting += PageStage_ManipulationInertiaStarting;
        _pageStage.ManipulationCompleted += PageStage_ManipulationCompleted;

        if (toolbar.Children.OfType<StackPanel>().FirstOrDefault() is { } toolbarItems)
        {
            _fullscreenButton = CreateToolbarButton("Pantalla completa", 118);
            _fullscreenButton.Margin = new Thickness(12, 0, 0, 0);
            _fullscreenButton.ToolTip = "Entrar o salir de pantalla completa (F11)";
            _fullscreenButton.Click += (_, _) => ToggleFullscreen();
            toolbarItems.Children.Add(_fullscreenButton);
        }

        _translationText = new TextBlock
        {
            Foreground = Brushes.Black,
            FontSize = Math.Max(18d, SystemFonts.MessageFontSize * 1.45d),
            FontWeight = SystemFonts.MessageFontWeight,
            FontFamily = SystemFonts.MessageFontFamily,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = Math.Max(24d, SystemFonts.MessageFontSize * 1.9d),
            MaxWidth = 720
        };
        _translationCard = new Border
        {
            Child = _translationText,
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(20, 13, 20, 14),
            Margin = new Thickness(40),
            MaxWidth = 780,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_translationCard, 2000);
        _viewerHost.Children.Add(_translationCard);

        AddPageTurnButtons();
        _viewerHost.PreviewMouseLeftButtonDown += ViewerHost_PreviewMouseLeftButtonDown;
        _viewerHost.PreviewTouchDown += ViewerHost_PreviewTouchDown;
        _viewerHost.PreviewTouchUp += ViewerHost_PreviewTouchUp;
        _viewerHost.LostTouchCapture += (_, _) => HideTranslationCard();
        _scrollViewer.LostMouseCapture += (_, _) => EndMouseTranslationHold();
        _viewerHost.MouseMove += ViewerHost_MouseMove;
        _viewerHost.MouseLeave += (_, _) => HideEdgeNavigationButtons();
    }

    private void AddPageTurnButtons()
    {
        _edgePreviousButton = CreateEdgeNavigationButton("‹", HorizontalAlignment.Left);
        _edgePreviousButton.Click += (_, _) => NavigateByArrow(rightArrow: false);
        _viewerHost.Children.Add(_edgePreviousButton);

        _edgeNextButton = CreateEdgeNavigationButton("›", HorizontalAlignment.Right);
        _edgeNextButton.Click += (_, _) => NavigateByArrow(rightArrow: true);
        _viewerHost.Children.Add(_edgeNextButton);
        UpdateReaderEdgeButtons(_pageIndex);
    }

    private void UpdateReaderEdgeButtons(int index)
    {
        bool canGoBack = index > 0;
        bool canGoForward = index >= 0 && index < PageCount - 1;
        _leftEdgeNavigationAvailable = _rightToLeft ? canGoForward : canGoBack;
        _rightEdgeNavigationAvailable = _rightToLeft ? canGoBack : canGoForward;
        if (_edgePreviousButton is not null)
        {
            _edgePreviousButton.Visibility = _leftEdgeNavigationAvailable
                ? Visibility.Visible
                : Visibility.Collapsed;
            _edgePreviousButton.Opacity = 0;
            _edgePreviousButton.IsHitTestVisible = false;
        }
        if (_edgeNextButton is not null)
        {
            _edgeNextButton.Visibility = _rightEdgeNavigationAvailable
                ? Visibility.Visible
                : Visibility.Collapsed;
            _edgeNextButton.Opacity = 0;
            _edgeNextButton.IsHitTestVisible = false;
        }
    }

    private void ViewerHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (DateTime.UtcNow < _ignoreSyntheticMouseUntilUtc || _dragging)
        {
            HideEdgeNavigationButtons();
            return;
        }

        Point point = e.GetPosition(_viewerHost);
        const double revealDistance = 82;
        SetEdgeButtonReveal(
            _edgePreviousButton,
            _leftEdgeNavigationAvailable && point.X <= revealDistance);
        SetEdgeButtonReveal(
            _edgeNextButton,
            _rightEdgeNavigationAvailable && point.X >= _viewerHost.ActualWidth - revealDistance);
    }

    private static void SetEdgeButtonReveal(Button? button, bool reveal)
    {
        if (button is null || button.Visibility != Visibility.Visible)
        {
            return;
        }
        button.Opacity = reveal ? 0.72 : 0;
        button.IsHitTestVisible = reveal;
    }

    private void HideEdgeNavigationButtons()
    {
        SetEdgeButtonReveal(_edgePreviousButton, false);
        SetEdgeButtonReveal(_edgeNextButton, false);
    }

    private static Button CreateEdgeNavigationButton(string text, HorizontalAlignment alignment)
    {
        var button = new Button
        {
            Content = text,
            Width = 54,
            Height = 92,
            Margin = new Thickness(14),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 42,
            FontWeight = FontWeights.Light,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(145, 12, 14, 16)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Opacity = 0.72,
            Cursor = Cursors.Hand
        };
        Panel.SetZIndex(button, 1200);
        return button;
    }

    private void RebuildTranslationHitAreas(int pageIndex, int pageWidth, int pageHeight)
    {
        _translationHitCanvas.Children.Clear();
    }

    internal static NormalizedRect ResolveReaderHitBox(ComicRegion region) =>
        ComicRegionHitResolver.ResolveHitBox(region);

    private ComicRegion? ResolveReaderRegionAt(Point pagePoint)
    {
        if (_readerDocument is null
            || _pageIndex < 0
            || _pageIndex >= _readerDocument.Pages.Count
            || _pageStage.ActualWidth <= 1
            || _pageStage.ActualHeight <= 1
            || pagePoint.X < 0
            || pagePoint.Y < 0
            || pagePoint.X > _pageStage.ActualWidth
            || pagePoint.Y > _pageStage.ActualHeight)
        {
            return null;
        }

        double x = pagePoint.X / _pageStage.ActualWidth * 1000d;
        double y = pagePoint.Y / _pageStage.ActualHeight * 1000d;
        return ComicRegionHitResolver.Resolve(_readerDocument.Pages[_pageIndex].Regions, x, y);
    }

    private void ViewerHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DateTime.UtcNow < _ignoreSyntheticMouseUntilUtc)
        {
            e.Handled = true;
            return;
        }

        if (ResolveReaderRegionAt(e.GetPosition(_pageStage)) is { } region)
        {
            BeginMouseTranslationHold(region);
            e.Handled = true;
            return;
        }

        HideTranslationCard();
    }

    private void ZoomReaderAroundPoint(double percent, Point viewportPoint)
    {
        double oldExtentWidth = Math.Max(1, _scrollViewer.ExtentWidth);
        double oldExtentHeight = Math.Max(1, _scrollViewer.ExtentHeight);
        double horizontalRatio = (_scrollViewer.HorizontalOffset + viewportPoint.X) / oldExtentWidth;
        double verticalRatio = (_scrollViewer.VerticalOffset + viewportPoint.Y) / oldExtentHeight;

        SetZoom(percent, ReaderFitMode.None);
        _scrollViewer.UpdateLayout();
        _scrollViewer.ScrollToHorizontalOffset(
            horizontalRatio * _scrollViewer.ExtentWidth - viewportPoint.X);
        _scrollViewer.ScrollToVerticalOffset(
            verticalRatio * _scrollViewer.ExtentHeight - viewportPoint.Y);
    }

    private void PageStage_ManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        _touchGestureTranslation = default;
        _touchGestureStartedUtc = DateTime.UtcNow;
        _touchGestureScaled = false;
        _touchGestureElapsedAtRelease = TimeSpan.Zero;
        e.ManipulationContainer = _viewerHost;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        e.Handled = true;
    }

    private void PageStage_ManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        ManipulationDelta delta = e.DeltaManipulation;
        double scaleDelta = (delta.Scale.X + delta.Scale.Y) / 2d;
        if (double.IsFinite(scaleDelta) && Math.Abs(scaleDelta - 1) > 0.002)
        {
            _touchGestureScaled = true;
            ZoomReaderAroundPoint(_zoomSlider.Value * scaleDelta, e.ManipulationOrigin);
        }

        if (Math.Abs(delta.Translation.X) > 0.01 || Math.Abs(delta.Translation.Y) > 0.01)
        {
            _touchGestureTranslation += delta.Translation;
            _scrollViewer.ScrollToHorizontalOffset(
                _scrollViewer.HorizontalOffset - delta.Translation.X);
            _scrollViewer.ScrollToVerticalOffset(
                _scrollViewer.VerticalOffset - delta.Translation.Y);
        }

        e.Handled = true;
    }

    private void PageStage_ManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
    {
        TryNavigateFromSwipe(
            _touchGestureTranslation,
            _touchGestureElapsedAtRelease > TimeSpan.Zero
                ? _touchGestureElapsedAtRelease
                : DateTime.UtcNow - _touchGestureStartedUtc,
            _touchGestureScaled);
        e.Handled = true;
    }

    private bool TryNavigateFromSwipe(Vector gesture, TimeSpan elapsed, bool scaledGesture)
    {
        bool towardRight = gesture.X > 0;
        bool atHorizontalEdge = _scrollViewer.ScrollableWidth <= 2
            || (towardRight
                ? _scrollViewer.HorizontalOffset <= 2
                : _scrollViewer.HorizontalOffset >= _scrollViewer.ScrollableWidth - 2);
        int pageDelta = ResolveSwipePageDelta(
            gesture,
            elapsed,
            scaledGesture,
            _rightToLeft,
            _pageIndex,
            PageCount,
            atHorizontalEdge,
            _scrollViewer.ViewportWidth);
        if (pageDelta == 0)
        {
            return false;
        }

        _ = ShowPageAsync(_pageIndex + pageDelta);
        return true;
    }

    internal static int ResolveSwipePageDelta(
        Vector gesture,
        TimeSpan elapsed,
        bool scaledGesture,
        bool rightToLeft,
        int pageIndex,
        int pageCount,
        bool atHorizontalEdge,
        double viewportWidth)
    {
        if (scaledGesture
            || !atHorizontalEdge
            || pageCount < 2
            || elapsed.TotalMilliseconds > 1_800)
        {
            return 0;
        }

        double horizontal = Math.Abs(gesture.X);
        double requiredDistance = Math.Max(150, viewportWidth * 0.18);
        if (horizontal < requiredDistance || Math.Abs(gesture.Y) > horizontal * 0.58)
        {
            return 0;
        }

        bool towardRight = gesture.X > 0;
        bool advance = rightToLeft ? towardRight : !towardRight;
        int delta = advance ? 1 : -1;
        int target = pageIndex + delta;
        return target >= 0 && target < pageCount ? delta : 0;
    }

    private void PageStage_ManipulationInertiaStarting(
        object? sender,
        ManipulationInertiaStartingEventArgs e)
    {
        _touchGestureElapsedAtRelease = DateTime.UtcNow - _touchGestureStartedUtc;
        e.TranslationBehavior.DesiredDeceleration = 0.0022;
        e.ExpansionBehavior.DesiredDeceleration = 0.003;
        e.Handled = true;
    }

    private void ViewerHost_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        _ignoreSyntheticMouseUntilUtc = DateTime.UtcNow.AddMilliseconds(650);
        if (ResolveReaderRegionAt(e.GetTouchPoint(_pageStage).Position) is { } region)
        {
            ShowTranslationCard(region);
            e.TouchDevice.Capture(_viewerHost);
            e.Handled = true;
            return;
        }

        HideTranslationCard();
    }

    private void ViewerHost_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (!_viewerHost.AreAnyTouchesCapturedWithin)
        {
            return;
        }

        if (_viewerHost.AreAnyTouchesCapturedWithin)
        {
            e.TouchDevice.Capture(null);
        }
        HideTranslationCard();
        e.Handled = true;
    }

    private void ShowTranslationCard(ComicRegion region)
    {
        if (_translationCard is null || _translationText is null)
        {
            return;
        }

        _translationText.Text = region.HasRenderableTranslation
            ? region.Translation.Trim()
            : "Traducción pendiente";
        _translationText.Foreground = region.HasRenderableTranslation
            ? Brushes.Black
            : new SolidColorBrush(Color.FromRgb(120, 80, 20));
        _translationCard.Visibility = Visibility.Visible;
        Panel.SetZIndex(_translationCard, 2000);
    }

    private void HideTranslationCard()
    {
        if (_translationCard is not null)
        {
            _translationCard.Visibility = Visibility.Collapsed;
        }
    }

    private void BeginMouseTranslationHold(ComicRegion region)
    {
        _translationMouseHeld = true;
        ShowTranslationCard(region);
        Mouse.Capture(_scrollViewer, CaptureMode.Element);
    }

    private void EndMouseTranslationHold()
    {
        if (!_translationMouseHeld)
        {
            return;
        }

        _translationMouseHeld = false;
        HideTranslationCard();
        if (Mouse.Captured == _scrollViewer)
        {
            Mouse.Capture(null);
        }
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _windowStyleBeforeFullscreen = WindowStyle;
            _windowStateBeforeFullscreen = WindowState;
            _resizeModeBeforeFullscreen = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            if (_readerToolbar is not null) _readerToolbar.Visibility = Visibility.Collapsed;
            if (_readerStatus is not null) _readerStatus.Visibility = Visibility.Collapsed;
            if (_readerRoot?.RowDefinitions.Count >= 3)
            {
                _readerRoot.RowDefinitions[0].Height = new GridLength(0);
                _readerRoot.RowDefinitions[2].Height = new GridLength(0);
            }
            if (_fullscreenButton is not null) _fullscreenButton.Content = "Salir de pantalla completa";
            _isFullscreen = true;
        }
        else
        {
            if (_readerToolbar is not null) _readerToolbar.Visibility = Visibility.Visible;
            if (_readerStatus is not null) _readerStatus.Visibility = Visibility.Visible;
            if (_readerRoot?.RowDefinitions.Count >= 3)
            {
                _readerRoot.RowDefinitions[0].Height = GridLength.Auto;
                _readerRoot.RowDefinitions[2].Height = GridLength.Auto;
            }
            WindowStyle = _windowStyleBeforeFullscreen;
            ResizeMode = _resizeModeBeforeFullscreen;
            WindowState = _windowStateBeforeFullscreen;
            if (_fullscreenButton is not null) _fullscreenButton.Content = "Pantalla completa";
            _isFullscreen = false;
        }

        Dispatcher.BeginInvoke(
            () => FitToViewport(_fitMode == ReaderFitMode.None ? ReaderFitMode.Page : _fitMode),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private string GetReaderTitle() =>
        _readerDocument?.Title
        ?? Path.GetFileNameWithoutExtension(_archivePath)
        ?? "Cómic";

    private string GetPageDisplayName(int index)
    {
        if (_readerDocument is not null)
        {
            return _readerDocument.Pages[index].DisplayName;
        }
        return _pages[index].Name;
    }

    private string GetPageRegionSummary(int index)
    {
        if (_readerDocument is null)
        {
            return "sin traducciones asociadas";
        }

        int available = _readerDocument.Pages[index].Regions.Count(region =>
            region.IsEnabled && region.HasRenderableTranslation);
        return available == 1 ? "1 bocadillo traducido" : $"{available} bocadillos traducidos";
    }
}

internal sealed class ReaderTranslationEditorWindow : Window
{
    private readonly TextBox _translationBox;

    public ReaderTranslationEditorWindow(string original, string translation)
    {
        Title = "Corregir traducción · Tinta ES";
        Width = 620;
        Height = 410;
        MinWidth = 460;
        MinHeight = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 27, 30));
        Foreground = Brushes.White;

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "TEXTO ORIGINAL",
            Foreground = new SolidColorBrush(Color.FromRgb(163, 171, 178)),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 7)
        });

        var originalBox = new TextBox
        {
            Text = original,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(31, 35, 39)),
            Foreground = new SolidColorBrush(Color.FromRgb(190, 197, 202)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 72, 78)),
            Padding = new Thickness(10)
        };
        Grid.SetRow(originalBox, 1);
        root.Children.Add(originalBox);

        var label = new TextBlock
        {
            Text = "TRADUCCIÓN AL ESPAÑOL",
            Foreground = new SolidColorBrush(Color.FromRgb(238, 89, 75)),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 18, 0, 7)
        };
        Grid.SetRow(label, 2);
        root.Children.Add(label);

        _translationBox = new TextBox
        {
            Text = string.Equals(translation?.Trim(), ComicRegion.PendingTranslationMarker, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : translation,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 18,
            Background = new SolidColorBrush(Color.FromRgb(31, 35, 39)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(84, 92, 99)),
            Padding = new Thickness(12)
        };
        Grid.SetRow(_translationBox, 3);
        root.Children.Add(_translationBox);

        var cancel = new Button
        {
            Content = "Cancelar",
            Width = 100,
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        var save = new Button
        {
            Content = "Guardar",
            Width = 110,
            Height = 34,
            IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(238, 89, 75)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_translationBox.Text))
            {
                MessageBox.Show(this, "Escribe una traducción antes de guardar.", "Tinta ES",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            _translationBox.Focus();
            _translationBox.SelectAll();
        };
    }

    public string Translation => _translationBox.Text.Trim();
}
