using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Corrige la posición física de la ventana después de que WPF aplique CenterScreen y el escalado
/// por monitor. Reducir únicamente Height no basta: si el centrado inicial produjo un Top negativo,
/// la barra de título y el botón de cerrar permanecen fuera de pantalla.
/// </summary>
public partial class MainWindow
{
    private const uint WindowGuardMonitorDefaultToNearest = 2;
    private const uint WindowGuardNoZOrder = 0x0004;
    private const uint WindowGuardNoActivate = 0x0010;

    private static readonly bool WindowVisibilityGuardRegistered = RegisterWindowVisibilityGuard();
    private bool _windowVisibilityGuardInstalled;
    private bool _windowVisibilityGuardPending;

    private static bool RegisterWindowVisibilityGuard()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_WindowVisibilityGuardLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_WindowVisibilityGuardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.InstallWindowVisibilityGuard();
        }
    }

    private void InstallWindowVisibilityGuard()
    {
        if (_windowVisibilityGuardInstalled)
        {
            QueueWindowVisibilityGuard(DispatcherPriority.Loaded);
            return;
        }

        _windowVisibilityGuardInstalled = true;
        ContentRendered += (_, _) => QueueWindowVisibilityGuard(DispatcherPriority.Render);
        DpiChanged += (_, _) => QueueWindowVisibilityGuard(DispatcherPriority.Render);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                QueueWindowVisibilityGuard(DispatcherPriority.Background);
            }
        };
        QueueWindowVisibilityGuard(DispatcherPriority.Loaded);
    }

    private void QueueWindowVisibilityGuard(DispatcherPriority priority)
    {
        if (_windowVisibilityGuardPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _windowVisibilityGuardPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _windowVisibilityGuardPending = false;
                KeepWindowInsideWorkingArea();
            },
            priority);
    }

    private void KeepWindowInsideWorkingArea()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !WindowGuardGetWindowRect(handle, out WindowGuardRect window))
        {
            return;
        }

        nint monitor = WindowGuardMonitorFromWindow(handle, WindowGuardMonitorDefaultToNearest);
        if (monitor == 0)
        {
            return;
        }

        var monitorInfo = new WindowGuardMonitorInfo
        {
            Size = Marshal.SizeOf<WindowGuardMonitorInfo>()
        };
        if (!WindowGuardGetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        int workWidth = Math.Max(1, monitorInfo.Work.Right - monitorInfo.Work.Left);
        int workHeight = Math.Max(1, monitorInfo.Work.Bottom - monitorInfo.Work.Top);
        int width = Math.Min(Math.Max(1, window.Right - window.Left), workWidth);
        int height = Math.Min(Math.Max(1, window.Bottom - window.Top), workHeight);
        int maximumLeft = monitorInfo.Work.Right - width;
        int maximumTop = monitorInfo.Work.Bottom - height;
        int left = Math.Clamp(window.Left, monitorInfo.Work.Left, maximumLeft);
        int top = Math.Clamp(window.Top, monitorInfo.Work.Top, maximumTop);

        if (left == window.Left
            && top == window.Top
            && width == window.Right - window.Left
            && height == window.Bottom - window.Top)
        {
            return;
        }

        WindowGuardSetWindowPos(
            handle,
            0,
            left,
            top,
            width,
            height,
            WindowGuardNoZOrder | WindowGuardNoActivate);
    }

    // Los métodos llevan prefijo para no colisionar con los P/Invoke de otras partes de
    // MainWindow. EntryPoint debe apuntar expresamente al nombre exportado por user32.dll;
    // de lo contrario .NET intenta resolver literalmente "WindowGuardGetWindowRect", etc.
    [DllImport(
        "user32.dll",
        EntryPoint = "MonitorFromWindow",
        ExactSpelling = true)]
    private static extern nint WindowGuardMonitorFromWindow(nint hwnd, uint flags);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetMonitorInfoW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowGuardGetMonitorInfo(
        nint monitor,
        ref WindowGuardMonitorInfo monitorInfo);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowRect",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowGuardGetWindowRect(nint hwnd, out WindowGuardRect rectangle);

    [DllImport(
        "user32.dll",
        EntryPoint = "SetWindowPos",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowGuardSetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowGuardMonitorInfo
    {
        public int Size;
        public WindowGuardRect Monitor;
        public WindowGuardRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowGuardRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
