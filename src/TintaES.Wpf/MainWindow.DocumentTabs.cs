using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Espacio multidocumento. Cada pestaña conserva su lista de páginas, temporales,
/// proyecto asociado, selección y pila de deshacer. El lienzo sigue siendo único:
/// al cambiar de pestaña se aparca la sesión visible y se restaura la elegida.
/// </summary>
public partial class MainWindow
{
    private static readonly bool DocumentTabsRegistered = RegisterDocumentTabs();

    private readonly List<ComicDocumentSession> _documentSessions = [];
    private ComicDocumentSession? _activeDocumentSession;
    private bool _documentTabsInstalled;
    private bool _switchingDocument;
    private bool _documentOpenPending;

    private static bool RegisterDocumentTabs()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_DocumentTabsLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_DocumentTabsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallDocumentTabs,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallDocumentTabs()
    {
        if (_documentTabsInstalled)
        {
            RefreshDocumentTabs();
            return;
        }

        _documentTabsInstalled = true;
        if (_comicPages.Count > 0 && _activeDocumentSession is null)
        {
            var adopted = new ComicDocumentSession
            {
                Title = ResolveDocumentTitle()
            };
            _documentSessions.Add(adopted);
            _activeDocumentSession = adopted;
            CaptureActiveDocumentState();
        }

        RefreshDocumentTabs();
    }

    /// <summary>
    /// Se llama justo antes de que el cargador existente prepare su carpeta temporal.
    /// Aparca el documento actual sin borrar su espacio de trabajo y crea la sesión
    /// vacía en la que se abrirá el nuevo archivo.
    /// </summary>
    private void BeginNewDocumentWorkspace()
    {
        if (_activeDocumentSession is not null && _comicPages.Count > 0)
        {
            CaptureActiveDocumentState();
        }
        else if (_activeDocumentSession is not null)
        {
            _documentSessions.Remove(_activeDocumentSession);
            DeleteDocumentWorkspaceSafely(_activeDocumentSession.Workspace);
        }
        else if (_comicPages.Count > 0)
        {
            var adopted = new ComicDocumentSession
            {
                Title = ResolveDocumentTitle()
            };
            _documentSessions.Add(adopted);
            _activeDocumentSession = adopted;
            CaptureActiveDocumentState();
        }

        // El espacio anterior ya pertenece a su sesión aparcada. Se desacopla antes de
        // preparar el siguiente para que el cargador no pueda eliminarlo por accidente.
        _comicWorkspace = null;
        _currentProjectPath = null;
        _comicPages.Clear();
        _comicPageIndex = -1;
        _visibleComicPageIndex = -1;
        _comicTitle = "Cargando…";
        ClearComicPageBitmapCache();
        ClearPerDocumentEditorState();

        var created = new ComicDocumentSession
        {
            Title = "Cargando…"
        };
        _documentSessions.Add(created);
        _activeDocumentSession = created;
        RefreshDocumentTabs();
    }

    private void CaptureActiveDocumentState()
    {
        if (_activeDocumentSession is null)
        {
            return;
        }

        PersistVisibleComicPageRegions();
        ComicDocumentSession session = _activeDocumentSession;
        session.Pages.Clear();
        session.Pages.AddRange(_comicPages);
        session.Title = ResolveDocumentTitle();
        session.Workspace = _comicWorkspace;
        session.ProjectPath = _currentProjectPath;
        session.PageIndex = _comicPageIndex;
        session.VisiblePageIndex = _visibleComicPageIndex;

        session.EditorHistories.Clear();
        foreach ((int index, EditorPageHistory history) in _editorPageHistories)
        {
            session.EditorHistories[index] = history;
        }

        session.SelectedPageIndices.Clear();
        session.SelectedPageIndices.UnionWith(_selectedComicPageIndices);
        session.ExportedPageIndices.Clear();
        session.ExportedPageIndices.UnionWith(_exportedComicPageIndices);
        session.PageSelectionAnchorIndex = _pageSelectionAnchorIndex;
    }

    private void SynchronizeActiveDocumentState()
    {
        CaptureActiveDocumentState();
        RefreshDocumentTabs();
    }

    private void MarkActiveDocumentDirty(int? pageIndex = null)
    {
        if (_switchingDocument || _activeDocumentSession is null || _comicPages.Count == 0)
        {
            return;
        }

        int index = pageIndex ?? _comicPageIndex;
        if (index >= 0 && index < _comicPages.Count)
        {
            _activeDocumentSession.DirtyPages.Add(index);
            RefreshDocumentTabs();
        }
    }

