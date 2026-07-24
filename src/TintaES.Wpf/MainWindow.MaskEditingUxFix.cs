using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Corrige el flujo visual de las herramientas de máscara. Pincel y Borrador trabajan sobre la
/// máscara visible, no sobre el resultado traducido, y el control inferior se identifica como
/// diámetro del pincel.
/// </summary>
public partial class MainWindow
{
    private static readonly bool MaskEditingUxFixRegistered = RegisterMaskEditingUxFix();
    private bool _maskEditingUxFixInstalled;
    private bool _maskViewCorrectionPending;

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
        window.ToggleManualMaskToolInMaskView(requested);
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

        DependencyPropertyDescriptor? sourceDescriptor = DependencyPropertyDescriptor.FromProperty(
            Image.SourceProperty,
            typeof(Image));
        sourceDescriptor?.AddValueChanged(PageImage, PageImage_SourceChanged_KeepMaskView);
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

    private void ToggleManualMaskToolInMaskView(ManualMaskTool requested)
    {
        if (_maskEditorBusy || _originalBitmap is null)
        {
            return;
        }

        if (_manualMaskTool == requested)
        {
            LeaveManualMaskView();
            return;
        }

        if (requested == ManualMaskTool.Erase && _maskBitmap is null)
        {
            SetFooterStatus("Todavía no hay máscara que borrar. Usa primero el Pincel.", "#C99A35");
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

        ShowPreviewMode("mask");
        OverlayCanvas.Visibility = Visibility.Visible;
        SetMaskEditingRegionLayersVisible(false);
        OverlayCanvas.Cursor = System.Windows.Input.Cursors.Cross;
        UpdateManualMaskButtonState();

        SetFooterStatus(
            requested == ManualMaskTool.Paint
                ? $"Pincel activo · tamaño {Math.Round(CurrentMaskBrushSize)} px. Arrastra para añadir máscara."
                : $"Borrador activo · tamaño {Math.Round(CurrentMaskBrushSize)} px. Arrastra para recuperar el original.",
            "#4CB2BB");
    }

    private void LeaveManualMaskView()
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
        SetFooterStatus("Edición de máscara finalizada.", "#6C747A");
    }

    private void PageImage_SourceChanged_KeepMaskView(object? sender, EventArgs e)
    {
        if (_manualMaskTool == ManualMaskTool.None
            || _maskBitmap is null
            || ReferenceEquals(PageImage.Source, _maskBitmap)
            || _maskViewCorrectionPending)
        {
            return;
        }

        _maskViewCorrectionPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _maskViewCorrectionPending = false;
                if (_manualMaskTool == ManualMaskTool.None || _maskBitmap is null)
                {
                    return;
                }

                _previewMode = "mask";
                PageImage.Source = _maskBitmap;
                OverlayCanvas.Visibility = Visibility.Visible;
                SetMaskEditingRegionLayersVisible(false);
            },
            DispatcherPriority.Render);
    }

    private void SetMaskEditingRegionLayersVisible(bool visible)
    {
        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (layer.Tag is ComicRegion)
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
