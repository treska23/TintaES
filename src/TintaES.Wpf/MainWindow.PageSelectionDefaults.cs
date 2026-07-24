using System.Windows;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Hace explícito el comportamiento normal de exportación: un cómic recién abierto empieza con
/// todas sus páginas seleccionadas. La selección por bloques de veinte sigue disponible, pero
/// únicamente cuando el usuario la solicita desde el panel.
/// </summary>
public partial class MainWindow
{
    private static readonly bool PageSelectionDefaultsRegistered = RegisterPageSelectionDefaults();

    private bool _pageSelectionDefaultsInstalled;
    private string? _pageSelectionDefaultsSessionKey;
    private int _lastObservedExportedPageCount;

    private static bool RegisterPageSelectionDefaults()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_PageSelectionDefaultsLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_PageSelectionDefaultsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.Dispatcher.BeginInvoke(
            window.InstallPageSelectionDefaults,
            DispatcherPriority.ApplicationIdle);
    }

    private void InstallPageSelectionDefaults()
    {
        if (_pageSelectionDefaultsInstalled)
        {
            return;
        }

        _pageSelectionDefaultsInstalled = true;

        // SyncPageSelectionPanel ya detecta cada sesión nueva y selecciona todas sus páginas.
        // Ejecutar esto en LayoutUpdated repetía recuentos y textos continuamente.
        ApplyPageSelectionDefaults();
    }

    private void ApplyPageSelectionDefaults()
    {
        if (_comicPages.Count == 0)
        {
            _pageSelectionDefaultsSessionKey = null;
            _lastObservedExportedPageCount = 0;
            UpdateCbzExportSelectionCaption();
            return;
        }

        string sessionKey = $"{_comicPages.Count}|{_comicPages[0].SourcePath}|{_comicPages[^1].SourcePath}";
        if (!string.Equals(sessionKey, _pageSelectionDefaultsSessionKey, StringComparison.OrdinalIgnoreCase))
        {
            _pageSelectionDefaultsSessionKey = sessionKey;
            _lastObservedExportedPageCount = 0;
            Dispatcher.BeginInvoke(
                () =>
                {
                    string currentKey = _comicPages.Count == 0
                        ? string.Empty
                        : $"{_comicPages.Count}|{_comicPages[0].SourcePath}|{_comicPages[^1].SourcePath}";
                    if (string.Equals(currentKey, sessionKey, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectAllComicPages();
                        UpdateCbzExportSelectionCaption();
                    }
                },
                DispatcherPriority.ContextIdle);
            return;
        }

        if (!_comicBatchBusy
            && _comicPages.Count > 0
            && _exportedComicPageIndices.Count == _comicPages.Count
            && _lastObservedExportedPageCount < _comicPages.Count
            && _selectedComicPageIndices.Count == 0)
        {
            _lastObservedExportedPageCount = _exportedComicPageIndices.Count;
            SelectAllComicPages();
        }
        else if (!_comicBatchBusy)
        {
            _lastObservedExportedPageCount = _exportedComicPageIndices.Count;
        }

        UpdateCbzExportSelectionCaption();
    }

    private void UpdateCbzExportSelectionCaption()
    {
        if (_exportComicButton is null)
        {
            return;
        }

        int total = _comicPages.Count;
        int selected = _selectedComicPageIndices.Count;
        string content = total == 0
            ? "Exportar CBZ"
            : selected == total
                ? $"Exportar CBZ ({total})"
                : $"Exportar CBZ ({selected}/{total})";
        string toolTip = total == 0
            ? "Exportar páginas a un archivo CBZ"
            : $"Se exportarán {selected} de {total} páginas. La selección se controla en el panel izquierdo.";

        if (!Equals(_exportComicButton.Content, content))
        {
            _exportComicButton.Content = content;
        }
        if (!Equals(_exportComicButton.ToolTip, toolTip))
        {
            _exportComicButton.ToolTip = toolTip;
        }
    }
}
