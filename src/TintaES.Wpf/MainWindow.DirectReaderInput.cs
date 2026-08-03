using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private bool _directReaderInputInstalled;

    private void InstallDirectReaderInput()
    {
        if (_directReaderInputInstalled)
        {
            return;
        }

        _directReaderInputInstalled = true;
        ImageScrollViewer.PanningMode = PanningMode.None;
        ImageScrollViewer.Cursor = Cursors.Hand;
        ImageStage.IsManipulationEnabled = true;

        // Ratón: la traducción aparece por hover. El botón izquierdo queda reservado para
        // arrastrar la página, incluso cuando el puntero está sobre una zona traducida.
        ImageStage.MouseMove += MainImage_MouseMoveForTranslation;
        ImageStage.MouseLeave += MainImage_MouseLeaveForTranslation;

        // Táctil: no existe hover, así que la traducción permanece visible mientras el dedo
        // está apoyado sobre el texto.
        ImageStage.PreviewTouchDown += MainImage_PreviewTouchDown;
        ImageStage.PreviewTouchUp += MainImage_PreviewTouchUp;
        ImageStage.LostTouchCapture += (_, _) => HideMainTranslation();

        ImageScrollViewer.LostMouseCapture += (_, _) =>
        {
            EndSpacePan();
            HideMainTranslation();
        };
        ImageStage.ManipulationStarting += MainImage_ManipulationStarting;
        ImageStage.ManipulationDelta += MainImage_ManipulationDelta;
        ImageStage.ManipulationInertiaStarting += MainImage_ManipulationInertiaStarting;
    }

    private void MainImage_ManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = ImageScrollViewer;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        e.Handled = true;
    }

    private void MainImage_ManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        ManipulationDelta delta = e.DeltaManipulation;
        double scaleDelta = (delta.Scale.X + delta.Scale.Y) / 2d;
        if (double.IsFinite(scaleDelta) && Math.Abs(scaleDelta - 1) > 0.002)
        {
            ZoomMainReaderAroundPoint(ZoomSlider.Value * scaleDelta, e.ManipulationOrigin);
        }

        ImageScrollViewer.ScrollToHorizontalOffset(
            ImageScrollViewer.HorizontalOffset - delta.Translation.X);
        ImageScrollViewer.ScrollToVerticalOffset(
            ImageScrollViewer.VerticalOffset - delta.Translation.Y);
        e.Handled = true;
    }

    private static void MainImage_ManipulationInertiaStarting(
        object? sender,
        ManipulationInertiaStartingEventArgs e)
    {
        e.TranslationBehavior.DesiredDeceleration = 0.0022;
        e.ExpansionBehavior.DesiredDeceleration = 0.003;
        e.Handled = true;
    }

    private void ZoomMainReaderAroundPoint(double percent, Point viewportPoint)
    {
        double oldExtentWidth = Math.Max(1, ImageScrollViewer.ExtentWidth);
        double oldExtentHeight = Math.Max(1, ImageScrollViewer.ExtentHeight);
        double horizontalRatio = (ImageScrollViewer.HorizontalOffset + viewportPoint.X) / oldExtentWidth;
        double verticalRatio = (ImageScrollViewer.VerticalOffset + viewportPoint.Y) / oldExtentHeight;

        ZoomSlider.Value = Math.Clamp(percent, ZoomSlider.Minimum, ZoomSlider.Maximum);
        ImageScrollViewer.UpdateLayout();
        ImageScrollViewer.ScrollToHorizontalOffset(
            horizontalRatio * ImageScrollViewer.ExtentWidth - viewportPoint.X);
        ImageScrollViewer.ScrollToVerticalOffset(
            verticalRatio * ImageScrollViewer.ExtentHeight - viewportPoint.Y);
    }
}
