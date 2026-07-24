using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Añade un acceso directo para volver al primer bloque de veinte páginas sin cambiar
/// el comportamiento normal, que mantiene todo el cómic seleccionado al abrirlo.
/// </summary>
public partial class MainWindow
{
    private static readonly bool FirstPageBatchButtonRegistered = RegisterFirstPageBatchButton();

    private Button? _firstPageBatchButton;
    private bool _firstPageBatchButtonHookInstalled;

    private static bool RegisterFirstPageBatchButton()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_FirstPageBatchButtonLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_FirstPageBatchButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.Dispatcher.BeginInvoke(
            window.InstallFirstPageBatchButtonHook,
            DispatcherPriority.ApplicationIdle);
    }

    private void InstallFirstPageBatchButtonHook()
    {
        if (_firstPageBatchButtonHookInstalled)
        {
            return;
        }

        _firstPageBatchButtonHookInstalled = true;
        LayoutUpdated += (_, _) => TryInstallFirstPageBatchButton();
        TryInstallFirstPageBatchButton();
    }

    private void TryInstallFirstPageBatchButton()
    {
        if (_firstPageBatchButton is not null
            || _pageSelectionPanel?.Child is not Grid panelGrid)
        {
            return;
        }

        WrapPanel? actionPanel = panelGrid.Children.OfType<WrapPanel>().FirstOrDefault();
        if (actionPanel is null)
        {
            return;
        }

        _firstPageBatchButton = CreatePageSelectionButton(
            "20 primeras",
            (_, _) =>
            {
                SelectFirstComicPageBatch();
                UpdateCbzExportSelectionCaption();
            });

        // Orden: Todas · Ninguna · 20 primeras · 20 siguientes.
        actionPanel.Children.Insert(Math.Min(2, actionPanel.Children.Count), _firstPageBatchButton);
    }

    private void SelectFirstComicPageBatch()
    {
        ApplyPageSelection(Enumerable.Range(0, Math.Min(SafeExportBatchSize, _comicPages.Count)));
    }
}
