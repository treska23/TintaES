using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Añade el análisis responsivo, las coordenadas y los microajustes. No sustituye los eventos
/// normales del XAML ni instala una segunda ruta de renderizado.
/// </summary>
public partial class MainWindow
{
    private readonly DialogueOnlyResultService _dialogueOnlyResultService = new();
    private bool _fastEditingHandlersInstalled;
    private bool _syncingPositionEditor;
    private TextBox? _positionXTextBox;
    private TextBox? _positionYTextBox;
    private RegionVisual? _activeMoveVisual;
    private Point _dragStartPointer;
    private double _dragStartOffsetX;
    private double _dragStartOffsetY;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        InstallFastEditingHandlers();
    }

    private void InstallFastEditingHandlers()
    {
        if (_fastEditingHandlersInstalled)
        {
            return;
        }

        _fastEditingHandlersInstalled = true;

        AnalyzeButton.Click -= AnalyzeButton_Click;
        AnalyzeButton.Click += AnalyzeButton_Click_Responsive;
        PreviewKeyDown += MainWindow_PreviewKeyDown_Fast;
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

    private async void AnalyzeButton_Click_Responsive(object sender, RoutedEventArgs e)
    {
        if (_originalBitmap is null
            || ModelComboBox.SelectedValue is not string model
            || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _analysisCancellation.Token;

        SetBusy(true);
        BusyTitleText.Text = "Localizando las letras…";
        BusyProgressBar.IsIndeterminate = false;
        BusyProgressBar.Value = 2;
        FooterProgressBar.IsIndeterminate = false;
        FooterProgressBar.Value = 2;
        FooterStatusText.Text = "Preparando CTD y LaMa…";
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            var progress = new Progress<AnalysisProgress>(value =>
            {
                BusyTitleText.Text = value.Message;
                BusyProgressBar.IsIndeterminate = false;
                BusyProgressBar.Value = value.Percentage;
                FooterProgressBar.IsIndeterminate = false;
                FooterProgressBar.Value = value.Percentage;
                FooterStatusText.Text = value.Message;
            });

            OrganicAnalysisResult organic = await _organicEngine.AnalyzeAsync(
                _sourcePath ?? throw new InvalidOperationException("No hay una página cargada."),
                progress,
                cancellationToken);

            BusyTitleText.Text = "Preparando todo el texto detectado…";
            FooterStatusText.Text = "Aplicando la limpieza solo sobre letras verificadas…";
            DialogueOnlyResult filtered = await Task.Run(
                () => _dialogueOnlyResultService.Build(
                    _originalBitmap,
                    organic.CleanedBitmap,
                    organic.MaskBitmap,
                    organic.Analysis.Regions,
                    includeAllDetectedText: true),
                cancellationToken);

            var analysis = new ComicAnalysis(organic.Analysis.SourceLanguage, filtered.Regions);
            if (analysis.Regions.Count > 0)
            {
                BusyTitleText.Text = $"Traduciendo {analysis.Regions.Count} textos con contexto…";
                BusyProgressBar.Value = 96;
                FooterProgressBar.Value = 96;
                FooterStatusText.Text = $"Traduciendo {analysis.Regions.Count} textos con {model}…";
                await _ollama.TranslateRegionsAsync(
                    analysis.Regions,
                    model,
                    cancellationToken,
                    progress);

                ComicRegion[] incomplete = analysis.Regions
                    .Where(region => region.IsEnabled && !region.HasRenderableTranslation)
                    .ToArray();
                if (incomplete.Length > 0)
                {
                    throw new InvalidOperationException(
                        $"La traducción no devolvió texto para {incomplete.Length} de " +
                        $"{analysis.Regions.Count} zonas. No se aplicará un resultado incompleto.");
                }
            }

            _cleanedBaseBitmap = filtered.CleanedBitmap;
            _cleanedBitmap = filtered.CleanedBitmap;
            _maskBitmap = filtered.MaskBitmap;
            MaskPreviewButton.IsEnabled = true;
            CleanPreviewButton.IsEnabled = true;
            ResultPreviewButton.IsEnabled = true;

            _regions.Clear();
            foreach (ComicRegion region in analysis.Regions)
            {
                region.FontScale = 1;
                region.ManualFontScale = 1;
                region.PropertyChanged += Region_PropertyChanged;
                _regions.Add(region);
            }

            LanguageText.Text = $"{analysis.SourceLanguage.ToUpperInvariant()} → ES";
            PageImage.Source = _cleanedBitmap;
            ShowPreviewMode("result");
            RebuildOverlay();
            FinalizeProgressiveOverlayTextLayout(finalPass: true);
            UpdateRegionCount();

            if (_regions.Count > 0)
            {
                RegionListBox.SelectedIndex = 0;
                string timing = organic.FromCache
                    ? "análisis recuperado de la caché"
                    : $"fondo reconstruido en {organic.ElapsedSeconds:0.#} s";
                SetFooterStatus($"Listo · {_regions.Count} textos · {timing}", "#58A77D");
            }
            else
            {
                SetFooterStatus("No se encontró texto legible. Puedes añadir una zona manual.", "#C99A35");
            }
        }
        catch (OperationCanceledException)
        {
            SetFooterStatus("Análisis cancelado.", "#C99A35");
        }
        catch (Exception exception)
        {
            SetFooterStatus("El análisis ha fallado. Consulta el mensaje de error.", "#EE594B");
            MessageBox.Show(
                this,
                $"No se pudo completar el análisis.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Firmas conservadas para que los antiguos instaladores puedan desconectarse sin volver a
    // introducir una segunda ruta. Ninguna se registra desde este archivo.
    private void ZoomSlider_ValueChanged_Fast(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ZoomSlider_ValueChanged(sender, e);

    private void RegionListBox_SelectionChanged_Fast(object sender, SelectionChangedEventArgs e) =>
        RegionListBox_SelectionChanged(sender, e);

    private void TranslationTextBox_TextChanged_Fast(object sender, TextChangedEventArgs e) =>
        TranslationTextBox_TextChanged(sender, e);

    private void FontScaleSlider_ValueChanged_Fast(
        object sender,
        RoutedPropertyChangedEventArgs<double> e) =>
        FontScaleSlider_ValueChanged(sender, e);

    private void RegionMoveThumb_DragStarted_Fast(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { Tag: RegionVisual visual })
        {
            return;
        }

        SelectRegionFromCanvas(visual.Region);
        _activeMoveVisual = visual;
        _dragStartPointer = Mouse.GetPosition(OverlayCanvas);
        _dragStartOffsetX = visual.Region.TextOffsetX;
        _dragStartOffsetY = visual.Region.TextOffsetY;
        Keyboard.Focus(this);
    }

    private void RegionMoveThumb_DragDelta_Fast(object sender, DragDeltaEventArgs e)
    {
        if (_originalBitmap is null
            || sender is not Thumb { Tag: RegionVisual visual }
            || _activeMoveVisual is null
            || !ReferenceEquals(_activeMoveVisual.Region, visual.Region))
        {
            return;
        }

        Point pointer = Mouse.GetPosition(OverlayCanvas);
        double deltaX = (pointer.X - _dragStartPointer.X) / _originalBitmap.PixelWidth * 1000;
        double deltaY = (pointer.Y - _dragStartPointer.Y) / _originalBitmap.PixelHeight * 1000;
        visual.Region.TextOffsetX = ClampOffsetX(visual.Region, _dragStartOffsetX + deltaX);
        visual.Region.TextOffsetY = ClampOffsetY(visual.Region, _dragStartOffsetY + deltaY);
        ApplyRegionPlacement(visual.Layer, visual.Text, visual.Region);
        SyncPositionEditor(visual.Region);
    }

    private void RegionMoveThumb_DragCompleted_Fast(object sender, DragCompletedEventArgs e)
    {
        if (sender is Thumb { Tag: RegionVisual visual })
        {
            SyncPositionEditor(visual.Region);
        }
        _activeMoveVisual = null;
    }

    private void MainWindow_PreviewKeyDown_Fast(object sender, KeyEventArgs e)
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
        text.RenderTransformOrigin = new Point(0.5, 0.5);
        text.RenderTransform = Transform.Identity;
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
