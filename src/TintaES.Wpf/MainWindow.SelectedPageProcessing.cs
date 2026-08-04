using System.Windows;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene un único controlador para la acción principal. La selección por checkbox se resuelve
/// dentro de AnalyzeComicButton_Click, que decide entre repasar un proyecto existente o ejecutar
/// detección y traducción desde cero. Este instalador no puede volver a sustituir esa decisión por
/// el antiguo procesamiento directo.
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

        // El controlador antiguo llamaba siempre al pipeline largo, aunque el botón mostrase
        // «Repasar traducción». Se elimina expresamente y se deja una sola ruta de ejecución.
        AnalyzeButton.Click -= AnalyzeSelectedComicPagesButton_Click;
        AnalyzeButton.Click -= AnalyzeComicButton_Click;
        AnalyzeButton.Click += AnalyzeComicButton_Click;
        _selectedPageProcessingInstalled = true;
    }

    /// <summary>
    /// Nombre conservado para compatibilidad con compilaciones o enlaces antiguos. Toda llamada
    /// termina en el controlador unificado y, por tanto, respeta Repasar traducción.
    /// </summary>
    private void AnalyzeSelectedComicPagesButton_Click(object sender, RoutedEventArgs e) =>
        AnalyzeComicButton_Click(sender, e);
}
