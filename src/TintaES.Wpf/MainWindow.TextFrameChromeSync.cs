using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Sincroniza el único marco visual de selección al cambiar entre Resultado, Máscara o dibujo.
/// Solo se ejecuta tras un clic de herramienta; no añade trabajo permanente al layout.
/// </summary>
public partial class MainWindow
{
    private static readonly bool TextFrameChromeSyncRegistered = RegisterTextFrameChromeSync();

    private static bool RegisterTextFrameChromeSync()
    {
        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(TextFrameChromeToolButtonClicked),
            handledEventsToo: true);
        return true;
    }

    private static void TextFrameChromeToolButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || Window.GetWindow(button) is not MainWindow window)
        {
            return;
        }

        bool affectsFrame = ReferenceEquals(button, window._maskPaintButton)
            || ReferenceEquals(button, window._maskEraseButton)
            || ReferenceEquals(button, window.AddRegionButton)
            || ReferenceEquals(button, window.OriginalPreviewButton)
            || ReferenceEquals(button, window.MaskPreviewButton)
            || ReferenceEquals(button, window.CleanPreviewButton)
            || ReferenceEquals(button, window.ResultPreviewButton);
        if (!affectsFrame)
        {
            return;
        }

        window.Dispatcher.BeginInvoke(
            window.RefreshSelectedTextFrame,
            DispatcherPriority.Render);
    }
}