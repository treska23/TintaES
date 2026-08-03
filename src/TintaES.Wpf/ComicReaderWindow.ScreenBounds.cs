using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene el lector dentro del área útil del monitor actual. Las medidas nativas llegan en
/// píxeles físicos y se convierten a unidades WPF con el DPI real de la ventana, de modo que
/// la barra superior y el pie no desaparecen con escalas de Windows del 125 %, 150 % o 200 %.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private static readonly bool ReaderScreenBoundsRegistered = RegisterReaderScreenBounds();

    private bool _readerScreenBoundsInstalled;
    private bool _applyingReaderScreenBounds;

    private static bool RegisterReaderScreenBounds()
    {
        EventManager.RegisterClassHandler(
            typeof(ComicReaderWindow),
            LoadedEvent,
            new RoutedEventHandler(ComicReaderWindow_ScreenBoundsLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void ComicReaderWindow_ScreenBoundsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComicReaderWindow reader)
        {
            reader.InstallReaderScreenBounds();
        }
    }

    private void InstallReaderScreenBounds()
    {
        if (!_readerScreenBoundsInstalled)
        {
            _readerScreenBoundsInstalled = true;
            LocationChanged += (_, _) => QueueReaderScreenBounds();
            StateChanged += (_, _) => QueueReaderScreenBounds();
        }

        ApplyReaderScreenBounds();
        Dispatcher.BeginInvoke(
            () =>
            {
                _scrollViewer.UpdateLayout();
                if (_pageImage.Source is not null)
                {
                    FitToViewport(_fitMode == ReaderFitMode.None
                        ? ReaderFitMode.Page
                        : _fitMode);
                }
            },
            DispatcherPriority.ContextIdle);
    }

    private void QueueReaderScreenBounds()
    {
        if (_applyingReaderScreenBounds || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(ApplyReaderScreenBounds, DispatcherPriority.Background);
    }

    private void ApplyReaderScreenBounds()
    {
        if (_applyingReaderScreenBounds || WindowState != WindowState.Normal)
        {
            return;
        }

        _applyingReaderScreenBounds = true;
        try
        {
            Rect workArea = GetCurrentMonitorWorkAreaInDips();
            const double outerMargin = 12;
            double maximumWidth = Math.Max(MinWidth, workArea.Width - outerMargin * 2);
            double maximumHeight = Math.Max(MinHeight, workArea.Height - outerMargin * 2);

            MaxWidth = maximumWidth;
            MaxHeight = maximumHeight;
            Width = Math.Min(Math.Max(MinWidth, Width), maximumWidth);
            Height = Math.Min(Math.Max(MinHeight, Height), maximumHeight);

            double minimumLeft = workArea.Left + outerMargin;
            double minimumTop = workArea.Top + outerMargin;
            double maximumLeft = workArea.Right - outerMargin - Width;
            double maximumTop = workArea.Bottom - outerMargin - Height;

            if (!double.IsFinite(Left) || Left < minimumLeft || Left > maximumLeft)
            {
                Left = Math.Clamp(
                    workArea.Left + (workArea.Width - Width) / 2,
                    minimumLeft,
                    Math.Max(minimumLeft, maximumLeft));
            }
            if (!double.IsFinite(Top) || Top < minimumTop || Top > maximumTop)
            {
                Top = Math.Clamp(
                    workArea.Top + (workArea.Height - Height) / 2,
                    minimumTop,
                    Math.Max(minimumTop, maximumTop));
            }
        }
        finally
        {
            _applyingReaderScreenBounds = false;
        }
    }

    private Rect GetCurrentMonitorWorkAreaInDips()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = Math.Max(0.01, dpi.DpiScaleX);
        double scaleY = Math.Max(0.01, dpi.DpiScaleY);
        return new Rect(
            info.Work.Left / scaleX,
            info.Work.Top / scaleY,
            (info.Work.Right - info.Work.Left) / scaleX,
            (info.Work.Bottom - info.Work.Top) / scaleY);
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
