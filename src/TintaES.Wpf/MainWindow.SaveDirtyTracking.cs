using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene una única semántica de guardado: un proyecto sin archivo o con páginas modificadas
/// necesita guardarse; un proyecto recién guardado no. Observa las regiones visibles para que
/// editar una traducción vuelva a habilitar Guardar en el mismo instante, sin depender de perder
/// el foco del cuadro de texto.
/// </summary>
public partial class MainWindow
{
    private bool _saveDirtyTrackingInstalled;

    private void InstallSaveDirtyTracking()
    {
        if (_saveDirtyTrackingInstalled)
        {
            RefreshDirtyAwareSaveCommands();
            return;
        }

        _saveDirtyTrackingInstalled = true;
        _regions.CollectionChanged += SaveDirtyTracking_RegionsChanged;
        foreach (var region in _regions)
        {
            region.PropertyChanged -= SaveDirtyTracking_RegionPropertyChanged;
            region.PropertyChanged += SaveDirtyTracking_RegionPropertyChanged;
        }

        RefreshDirtyAwareSaveCommands();
    }

    private void SaveDirtyTracking_RegionsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (object item in e.OldItems)
            {
                if (item is TintaES.Core.ComicRegion region)
                {
                    region.PropertyChanged -= SaveDirtyTracking_RegionPropertyChanged;
                }
            }
        }

        if (e.NewItems is not null)
        {
            foreach (object item in e.NewItems)
            {
                if (item is TintaES.Core.ComicRegion region)
                {
                    region.PropertyChanged -= SaveDirtyTracking_RegionPropertyChanged;
                    region.PropertyChanged += SaveDirtyTracking_RegionPropertyChanged;
                }
            }
        }
    }

    private void SaveDirtyTracking_RegionPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (_syncingEditor
            || _applyingEditorSnapshot
            || _switchingDocument
            || _pageNavigationBusy
            || _comicBatchBusy
            || _visibleComicPageIndex < 0
            || _visibleComicPageIndex >= _comicPages.Count)
        {
            return;
        }

        MarkActiveDocumentDirty(_visibleComicPageIndex);
        RefreshDirtyAwareSaveCommands();
    }

    private bool ProjectNeedsUserSave()
    {
        if (_comicPages.Count == 0)
        {
            return false;
        }

        bool projectFileMissing = string.IsNullOrWhiteSpace(_currentProjectPath)
                                  || !File.Exists(_currentProjectPath);
        return projectFileMissing || (_activeDocumentSession?.DirtyPages.Count ?? 0) > 0;
    }

    private bool CurrentPageNeedsUserSave()
    {
        if (_comicPageIndex < 0 || _comicPageIndex >= _comicPages.Count)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_currentProjectPath) || !File.Exists(_currentProjectPath))
        {
            return true;
        }

        return _activeDocumentSession?.DirtyPages.Contains(_comicPageIndex) == true;
    }

    private void RefreshDirtyAwareSaveCommands()
    {
        UpdateProjectCommandAvailability();
        RefreshPageSaveAvailability();
        UpdateClassicMenuAvailability();
    }
}
