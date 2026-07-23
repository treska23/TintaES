using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TintaES.Core;
using TintaES.Wpf.Controls;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene la edición interactiva ligera. Zoom, selección, escritura, escala y arrastre
/// actualizan únicamente lo imprescindible y no reconstruyen todas las geometrías.
/// </summary>
public partial class MainWindow
{
    private readonly DialogueOnlyResultService _dialogueOnlyResultService = new();
    private bool _fastEditingHandlersInstalled;

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

        ZoomSlider.ValueChanged -= ZoomSlider_ValueChanged;
        ZoomSlider.ValueChanged += ZoomSlider_ValueChanged_Fast;

        RegionListBox.SelectionChanged -= RegionListBox_SelectionChanged;
        RegionListBox.SelectionChanged += RegionListBox_SelectionChanged_Fast;

        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged;
        TranslationTextBox.TextChanged += TranslationTextBox_TextChanged_Fast;

        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged;
        FontScaleSlider.ValueChanged += FontScaleSlider_ValueChanged_Fast;
        FontScaleSlider.Minimum = 25;
        FontScaleSlider.Maximum = 250;

        AnalyzeButton.Click -= AnalyzeButton_Click;
        AnalyzeButton.Click += AnalyzeButton_Click_Responsive;
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

        // Entregamos un ciclo de render a WPF antes de arrancar el trabajo pesado.
        // Así el overlay y la barra de progreso aparecen al instante al pulsar el botón.
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

            BusyTitleText.Text = "Conservando solo el texto de bocadillos…";
            FooterStatusText.Text = "Restaurando onomatopeyas y textos exteriores…";
            DialogueOnlyResult filtered = await Task.Run(
                () => _dialogueOnlyResultService.Build(
                    _originalBitmap,
                    organic.CleanedBitmap,
                    organic.MaskBitmap,
                    organic.Analysis.Regions),
                cancellationToken);

            var analysis = new ComicAnalysis(organic.Analysis.SourceLanguage, filtered.Regions);

            if (analysis.Regions.Count > 0)
            {
                BusyTitleText.Text = $"Traduciendo {analysis.Regions.Count} bocadillos de una vez…";
                BusyProgressBar.Value = 96;
                FooterProgressBar.Value = 96;
                FooterStatusText.Text = $"Traduciendo {analysis.Regions.Count} bocadillos con {model}…";
                await _ollama.TranslateRegionsAsync(
                    analysis.Regions,
                    model,
                    cancellationToken);
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
                // El autoajuste interno parte siempre de 100 %. La escala manual se aplica
                // después como transformación visual y por eso responde inmediatamente.
                region.FontScale = 1;
                region.PropertyChanged += Region_PropertyChanged;
                _regions.Add(region);
            }

            LanguageText.Text = $"{analysis.SourceLanguage.ToUpperInvariant()} → ES";
            PageImage.Source = _cleanedBitmap;
            ShowPreviewMode("result");
            RebuildOverlay();
            UpdateRegionCount();

            if (_regions.Count > 0)
            {
                RegionListBox.SelectedIndex = 0;
                string timing = organic.FromCache
                    ? "análisis recuperado de la caché"
                    : $"fondo reconstruido en {organic.ElapsedSeconds:0.#} s";
                SetFooterStatus($"Listo · {_regions.Count} bocadillos · {timing}", "#58A77D");
            }
            else
            {
                SetFooterStatus("No se encontraron bocadillos de diálogo. El resto de textos se ha conservado sin cambios.", "#C99A35");
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

    private void ZoomSlider_ValueChanged_Fast(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageStage is null || ZoomText is null)
        {
            return;
        }

        double scale = ZoomSlider.Value / 100;
        ImageStage.LayoutTransform = new ScaleTransform(scale, scale);
        ZoomText.Text = $"{Math.Round(ZoomSlider.Value)} %";
    }

