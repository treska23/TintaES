using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Deshacer y rehacer primero restauran el estado visible en memoria. La compresión de los PNG se
/// serializa después en segundo plano, de modo que el botón nunca parece quedarse sin responder.
/// </summary>
public partial class MainWindow
{
    private static readonly bool FastUndoRegistered = RegisterFastUndo();
    private bool _fastUndoSaveLoopRunning;
    private EditorUndoSaveRequest? _pendingEditorUndoSave;

    private static bool RegisterFastUndo()
    {
        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(FastUndoButton_ClickClassHandler),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(MainWindow_FastUndoPreviewKeyDown),
            handledEventsToo: true);
        return true;
    }

    private static void FastUndoButton_ClickClassHandler(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Window.GetWindow(button) is not MainWindow window)
        {
            return;
        }

        if (ReferenceEquals(button, window._undoEditorButton))
        {
            window.FastUndoEditorChange();
            e.Handled = true;
        }
        else if (ReferenceEquals(button, window._redoEditorButton))
        {
            window.FastRedoEditorChange();
            e.Handled = true;
        }
    }

    private static void MainWindow_FastUndoPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not MainWindow window || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        if (e.Key == Key.Z)
        {
            window.FastUndoEditorChange();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            window.FastRedoEditorChange();
            e.Handled = true;
        }
    }

    private void FastUndoEditorChange()
    {
        if (Keyboard.FocusedElement is TextBoxBase textBox && textBox.CanUndo)
        {
            textBox.Undo();
            SetFooterStatus("Cambio de texto deshecho.", "#4CB2BB");
            RefreshEditorToolAvailability();
            return;
        }

        if (_maskEditorBusy || _fastRegionDeletionBusy)
        {
            SetFooterStatus("Espera a que termine el trazo actual antes de deshacer.", "#C99A35");
            return;
        }

        EditorPageHistory? history = GetCurrentEditorHistory(create: false);
        if (history is null || history.Undo.Count == 0)
        {
            SetFooterStatus("No hay más cambios que deshacer.", "#6C747A");
            return;
        }

        history.Redo.Push(CaptureEditorSnapshot());
        EditorSnapshot target = history.Undo.Pop();
        ApplyEditorSnapshotImmediately(target);
        SetFooterStatus("Cambio deshecho. Guardando la página en segundo plano…", "#4CB2BB");
    }

    private void FastRedoEditorChange()
    {
        if (Keyboard.FocusedElement is TextBoxBase textBox && textBox.CanRedo)
        {
            textBox.Redo();
            SetFooterStatus("Cambio de texto rehecho.", "#4CB2BB");
            RefreshEditorToolAvailability();
            return;
        }

        if (_maskEditorBusy || _fastRegionDeletionBusy)
        {
            SetFooterStatus("Espera a que termine el trazo actual antes de rehacer.", "#C99A35");
            return;
        }

        EditorPageHistory? history = GetCurrentEditorHistory(create: false);
        if (history is null || history.Redo.Count == 0)
        {
            SetFooterStatus("No hay más cambios que rehacer.", "#6C747A");
            return;
        }

        history.Undo.Push(CaptureEditorSnapshot());
        EditorSnapshot target = history.Redo.Pop();
        ApplyEditorSnapshotImmediately(target);
        SetFooterStatus("Cambio rehecho. Guardando la página en segundo plano…", "#4CB2BB");
    }

    private void ApplyEditorSnapshotImmediately(EditorSnapshot snapshot)
    {
        if (_originalBitmap is null
            || _comicPageIndex < 0
            || _comicPageIndex >= _comicPages.Count)
        {
            return;
        }

        _applyingEditorSnapshot = true;
        try
        {
            bool regionsChanged = !FastUndoRegionsEqual(snapshot.Regions, _regions);
            if (regionsChanged)
            {
                foreach (ComicRegion region in _regions)
                {
                    region.PropertyChanged -= Region_PropertyChanged;
                }
                _regions.Clear();
                foreach (ComicRegion stored in snapshot.Regions)
                {
                    ComicRegion region = CloneEditorRegion(stored);
                    region.PropertyChanged += Region_PropertyChanged;
                    _regions.Add(region);
                }
            }

            _cleanedBaseBitmap = snapshot.CleanedBaseBitmap ?? _originalBitmap;
            _cleanedBitmap = snapshot.CleanedBitmap ?? _cleanedBaseBitmap;
            _maskBitmap = snapshot.MaskBitmap;

            ComicBookPageState page = _comicPages[_comicPageIndex];
            page.Processed = snapshot.Processed;
            page.Error = null;
            page.Regions.Clear();
            page.Regions.AddRange(_regions);
            PrepareFastDeletionPaths(_comicPageIndex, page, _maskBitmap is not null);
            UpdateFastDeletionBitmapCache(
                _comicPageIndex,
                page,
                _originalBitmap,
                _cleanedBaseBitmap ?? _originalBitmap,
                _maskBitmap);

            _selectedRegion = snapshot.SelectedRegionId is Guid selectedId
                ? _regions.FirstOrDefault(region => region.Id == selectedId)
                : null;

            PageImage.Source = _previewMode switch
            {
                "original" => _originalBitmap,
                "mask" when _maskBitmap is not null => _maskBitmap,
                _ => _cleanedBitmap ?? _cleanedBaseBitmap ?? _originalBitmap
            };
            MaskPreviewButton.IsEnabled = _maskBitmap is not null;
            CleanPreviewButton.IsEnabled = snapshot.Processed;
            ResultPreviewButton.IsEnabled = snapshot.Processed;

            if (regionsChanged)
            {
                RebuildOverlay();
                UpdateRegionCount();
            }
            else
            {
                QueueFastCanvasTextRefresh(forceLayout: false);
            }

            RegionListBox.SelectedItem = _selectedRegion;
            ShowRegionEditor(_selectedRegion);
            if (_manualMaskTool != ManualMaskTool.None)
            {
                OverlayCanvas.Visibility = Visibility.Visible;
                SetMaskEditingRegionLayersVisible(false);
            }

            QueueEditorUndoSnapshotSave(
                page,
                _cleanedBaseBitmap ?? _originalBitmap,
                _maskBitmap,
                _comicPageIndex);
        }
        finally
        {
            _applyingEditorSnapshot = false;
            RefreshEditorToolAvailability();
            RefreshManualMaskAvailability();
            RefreshPageSaveAvailability();
        }
    }

    private void QueueEditorUndoSnapshotSave(
        ComicBookPageState page,
        BitmapSource cleaned,
        BitmapSource? mask,
        int pageIndex)
    {
        _pendingEditorUndoSave = new EditorUndoSaveRequest(page, cleaned, mask, pageIndex);
        if (!_fastUndoSaveLoopRunning)
        {
            _ = RunEditorUndoSaveLoopAsync();
        }
    }

    private async Task RunEditorUndoSaveLoopAsync()
    {
        _fastUndoSaveLoopRunning = true;
        try
        {
            while (_pendingEditorUndoSave is EditorUndoSaveRequest request)
            {
                _pendingEditorUndoSave = null;
                try
                {
                    await SaveFastDeletionBitmapsAsync(request.Page, request.Cleaned, request.Mask);
                }
                catch (Exception exception)
                {
                    if (request.PageIndex == _comicPageIndex)
                    {
                        SetFooterStatus($"El cambio está aplicado, pero no se pudo guardar: {exception.Message}", "#EE594B");
                    }
                }
            }

            SetFooterStatus("Cambio aplicado y guardado.", "#58A77D");
        }
        finally
        {
            _fastUndoSaveLoopRunning = false;
            if (_pendingEditorUndoSave is not null)
            {
                _ = RunEditorUndoSaveLoopAsync();
            }
        }
    }

    private static bool FastUndoRegionsEqual(
        IReadOnlyList<ComicRegion> stored,
        IReadOnlyCollection<ComicRegion> current)
    {
        if (stored.Count != current.Count)
        {
            return false;
        }

        ComicRegion[] currentArray = current.ToArray();
        for (int index = 0; index < stored.Count; index++)
        {
            ComicRegion left = stored[index];
            ComicRegion right = currentArray[index];
            if (left.Id != right.Id
                || left.Order != right.Order
                || left.Original != right.Original
                || left.Translation != right.Translation
                || left.Type != right.Type
                || left.IsEnabled != right.IsEnabled
                || left.CleanupMode != right.CleanupMode
                || left.TextBox != right.TextBox
                || left.RenderBox != right.RenderBox
                || left.Rotation != right.Rotation
                || left.Vertical != right.Vertical
                || left.FontScale != right.FontScale
                || left.ManualFontScale != right.ManualFontScale
                || left.TextOffsetX != right.TextOffsetX
                || left.TextOffsetY != right.TextOffsetY
                || left.IsManual != right.IsManual
                || left.ManualLayoutSeedText != right.ManualLayoutSeedText
                || left.ManualBaseFontSize != right.ManualBaseFontSize
                || !left.SafePolygon.SequenceEqual(right.SafePolygon)
                || !FastUndoStylesEqual(left.Style, right.Style))
            {
                return false;
            }
        }
        return true;
    }

    private static bool FastUndoStylesEqual(ComicTextStyle left, ComicTextStyle right) =>
        left.FontCategory == right.FontCategory
        && left.FontFamily == right.FontFamily
        && left.FontWeight == right.FontWeight
        && left.FontSize == right.FontSize
        && left.FontWidthRatio == right.FontWidthRatio
        && left.LineHeightRatio == right.LineHeightRatio
        && left.OriginalLineCount == right.OriginalLineCount
        && left.Italic == right.Italic
        && left.Uppercase == right.Uppercase
        && left.TextColor == right.TextColor
        && left.OutlineColor == right.OutlineColor
        && left.OutlineWidth == right.OutlineWidth
        && left.Alignment == right.Alignment
        && left.BackgroundColor == right.BackgroundColor
        && left.Shadow == right.Shadow;

    private sealed record EditorUndoSaveRequest(
        ComicBookPageState Page,
        BitmapSource Cleaned,
        BitmapSource? Mask,
        int PageIndex);
}
