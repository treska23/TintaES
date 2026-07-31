using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Herramientas ligeras de posicionamiento del texto. No sustituye el análisis, la traducción,
/// la selección ni el renderizador canónico.
/// </summary>
public partial class MainWindow
{
    private bool _textPositionEditingInstalled;
    private bool _syncingPositionEditor;
    private TextBox? _positionXTextBox;
    private TextBox? _positionYTextBox;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        InstallTextPositionEditing();
    }

    private void InstallTextPositionEditing()
    {
        if (_textPositionEditingInstalled)
        {
            return;
        }

        _textPositionEditingInstalled = true;
        PreviewKeyDown += MainWindow_PreviewKeyDown_TextPosition;
        RegionListBox.SelectionChanged += RegionListBox_TextPositionSelectionChanged;
        InstallPositionEditors();

        FontScaleSlider.Minimum = 25;
        FontScaleSlider.Maximum = 250;
        Panel.SetZIndex(BusyOverlay, 10_000);
        BusyTitleText.TextWrapping = TextWrapping.Wrap;
        BusyTitleText.TextAlignment = TextAlignment.Center;
        if (BusyTitleText.Parent is StackPanel busyPanel)
        {
            busyPanel.Width = 560;
        }
    }

    private void InstallPositionEditors()
    {
        if (_positionXTextBox is not null
            || RegionEditorScroll.Content is not StackPanel editor)
        {
            return;
        }

        Brush muted = FindResource("MutedBrush") as Brush ?? Brushes.Gray;
        var container = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        container.Children.Add(new TextBlock
        {
            Text = "POSICIÓN DEL TEXTO",
            Foreground = muted,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        StackPanel xPanel = CreatePositionField("X (px)", out _positionXTextBox);
        StackPanel yPanel = CreatePositionField("Y (px)", out _positionYTextBox);
        Grid.SetColumn(xPanel, 0);
        Grid.SetColumn(yPanel, 2);
        grid.Children.Add(xPanel);
        grid.Children.Add(yPanel);
        container.Children.Add(grid);
        container.Children.Add(new TextBlock
        {
            Text = "Flechas: 1 px · Shift + flechas: 10 px",
            Foreground = muted,
            FontSize = 10,
            Margin = new Thickness(0, 6, 0, 0)
        });

        int deleteIndex = editor.Children.IndexOf(DeleteRegionButton);
        editor.Children.Insert(deleteIndex >= 0 ? deleteIndex : editor.Children.Count, container);
    }

    private StackPanel CreatePositionField(string label, out TextBox textBox)
    {
        Brush muted = FindResource("MutedBrush") as Brush ?? Brushes.Gray;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = muted,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 5)
        });

        textBox = new TextBox
        {
            ToolTip = "Coordenada del centro del texto sobre la página original"
        };
        textBox.LostKeyboardFocus += PositionTextBox_LostKeyboardFocus;
        textBox.KeyDown += PositionTextBox_KeyDown;
        panel.Children.Add(textBox);
        return panel;
    }

    // Firma temporal para que el instalador antiguo pueda desconectarla. No se registra ni
    // contiene una segunda ruta de análisis.
    private void AnalyzeButton_Click_Responsive(object sender, RoutedEventArgs e) =>
        AnalyzeButton_Click(sender, e);

    private void RegionListBox_TextPositionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedRegion is not null)
        {
            SyncPositionEditor(_selectedRegion);
        }
    }

    private void MainWindow_PreviewKeyDown_TextPosition(object sender, KeyEventArgs e)
    {
        if (_selectedRegion is null || _originalBitmap is null)
        {
            return;
        }

        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or Slider)
        {
            return;
        }

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        double dx = 0;
        double dy = 0;
        switch (e.Key)
        {
            case Key.Left:
                dx = -step;
                break;
            case Key.Right:
                dx = step;
                break;
            case Key.Up:
                dy = -step;
                break;
            case Key.Down:
                dy = step;
                break;
            default:
                return;
        }

        NudgeSelectedRegion(dx, dy);
        e.Handled = true;
    }

    private void NudgeSelectedRegion(double dxPixels, double dyPixels)
    {
        if (_selectedRegion is null || _originalBitmap is null)
        {
            return;
        }

        double dx = dxPixels / _originalBitmap.PixelWidth * 1000;
        double dy = dyPixels / _originalBitmap.PixelHeight * 1000;
        _selectedRegion.TextOffsetX = ClampOffsetX(_selectedRegion, _selectedRegion.TextOffsetX + dx);
        _selectedRegion.TextOffsetY = ClampOffsetY(_selectedRegion, _selectedRegion.TextOffsetY + dy);
        ApplyRegionPlacementToRegion(_selectedRegion);
        SyncPositionEditor(_selectedRegion);
    }

    private void PositionTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ApplyPositionEditorValues();

    private void PositionTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ApplyPositionEditorValues();
        Keyboard.Focus(this);
        e.Handled = true;
    }

    private void ApplyPositionEditorValues()
    {
        if (_syncingPositionEditor
            || _selectedRegion is null
            || _originalBitmap is null
            || _positionXTextBox is null
            || _positionYTextBox is null)
        {
            return;
        }

        if (!TryParseCoordinate(_positionXTextBox.Text, out double x)
            || !TryParseCoordinate(_positionYTextBox.Text, out double y))
        {
            SyncPositionEditor(_selectedRegion);
            return;
        }

        double targetX = Math.Clamp(x, 0, _originalBitmap.PixelWidth) / _originalBitmap.PixelWidth * 1000;
        double targetY = Math.Clamp(y, 0, _originalBitmap.PixelHeight) / _originalBitmap.PixelHeight * 1000;
        double baseCenterX = _selectedRegion.RenderBox.X + _selectedRegion.RenderBox.Width / 2;
        double baseCenterY = _selectedRegion.RenderBox.Y + _selectedRegion.RenderBox.Height / 2;

        _selectedRegion.TextOffsetX = ClampOffsetX(_selectedRegion, targetX - baseCenterX);
        _selectedRegion.TextOffsetY = ClampOffsetY(_selectedRegion, targetY - baseCenterY);
        ApplyRegionPlacementToRegion(_selectedRegion);
        SyncPositionEditor(_selectedRegion);
    }

    private static bool TryParseCoordinate(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void SyncPositionEditor(ComicRegion region)
    {
        if (_originalBitmap is null || _positionXTextBox is null || _positionYTextBox is null)
        {
            return;
        }

        double centerX = (region.RenderBox.X + region.RenderBox.Width / 2 + region.TextOffsetX)
            / 1000 * _originalBitmap.PixelWidth;
        double centerY = (region.RenderBox.Y + region.RenderBox.Height / 2 + region.TextOffsetY)
            / 1000 * _originalBitmap.PixelHeight;

        _syncingPositionEditor = true;
        try
        {
            _positionXTextBox.Text = Math.Round(centerX, 1).ToString("0.#", CultureInfo.CurrentCulture);
            _positionYTextBox.Text = Math.Round(centerY, 1).ToString("0.#", CultureInfo.CurrentCulture);
        }
        finally
        {
            _syncingPositionEditor = false;
        }
    }

    private static double ClampOffsetX(ComicRegion region, double offset) =>
        Math.Clamp(offset, -region.RenderBox.X, 1000 - region.RenderBox.Right);

    private static double ClampOffsetY(ComicRegion region, double offset) =>
        Math.Clamp(offset, -region.RenderBox.Y, 1000 - region.RenderBox.Bottom);

    private void ApplyRegionPlacementToRegion(ComicRegion region)
    {
        Grid? layer = OverlayCanvas.Children
            .OfType<Grid>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Tag, region));
        InteractiveComicTextElement? text = layer?.Children
            .OfType<InteractiveComicTextElement>()
            .FirstOrDefault();
        if (layer is not null && text is not null)
        {
            ApplyRegionPlacement(layer, text, region);
        }
    }

    private void ApplyRegionPlacement(Grid layer, FrameworkElement text, ComicRegion region)
    {
        if (_originalBitmap is null)
        {
            return;
        }

        NormalizedRect box = region.RenderBox;
        Canvas.SetLeft(layer, (box.X + region.TextOffsetX) / 1000 * _originalBitmap.PixelWidth);
        Canvas.SetTop(layer, (box.Y + region.TextOffsetY) / 1000 * _originalBitmap.PixelHeight);
        text.InvalidateVisual();
    }

    private void InvalidateRegionVisual(ComicRegion region)
    {
        Grid? layer = OverlayCanvas.Children
            .OfType<Grid>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Tag, region));
        InteractiveComicTextElement? text = layer?.Children
            .OfType<InteractiveComicTextElement>()
            .FirstOrDefault();
        if (layer is not null && text is not null)
        {
            ApplyRegionPlacement(layer, text, region);
        }
    }
}
