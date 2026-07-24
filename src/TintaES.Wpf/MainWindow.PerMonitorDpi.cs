using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene la ventana nítida y utilizable al moverla entre monitores con escalas distintas.
/// El manifiesto activa Per-Monitor V2; esta capa reajusta los límites y el layout después de
/// cada cambio de DPI o monitor sin aplicar un zoom fijo a la interfaz.
/// </summary>
public partial class MainWindow
{
    private const int WmDpiChanged = 0x02E0;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const double PreferredMinimumWidth = 840;
    private const double PreferredMinimumHeight = 600;

    private static readonly bool PerMonitorDpiRegistered = RegisterPerMonitorDpiSupport();

    private bool _perMonitorDpiInstalled;
    private bool _perMonitorLayoutPending;
    private HwndSource? _perMonitorHwndSource;

    private static bool RegisterPerMonitorDpiSupport()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_PerMonitorDpiLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_PerMonitorDpiLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.InstallPerMonitorDpiSupport();
        }
    }

    private void InstallPerMonitorDpiSupport()
    {
        if (_perMonitorDpiInstalled)
        {
            return;
        }

        _perMonitorDpiInstalled = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        DpiChanged += MainWindow_PerMonitorDpiChanged;
        LocationChanged += MainWindow_PerMonitorLocationChanged;
        SizeChanged += MainWindow_PerMonitorSizeChanged;
        Closed += MainWindow_PerMonitorClosed;

        _perMonitorHwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _perMonitorHwndSource?.AddHook(PerMonitorDpiWindowProc);

        QueuePerMonitorLayoutRefresh(DispatcherPriority.Loaded);
    }

    private void MainWindow_PerMonitorDpiChanged(object sender, DpiChangedEventArgs e) =>
        QueuePerMonitorLayoutRefresh(DispatcherPriority.Render);

    private void MainWindow_PerMonitorLocationChanged(object? sender, EventArgs e) =>
        QueuePerMonitorLayoutRefresh(DispatcherPriority.Background);

    private void MainWindow_PerMonitorSizeChanged(object sender, SizeChangedEventArgs e) =>
        QueuePerMonitorLayoutRefresh(DispatcherPriority.Background);

    private IntPtr PerMonitorDpiWindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmDpiChanged)
        {
            // WPF aplica el rectángulo sugerido por Windows. Después forzamos nuestro layout
            // responsive, pero no marcamos el mensaje como tratado para no anular a WPF.
            QueuePerMonitorLayoutRefresh(DispatcherPriority.Render);
        }

        return IntPtr.Zero;
    }

    private void QueuePerMonitorLayoutRefresh(DispatcherPriority priority)
    {
        if (_perMonitorLayoutPending || !IsLoaded)
        {
            return;
        }

        _perMonitorLayoutPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _perMonitorLayoutPending = false;
                ApplyPerMonitorLayout();
            },
            priority);
    }

    private void ApplyPerMonitorLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;

        IntPtr windowHandle = new WindowInteropHelper(this).Handle;
        IntPtr monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>()
            };

            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                double workWidth = Math.Max(1, monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left) / scaleX;
                double workHeight = Math.Max(1, monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top) / scaleY;

                MinWidth = Math.Min(PreferredMinimumWidth, workWidth);
                MinHeight = Math.Min(PreferredMinimumHeight, workHeight);

                if (WindowState == WindowState.Normal)
                {
                    if (Width > workWidth)
                    {
                        Width = workWidth;
                    }
                    if (Height > workHeight)
                    {
                        Height = workHeight;
                    }
                }
            }
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
        UpdateLayout();
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

    private void MainWindow_PerMonitorClosed(object? sender, EventArgs e)
    {
        if (_perMonitorHwndSource is not null)
        {
            _perMonitorHwndSource.RemoveHook(PerMonitorDpiWindowProc);
            _perMonitorHwndSource = null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }
}