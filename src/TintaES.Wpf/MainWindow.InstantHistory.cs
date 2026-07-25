using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Deshacer y rehacer son acciones de edición, no de guardado. Se mantienen completamente en memoria
/// y el usuario decide cuándo comprimir la página mediante Guardar, evitando tirones de CPU y disco.
/// </summary>
public partial class MainWindow
{
    private static readonly bool InstantHistoryRegistered = RegisterInstantHistory();

    private static bool RegisterInstantHistory()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_InstantHistoryLoaded),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(InstantHistoryButton_Click),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(MainWindow_InstantHistoryPreviewKeyDown),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_InstantHistoryLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            // Impide que FastUndo arranque la compresión PNG automática. La referencia pendiente se
            // descarta después de cada acción y Guardar página conserva el único punto de escritura.
            window._fastUndoSaveLoopRunning = true;
            window._pendingEditorUndoSave = null;
        }
    }

    private static void InstantHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Window.GetWindow(button) is not MainWindow window)
        {
            return;
        }

        if (ReferenceEquals(button, window._undoEditorButton))
        {
            window.Dispatcher.BeginInvoke(
                () => window.FinalizeInstantHistoryFeedback("Cambio deshecho."),
                DispatcherPriority.ContextIdle);
        }
        else if (ReferenceEquals(button, window._redoEditorButton))
        {
            window.Dispatcher.BeginInvoke(
                () => window.FinalizeInstantHistoryFeedback("Cambio rehecho."),
                DispatcherPriority.ContextIdle);
        }
    }

    private static void MainWindow_InstantHistoryPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not MainWindow window || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        string? message = e.Key switch
        {
            Key.Z => "Cambio deshecho.",
            Key.Y => "Cambio rehecho.",
            _ => null
        };
        if (message is null)
        {
            return;
        }

        window.Dispatcher.BeginInvoke(
            () => window.FinalizeInstantHistoryFeedback(message),
            DispatcherPriority.ContextIdle);
    }

    private void FinalizeInstantHistoryFeedback(string message)
    {
        _pendingEditorUndoSave = null;
        _fastUndoSaveLoopRunning = true;
        SetFooterStatus(message + " Guarda la página cuando termines.", "#4CB2BB");
        RefreshEditorToolAvailability();
        RefreshPageSaveAvailability();
    }
}
