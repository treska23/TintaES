using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Muestra y aplica un tamaño tipográfico numérico sin crear un editor visual alternativo.
/// El valor se guarda en la región y el renderizador canónico se invalida directamente.
/// </summary>
public partial class MainWindow
{
    private static readonly bool FontSizeNumberRegistered = RegisterFontSizeNumber();

    private bool _fontSizeNumberInstalled;
    private bool _syncingFontSizeNumber;
    private Guid? _fontSizeNumberRegionId;
    private TextBox? _fontSizeNumberTextBox;

    private static bool RegisterFontSizeNumber()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_FontSizeNumberLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_FontSizeNumberLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallFontSizeNumber,
                DispatcherPriority.Loaded);
        }
    }

    private void InstallFontSizeNumber()
    {
        if (_fontSizeNumberInstalled)
        {
            RefreshFontSizeNumber();
            return;
        }

        if (FontScaleSlider.Parent is not Panel host
            || FontScaleText.Parent is not FrameworkElement oldHeader)
        {
            return;
        }

        _fontSizeNumberInstalled = true;
        oldHeader.Visibility = Visibility.Collapsed;
        FontScaleSlider.Visibility = Visibility.Collapsed;
        FontScaleSlider.IsEnabled = false;
        FontScaleSlider.Focusable = false;

        var row = new Grid
        {
            Margin = new Thickness(0, 13, 0, 0)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "TAMAÑO (px)",
            VerticalAlignment = VerticalAlignment.Center
        };
        if (TryFindResource("LabelText") is Style labelStyle)
        {
            label.Style = labelStyle;
        }

        _fontSizeNumberTextBox = new TextBox
        {
            Width = 88,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            ToolTip = "Tamaño real de la fuente en píxeles. Pulsa Enter para aplicar."
        };
        _fontSizeNumberTextBox.PreviewTextInput += FontSizeNumberTextBox_PreviewTextInput;
        _fontSizeNumberTextBox.KeyDown += FontSizeNumberTextBox_KeyDown;
        _fontSizeNumberTextBox.LostKeyboardFocus += FontSizeNumberTextBox_LostKeyboardFocus;
        _fontSizeNumberTextBox.GotKeyboardFocus += FontSizeNumberTextBox_GotKeyboardFocus;
        DataObject.AddPastingHandler(_fontSizeNumberTextBox, FontSizeNumberTextBox_Pasting);

        Grid.SetColumn(label, 0);
        Grid.SetColumn(_fontSizeNumberTextBox, 1);
        row.Children.Add(label);
        row.Children.Add(_fontSizeNumberTextBox);

        int index = host.Children.IndexOf(oldHeader);
        host.Children.Insert(Math.Max(0, index), row);

        RegionListBox.SelectionChanged += RegionListBox_FontSizeNumberSelectionChanged;
        BusyOverlay.IsVisibleChanged += BusyOverlay_FontSizeNumberVisibilityChanged;
        RefreshFontSizeNumber();
    }

    private void RegionListBox_FontSizeNumberSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(RefreshFontSizeNumber, DispatcherPriority.DataBind);

    private void BusyOverlay_FontSizeNumberVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!BusyOverlay.IsVisible)
        {
            Dispatcher.BeginInvoke(RefreshFontSizeNumber, DispatcherPriority.ContextIdle);
        }
    }

    private void FontSizeNumberTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _fontSizeNumberTextBox?.SelectAll();

    private void FontSizeNumberTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitFontSizeNumber();

    private void FontSizeNumberTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitFontSizeNumber();
            Keyboard.Focus(this);
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Up or Key.Down)
            || _fontSizeNumberTextBox is null
            || !TryParseFontSize(_fontSizeNumberTextBox.Text, out double current))
        {
            return;
        }

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        _fontSizeNumberTextBox.Text = FormatFontSize(Math.Clamp(
            current + (e.Key == Key.Up ? step : -step),
            1,
            500));
        CommitFontSizeNumber();
        _fontSizeNumberTextBox.SelectAll();
        e.Handled = true;
    }

    private static void FontSizeNumberTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character =>
            !char.IsDigit(character)
            && character is not ','
            && character is not '.');
    }

    private static void FontSizeNumberTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text)
            || e.DataObject.GetData(DataFormats.Text) is not string text
            || !TryParseFontSize(text, out _))
        {
            e.CancelCommand();
        }
    }

    private void RefreshFontSizeNumber()
    {
        if (_fontSizeNumberTextBox is null)
        {
            return;
        }

        _syncingFontSizeNumber = true;
        try
        {
            _fontSizeNumberRegionId = _selectedRegion?.Id;
            _fontSizeNumberTextBox.IsEnabled = _selectedRegion is not null;
            _fontSizeNumberTextBox.Text = _selectedRegion is null
                ? string.Empty
                : FormatFontSize(GetDisplayedFontSize(_selectedRegion));
        }
        finally
        {
            _syncingFontSizeNumber = false;
        }
    }

    private double GetDisplayedFontSize(ComicRegion region)
    {
        if (region.IsManual
            && double.IsFinite(region.ManualBaseFontSize)
            && region.ManualBaseFontSize >= 1.2)
        {
            return region.ManualBaseFontSize * Math.Clamp(region.ManualFontScale, 0.25, 2.5);
        }

        if (_originalBitmap is not null && region.Style.FontSize > 0)
        {
            double scale = region.IsManual && region.Type != "sfx"
                ? region.ManualFontScale
                : region.FontScale;
            return Math.Max(
                1.2,
                region.Style.FontSize / 1000 * _originalBitmap.PixelHeight
                * Math.Clamp(scale, 0.25, 2.5));
        }

        if (_originalBitmap is not null)
        {
            double height = Math.Max(
                2,
                region.RenderBox.Height / 1000 * _originalBitmap.PixelHeight);
            int lines = Math.Max(1, region.Style.OriginalLineCount);
            return Math.Max(1.2, height * 0.72 / lines);
        }

        return 12;
    }

    private void CommitFontSizeNumber()
    {
        if (_syncingFontSizeNumber
            || _syncingEditor
            || _fontSizeNumberTextBox is null
            || _selectedRegion is null
            || _fontSizeNumberRegionId != _selectedRegion.Id)
        {
            return;
        }

        if (!TryParseFontSize(_fontSizeNumberTextBox.Text, out double requested))
        {
            RefreshFontSizeNumber();
            return;
        }

        requested = Math.Round(Math.Clamp(requested, 1, 500), 1);
        double current = GetDisplayedFontSize(_selectedRegion);
        if (Math.Abs(current - requested) < 0.05)
        {
            _fontSizeNumberTextBox.Text = FormatFontSize(requested);
            return;
        }

        PushEditorUndoSnapshot();
        _selectedRegion.ManualBaseFontSize = requested;
        _selectedRegion.ManualFontScale = 1;
        _selectedRegion.FontScale = 1;
        _selectedRegion.IsManual = true;
        _selectedRegion.Vertical = false;
        _validatedNativeBaseSizes.Add(_selectedRegion.Id);
        _selectedRegion.NotifyVisualChange();

        bool previousSyncingEditor = _syncingEditor;
        _syncingEditor = true;
        try
        {
            FontScaleSlider.Value = 100;
            FontScaleText.Text = "100 %";
        }
        finally
        {
            _syncingEditor = previousSyncingEditor;
        }

        InvalidateRegionVisual(_selectedRegion);
        PersistVisibleComicPageRegions();

        _syncingFontSizeNumber = true;
        try
        {
            _fontSizeNumberTextBox.Text = FormatFontSize(requested);
        }
        finally
        {
            _syncingFontSizeNumber = false;
        }
    }

    private static bool TryParseFontSize(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
         || double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        && double.IsFinite(value)
        && value > 0;

    private static string FormatFontSize(double value) =>
        Math.Round(value, 1).ToString("0.#", CultureInfo.CurrentCulture);
}
