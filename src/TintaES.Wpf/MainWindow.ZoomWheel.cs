using System.Windows.Input;

namespace TintaES.Wpf;

public partial class MainWindow
{
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);

        if (!IsActive
            || !ImageScrollViewer.IsMouseOver
            || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        double direction = e.Delta > 0 ? 1 : -1;
        double step = 5;
        double target = Math.Clamp(
            ZoomSlider.Value + direction * step,
            ZoomSlider.Minimum,
            ZoomSlider.Maximum);

        if (Math.Abs(target - ZoomSlider.Value) > 0.001)
        {
            ZoomSlider.Value = target;
        }

        e.Handled = true;
    }
}
