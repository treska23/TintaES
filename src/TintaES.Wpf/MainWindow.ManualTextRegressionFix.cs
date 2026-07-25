using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Edición tipográfica interactiva. Escribir y mover la escala solo modifican la región
/// seleccionada; no reconstruyen el lienzo, no recalculan la máscara y no regeneran imágenes.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ManualTextRegressionFixRegistered = RegisterManualTextRegressionFix();

    private bool _manualTextRegressionFixInstalled;

    private static bool RegisterManualTextRegressionFix()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ManualTextRegressionFixLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ManualTextRegressionFixLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallManualTextRegressionFix,
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void InstallManualTextRegressionFix()
    {
        if (_manualTextRegressionFixInstalled)
        {
            SynchronizeFixedManualScaleSlider();
            return;
        }

        _manualTextRegressionFixInstalled = true;
        InstallTextLayoutHooks();

        // Dejamos una única ruta para cada gesto. Las versiones anteriores mantenían varios
        // handlers superpuestos y cada punto del slider podía ejecutar tres comportamientos.
        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged;
        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged_Fast;
        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged_ManualLineLayout;
        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged_FixedManual;
        FontScaleSlider.ValueChanged += FontScaleSlider_ValueChanged_FixedManual;
        FontScaleSlider.PreviewMouseLeftButtonDown += FontScaleSlider_PreviewMouseLeftButtonDown_Isolated;
        FontScaleSlider.PreviewKeyDown += FontScaleSlider_PreviewKeyDown_Isolated;

        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged;
        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged_Fast;
        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged_LineLayout;
        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged_FixedManual;
        TranslationTextBox.TextChanged += TranslationTextBox_TextChanged_FixedManual;

        RegionListBox.SelectionChanged -= RegionListBox_SelectionChanged;
        RegionListBox.SelectionChanged -= RegionListBox_SelectionChanged_Fast;
        RegionListBox.SelectionChanged -= RegionListBox_SelectionChanged_LineLayout;
        RegionListBox.SelectionChanged -= RegionListBox_SelectionChanged_FixedManualScale;
        RegionListBox.SelectionChanged += RegionListBox_SelectionChanged_FixedManualScale;

        SynchronizeFixedManualScaleSlider();
    }

    private void FontScaleSlider_PreviewMouseLeftButtonDown_Isolated(object sender, MouseButtonEventArgs e) =>
        PrepareResultViewForFontScale();

    private void FontScaleSlider_PreviewKeyDown_Isolated(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown
            or Key.Home or Key.End)
        {
            PrepareResultViewForFontScale();
        }
    }

    private void PrepareResultViewForFontScale()
    {
        // Esta operación se ejecuta una sola vez al empezar el gesto, nunca en cada punto del slider.
        if (_manualMaskTool != ManualMaskTool.None)
        {
            LeaveManualMaskEditingOverPage();
        }
        else if (!string.Equals(_previewMode, "result", StringComparison.Ordinal))
        {
            ShowPreviewMode("result");
        }

        OverlayCanvas.Visibility = Visibility.Visible;
        SetMaskEditingRegionLayersVisible(true);
        RefreshSelectedTextFrame();
    }

    private void TranslationTextBox_TextChanged_FixedManual(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        ComicRegion region = _selectedRegion;
        region.Translation = TranslationTextBox.Text;
        if (region.Type != "sfx")
        {
            EnsureRegionUsesTextFrame(region);
        }

        // ComicRegion.PropertyChanged invalida únicamente el preview de esta región.
        // No RebuildOverlay, UpdateCleanedPreview ni guardado de PNG durante la escritura.
    }

    private void FontScaleSlider_ValueChanged_FixedManual(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontScaleText is null)
        {
            return;
        }

        FontScaleText.Text = $"{Math.Round(FontScaleSlider.Value)} %";
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        ComicRegion region = _selectedRegion;
        double targetScale = Math.Clamp(FontScaleSlider.Value / 100, 0.25, 2.5);
        if (region.Type == "sfx")
        {
            region.FontScale = targetScale;
            return;
        }

        EnsureRegionUsesTextFrame(region);
        region.ManualFontScale = targetScale;

        // Nada más: el ancho de la caja decide los saltos y la escala solo cambia el tamaño.
    }

    private void EnsureRegionUsesTextFrame(ComicRegion region)
    {
        if (region.IsManual)
        {
            if (region.ManualBaseFontSize <= 0)
            {
                region.ManualBaseFontSize = ResolveTextFrameBaseSize(region);
            }
            region.FontScale = 1;
            return;
        }

        region.ManualLayoutSeedText = region.Translation;
        region.ManualBaseFontSize = ResolveTextFrameBaseSize(region);
        region.ManualFontScale = Math.Clamp(region.FontScale, 0.25, 2.5);
        region.FontScale = 1;
        region.IsManual = true;
        region.Vertical = false;
        region.NotifyVisualChange();
    }

    private double ResolveTextFrameBaseSize(ComicRegion region)
    {
        if (region.ManualBaseFontSize > 0 && double.IsFinite(region.ManualBaseFontSize))
        {
            return region.ManualBaseFontSize;
        }

        if (_originalBitmap is null)
        {
            return 12;
        }

        if (region.Style.FontSize > 0 && double.IsFinite(region.Style.FontSize))
        {
            return Math.Max(1.2, region.Style.FontSize / 1000 * _originalBitmap.PixelHeight);
        }

        // Fallback constante respecto a la caja y al número de líneas detectado. No depende de la
        // longitud de la traducción, por lo que escribir o cambiar palabras no altera el tamaño base.
        double height = Math.Max(8, region.RenderBox.Height / 1000 * _originalBitmap.PixelHeight);
        int lines = Math.Max(1, region.Style.OriginalLineCount);
        double lineHeight = Math.Clamp(region.Style.LineHeightRatio, 0.82, 1.8);
        return Math.Clamp(height * 0.72 / (lines * lineHeight), 1.2, Math.Max(6, height * 0.8));
    }

    private void RegionListBox_SelectionChanged_FixedManualScale(object sender, SelectionChangedEventArgs e)
    {
        _selectedRegion = RegionListBox.SelectedItem as ComicRegion;
        ShowRegionEditor(_selectedRegion);
        SynchronizeFixedManualScaleSlider();
        RefreshSelectedTextFrame();
    }

    private void SynchronizeFixedManualScaleSlider()
    {
        ComicRegion? region = _selectedRegion;
        if (region is null || FontScaleSlider is null || FontScaleText is null)
        {
            RefreshSelectedTextFrame();
            return;
        }

        MigrateLegacyManualScale(region);
        double scale = region.IsManual && region.Type != "sfx"
            ? region.ManualFontScale
            : region.FontScale;

        _syncingEditor = true;
        try
        {
            FontScaleSlider.Value = Math.Clamp(
                scale * 100,
                FontScaleSlider.Minimum,
                FontScaleSlider.Maximum);
            FontScaleText.Text = $"{Math.Round(FontScaleSlider.Value)} %";
        }
        finally
        {
            _syncingEditor = false;
        }
    }

    private void MigrateLegacyManualScale(ComicRegion region)
    {
        if (!region.IsManual || region.Type == "sfx")
        {
            return;
        }

        if (Math.Abs(region.ManualFontScale - 1) <= 0.001
            && Math.Abs(region.FontScale - 1) > 0.001)
        {
            region.ManualFontScale = Math.Clamp(region.FontScale, 0.25, 2.5);
            region.FontScale = 1;
        }

        if (region.ManualBaseFontSize <= 0 || !double.IsFinite(region.ManualBaseFontSize))
        {
            region.ManualBaseFontSize = ResolveTextFrameBaseSize(region);
        }
    }
}
