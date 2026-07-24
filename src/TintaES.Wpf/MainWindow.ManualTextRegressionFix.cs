using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene el control de escala completamente aislado de la máscara, el fondo limpio y el resto
/// de zonas. Al moverlo solo cambia ManualFontScale en la región seleccionada.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ManualTextRegressionFixRegistered = RegisterManualTextRegressionFix();

    private bool _manualTextRegressionFixInstalled;
    private Guid? _manualTextSeedRegionId;
    private string? _manualTextSeed;

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
        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged_FixedManual;
        FontScaleSlider.ValueChanged += FontScaleSlider_ValueChanged_FixedManual;
        FontScaleSlider.PreviewMouseLeftButtonDown += FontScaleSlider_PreviewMouseLeftButtonDown_Isolated;
        FontScaleSlider.PreviewKeyDown += FontScaleSlider_PreviewKeyDown_Isolated;

        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged;
        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged_LineLayout;
        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged_FixedManual;
        TranslationTextBox.TextChanged += TranslationTextBox_TextChanged_FixedManual;
        TranslationTextBox.GotKeyboardFocus += TranslationTextBox_GotKeyboardFocus_CaptureManualSeed;

        RegionListBox.SelectionChanged -= RegionListBox_SelectionChanged;
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
        // La escala tipográfica no forma parte del editor de máscara. Si este quedó activo,
        // restauramos primero el resultado y todas las capas de texto una sola vez.
        if (_manualMaskTool != ManualMaskTool.None)
        {
            LeaveManualMaskView();
            return;
        }

        if (!string.Equals(_previewMode, "result", StringComparison.Ordinal))
        {
            ShowPreviewMode("result");
        }

        OverlayCanvas.Visibility = Visibility.Visible;
        SetMaskEditingRegionLayersVisible(true);
    }

    private void TranslationTextBox_GotKeyboardFocus_CaptureManualSeed(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (_selectedRegion is null || _selectedRegion.IsManual)
        {
            _manualTextSeedRegionId = null;
            _manualTextSeed = null;
            return;
        }

        _manualTextSeedRegionId = _selectedRegion.Id;
        _manualTextSeed = _selectedRegion.Translation;
    }

    private void TranslationTextBox_TextChanged_FixedManual(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        ComicRegion region = _selectedRegion;
        string previous = region.Translation;
        region.Translation = TranslationTextBox.Text;

        if (region.Type == "sfx")
        {
            return;
        }

        if (!region.IsManual)
        {
            string seed = _manualTextSeedRegionId == region.Id
                ? _manualTextSeed ?? previous
                : previous;

            region.ManualLayoutSeedText = seed;
            region.ManualBaseFontSize = ResolveFontScaleBaseSize(region, seed);
            region.ManualFontScale = Math.Clamp(region.FontScale, 0.25, 2.5);
            region.FontScale = 1;
            region.IsManual = true;
            region.Vertical = false;
        }

        // Translation dispara PropertyChanged y el preview ligero repinta únicamente esta zona.
        // No se toca la máscara, no se regenera el fondo y no se reconstruye el overlay.
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

        PrepareResultViewForFontScale();

        ComicRegion region = _selectedRegion;
        double targetScale = Math.Clamp(FontScaleSlider.Value / 100, 0.25, 2.5);

        if (region.Type == "sfx")
        {
            region.FontScale = targetScale;
            return;
        }

        EnsureSelectedRegionHasFixedScaleLayout(region);
        region.ManualFontScale = targetScale;

        // Nada más. En particular: no CleanupMode, no UpdateCleanedPreview, no RebuildOverlay,
        // no ShowPreviewMode(mask) y ninguna modificación sobre las demás regiones.
    }

    private void EnsureSelectedRegionHasFixedScaleLayout(ComicRegion region)
    {
        if (region.IsManual)
        {
            if (region.ManualBaseFontSize <= 0)
            {
                region.ManualBaseFontSize = ResolveFontScaleBaseSize(region, region.DisplayText);
            }
            region.FontScale = 1;
            return;
        }

        string stableText = CreateCheapStableLineLayout(region);
        double previousScale = Math.Clamp(region.FontScale, 0.25, 2.5);

        region.ManualLayoutSeedText = stableText;
        region.ManualBaseFontSize = ResolveFontScaleBaseSize(region, stableText);
        region.FontScale = 1;
        region.ManualFontScale = previousScale;
        region.IsManual = true;
        region.Vertical = false;

        // Conservamos visualmente los saltos que tenía la composición automática. Esto solo ocurre
        // una vez, al pasar la zona a escala manual; después el fader no vuelve a cambiar líneas.
        if (!string.Equals(region.Translation, stableText, StringComparison.Ordinal))
        {
            _syncingEditor = true;
            try
            {
                int caret = Math.Min(TranslationTextBox.CaretIndex, stableText.Length);
                region.Translation = stableText;
                TranslationTextBox.Text = stableText;
                TranslationTextBox.CaretIndex = caret;
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
    }

    private string CreateCheapStableLineLayout(ComicRegion region)
    {
        string text = NormalizeFontScaleNewLines(region.Translation);
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\n') || _originalBitmap is null)
        {
            return text;
        }

        string[] words = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
        {
            return text;
        }

        double fontSize = ResolveFontScaleBaseSize(region, text)
            * Math.Clamp(region.FontScale, 0.25, 2.5);
        double maxWidth = Math.Max(8, region.RenderBox.Width / 1000 * _originalBitmap.PixelWidth * 0.9);
        Typeface typeface = CreateFontScaleTypeface(region);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var lines = new List<string>();
        var current = new List<string>();

        foreach (string word in words)
        {
            string candidate = current.Count == 0 ? word : $"{string.Join(' ', current)} {word}";
            if (current.Count > 0
                && MeasureFontScaleText(candidate, region, typeface, fontSize, pixelsPerDip) > maxWidth)
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

    private double ResolveFontScaleBaseSize(ComicRegion region, string text)
    {
        if (_originalBitmap is null)
        {
            return Math.Max(1.2, region.ManualBaseFontSize > 0 ? region.ManualBaseFontSize : 12);
        }

        if (region.Style.FontSize > 0)
        {
            return Math.Max(1.2, region.Style.FontSize / 1000 * _originalBitmap.PixelHeight);
        }

        if (region.ManualBaseFontSize > 0)
        {
            return region.ManualBaseFontSize;
        }

        double width = Math.Max(8, region.RenderBox.Width / 1000 * _originalBitmap.PixelWidth);
        double height = Math.Max(8, region.RenderBox.Height / 1000 * _originalBitmap.PixelHeight);
        string normalized = NormalizeFontScaleNewLines(text);
        string[] lines = normalized.Split('\n');
        int lineCount = Math.Max(1, lines.Length);
        int longest = Math.Max(1, lines.Select(line => line.Length).DefaultIfEmpty(1).Max());
        double lineHeightRatio = Math.Clamp(region.Style.LineHeightRatio, 0.82, 1.8);
        double byHeight = height * 0.78 / (lineCount * lineHeightRatio);
        double byWidth = width * 1.55 / Math.Max(4, longest);
        return Math.Clamp(Math.Min(byHeight, byWidth), 1.2, Math.Max(6, height * 0.9));
    }

    private static double MeasureFontScaleText(
        string text,
        ComicRegion region,
        Typeface typeface,
        double fontSize,
        double pixelsPerDip)
    {
        string measured = region.Style.Uppercase
            ? text.ToUpper(CultureInfo.GetCultureInfo("es-ES"))
            : text;
        return new FormattedText(
            measured,
            CultureInfo.GetCultureInfo("es-ES"),
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(1.2, fontSize),
            Brushes.Black,
            pixelsPerDip).WidthIncludingTrailingWhitespace;
    }

    private static Typeface CreateFontScaleTypeface(ComicRegion region)
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

    private static string NormalizeFontScaleNewLines(string? text) =>
        (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private void RegionListBox_SelectionChanged_FixedManualScale(object sender, SelectionChangedEventArgs e)
    {
        _selectedRegion = RegionListBox.SelectedItem as ComicRegion;
        ShowRegionEditor(_selectedRegion);
        SynchronizeFixedManualScaleSlider();
    }

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

        region.ManualFontScale = Math.Clamp(region.FontScale, 0.25, 2.5);
        region.FontScale = 1;
        if (region.ManualBaseFontSize <= 0)
        {
            region.ManualBaseFontSize = ResolveFontScaleBaseSize(region, region.DisplayText);
        }
    }
}