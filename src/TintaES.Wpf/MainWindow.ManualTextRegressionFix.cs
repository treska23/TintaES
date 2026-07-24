using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Unifica el editor lateral con el modelo manual. El control de escala es un multiplicador puro:
/// al tocarlo se congelan las líneas visibles y nunca se vuelve a ajustar el texto a la máscara,
/// al rectángulo ni al polígono del bocadillo.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ManualTextRegressionFixRegistered = RegisterManualTextRegressionFix();
    private readonly HashSet<Guid> _pureManualScaleBaselines = [];
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
        InstallTextLayoutHooks();

        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged;
        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged_ManualLineLayout;
        FontScaleSlider.ValueChanged += FontScaleSlider_ValueChanged_FixedManual;

        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged;
        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged_LineLayout;
        TranslationTextBox.TextChanged += TranslationTextBox_TextChanged_FixedManual;
        TranslationTextBox.GotKeyboardFocus += TranslationTextBox_GotKeyboardFocus_CaptureManualSeed;

        RegionListBox.SelectionChanged -= RegionListBox_SelectionChanged;
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
            if (string.IsNullOrWhiteSpace(region.ManualLayoutSeedText))
            {
                region.ManualLayoutSeedText = _automaticLinePreviews.TryGetValue(region.Id, out string? preview)
                    && !string.IsNullOrWhiteSpace(preview)
                        ? preview
                        : region.Translation;
            }

            region.ManualBaseFontSize = PureScaleReferenceSize(region);
            region.ManualFontScale = 1;
            region.FontScale = 1;
            region.IsManual = true;
            _pureManualScaleBaselines.Add(region.Id);
            SynchronizeFixedManualScaleSlider();
        }

        region.Vertical = false;
        bool cleanupChanged = EnsureManualDialogueCleanup(region);
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

        double targetScale = FontScaleSlider.Value / 100;
        FontScaleText.Text = $"{Math.Round(FontScaleSlider.Value)} %";
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        ComicRegion region = _selectedRegion;
        if (region.Type != "sfx")
        {
            if (!region.IsManual)
            {
                FreezeRegionForPureScale(region, targetScale);
            }
            else
            {
                EnsurePureScaleBaseline(region);
                region.ManualFontScale = targetScale;
            }

            bool cleanupChanged = EnsureManualDialogueCleanup(region);
            if (cleanupChanged)
            {
                QueueManualCleanupRefresh();
            }
            return;
        }

        // Las onomatopeyas todavía usan su renderer especializado, pero no reconstruimos el lienzo.
        region.FontScale = targetScale;
    }

    private void FreezeRegionForPureScale(ComicRegion region, double targetScale)
    {
        double previousAutomaticScale = Math.Clamp(region.FontScale, 0.25, 2.5);
        double referenceSize = PureScaleReferenceSize(region);
        string stableText = PureScaleStableLines(region, referenceSize * previousAutomaticScale);

        region.ManualLayoutSeedText = stableText;
        region.ManualBaseFontSize = referenceSize;
        region.FontScale = 1;
        region.ManualFontScale = targetScale;
        region.IsManual = true;
        region.Vertical = false;
        _pureManualScaleBaselines.Add(region.Id);

        if (!string.Equals(region.Translation, stableText, StringComparison.Ordinal))
        {
            _syncingEditor = true;
            try
            {
                region.Translation = stableText;
                if (ReferenceEquals(region, _selectedRegion))
                {
                    int caret = Math.Min(TranslationTextBox.CaretIndex, stableText.Length);
                    TranslationTextBox.Text = stableText;
                    TranslationTextBox.CaretIndex = caret;
                }
            }
            finally
            {
                _syncingEditor = false;
            }
        }
        else
        {
            region.NotifyVisualChange();
        }

        SetFooterStatus(
            "Escala manual activa: el control solo agranda o reduce; no vuelve a encajar el texto.",
            "#4CB2BB");
    }

    private void EnsurePureScaleBaseline(ComicRegion region)
    {
        if (!_pureManualScaleBaselines.Add(region.Id))
        {
            return;
        }

        // Las versiones anteriores podían guardar como base un tamaño ya encogido para caber en
        // la máscara. Al tocar de nuevo el control recuperamos una referencia tipográfica estable.
        region.ManualBaseFontSize = PureScaleReferenceSize(region);
        region.ManualLayoutSeedText ??= region.Translation;
        region.FontScale = 1;
        region.NotifyVisualChange();
    }

    private double PureScaleReferenceSize(ComicRegion region)
    {
        if (_originalBitmap is null)
        {
            return Math.Max(1.2, region.ManualBaseFontSize > 0 ? region.ManualBaseFontSize : 12);
        }

        if (region.Style.FontSize > 0)
        {
            return Math.Max(1.2, region.Style.FontSize / 1000 * _originalBitmap.PixelHeight);
        }

        double width = Math.Max(8, region.RenderBox.Width / 1000 * _originalBitmap.PixelWidth);
        double height = Math.Max(8, region.RenderBox.Height / 1000 * _originalBitmap.PixelHeight);
        string text = PureScaleNormalizeNewLines(region.DisplayText);
        int lineCount = Math.Max(1, text.Count(character => character == '\n') + 1);
        int longestLine = Math.Max(1, text.Split('\n').Select(line => line.Length).DefaultIfEmpty(1).Max());
        double lineHeightRatio = Math.Clamp(region.Style.LineHeightRatio, 0.82, 1.8);
        double byHeight = height * 0.82 / (lineCount * lineHeightRatio);
        double byWidth = width * 1.65 / Math.Max(4, longestLine);
        return Math.Clamp(Math.Min(byHeight, byWidth), 1.2, Math.Max(6, height * 0.9));
    }

    private string PureScaleStableLines(ComicRegion region, double currentFontSize)
    {
        string text = PureScaleNormalizeNewLines(region.Translation);
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\n'))
        {
            return text;
        }

        if (_automaticLinePreviews.TryGetValue(region.Id, out string? preview)
            && !string.IsNullOrWhiteSpace(preview))
        {
            return PureScaleNormalizeNewLines(preview);
        }

        if (_originalBitmap is null)
        {
            return text;
        }

        string[] words = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
        {
            return text;
        }

        double maximumWidth = Math.Max(8, region.RenderBox.Width / 1000 * _originalBitmap.PixelWidth * 0.9);
        Typeface typeface = PureScaleTypeface(region);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var lines = new List<string>();
        var current = new List<string>();

        foreach (string word in words)
        {
            string candidate = current.Count == 0 ? word : string.Join(' ', current) + " " + word;
            if (current.Count > 0
                && PureScaleMeasure(candidate, region, typeface, currentFontSize, pixelsPerDip) > maximumWidth)
            {
                lines.Add(string.Join(' ', current));
                current.Clear();
            }
            current.Add(word);
        }

        if (current.Count > 0)
        {
            lines.Add(string.Join(' ', current));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static double PureScaleMeasure(
        string text,
        ComicRegion region,
        Typeface typeface,
        double fontSize,
        double pixelsPerDip)
    {
        string measuredText = region.Style.Uppercase
            ? text.ToUpper(CultureInfo.GetCultureInfo("es-ES"))
            : text;
        var formatted = new FormattedText(
            measuredText,
            CultureInfo.GetCultureInfo("es-ES"),
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(1.2, fontSize),
            Brushes.Black,
            pixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static Typeface PureScaleTypeface(ComicRegion region)
    {
        string family = !string.IsNullOrWhiteSpace(region.Style.FontFamily)
            ? region.Style.FontFamily
            : region.Style.FontCategory switch
            {
                "comic" => "Comic Sans MS",
                "handwritten" => "Segoe Print",
                "condensed" => "Arial Narrow",
                "serif" => "Georgia",
                "display" => "Impact",
                "monospace" => "Consolas",
                _ => "Arial"
            };
        FontStyle style = region.Style.Italic ? FontStyles.Italic : FontStyles.Normal;
        FontWeight weight = FontWeight.FromOpenTypeWeight(Math.Clamp(region.Style.FontWeight, 100, 999));
        return new Typeface(new FontFamily(family), style, weight, FontStretches.Normal);
    }

    private static string PureScaleNormalizeNewLines(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private void RegionListBox_SelectionChanged_FixedManualScale(object sender, SelectionChangedEventArgs e)
    {
        _selectedRegion = RegionListBox.SelectedItem as ComicRegion;
        ShowRegionEditor(_selectedRegion);
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
        region.ManualBaseFontSize = PureScaleReferenceSize(region);
        _pureManualScaleBaselines.Add(region.Id);
    }

    private bool EnsureManualDialogueCleanup(ComicRegion region)
    {
        if (_maskBitmap is null
            || !string.Equals(region.CleanupMode, "none", StringComparison.OrdinalIgnoreCase)
            || region.Type is not ("dialogue" or "thought" or "narration" or "caption"))
        {
            return false;
        }

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
