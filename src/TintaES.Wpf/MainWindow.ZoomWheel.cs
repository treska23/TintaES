using System.Windows;
using System.Windows.Input;

namespace TintaES.Wpf;

public partial class MainWindow
{
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);

        if (!IsActive
            || !ImageScrollViewer.IsMouseOver)
        {
            return;
        }

        Point pointer = e.GetPosition(ImageScrollViewer);
        double oldExtentWidth = Math.Max(1, ImageScrollViewer.ExtentWidth);
        double oldExtentHeight = Math.Max(1, ImageScrollViewer.ExtentHeight);
        double horizontalRatio = (ImageScrollViewer.HorizontalOffset + pointer.X) / oldExtentWidth;
        double verticalRatio = (ImageScrollViewer.VerticalOffset + pointer.Y) / oldExtentHeight;

        double direction = e.Delta > 0 ? 1 : -1;
        double step = 8;
        double target = Math.Clamp(
            ZoomSlider.Value + direction * step,
            ZoomSlider.Minimum,
            ZoomSlider.Maximum);

        if (Math.Abs(target - ZoomSlider.Value) > 0.001)
        {
            ZoomSlider.Value = target;
            ImageScrollViewer.UpdateLayout();
            ImageScrollViewer.ScrollToHorizontalOffset(
                horizontalRatio * ImageScrollViewer.ExtentWidth - pointer.X);
            ImageScrollViewer.ScrollToVerticalOffset(
                verticalRatio * ImageScrollViewer.ExtentHeight - pointer.Y);
        }

        e.Handled = true;
    }
}
