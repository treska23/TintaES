using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Barra contextual inspirada en la barra de opciones de los editores gráficos. La paleta flotante
/// conserva solo herramientas; el diámetro aparece fuera del lienzo cuando Pincel o Borrador están activos.
/// </summary>
public partial class MainWindow
{
    private static readonly bool ContextualBrushOptionsRegistered = RegisterContextualBrushOptions();

    private bool _contextualBrushOptionsInstalled;
    private TextBox? _brushDiameterTextBox;

    private static bool RegisterContextualBrushOptions()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_ContextualBrushOptionsLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_ContextualBrushOptionsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Dispatcher.BeginInvoke(
                window.InstallContextualBrushOptions,
                DispatcherPriority.ContextIdle);
        }
    }

    private void InstallContextualBrushOptions()
    {
        if (_contextualBrushOptionsInstalled)
        {
            SyncContextualBrushOptions();
            return;
        }

        TryInstallFloatingEditorPalette();
        if (!_floatingEditorPaletteInstalled
            || _maskBrushSizeSlider is null
            || OriginalPreviewButton.Parent is not StackPanel optionsHost)
        {
            Dispatcher.BeginInvoke(InstallContextualBrushOptions, DispatcherPriority.ContextIdle);
            return;
        }

        _contextualBrushOptionsInstalled = true;

        Panel? oldSliderParent = _maskBrushSizeSlider.Parent as Panel;
        oldSliderParent?.Children.Remove(_maskBrushSizeSlider);
        if (_maskBrushSizeText?.Parent is Panel oldTextParent)
        {
            oldTextParent.Children.Remove(_maskBrushSizeText);
        }

        CollapseOldMaskInspectorBlock(oldSliderParent);

        _maskBrushSizeSlider.Width = 128;
        _maskBrushSizeSlider.Margin = new Thickness(7, 0, 7, 0);
        _maskBrushSizeSlider.VerticalAlignment = VerticalAlignment.Center;
        _maskBrushSizeSlider.ToolTip = "Diámetro del pincel o borrador";

        _brushDiameterTextBox = new TextBox
        {
            Width = 58,
            Height = 27,
            TextAlignment = TextAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Diámetro en píxeles. Pulsa Enter para aplicar."
        };
        _brushDiameterTextBox.KeyDown += BrushDiameterTextBox_KeyDown;
        _brushDiameterTextBox.LostKeyboardFocus += (_, _) => CommitBrushDiameterText();
        _brushDiameterTextBox.GotKeyboardFocus += (_, _) => _brushDiameterTextBox.SelectAll();

        _maskBrushOptionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "Opciones de la herramienta activa"
        };
        _maskBrushOptionsPanel.Children.Add(new TextBlock
        {
            Text = "◯",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FindResource("MutedBrush") as Brush ?? Brushes.Gray,
            ToolTip = "Diámetro del trazo"
        });
        _maskBrushOptionsPanel.Children.Add(new TextBlock
        {
            Text = " Diámetro",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FindResource("MutedBrush") as Brush ?? Brushes.Gray
        });
        _maskBrushOptionsPanel.Children.Add(_maskBrushSizeSlider);
        _maskBrushOptionsPanel.Children.Add(_brushDiameterTextBox);
        _maskBrushOptionsPanel.Children.Add(new TextBlock
        {
            Text = " px",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FindResource("MutedBrush") as Brush ?? Brushes.Gray,
            Margin = new Thickness(3, 0, 0, 0)
        });

        int insertIndex = Math.Min(
            optionsHost.Children.Count,
            Math.Max(0, optionsHost.Children.IndexOf(ResultPreviewButton) + 1));
        optionsHost.Children.Insert(insertIndex, _maskBrushOptionsPanel);

        _maskBrushSizeSlider.ValueChanged += (_, _) => SyncBrushDiameterText();
        _maskPaintButton?.AddHandler(
            Button.ClickEvent,
            new RoutedEventHandler(ContextualBrushTool_Click),
            handledEventsToo: true);
        _maskEraseButton?.AddHandler(
            Button.ClickEvent,
            new RoutedEventHandler(ContextualBrushTool_Click),
            handledEventsToo: true);
        PreviewKeyDown += MainWindow_ContextualBrushOptionsPreviewKeyDown;

        SyncContextualBrushOptions();
    }

    private static void CollapseOldMaskInspectorBlock(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Border border)
            {
                border.Visibility = Visibility.Collapsed;
                return;
            }
        }
    }

    private void ContextualBrushTool_Click(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(SyncContextualBrushOptions, DispatcherPriority.Input);

    private void SyncContextualBrushOptions()
    {
        if (_maskBrushOptionsPanel is not null)
        {
            _maskBrushOptionsPanel.Visibility = _manualMaskTool == ManualMaskTool.None
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        SyncBrushDiameterText();
        ApplyCompactCanvasToolIcons();
    }

    private void SyncBrushDiameterText()
    {
        string value = Math.Round(CurrentMaskBrushSize)
            .ToString("0", CultureInfo.CurrentCulture);
        if (_brushDiameterTextBox is not null && !_brushDiameterTextBox.IsKeyboardFocused)
        {
            _brushDiameterTextBox.Text = value;
        }
        if (_maskBrushSizeText is not null)
        {
            _maskBrushSizeText.Text = value + " px";
        }
    }

    private void BrushDiameterTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }
        CommitBrushDiameterText();
        Keyboard.Focus(this);
        e.Handled = true;
    }

    private void CommitBrushDiameterText()
    {
        if (_brushDiameterTextBox is null || _maskBrushSizeSlider is null)
        {
            return;
        }

        bool parsed = double.TryParse(
            _brushDiameterTextBox.Text,
            NumberStyles.Float,
            CultureInfo.CurrentCulture,
            out double diameter)
            || double.TryParse(
                _brushDiameterTextBox.Text.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out diameter);
        if (!parsed || !double.IsFinite(diameter))
        {
            SyncBrushDiameterText();
            return;
        }

        _maskBrushSizeSlider.Value = Math.Clamp(
            diameter,
            _maskBrushSizeSlider.Minimum,
            _maskBrushSizeSlider.Maximum);
        SyncBrushDiameterText();
    }

    private void MainWindow_ContextualBrushOptionsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_manualMaskTool == ManualMaskTool.None
            || _maskBrushSizeSlider is null
            || Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }

        double delta = e.Key switch
        {
            Key.OemOpenBrackets => -4,
            Key.Oem6 => 4,
            _ => 0
        };
        if (delta == 0)
        {
            return;
        }

        _maskBrushSizeSlider.Value = Math.Clamp(
            _maskBrushSizeSlider.Value + delta,
            _maskBrushSizeSlider.Minimum,
            _maskBrushSizeSlider.Maximum);
        e.Handled = true;
    }
}
