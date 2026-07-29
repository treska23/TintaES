using System.Windows;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Hace que los checkboxes del panel izquierdo controlen también qué páginas se analizan y
/// traducen. La selección sigue reutilizándose para la exportación CBZ.
/// </summary>
public partial class MainWindow
{
    private static readonly bool SelectedPageProcessingRegistered = RegisterSelectedPageProcessing();
    private bool _selectedPageProcessingInstalled;

    private static bool RegisterSelectedPageProcessing()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_SelectedPageProcessingLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_SelectedPageProcessingLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallSelectedPageProcessing,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallSelectedPageProcessing()
    {
        if (_selectedPageProcessingInstalled)
        {
            return;
        }

        if (AnalyzeButton is null)
        {
            Dispatcher.BeginInvoke(InstallSelectedPageProcessing, DispatcherPriority.ApplicationIdle);
            return;
        }

        // InstallComicBookHandlers añadió el manejador general. Lo sustituimos una vez que la
        // interfaz actual ya está montada para aplicar la selección del panel izquierdo.
        AnalyzeButton.Click -= AnalyzeComicButton_Click;
        AnalyzeButton.Click += AnalyzeSelectedComicPagesButton_Click;
        _selectedPageProcessingInstalled = true;
    }

    private void AnalyzeSelectedComicPagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0)
        {
            AnalyzeComicButton_Click(sender, e);
            return;
        }

        IReadOnlyList<int> selected = GetSelectedComicPageIndices();
        if (selected.Count == 0)
        {
            SetFooterStatus("No hay páginas seleccionadas para analizar y traducir.", "#C99A35");
            return;
        }

        var selectedSet = selected.ToHashSet();
        var temporarilySuppressed = new List<ComicBookPageState>();
        foreach ((ComicBookPageState page, int index) in _comicPages.Select((page, index) => (page, index)))
        {
            if (!selectedSet.Contains(index) && PageNeedsTranslation(page))
            {
                page.SuppressBatchProcessing = true;
                temporarilySuppressed.Add(page);
            }
        }

        try
        {
            // El manejador original captura inmediatamente la lista pending antes de su primer
            // await. Restauramos después el estado de las páginas no seleccionadas; no se pierden
            // ni se marcan como traducidas.
            AnalyzeComicButton_Click(sender, e);
        }
        finally
        {
            foreach (ComicBookPageState page in temporarilySuppressed)
            {
                page.SuppressBatchProcessing = false;
            }
        }
    }
}
