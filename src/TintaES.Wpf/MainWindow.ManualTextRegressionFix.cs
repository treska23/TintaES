using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Unifica el editor lateral con el modelo manual. La implementación original del slider seguía
/// escribiendo FontScale (ajuste automático), aunque ComicRegion reserva ManualFontScale para los
/// cambios hechos por el usuario. También captura la composición anterior antes del primer Enter.
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

        // Garantiza que los handlers auxiliares existen antes de sustituir los dos que quedaron
        // conectados al comportamiento antiguo.
        InstallTextLayoutHooks();

        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged;
        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged_ManualLineLayout;
        FontScaleSlider.ValueChanged += FontScaleSlider_ValueChanged_FixedManual;

        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged_LineLayout;
        TranslationTextBox.TextChanged += TranslationTextBox_TextChanged_FixedManual;
        TranslationTextBox.GotKeyboardFocus += TranslationTextBox_GotKeyboardFocus_CaptureManualSeed;
        RegionListBox.SelectionChanged += RegionListBox_SelectionChanged_FixedManualScale;

        SynchronizeFixedManualScaleSlider();
    }

    private void TranslationTextBox_GotKeyboardFocus_CaptureManualSeed(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        ComicRegion? region = _selectedRegion;
        if (region is null || region.IsManual)
        {
            return;
        }

        region.ManualLayoutSeedText = _automaticLinePreviews.TryGetValue(region.Id, out string? preview)
            && !string.IsNullOrWhiteSpace(preview)
                ? preview
                : region.Translation;
    }

    private void TranslationTextBox_TextChanged_FixedManual(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null || _selectedRegion.Type == "sfx")
        {
            return;
        }

        ComicRegion region = _selectedRegion;
        if (!region.IsManual)
        {
            // GotKeyboardFocus se ejecuta antes de que el TextBox cambie. El fallback solo cubre
            // modificaciones programáticas donde no hubo foco de teclado.
            if (string.IsNullOrWhiteSpace(region.ManualLayoutSeedText))
            {
                region.ManualLayoutSeedText = _automaticLinePreviews.TryGetValue(region.Id, out string? preview)
                    && !string.IsNullOrWhiteSpace(preview)
                        ? preview
                        : region.Translation;
            }

            region.ManualBaseFontSize = 0;
            region.ManualFontScale = 1;
            region.IsManual = true;
            SynchronizeFixedManualScaleSlider();
        }

        region.Vertical = false;
        bool cleanupChanged = EnsureManualDialogueCleanup(region);
        region.NotifyVisualChange();
        RefreshManualLineVisual(region);
        QueueFastCanvasTextRefresh(forceLayout: false);

        if (cleanupChanged)
        {
            UpdateCleanedPreview();
        }
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
        double scale = FontScaleSlider.Value / 100;
        if (region.IsManual && region.Type != "sfx")
        {
            region.ManualFontScale = scale;
            bool cleanupChanged = EnsureManualDialogueCleanup(region);
            region.NotifyVisualChange();
            RefreshManualLineVisual(region);
            QueueFastCanvasTextRefresh(forceLayout: false);

            if (cleanupChanged)
            {
                UpdateCleanedPreview();
            }
            return;
        }

        region.FontScale = scale;
        region.NotifyVisualChange();
        RebuildOverlay();
        QueueFastCanvasTextRefresh(forceLayout: false);
    }

    private void RegionListBox_SelectionChanged_FixedManualScale(object sender, SelectionChangedEventArgs e) =>
        SynchronizeFixedManualScaleSlider();

    private void SynchronizeFixedManualScaleSlider()
    {
        ComicRegion? region = _selectedRegion;
        if (region is null || FontScaleSlider is null || FontScaleText is null)
        {
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
        if (!region.IsManual
            || region.Type == "sfx"
            || Math.Abs(region.ManualFontScale - 1) > 0.001
            || Math.Abs(region.FontScale - 1) < 0.001)
        {
            return;
        }

        double previousScale = Math.Clamp(region.FontScale, 0.25, 2.5);
        region.FontScale = 1;
        region.ManualFontScale = previousScale;

        // Las versiones defectuosas podían congelar la base después de introducir los saltos y,
        // por tanto, ya encogida. Cuando existe tamaño detectado recuperamos esa referencia.
        if (_originalBitmap is not null && region.Style.FontSize > 0)
        {
            region.ManualBaseFontSize = Math.Max(
                1.2,
                region.Style.FontSize / 1000 * _originalBitmap.PixelHeight);
        }
        else
        {
            region.ManualBaseFontSize = 0;
        }

        region.NotifyVisualChange();
    }

    private bool EnsureManualDialogueCleanup(ComicRegion region)
    {
        if (_maskBitmap is null
            || !string.Equals(region.CleanupMode, "none", StringComparison.OrdinalIgnoreCase)
            || region.Type is not ("dialogue" or "thought" or "narration" or "caption"))
        {
            return false;
        }

        // En estos tipos, dejar "No borrar" mientras existe una máscara solo superpone el español
        // al texto original. Al empezar a editar manualmente recuperamos la limpieza automática.
        region.CleanupMode = "auto";
        if (ReferenceEquals(region, _selectedRegion) && CleanupComboBox is not null)
        {
            _syncingEditor = true;
            try
            {
                CleanupComboBox.SelectedValue = "auto";
            }
            finally
            {
                _syncingEditor = false;
            }
        }
        return true;
    }
}
