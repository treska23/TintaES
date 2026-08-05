using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene la acción principal dedicada siempre al pipeline completo. Repasar traducción es una
/// orden independiente y nunca sustituye ni cambia el significado de Detectar y traducir.
/// </summary>
public partial class MainWindow
{
    private static readonly bool PrimaryTranslationActionRegistered =
        RegisterPrimaryTranslationAction();

    private bool _primaryTranslationActionInstalled;
    private bool _refreshingPrimaryTranslationAction;

    private static bool RegisterPrimaryTranslationAction()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_PrimaryTranslationActionLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_PrimaryTranslationActionLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallPrimaryTranslationAction,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallPrimaryTranslationAction()
    {
        if (_primaryTranslationActionInstalled || AnalyzeButton is null)
        {
            return;
        }

        _primaryTranslationActionInstalled = true;
        AnalyzeButton.LayoutUpdated += (_, _) => RefreshPrimaryTranslationAction();
        PreviewMouseUp += MainWindow_PrimaryTranslationActionInputFinished;
        PreviewKeyUp += MainWindow_PrimaryTranslationActionInputFinished;
        RefreshPrimaryTranslationAction();
    }

    private void MainWindow_PrimaryTranslationActionInputFinished(
        object sender,
        InputEventArgs e)
    {
        Dispatcher.BeginInvoke(
            RefreshPrimaryTranslationAction,
            DispatcherPriority.Background);
    }

    private void RefreshPrimaryTranslationAction()
    {
        if (_refreshingPrimaryTranslationAction || AnalyzeButton is null)
        {
            return;
        }

        _refreshingPrimaryTranslationAction = true;
        try
        {
            string sessionKey = BuildActiveDocumentSessionKey();
            bool selectionInitialized = string.Equals(
                _pageSelectionSessionKey,
                sessionKey,
                StringComparison.OrdinalIgnoreCase);
            int[] selected = _comicPages.Count == 0
                ? []
                : selectionInitialized
                    ? GetSelectedComicPageIndices()
                        .Where(index => index >= 0 && index < _comicPages.Count)
                        .ToArray()
                    : Enumerable.Range(0, _comicPages.Count).ToArray();

            const string caption = "✦  Detectar y traducir";
            if (!string.Equals(AnalyzeButton.Content?.ToString(), caption, StringComparison.Ordinal))
            {
                AnalyzeButton.Content = caption;
            }

            AnalyzeButton.ToolTip = selected.Length == 0
                ? "Marca al menos una página en la columna izquierda"
                : "Volver a detectar los bocadillos, ejecutar OCR y traducir únicamente las páginas marcadas";

            bool busy = _comicBatchBusy
                        || _pageNavigationBusy
                        || BusyOverlay.Visibility == Visibility.Visible;
            bool hasModel = ModelComboBox.SelectedItem is not null;
            AnalyzeButton.IsEnabled = selected.Length > 0 && hasModel && !busy;

            // La acción de repaso vive en un botón separado, pero comparte el mismo refresco para
            // que aparezca o se desactive inmediatamente al cambiar los checkbox.
            RefreshProjectRetranslationAction();
        }
        finally
        {
            _refreshingPrimaryTranslationAction = false;
        }
    }
}
