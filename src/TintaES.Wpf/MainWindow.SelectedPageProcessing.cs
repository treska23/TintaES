using System.Windows;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Hace que los checkboxes del panel izquierdo controlen también qué páginas se analizan y
/// traducen. Antes de iniciar el OCR prepara una única ficha documental de la obra, que se
/// reutiliza para todas las páginas seleccionadas.
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

        AnalyzeButton.Click -= AnalyzeComicButton_Click;
        AnalyzeButton.Click -= AnalyzeSelectedComicPagesButton_Click;
        AnalyzeButton.Click += AnalyzeSelectedComicPagesButton_Click;
        _selectedPageProcessingInstalled = true;
    }

    private async void AnalyzeSelectedComicPagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureComicResearchContextAsync())
        {
            return;
        }

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

        if (ModelComboBox.SelectedValue is not string model || string.IsNullOrWhiteSpace(model))
        {
            SetFooterStatus("Selecciona un modelo de traducción antes de continuar.", "#C99A35");
            return;
        }

        await AnalyzeSelectedComicPagesReliablyAsync(selected, model);
    }
}