    private void MarkActiveDocumentPageSaved(int pageIndex)
    {
        _activeDocumentSession?.DirtyPages.Remove(pageIndex);
        SynchronizeActiveDocumentState();
    }

    private void MarkActiveDocumentSaved()
    {
        if (_activeDocumentSession is not null)
        {
            _activeDocumentSession.DirtyPages.Clear();
        }
        SynchronizeActiveDocumentState();
    }

    private async Task AwaitCurrentDocumentReadyForOpenAsync()
    {
        _documentOpenPending = true;
        try
        {
            if (HasDocumentOperationInProgress())
            {
                _analysisCancellation?.Cancel();
                SetFooterStatus(
                    "Deteniendo la operación actual para abrir el nuevo documento en otra pestaña…",
                    "#C99A35");
            }

            while (HasDocumentOperationInProgress())
            {
                await Task.Delay(35);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
        finally
        {
            _documentOpenPending = false;
        }
    }

    private bool HasDocumentOperationInProgress() =>
        _comicBatchBusy
        || _pageNavigationBusy
        || _pageSaveBusy
        || _addingImagePages
        || _maskEditorBusy
        || _fastRegionDeletionBusy
        || BusyOverlay.Visibility == Visibility.Visible;

    private async Task SwitchDocumentAsync(ComicDocumentSession target)
    {
        if (_switchingDocument || ReferenceEquals(target, _activeDocumentSession))
        {
            return;
        }

        await AwaitCurrentDocumentReadyForOpenAsync();
        _switchingDocument = true;
        try
        {
            CaptureActiveDocumentState();
            _activeDocumentSession = target;
            RestoreDocumentRuntimeState(target);
            RefreshDocumentTabs();

            if (_comicPages.Count > 0)
            {
                int index = Math.Clamp(target.PageIndex, 0, _comicPages.Count - 1);
                await ShowComicPageFastAsync(index);
                target.PageIndex = _comicPageIndex;
                target.VisiblePageIndex = _visibleComicPageIndex;
            }
            else
            {
                ResetDocumentRuntimeToEmpty();
            }
        }
        finally
        {
            _switchingDocument = false;
            RefreshDocumentTabs();
        }
    }

    private void RestoreDocumentRuntimeState(ComicDocumentSession session)
    {
        foreach (var region in _regions)
        {
            region.PropertyChanged -= Region_PropertyChanged;
        }
        _regions.Clear();
        OverlayCanvas.Children.Clear();

        _comicPages.Clear();
        _comicPages.AddRange(session.Pages);
        _comicTitle = session.Title;
        _comicWorkspace = session.Workspace;
        _currentProjectPath = session.ProjectPath;
        _comicPageIndex = session.PageIndex;
        _visibleComicPageIndex = -1;
        _selectedRegion = null;

        ClearComicPageBitmapCache();
        ClearPerDocumentEditorState();
        foreach ((int index, EditorPageHistory history) in session.EditorHistories)
        {
            _editorPageHistories[index] = history;
        }

        _selectedComicPageIndices.UnionWith(session.SelectedPageIndices);
        _exportedComicPageIndices.UnionWith(session.ExportedPageIndices);
        _pageSelectionAnchorIndex = session.PageSelectionAnchorIndex;
        _editorHistorySessionKey = BuildActiveDocumentSessionKey();
        _pageSelectionSessionKey = BuildActiveDocumentSessionKey();
    }

    private void ClearPerDocumentEditorState()
    {
        _editorPageHistories.Clear();
        _editorHistorySessionKey = null;
        _textEditBaseline = null;
        _textEditRegionId = null;
        _selectedComicPageIndices.Clear();
        _exportedComicPageIndices.Clear();
        _pageSelectionAnchorIndex = -1;
        _pageCheckAnchorIndex = -1;
        _pageSelectionSessionKey = null;
    }

    private string BuildActiveDocumentSessionKey() =>
        _activeDocumentSession is not null
            ? _activeDocumentSession.Id.ToString("N")
            : _comicPages.Count == 0
                ? string.Empty
                : $"{_comicPages.Count}|{_comicPages[0].SourcePath}|{_comicPages[^1].SourcePath}";

    private async Task CloseDocumentAsync(ComicDocumentSession session)
    {
        if (_switchingDocument)
        {
            return;
        }

        if (ReferenceEquals(session, _activeDocumentSession))
        {
            CaptureActiveDocumentState();
        }

        if (session.DirtyPages.Count > 0)
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                $"«{session.Title}» tiene cambios sin guardar.\n\n¿Quieres cerrar esta pestaña y descartar esos cambios?",
                "Cerrar documento",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        await AwaitCurrentDocumentReadyForOpenAsync();
        bool wasActive = ReferenceEquals(session, _activeDocumentSession);
        int removedIndex = _documentSessions.IndexOf(session);
        _documentSessions.Remove(session);

        if (wasActive)
        {
            _activeDocumentSession = null;
            if (_documentSessions.Count > 0)
            {
                int nextIndex = Math.Clamp(removedIndex, 0, _documentSessions.Count - 1);
                ComicDocumentSession next = _documentSessions[nextIndex];
                _switchingDocument = true;
                try
                {
                    _activeDocumentSession = next;
                    RestoreDocumentRuntimeState(next);
                    RefreshDocumentTabs();
                    if (_comicPages.Count > 0)
                    {
                        await ShowComicPageFastAsync(
                            Math.Clamp(next.PageIndex, 0, _comicPages.Count - 1));
                    }
                    else
                    {
                        ResetDocumentRuntimeToEmpty();
                    }
                }
                finally
                {
                    _switchingDocument = false;
                }
            }
            else
            {
                ResetDocumentRuntimeToEmpty();
            }
        }

        DeleteDocumentWorkspaceSafely(session.Workspace);
        RefreshDocumentTabs();
    }

    private void AbandonEmptyDocumentAfterOpenFailure()
    {
        ComicDocumentSession? failed = _activeDocumentSession;
        if (failed is null || _comicPages.Count > 0 || failed.Pages.Count > 0)
        {
            return;
        }

        int failedIndex = _documentSessions.IndexOf(failed);
        _documentSessions.Remove(failed);
        DeleteDocumentWorkspaceSafely(_comicWorkspace ?? failed.Workspace);
        _activeDocumentSession = null;

        if (_documentSessions.Count == 0)
        {
            ResetDocumentRuntimeToEmpty();
            return;
        }

        ComicDocumentSession previous =
            _documentSessions[Math.Clamp(failedIndex - 1, 0, _documentSessions.Count - 1)];
        _activeDocumentSession = previous;
        RestoreDocumentRuntimeState(previous);
        RefreshDocumentTabs();
        if (_comicPages.Count > 0)
        {
            _ = ShowComicPageFastAsync(Math.Clamp(previous.PageIndex, 0, _comicPages.Count - 1));
        }
    }

    private void ResetDocumentRuntimeToEmpty()
    {
        _analysisCancellation?.Cancel();
        _comicBatchBusy = false;
        _pageNavigationBusy = false;
        _comicPages.Clear();
        _comicPageIndex = -1;
        _visibleComicPageIndex = -1;
        _comicTitle = null;
        _comicWorkspace = null;
        _currentProjectPath = null;
        _sourcePath = null;
        _originalBitmap = null;
        _cleanedBaseBitmap = null;
        _cleanedBitmap = null;
        _maskBitmap = null;
        _selectedRegion = null;
        foreach (var region in _regions)
        {
            region.PropertyChanged -= Region_PropertyChanged;
        }
        _regions.Clear();
        ClearComicPageBitmapCache();
        ClearPerDocumentEditorState();

        PageImage.Source = null;
        OverlayCanvas.Children.Clear();
        OverlayCanvas.Visibility = Visibility.Collapsed;
        ImageScrollViewer.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        PageNameText.Text = "Ninguna página abierta";
        PageInfoText.Text = "Abre un cómic, una carpeta o un proyecto";
        LanguageText.Text = "— → ES";
        OriginalPreviewButton.IsEnabled = false;
        MaskPreviewButton.IsEnabled = false;
        CleanPreviewButton.IsEnabled = false;
        ResultPreviewButton.IsEnabled = false;
        RegionListBox.SelectedItem = null;
        ShowRegionEditor(null);
        UpdateRegionCount();
        SetBusy(false);
        UpdateComicControls();
        SyncDirectPageSelector();
        UpdateProjectCommandAvailability();
        UpdateClassicMenuAvailability();
        RefreshEditorToolAvailability();
        RefreshPageSaveAvailability();
    }

    private void RefreshDocumentTabs()
    {
        if (!_documentTabsInstalled || DocumentTabsPanel is null)
        {
            return;
        }

        DocumentTabsPanel.Children.Clear();
        bool hasDocuments = _documentSessions.Count > 0;
        DocumentTabsHost.Visibility = hasDocuments ? Visibility.Visible : Visibility.Collapsed;
        DocumentTabsRow.Height = hasDocuments ? new GridLength(38) : new GridLength(0);

        foreach (ComicDocumentSession session in _documentSessions)
        {
            bool active = ReferenceEquals(session, _activeDocumentSession);
            var border = new Border
            {
                MinWidth = 150,
                MaxWidth = 300,
                Height = 34,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(10, 0, 4, 0),
                Background = new SolidColorBrush(
                    active ? Color.FromRgb(42, 46, 50) : Color.FromRgb(28, 31, 34)),
                BorderBrush = new SolidColorBrush(
                    active ? Color.FromRgb(238, 89, 75) : Color.FromRgb(56, 62, 67)),
                BorderThickness = new Thickness(0, active ? 2 : 1, 0, 0),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Tag = session
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string dirtyMark = session.DirtyPages.Count > 0 ? " ●" : string.Empty;
            var select = new Button
            {
                Content = session.Title + dirtyMark,
                ToolTip = ResolveDocumentToolTip(session),
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(
                    active ? Color.FromRgb(242, 238, 229) : Color.FromRgb(175, 181, 186)),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Cursor = Cursors.Hand
            };
            select.Click += async (_, _) => await SwitchDocumentAsync(session);

            var close = new Button
            {
                Content = "×",
                ToolTip = $"Cerrar {session.Title}",
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(7, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(175, 181, 186)),
                FontSize = 17,
                Cursor = Cursors.Hand
            };
            close.Click += async (_, args) =>
            {
                args.Handled = true;
                await CloseDocumentAsync(session);
            };

            Grid.SetColumn(select, 0);
            Grid.SetColumn(close, 1);
            grid.Children.Add(select);
            grid.Children.Add(close);
            border.Child = grid;
            DocumentTabsPanel.Children.Add(border);
        }
    }

    private string ResolveDocumentTitle()
    {
        if (!string.IsNullOrWhiteSpace(_comicTitle))
        {
            return _comicTitle.Trim();
        }
        if (!string.IsNullOrWhiteSpace(_currentProjectPath))
        {
            return Path.GetFileNameWithoutExtension(_currentProjectPath);
        }
        if (_comicPages.Count == 1)
        {
            return Path.GetFileNameWithoutExtension(_comicPages[0].DisplayName);
        }
        return _comicPages.Count > 1 ? $"Cómic · {_comicPages.Count} páginas" : "Documento";
    }

    private static string ResolveDocumentToolTip(ComicDocumentSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.ProjectPath))
        {
            return session.ProjectPath;
        }
        if (session.Pages.Count == 1)
        {
            return session.Pages[0].SourcePath;
        }
        return $"{session.Pages.Count} páginas";
    }

    private void CleanupAllDocumentWorkspaces()
    {
        CaptureActiveDocumentState();
        foreach (string? workspace in _documentSessions
                     .Select(session => session.Workspace)
                     .Append(_comicWorkspace)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DeleteDocumentWorkspaceSafely(workspace);
        }
    }

    private static void DeleteDocumentWorkspaceSafely(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return;
        }

        string temporaryRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "TintaES")) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(workspace);
        string leaf = Path.GetFileName(target.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (!target.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !leaf.StartsWith("comic-", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.Delete(target, recursive: true);
        }
        catch
        {
            // Windows puede mantener un bitmap abierto unos instantes. El directorio queda
            // aislado bajo Temp y el sistema podrá limpiarlo más adelante.
        }
    }

    private sealed class ComicDocumentSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Title { get; set; } = "Documento";
        public string? Workspace { get; set; }
        public string? ProjectPath { get; set; }
        public int PageIndex { get; set; }
        public int VisiblePageIndex { get; set; } = -1;
        public List<ComicBookPageState> Pages { get; } = [];
        public HashSet<int> DirtyPages { get; } = [];
        public Dictionary<int, EditorPageHistory> EditorHistories { get; } = [];
        public HashSet<int> SelectedPageIndices { get; } = [];
        public HashSet<int> ExportedPageIndices { get; } = [];
        public int PageSelectionAnchorIndex { get; set; } = -1;
    }
}
