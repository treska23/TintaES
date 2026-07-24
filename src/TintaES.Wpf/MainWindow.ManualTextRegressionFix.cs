using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Unifica el editor lateral con el modelo manual. El preview ligero escucha directamente los
/// cambios de ComicRegion, por lo que escribir o mover el slider no debe reconstruir el overlay
/// ni despertar los renderizadores tipográficos precisos reservados para la exportación.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ManualTextRegressionFixRegistered = RegisterManualTextRegressionFix();
    private bool _manualTextRegressionFixInstalled;
    private bool _manualCleanupRefreshPending;

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

        // Garantiza que los handlers auxiliares existen antes de sustituir los que quedaron
        // conectados al comportamiento antiguo.
        InstallTextLayoutHooks();

        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged;
        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged_ManualLineLayout;
        FontScaleSlider.ValueChanged += FontScaleSlider_ValueChanged_FixedManual;

        // El handler XAML original llamaba RebuildOverlay en cada carácter. Lo retiramos junto al
        // auxiliar anterior y dejamos una única ruta que modifica el modelo; FastComicTextPreview
        // recibe PropertyChanged y repinta solo la zona afectada.
        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged;
        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged_LineLayout;
        TranslationTextBox.TextChanged += TranslationTextBox_TextChanged_FixedManual;
        TranslationTextBox.GotKeyboardFocus += TranslationTextBox_GotKeyboardFocus_CaptureManualSeed;

        // El handler auxiliar antiguo volvía a mostrar ManualComicTextElement al seleccionar una
        // zona. Ese renderer construye geometrías completas y hacía que el primer movimiento del
        // slider arrancase ya bloqueado. La selección usa exclusivamente el preview ligero.
        RegionListBox.SelectionChanged -= RegionListBox_SelectionChanged_LineLayout;
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
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        ComicRegion region = _selectedRegion;
        region.Translation = TranslationTextBox.Text;

        if (region.Type == "sfx")
        {
            return;
        }

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

        // No RefreshManualLineVisual, RebuildOverlay ni QueueFastCanvasTextRefresh: la vista rápida
        // está suscrita a PropertyChanged y WPF agrupa sus InvalidateVisual en el siguiente frame.
        if (cleanupChanged)
        {
            QueueManualCleanupRefresh();
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
            if (cleanupChanged)
            {
                QueueManualCleanupRefresh();
            }
            return;
        }

        region.FontScale = scale;

        // Cambiar FontScale/ManualFontScale ya provoca PropertyChanged. El renderer ligero repinta
        // únicamente este texto; no reconstruimos todas las cajas ni activamos el renderer final.
    }

    private void RegionListBox_SelectionChanged_FixedManualScale(object sender, SelectionChangedEventArgs e)
    {
        SynchronizeFixedManualScaleSlider();
        QueueFastCanvasTextRefresh(forceLayout: false);
    }

    private void SynchronizeFixedManualScaleSlider()
    {
        ComicRegion? region = _selectedRegion;
        if (region is null || FontScaleSlider is null || FontScaleText is null)
        {
            return;
        }

        MigrateLegacyManualScale(region);
        bool cleanupChanged = region.IsManual && EnsureManualDialogueCleanup(region);
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

        if (cleanupChanged)
        {
            QueueManualCleanupRefresh();
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

    private void QueueManualCleanupRefresh()
    {
        if (_manualCleanupRefreshPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _manualCleanupRefreshPending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _manualCleanupRefreshPending = false;
                if (_originalBitmap is not null && !_pageNavigationBusy && !_comicBatchBusy)
                {
                    UpdateCleanedPreview();
                    QueueFastCanvasTextRefresh(forceLayout: true);
                }
            },
            DispatcherPriority.ContextIdle);
    }
}
