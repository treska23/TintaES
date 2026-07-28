using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Deshacer y rehacer restauran primero el estado visible en memoria. Las capas de texto que no han
/// cambiado se conservan y no se vuelve a generar el PNG hasta que el usuario pulsa Guardar.
/// </summary>
public partial class MainWindow
{
    private static readonly bool FastUndoRegistered = RegisterFastUndo();

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
        MarkActiveDocumentDirty();
        SetFooterStatus("Cambio deshecho. Guarda la página cuando termines.", "#4CB2BB");
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
        MarkActiveDocumentDirty();
        SetFooterStatus("Cambio rehecho. Guarda la página cuando termines.", "#4CB2BB");
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
            bool structureChanged = !FastUndoStructureMatches(snapshot.Regions, _regions);
            if (structureChanged)
            {
                ReplaceAllUndoRegions(snapshot.Regions);
                RebuildOverlay();
                UpdateRegionCount();
            }
            else
            {
                ReplaceOnlyChangedUndoRegions(snapshot.Regions);
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

            RegionListBox.SelectedItem = _selectedRegion;
            ShowRegionEditor(_selectedRegion);
            QueueFastCanvasTextRefresh(forceLayout: false);
            SyncSelectedTextFrameChrome();

            if (_manualMaskTool != ManualMaskTool.None)
            {
                OverlayCanvas.Visibility = Visibility.Visible;
                SetMaskEditingRegionLayersVisible(false);
            }

            // No se comprime ni se escribe nada aquí. Guardar página es el único punto de escritura.
        }
        finally
        {
            _applyingEditorSnapshot = false;
            RefreshEditorToolAvailability();
            RefreshManualMaskAvailability();
            RefreshPageSaveAvailability();
        }
    }

    private void ReplaceAllUndoRegions(IReadOnlyList<ComicRegion> storedRegions)
    {
        foreach (ComicRegion region in _regions)
        {
            region.PropertyChanged -= Region_PropertyChanged;
        }
        _regions.Clear();
        foreach (ComicRegion stored in storedRegions)
        {
            ComicRegion region = CloneEditorRegion(stored);
            region.PropertyChanged += Region_PropertyChanged;
            _regions.Add(region);
        }
    }

    private void ReplaceOnlyChangedUndoRegions(IReadOnlyList<ComicRegion> storedRegions)
    {
        bool anyChanged = false;
        for (int index = 0; index < storedRegions.Count; index++)
        {
            ComicRegion current = _regions[index];
            ComicRegion stored = storedRegions[index];
            if (FastUndoRegionEqual(stored, current))
            {
                continue;
            }

            anyChanged = true;
            current.PropertyChanged -= Region_PropertyChanged;
            ComicRegion replacement = CloneEditorRegion(stored);
            replacement.PropertyChanged += Region_PropertyChanged;
            _regions[index] = replacement;

            Grid[] oldLayers = OverlayCanvas.Children
                .OfType<Grid>()
                .Where(layer => layer.Tag is ComicRegion tagged && tagged.Id == current.Id)
                .ToArray();
            foreach (Grid layer in oldLayers)
            {
                OverlayCanvas.Children.Remove(layer);
            }
            if (replacement.IsEnabled)
            {
                AddRegionVisual(replacement);
            }
        }

        if (anyChanged)
        {
            RegionListBox.Items.Refresh();
            UpdateRegionCount();
        }
    }

    private static bool FastUndoStructureMatches(
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
            if (stored[index].Id != currentArray[index].Id)
            {
                return false;
            }
        }
        return true;
    }

    private static bool FastUndoRegionEqual(ComicRegion left, ComicRegion right) =>
        left.Id == right.Id
        && left.Order == right.Order
        && left.Original == right.Original
        && left.Translation == right.Translation
        && left.Type == right.Type
        && left.IsEnabled == right.IsEnabled
        && left.CleanupMode == right.CleanupMode
        && left.TextBox == right.TextBox
        && left.RenderBox == right.RenderBox
        && left.Rotation == right.Rotation
        && left.Vertical == right.Vertical
        && left.FontScale == right.FontScale
        && left.ManualFontScale == right.ManualFontScale
        && left.TextOffsetX == right.TextOffsetX
        && left.TextOffsetY == right.TextOffsetY
        && left.IsManual == right.IsManual
        && left.ManualLayoutSeedText == right.ManualLayoutSeedText
        && left.ManualBaseFontSize == right.ManualBaseFontSize
        && left.SafePolygon.SequenceEqual(right.SafePolygon)
        && FastUndoStylesEqual(left.Style, right.Style);

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
}
