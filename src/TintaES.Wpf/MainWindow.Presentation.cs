using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private const int WmDpiChanged = 0x02E0;
    private const uint MonitorDefaultToNearest = 2;
    private const double PreferredMinimumWidth = 840;
    private const double PreferredMinimumHeight = 600;

    private readonly ConditionalWeakTable<Grid, object> _preparedOverlayLayers = new();
    private bool _presentationHooksAttached;
    private bool _monitorLayoutRefreshPending;
    private HwndSource? _presentationHwndSource;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint handle = new WindowInteropHelper(this).Handle;
        _presentationHwndSource = HwndSource.FromHwnd(handle);
        _presentationHwndSource?.AddHook(PresentationWindowProc);

        DpiChanged += MainWindow_DpiChanged;
        LocationChanged += MainWindow_MonitorLocationChanged;
        SizeChanged += MainWindow_MonitorSizeChanged;
        Closed += MainWindow_PresentationClosed;

        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        FitWindowToCurrentMonitor();
        AttachPresentationHooks();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        FitWindowToCurrentMonitor();
    }

    private void AttachPresentationHooks()
    {
        if (_presentationHooksAttached)
        {
            return;
        }

        _presentationHooksAttached = true;

        if (FindResource("InkBrush") is Brush inkBrush)
        {
            RegionListBox.Foreground = inkBrush;
        }

        OverlayCanvas.LayoutUpdated += OverlayCanvas_PresentationLayoutUpdated;
    }

    private IntPtr PresentationWindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmDpiChanged)
        {
            // Dejamos que WPF procese el rectángulo sugerido por Windows y recalculamos el
            // layout después. Marcarlo como tratado impediría el comportamiento Per-Monitor V2.
            QueueMonitorLayoutRefresh(DispatcherPriority.Render);
        }

        return IntPtr.Zero;
    }

    private void MainWindow_DpiChanged(object sender, DpiChangedEventArgs e) =>
        QueueMonitorLayoutRefresh(DispatcherPriority.Render);

    private void MainWindow_MonitorLocationChanged(object? sender, EventArgs e) =>
        QueueMonitorLayoutRefresh(DispatcherPriority.Background);

    private void MainWindow_MonitorSizeChanged(object sender, SizeChangedEventArgs e) =>
        QueueMonitorLayoutRefresh(DispatcherPriority.Background);

    private void QueueMonitorLayoutRefresh(DispatcherPriority priority)
    {
        if (_monitorLayoutRefreshPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _monitorLayoutRefreshPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _monitorLayoutRefreshPending = false;
                FitWindowToCurrentMonitor();
            },
            priority);
    }

    private void FitWindowToCurrentMonitor()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        nint monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        uint windowDpi = GetDpiForWindow(handle);
        double dpiScale = windowDpi > 0 ? windowDpi / 96d : 1d;
        double workWidth = Math.Max(640, (info.Work.Right - info.Work.Left) / dpiScale);
        double workHeight = Math.Max(460, (info.Work.Bottom - info.Work.Top) / dpiScale);

        MinWidth = Math.Min(PreferredMinimumWidth, workWidth);
        MinHeight = Math.Min(PreferredMinimumHeight, workHeight);
        MaxWidth = workWidth;
        MaxHeight = workHeight;

        if (WindowState == WindowState.Normal)
        {
            Width = Math.Min(Width, workWidth);
            Height = Math.Min(Height, workHeight);
        }

        ApplyResponsiveWorkspaceColumns();

        if (Content is FrameworkElement root)
        {
            root.InvalidateMeasure();
            root.InvalidateArrange();
            root.InvalidateVisual();
        }

        ImageScrollViewer.InvalidateMeasure();
        ImageStage.InvalidateMeasure();
        OverlayCanvas.InvalidateMeasure();
        OverlayCanvas.InvalidateVisual();
    }

    private void ApplyResponsiveWorkspaceColumns()
    {
        double availableWidth = ActualWidth > 0 ? ActualWidth : Width;

        double selectorWidth = availableWidth switch
        {
            < 980 => 190,
            < 1180 => 215,
            < 1380 => 232,
            _ => 252
        };

        if (_pageSelectionColumn is not null)
        {
            bool selectorVisible = _pageSelectionPanel?.Visibility == Visibility.Visible;
            _pageSelectionColumn.Width = selectorVisible
                ? new GridLength(selectorWidth)
                : new GridLength(0);
        }

        if (ImageScrollViewer.Parent is Grid imageViewportGrid
            && imageViewportGrid.Parent is Grid pageAreaGrid
            && pageAreaGrid.Parent is Border pageBorder
            && pageBorder.Parent is Grid contentGrid
            && contentGrid.ColumnDefinitions.Count >= 2)
        {
            double editorWidth = availableWidth switch
            {
                < 980 => 285,
                < 1180 => 315,
                < 1380 => 350,
                _ => 390
            };

            contentGrid.ColumnDefinitions[^1].Width = new GridLength(editorWidth);
        }
    }

    private void MainWindow_PresentationClosed(object? sender, EventArgs e)
    {
        DpiChanged -= MainWindow_DpiChanged;
        LocationChanged -= MainWindow_MonitorLocationChanged;
        SizeChanged -= MainWindow_MonitorSizeChanged;

        if (_presentationHwndSource is not null)
        {
            _presentationHwndSource.RemoveHook(PresentationWindowProc);
            _presentationHwndSource = null;
        }
    }

    private void OverlayCanvas_PresentationLayoutUpdated(object? sender, EventArgs e)
    {
        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (_preparedOverlayLayers.TryGetValue(layer, out _))
            {
                continue;
            }

            // Marcamos la capa antes de añadir posibles hijos manuales para que un nuevo ciclo
            // de LayoutUpdated no vuelva a preparar la misma capa.
            _preparedOverlayLayers.Add(layer, new object());

            Border? border = layer.Children.OfType<Border>().FirstOrDefault();
            Thumb[] thumbs = layer.Children.OfType<Thumb>().ToArray();
            ComicTextElement? text = layer.Children.OfType<ComicTextElement>().FirstOrDefault();
            ComicRegion? region = layer.Tag as ComicRegion;

            if (thumbs.Length > 0)
            {
                Thumb moveThumb = thumbs[0];
                moveThumb.Background = Brushes.Transparent;
                moveThumb.BorderBrush = Brushes.Transparent;
                moveThumb.Opacity = 0;
                moveThumb.Focusable = false;
                Panel.SetZIndex(moveThumb, 20);

                moveThumb.DragStarted -= RegionMoveThumb_DragStarted;
                moveThumb.DragDelta -= RegionMoveThumb_DragDelta;
                moveThumb.DragCompleted -= RegionThumb_DragCompleted;
                moveThumb.DragStarted += RegionMoveThumb_DragStarted_Fast;
                moveThumb.DragDelta += RegionMoveThumb_DragDelta_Fast;
                moveThumb.DragCompleted += RegionMoveThumb_DragCompleted_Fast;
            }

            if (text is not null)
            {
                Panel.SetZIndex(text, 10);
                text.Visibility = Visibility.Visible;
                if (region is not null)
                {
                    ApplyRegionPlacement(layer, text, region);
                }
                text.InvalidateVisual();
            }

            if (region is not null)
            {
                EnsureManualLineVisual(layer, region, invalidate: false);
            }

            if (border is not null)
            {
                border.Visibility = Visibility.Collapsed;
            }
            foreach (Thumb thumb in thumbs.Skip(1))
            {
                thumb.Visibility = Visibility.Collapsed;
                thumb.Opacity = 0;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}