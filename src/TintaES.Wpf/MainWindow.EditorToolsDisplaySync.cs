using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private static readonly bool EditorDisplaySyncRegistered = RegisterEditorDisplaySync();
    private bool _editorDisplaySyncInstalled;
    private bool _editorDisplayRefreshPending;

    private static bool RegisterEditorDisplaySync()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_EditorDisplaySyncLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_EditorDisplaySyncLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.InstallEditorDisplaySync();
        }
    }

    private void InstallEditorDisplaySync()
    {
        if (_editorDisplaySyncInstalled)
        {
            return;
        }

        _editorDisplaySyncInstalled = true;
        _regions.CollectionChanged += EditorRegions_CollectionChanged;
    }

    private void EditorRegions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_editorDisplayRefreshPending)
        {
            return;
        }

        _editorDisplayRefreshPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _editorDisplayRefreshPending = false;
                if (_originalBitmap is null)
                {
                    return;
                }

                if (_previewMode is "result" or "clean")
                {
                    PageImage.Source = _cleanedBitmap ?? _cleanedBaseBitmap ?? _originalBitmap;
                }
                else if (_previewMode == "mask" && _maskBitmap is not null)
                {
                    PageImage.Source = _maskBitmap;
                }

                MaskPreviewButton.IsEnabled = _maskBitmap is not null;
            },
            DispatcherPriority.Render);
    }
}
