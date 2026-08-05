using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene una única entrada para la acción principal. El clic se intercepta como evento de
/// clase antes de cualquier controlador heredado del XAML o instalado por módulos antiguos; así
/// ninguna segunda ruta puede iniciar un lote distinto del que marcan los checkbox.
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

        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(MainWindow_PrimaryTranslationButtonClassClick));
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

    private static void MainWindow_PrimaryTranslationButtonClassClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button
            || Window.GetWindow(button) is not MainWindow window
            || !ReferenceEquals(button, window.AnalyzeButton))
        {
            return;
        }

        // Los controladores de instancia se ejecutan después de los controladores de clase.
        // Marcar el evento aquí garantiza que solo exista una orden de traducción por clic,
        // independientemente del orden en que los módulos hayan instalado sus handlers.
        e.Handled = true;
        window.AnalyzeComicButton_Click(button, e);
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

        // Se retiran las rutas conocidas. El class handler de arriba sigue siendo la autoridad
        // incluso si otro instalador antiguo intenta volver a conectar una de ellas después.
        AnalyzeButton.Click -= AnalyzeButton_Click;
        AnalyzeButton.Click -= AnalyzeButton_Click_Responsive;
        AnalyzeButton.Click -= AnalyzeSelectedComicPagesButton_Click;
        AnalyzeButton.Click -= AnalyzeComicButton_Click;
        _selectedPageProcessingInstalled = true;
    }

    /// <summary>
    /// Nombre conservado para compatibilidad con compilaciones o enlaces antiguos. Toda llamada
    /// termina en el controlador unificado y usa una instantánea de los checkbox visibles.
    /// </summary>
    private void AnalyzeSelectedComicPagesButton_Click(object sender, RoutedEventArgs e) =>
        AnalyzeComicButton_Click(sender, e);
}
