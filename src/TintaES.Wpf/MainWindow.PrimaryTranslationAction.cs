using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene el botón principal sincronizado con los checkbox de páginas. Los módulos antiguos
/// todavía escriben su título genérico al actualizar la barra; este controlador restablece el
/// estado correcto inmediatamente y sin crear un segundo botón.
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
        if (_refreshingPrimaryTranslationAction
            || AnalyzeButton is null
            || _comicPages.Count == 0)
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
            int[] selected = selectionInitialized
                ? GetSelectedComicPageIndices()
                    .Where(index => index >= 0 && index < _comicPages.Count)
                    .ToArray()
                : Enumerable.Range(0, _comicPages.Count).ToArray();

            // Un archivo .tinta ya contiene el trabajo editable del proyecto. Su acción principal
            // es repasar las traducciones guardadas, aunque alguna página concreta esté incompleta.
            // El servicio de repaso filtrará las páginas sin texto sin volver a ejecutar el OCR.
            bool openedProject = !string.IsNullOrWhiteSpace(_currentProjectPath);
            bool review = openedProject || SelectedPagesCanBeReviewed(selected);
            string caption = review
                ? "✦  Repasar traducción"
                : "✦  Detectar y traducir selección";
            string toolTip = selected.Length == 0
                ? "Marca al menos una página en la columna izquierda"
                : review
                    ? "Repasar solo el texto de las páginas marcadas, sin repetir OCR ni detección"
                    : "Detectar y traducir únicamente las páginas marcadas";

            if (!string.Equals(AnalyzeButton.Content?.ToString(), caption, StringComparison.Ordinal))
            {
                AnalyzeButton.Content = caption;
            }
            AnalyzeButton.ToolTip = toolTip;

            bool busy = _comicBatchBusy
                        || _pageNavigationBusy
                        || BusyOverlay.Visibility == Visibility.Visible;
            bool hasModel = ModelComboBox.SelectedItem is not null;
            bool enabled = selected.Length > 0 && hasModel && !busy;
            if (AnalyzeButton.IsEnabled != enabled)
            {
                AnalyzeButton.IsEnabled = enabled;
            }
        }
        finally
        {
            _refreshingPrimaryTranslationAction = false;
        }
    }
}