    private void RegionListBox_SelectionChanged_Fast(object sender, SelectionChangedEventArgs e)
    {
        _selectedRegion = RegionListBox.SelectedItem as ComicRegion;
        ShowRegionEditor(_selectedRegion);

        if (_selectedRegion is null)
        {
            return;
        }

        _syncingEditor = true;
        try
        {
            double percent = Math.Clamp(_selectedRegion.ManualFontScale * 100, FontScaleSlider.Minimum, FontScaleSlider.Maximum);
            FontScaleSlider.Value = percent;
            FontScaleText.Text = $"{Math.Round(percent)} %";
        }
        finally
        {
            _syncingEditor = false;
        }
    }

    private void TranslationTextBox_TextChanged_Fast(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        // FormattedText conserva los saltos de línea escritos por el usuario. No usamos
        // Vertical como bandera de edición porque eso cambiaba el algoritmo tipográfico.
        _selectedRegion.Translation = TranslationTextBox.Text;
        _selectedRegion.NotifyVisualChange();
        InvalidateRegionVisual(_selectedRegion);
    }

    private void FontScaleSlider_ValueChanged_Fast(object sender, RoutedPropertyChangedEventArgs<double> e)
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

        // Escala real: 50 % significa la mitad y 150 % significa una vez y media.
        // No se vuelve a ejecutar el autoajuste ni se reconstruye el overlay.
        _selectedRegion.ManualFontScale = FontScaleSlider.Value / 100;
        ApplyTextTransformToRegion(_selectedRegion);
    }

    private void RegionMoveThumb_DragStarted_Fast(object sender, DragStartedEventArgs e)
    {
        if (sender is Thumb { Tag: RegionVisual visual })
        {
            SelectRegionFromCanvas(visual.Region);
        }
    }

    private void RegionMoveThumb_DragDelta_Fast(object sender, DragDeltaEventArgs e)
    {
        if (_originalBitmap is null || sender is not Thumb { Tag: RegionVisual visual })
        {
            return;
        }

        double deltaX = e.HorizontalChange / _originalBitmap.PixelWidth * 1000;
        double deltaY = e.VerticalChange / _originalBitmap.PixelHeight * 1000;
        double limitX = Math.Max(10, visual.Region.RenderBox.Width * 0.48);
        double limitY = Math.Max(10, visual.Region.RenderBox.Height * 0.48);

        visual.Region.TextOffsetX = Math.Clamp(visual.Region.TextOffsetX + deltaX, -limitX, limitX);
        visual.Region.TextOffsetY = Math.Clamp(visual.Region.TextOffsetY + deltaY, -limitY, limitY);

        // Solo cambia una transformación GPU/barata. RenderBox, SafePolygon y el tamaño
        // de fuente permanecen intactos durante todo el arrastre.
        ApplyTextTransform(visual.Text, visual.Region);
    }

    private void RegionMoveThumb_DragCompleted_Fast(object sender, DragCompletedEventArgs e)
    {
        // Nada que reconstruir: el desplazamiento ya quedó guardado en ComicRegion.
    }

    private void ApplyTextTransformToRegion(ComicRegion region)
    {
        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (!ReferenceEquals(layer.Tag, region))
            {
                continue;
            }

            ComicTextElement? text = layer.Children.OfType<ComicTextElement>().FirstOrDefault();
            if (text is not null)
            {
                ApplyTextTransform(text, region);
            }
            break;
        }
    }

    private static void ApplyTextTransform(ComicTextElement text, ComicRegion region)
    {
        double scale = Math.Clamp(region.ManualFontScale, 0.25, 2.5);
        double offsetX = region.TextOffsetX / 1000 * text.PageWidth;
        double offsetY = region.TextOffsetY / 1000 * text.PageHeight;
        var transforms = new TransformGroup();
        transforms.Children.Add(new ScaleTransform(scale, scale));
        transforms.Children.Add(new TranslateTransform(offsetX, offsetY));
        text.RenderTransformOrigin = new Point(0.5, 0.5);
        text.RenderTransform = transforms;
    }

    private void InvalidateRegionVisual(ComicRegion region)
    {
        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (!ReferenceEquals(layer.Tag, region))
            {
                continue;
            }

            ComicTextElement? text = layer.Children.OfType<ComicTextElement>().FirstOrDefault();
            if (text is not null)
            {
                text.InvalidateVisual();
                ApplyTextTransform(text, region);
            }
            break;
        }
    }
}
