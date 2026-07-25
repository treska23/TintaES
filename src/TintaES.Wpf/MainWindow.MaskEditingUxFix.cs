using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// El pincel y el borrador trabajan sobre la página visible. La máscara en blanco y negro sigue
/// disponible como vista de diagnóstico, pero no se fuerza durante la edición porque el usuario
/// necesita ver exactamente qué texto original está borrando o recuperando.
/// </summary>
public partial class MainWindow
{
    private static readonly bool MaskEditingUxFixRegistered = RegisterMaskEditingUxFix();
    private bool _maskEditingUxFixInstalled;

    private static bool RegisterMaskEditingUxFix()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_MaskEditingUxFixLoaded),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(MaskToolButton_ClickClassHandler),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_MaskEditingUxFixLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallMaskEditingUxFix,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private static void MaskToolButton_ClickClassHandler(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || Window.GetWindow(button) is not MainWindow window
            || (!ReferenceEquals(button, window._maskPaintButton)
                && !ReferenceEquals(button, window._maskEraseButton)))
        {
            return;
        }

        ManualMaskTool requested = ReferenceEquals(button, window._maskPaintButton)
            ? ManualMaskTool.Paint
            : ManualMaskTool.Erase;
        window.ToggleManualMaskToolOverPage(requested);
        e.Handled = true;
    }

    private void InstallMaskEditingUxFix()
    {
        if (_maskEditingUxFixInstalled)
        {
            ClarifyMaskBrushSizeControl();
            return;
        }

        _maskEditingUxFixInstalled = true;
        ClarifyMaskBrushSizeControl();
    }

    private void ClarifyMaskBrushSizeControl()
    {
        if (_maskBrushSizeSlider is null)
        {
            return;
        }

        _maskBrushSizeSlider.ToolTip = "Diámetro del trazo del pincel o borrador, en píxeles";
        _maskBrushSizeSlider.ValueChanged -= MaskBrushSizeSlider_ClarifiedValueChanged;
        _maskBrushSizeSlider.ValueChanged += MaskBrushSizeSlider_ClarifiedValueChanged;
        MaskBrushSizeSlider_ClarifiedValueChanged(_maskBrushSizeSlider, null!);

        if (_maskBrushSizeSlider.Parent is Panel parent
            && !parent.Children.OfType<TextBlock>().Any(text => Equals(text.Tag, "mask-brush-size-label")))
        {
            int index = parent.Children.IndexOf(_maskBrushSizeSlider);
            parent.Children.Insert(Math.Max(0, index), new TextBlock
            {
                Tag = "mask-brush-size-label",
                Text = "TAMAÑO DEL PINCEL · diámetro del trazo",
                Foreground = FindResource("MutedBrush") as Brush,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 11, 0, 0)
            });
        }
    }

    private void MaskBrushSizeSlider_ClarifiedValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_maskBrushSizeText is not null)
        {
            _maskBrushSizeText.Text = $"Tamaño: {Math.Round(CurrentMaskBrushSize)} px";
        }
    }

    private void ToggleManualMaskToolOverPage(ManualMaskTool requested)
    {
        if (_maskEditorBusy || _originalBitmap is null)
        {
            return;
        }

        if (_manualMaskTool == requested)
        {
            LeaveManualMaskEditingOverPage();
            return;
        }

        if (requested == ManualMaskTool.Erase && _maskBitmap is null)
        {
            SetFooterStatus("Todavía no hay nada que recuperar. Usa primero el Pincel.", "#C99A35");
            return;
        }

        if (_drawingRegion)
        {
            SetDrawingRegionMode(false);
        }

        CancelManualMaskStroke();
        _manualMaskTool = requested;
        _maskBitmap ??= CreateEmptyEditableMask();
        MaskPreviewButton.IsEnabled = true;

        // Se muestra el fondo que se está corrigiendo, no la máscara binaria. Las traducciones se
        // ocultan para que el texto inglés y el resultado del pincel queden totalmente visibles.
        _previewMode = "result";
        PageImage.Source = _cleanedBitmap ?? _cleanedBaseBitmap ?? _originalBitmap;
        OverlayCanvas.Visibility = Visibility.Visible;
        SetMaskEditingRegionLayersVisible(false);
        OverlayCanvas.Cursor = System.Windows.Input.Cursors.Cross;
        UpdateManualMaskButtonState();

        SetFooterStatus(
            requested == ManualMaskTool.Paint
                ? $"Pincel activo · {Math.Round(CurrentMaskBrushSize)} px. Pinta directamente sobre el texto original que quieras borrar."
                : $"Borrador activo · {Math.Round(CurrentMaskBrushSize)} px. Pinta para recuperar la imagen original.",
            "#4CB2BB");
    }

    private void LeaveManualMaskEditingOverPage()
    {
        CancelManualMaskStroke();
        _manualMaskTool = ManualMaskTool.None;
        OverlayCanvas.Cursor = _drawingRegion
            ? System.Windows.Input.Cursors.Cross
            : System.Windows.Input.Cursors.Arrow;
        SetMaskEditingRegionLayersVisible(true);
        ShowPreviewMode("result");
        UpdateManualMaskButtonState();
        QueueFastCanvasTextRefresh(forceLayout: false);
        SetFooterStatus("Edición del fondo finalizada.", "#6C747A");
    }

    private void SetMaskEditingRegionLayersVisible(bool visible)
    {
        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (layer.Tag is TintaES.Core.ComicRegion)
            {
                layer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private BitmapSource CreateEmptyEditableMask()
    {
        int width = Math.Max(1, _originalBitmap?.PixelWidth ?? 1);
        int height = Math.Max(1, _originalBitmap?.PixelHeight ?? 1);
        int stride = width;
        byte[] pixels = new byte[stride * height];
        BitmapSource mask = BitmapSource.Create(
            width,
            height,
            _originalBitmap?.DpiX > 0 ? _originalBitmap.DpiX : 96,
            _originalBitmap?.DpiY > 0 ? _originalBitmap.DpiY : 96,
            PixelFormats.Gray8,
            null,
            pixels,
            stride);
        mask.Freeze();
        return mask;
    }
}