using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private readonly ConditionalWeakTable<Grid, object> _preparedOverlayLayers = new();
    private bool _presentationHooksAttached;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
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

        HwndSource? source = HwndSource.FromHwnd(handle);
        Matrix fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        Point topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        Point bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
        double workWidth = Math.Max(640, bottomRight.X - topLeft.X);
        double workHeight = Math.Max(460, bottomRight.Y - topLeft.Y);

        MinWidth = Math.Min(MinWidth, workWidth);
        MinHeight = Math.Min(MinHeight, workHeight);
        MaxWidth = workWidth;
        MaxHeight = workHeight;
        Width = Math.Min(Width, workWidth);
        Height = Math.Min(Height, workHeight);

        double maxLeft = topLeft.X + Math.Max(0, workWidth - Width);
        double maxTop = topLeft.Y + Math.Max(0, workHeight - Height);
        Left = Math.Clamp(Left, topLeft.X, maxLeft);
        Top = Math.Clamp(Top, topLeft.Y, maxTop);
    }

    private void OverlayCanvas_PresentationLayoutUpdated(object? sender, EventArgs e)
    {
        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (_preparedOverlayLayers.TryGetValue(layer, out _))
            {
                continue;
            }

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

                // El handler original modificaba RenderBox y SafePolygon en cada píxel del
                // arrastre. El nuevo mueve el contenedor completo según la posición absoluta
                // del puntero, manteniendo fuente, geometría e hit-area sincronizados.
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

            if (border is not null)
            {
                border.Visibility = Visibility.Collapsed;
            }
            foreach (Thumb thumb in thumbs.Skip(1))
            {
                thumb.Visibility = Visibility.Collapsed;
                thumb.Opacity = 0;
            }

            _preparedOverlayLayers.Add(layer, new object());
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

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
