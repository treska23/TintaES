using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Compatibilidad del editor con el overlay canónico. Ya no crea TextBlocks, Adorners ni un
/// segundo renderizador: la selección solo muestra el tirador transparente de la región activa.
/// </summary>
public partial class MainWindow
{
    private const string NativeTextBlockTag = "tinta-native-text-frame";
    private static readonly bool TextFrameEditingRegistered = RegisterTextFrameEditing();

    private readonly HashSet<Guid> _validatedNativeBaseSizes = [];
    private bool _textFrameEditingInstalled;
    private bool _nativeTextFrameRefreshPending;

    private static bool RegisterTextFrameEditing()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_TextFrameEditingLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_TextFrameEditingLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.InstallTextFrameEditing();
        }
    }

    private void InstallTextFrameEditing()
    {
        if (_textFrameEditingInstalled)
        {
            QueueNativeTextFrameRefresh();
            return;
        }

        _textFrameEditingInstalled = true;
        RegionListBox.SelectionChanged += RegionListBox_OrganicFrameSelectionChanged;
        BusyOverlay.IsVisibleChanged += BusyOverlay_OrganicFrameVisibilityChanged;
        ResultPreviewButton.Click += ResultPreviewButton_OrganicFrameClick;
        _regions.CollectionChanged += Regions_OrganicFrameCollectionChanged;
        QueueNativeTextFrameRefresh();
    }

    private void RegionListBox_OrganicFrameSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        QueueNativeTextFrameRefresh();

    private void BusyOverlay_OrganicFrameVisibilityChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (!BusyOverlay.IsVisible)
        {
            QueueNativeTextFrameRefresh();
        }
    }

    private void ResultPreviewButton_OrganicFrameClick(object sender, RoutedEventArgs e) =>
        QueueNativeTextFrameRefresh();

    private void Regions_OrganicFrameCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e) =>
        QueueNativeTextFrameRefresh();

    private void QueueNativeTextFrameRefresh()
    {
        if (_nativeTextFrameRefreshPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _nativeTextFrameRefreshPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _nativeTextFrameRefreshPending = false;
                RefreshSelectedTextFrame();
            },
            DispatcherPriority.Render);
    }

    private void RefreshSelectedTextFrame()
    {
        RefreshRegionSelectionChrome();
    }

    private void EnsureNativeAdornerLayer()
    {
    }

    private void RemoveNativeTextFrameAdorner()
    {
    }
}
