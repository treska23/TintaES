using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TintaES.Core;
using TintaES.Wpf.Controls;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

public partial class MainWindow : Window
{
    private readonly OllamaClient _ollama = new();
    private readonly OrganicEngineService _organicEngine = new();
    private readonly ImageProcessingService _processingService = new();
    private readonly ImageExportService _exportService = new();
    private readonly ObservableCollection<ComicRegion> _regions = [];
    private BitmapSource? _originalBitmap;
    private BitmapSource? _cleanedBaseBitmap;
    private BitmapSource? _cleanedBitmap;
    private BitmapSource? _maskBitmap;
    private string? _sourcePath;
    private ComicRegion? _selectedRegion;
    private CancellationTokenSource? _analysisCancellation;
    private bool _syncingEditor;
    private bool _suppressSelectionRebuild;
    private string _previewMode = "result";

    public MainWindow()
    {
        InitializeComponent();
        RegionListBox.ItemsSource = _regions;
        ConfigureEditorChoices();
        Loaded += MainWindow_Loaded;
    }

    private void ConfigureEditorChoices()
    {
        TypeComboBox.ItemsSource = new[]
        {
            new Choice("Diálogo", "dialogue"),
            new Choice("Pensamiento", "thought"),
            new Choice("Narración", "narration"),
            new Choice("Cartucho", "caption"),
            new Choice("Onomatopeya", "sfx"),
            new Choice("Letrero", "sign"),
            new Choice("Otro", "other")
        };
        CleanupComboBox.ItemsSource = new[]
        {
            new Choice("Automática", "auto"),
            new Choice("Color uniforme", "solid"),
            new Choice("Reconstruir textura", "texture"),
            new Choice("No borrar", "none")
        };
        FontCategoryComboBox.ItemsSource = new[]
        {
            new Choice("Cómic", "comic"),
            new Choice("Manual", "handwritten"),
            new Choice("Palo seco", "sans"),
            new Choice("Condensada", "condensed"),
            new Choice("Serifa", "serif"),
            new Choice("Impacto", "display"),
            new Choice("Monoespaciada", "monospace")
        };

        foreach (ComboBox comboBox in new[] { TypeComboBox, CleanupComboBox, FontCategoryComboBox })
        {
            comboBox.DisplayMemberPath = nameof(Choice.Label);
            comboBox.SelectedValuePath = nameof(Choice.Value);
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = WarmOrganicEngineAsync();
        await RefreshModelsAsync();
    }

    private async Task WarmOrganicEngineAsync()
    {
        try
        {
            await _organicEngine.WarmUpAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or TaskCanceledException)
        {
            // Analizar volverá a intentarlo y mostrará el error en contexto si el motor
            // sigue sin estar disponible. La precarga nunca bloquea la ventana.
        }
    }

    private async Task RefreshModelsAsync()
    {
        RefreshModelsButton.IsEnabled = false;
        SetOllamaStatus("Conectando con Ollama…", "#C99A35");
        try
        {
            IReadOnlyList<OllamaModel> models = await _ollama.GetModelsAsync();
            ModelComboBox.ItemsSource = models;
            ModelComboBox.DisplayMemberPath = nameof(OllamaModel.Name);
            ModelComboBox.SelectedValuePath = nameof(OllamaModel.Name);
            OllamaModel? preferred = models.FirstOrDefault(model => model.Name.Equals("translategemma:4b", StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault(model => model.Name.StartsWith("translategemma", StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault(model => model.Name.Equals("qwen3.5:9b", StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault(model => model.Name.Contains("qwen3.5", StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault();
            ModelComboBox.SelectedItem = preferred;

            if (preferred is null)
            {
                SetOllamaStatus("Ollama no tiene modelos instalados", "#EE594B");
            }
            else
            {
                SetOllamaStatus($"Ollama listo · {preferred.Name}", "#58A77D");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            ModelComboBox.ItemsSource = null;
            SetOllamaStatus("Ollama no está disponible", "#EE594B");
            SetFooterStatus("Abre Ollama para poder analizar páginas.", "#EE594B");
        }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
            UpdateActionAvailability();
        }
    }

    private void SetOllamaStatus(string message, string color)
    {
        OllamaStatusText.Text = message;
        OllamaStatusDot.Fill = BrushFrom(color);
    }

    private void SetFooterStatus(string message, string color = "#6C747A")
    {
        FooterStatusText.Text = message;
        FooterStatusDot.Fill = BrushFrom(color);
    }

    private static SolidColorBrush BrushFrom(string color)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void OpenImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir página de cómic",
            Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|Todos los archivos|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            LoadImage(dialog.FileName);
        }
    }

    private void LoadImage(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            _analysisCancellation?.Cancel();
            _originalBitmap = bitmap;
            _cleanedBaseBitmap = bitmap;
            _cleanedBitmap = bitmap;
            _maskBitmap = null;
            _sourcePath = path;
            _regions.Clear();
            _selectedRegion = null;
            PageImage.Source = bitmap;
            ImageStage.Width = bitmap.PixelWidth;
            ImageStage.Height = bitmap.PixelHeight;
            PageImage.Width = bitmap.PixelWidth;
            PageImage.Height = bitmap.PixelHeight;
            OverlayCanvas.Width = bitmap.PixelWidth;
            OverlayCanvas.Height = bitmap.PixelHeight;
            PageNameText.Text = Path.GetFileName(path);
            PageInfoText.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} px";
            LanguageText.Text = "— → ES";
            EmptyState.Visibility = Visibility.Collapsed;
            ImageScrollViewer.Visibility = Visibility.Visible;
            RegionListBox.SelectedItem = null;
            _previewMode = "result";
            OverlayCanvas.Visibility = Visibility.Visible;
            OriginalPreviewButton.IsEnabled = true;
            MaskPreviewButton.IsEnabled = false;
            CleanPreviewButton.IsEnabled = false;
            ResultPreviewButton.IsEnabled = false;
            ShowRegionEditor(null);
            RebuildOverlay();
            UpdateRegionCount();
            SetFooterStatus("Página cargada. Pulsa Analizar y traducir.", "#4CB2BB");
            UpdateActionAvailability();

            Dispatcher.BeginInvoke(FitImageToViewport, DispatcherPriority.Loaded);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"No se pudo abrir la imagen.\n\n{exception.Message}",
                "Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void FitImageToViewport()
    {
        if (_originalBitmap is null || ImageScrollViewer.ViewportWidth <= 0 || ImageScrollViewer.ViewportHeight <= 0)
        {
            return;
        }
        double availableWidth = Math.Max(100, ImageScrollViewer.ViewportWidth - 52);
        double availableHeight = Math.Max(100, ImageScrollViewer.ViewportHeight - 52);
        double scale = Math.Min(availableWidth / _originalBitmap.PixelWidth, availableHeight / _originalBitmap.PixelHeight);
        ZoomSlider.Value = Math.Clamp(scale * 100, ZoomSlider.Minimum, Math.Min(100, ZoomSlider.Maximum));
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_originalBitmap is null || ModelComboBox.SelectedValue is not string model || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            BusyTitleText.Text = "Localizando las letras…";
            BusyProgressBar.IsIndeterminate = false;
            BusyProgressBar.Value = 2;
            FooterProgressBar.IsIndeterminate = false;
            FooterProgressBar.Value = 2;
            FooterStatusText.Text = "Preparando CTD y LaMa…";

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
                _analysisCancellation.Token);
            ComicAnalysis analysis = organic.Analysis;
            _cleanedBaseBitmap = organic.CleanedBitmap;
            _cleanedBitmap = organic.CleanedBitmap;
            _maskBitmap = organic.MaskBitmap;
            MaskPreviewButton.IsEnabled = true;
            CleanPreviewButton.IsEnabled = true;
            ResultPreviewButton.IsEnabled = true;
            ShowPreviewMode("result");

            _regions.Clear();
            foreach (ComicRegion region in analysis.Regions)
            {
                region.PropertyChanged += Region_PropertyChanged;
                _regions.Add(region);
            }
            LanguageText.Text = $"{analysis.SourceLanguage.ToUpperInvariant()} → ES";
            RebuildOverlay();
            UpdateRegionCount();

            if (analysis.Regions.Count > 0)
            {
                BusyTitleText.Text = $"Traduciendo {analysis.Regions.Count} textos de una vez…";
                BusyProgressBar.Value = 96;
                FooterProgressBar.Value = 96;
                FooterStatusText.Text = $"Traduciendo {analysis.Regions.Count} textos con {model}…";
                await _ollama.TranslateRegionsAsync(
                    analysis.Regions,
                    model,
                    _analysisCancellation.Token,
                    progress);
            }

            UpdateCleanedPreview();
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
                MessageBox.Show(
                    this,
                    "El OCR local no ha encontrado texto legible en esta página. Puedes añadir zonas manualmente.",
                    "Tinta ES",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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

    private void SetBusy(bool busy)
    {
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyProgressBar.IsIndeterminate = busy;
        BusyProgressBar.Value = 0;
        FooterProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        FooterProgressBar.IsIndeterminate = busy;
        FooterProgressBar.Value = 0;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        // Abrir cómic no sustituye el trabajo actual: crea una pestaña independiente.
        // Por eso permanece disponible incluso mientras el documento activo termina o cancela
        // una operación.
        OpenImageButton.IsEnabled = true;
        if (_openFolderButton is not null)
        {
            _openFolderButton.IsEnabled = true;
        }
        AnalyzeButton.IsEnabled = !busy && _originalBitmap is not null && ModelComboBox.SelectedItem is not null;
        AddRegionButton.IsEnabled = !busy && _originalBitmap is not null;
        ExportButton.IsEnabled = !busy && _originalBitmap is not null;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _analysisCancellation?.Cancel();
    }

    private void UpdateActionAvailability()
    {
        bool hasImage = _originalBitmap is not null;
        bool hasModel = ModelComboBox.SelectedItem is not null;
        AnalyzeButton.IsEnabled = hasImage && hasModel && BusyOverlay.Visibility != Visibility.Visible;
        AddRegionButton.IsEnabled = hasImage && BusyOverlay.Visibility != Visibility.Visible;
        ExportButton.IsEnabled = hasImage && BusyOverlay.Visibility != Visibility.Visible;
    }

    private void UpdateCleanedPreview()
    {
        if (_originalBitmap is null)
        {
            return;
        }
        _cleanedBitmap = _processingService.CleanText(_cleanedBaseBitmap ?? _originalBitmap, _regions);
        if (_previewMode == "result")
        {
            PageImage.Source = _cleanedBitmap;
        }
        RebuildOverlay();
    }

    private void PreviewModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode })
        {
            ShowPreviewMode(mode);
        }
    }

    private void ShowPreviewMode(string mode)
    {
        if (_originalBitmap is null)
        {
            return;
        }

        _previewMode = mode;
        PageImage.Source = mode switch
        {
            "original" => _originalBitmap,
            "mask" when _maskBitmap is not null => _maskBitmap,
            "clean" when _cleanedBaseBitmap is not null => _cleanedBaseBitmap,
            _ => _cleanedBitmap ?? _cleanedBaseBitmap ?? _originalBitmap
        };
        OverlayCanvas.Visibility = mode == "result" ? Visibility.Visible : Visibility.Hidden;

        foreach (Button button in new[]
                 {
                     OriginalPreviewButton,
                     MaskPreviewButton,
                     CleanPreviewButton,
                     ResultPreviewButton
                 })
        {
            bool selected = string.Equals(button.Tag as string, mode, StringComparison.Ordinal);
            button.BorderBrush = selected ? BrushFrom("#EE594B") : BrushFrom("#42484E");
            button.Foreground = selected ? Brushes.White : BrushFrom("#B7BEC4");
        }
    }

    private void RebuildOverlay()
    {
        OverlayCanvas.Children.Clear();
        if (_originalBitmap is null)
        {
            return;
        }

        foreach (ComicRegion region in _regions.Where(region => region.IsEnabled))
        {
            AddRegionVisual(region);
        }
    }

    private void AddRegionVisual(ComicRegion region)
    {
        if (_originalBitmap is null)
        {
            return;
        }
        var layer = new Grid
        {
            Tag = region,
            ClipToBounds = true,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(region.Rotation)
        };
        var text = new ComicTextElement
        {
            Region = region,
            PageWidth = _originalBitmap.PixelWidth,
            PageHeight = _originalBitmap.PixelHeight,
            IsHitTestVisible = false
        };
        layer.Children.Add(text);

        var moveThumb = new Thumb
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Tag = new RegionVisual(region, layer, text)
        };
        moveThumb.DragStarted += RegionMoveThumb_DragStarted;
        moveThumb.DragDelta += RegionMoveThumb_DragDelta;
        moveThumb.DragCompleted += RegionThumb_DragCompleted;
        layer.Children.Add(moveThumb);

        var border = new Border
        {
            BorderBrush = region == _selectedRegion ? BrushFrom("#EE594B") : BrushFrom("#4CB2BB"),
            BorderThickness = new Thickness(Math.Max(2, 2 / CurrentZoom)),
            // El marco es una ayuda de edición, nunca una placa rectangular sobre el bocadillo.
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        layer.Children.Add(border);

        double handleSize = Math.Clamp(16 / CurrentZoom, 24, 72);
        var resizeThumb = new Thumb
        {
            Width = handleSize,
            Height = handleSize,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Background = region == _selectedRegion ? BrushFrom("#EE594B") : BrushFrom("#4CB2BB"),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(Math.Max(1, 1 / CurrentZoom)),
            Tag = new RegionVisual(region, layer, text)
        };
        resizeThumb.DragStarted += RegionResizeThumb_DragStarted;
        resizeThumb.DragDelta += RegionResizeThumb_DragDelta;
        resizeThumb.DragCompleted += RegionThumb_DragCompleted;
        layer.Children.Add(resizeThumb);

        PositionLayer(layer, text, region);
        OverlayCanvas.Children.Add(layer);
    }

    private void PositionLayer(Grid layer, ComicTextElement text, ComicRegion region)
    {
        if (_originalBitmap is null)
        {
            return;
        }
        NormalizedRect box = region.RenderBox;
        double width = box.Width / 1000 * _originalBitmap.PixelWidth;
        double height = box.Height / 1000 * _originalBitmap.PixelHeight;
        layer.Width = width;
        layer.Height = height;
        text.Width = width;
        text.Height = height;
        Canvas.SetLeft(layer, box.X / 1000 * _originalBitmap.PixelWidth);
        Canvas.SetTop(layer, box.Y / 1000 * _originalBitmap.PixelHeight);
        text.InvalidateVisual();
    }

    private void RegionMoveThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is Thumb { Tag: RegionVisual visual })
        {
            SelectRegionFromCanvas(visual.Region);
        }
    }

    private void RegionMoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_originalBitmap is null || sender is not Thumb { Tag: RegionVisual visual })
        {
            return;
        }
        double deltaX = e.HorizontalChange / _originalBitmap.PixelWidth * 1000;
        double deltaY = e.VerticalChange / _originalBitmap.PixelHeight * 1000;
        NormalizedRect old = visual.Region.RenderBox;
        var moved = new NormalizedRect(
            Math.Clamp(old.X + deltaX, 0, 1000 - old.Width),
            Math.Clamp(old.Y + deltaY, 0, 1000 - old.Height),
            old.Width,
            old.Height);
        visual.Region.RenderBox = moved;
        visual.Region.SafePolygon = visual.Region.SafePolygon
            .Select(point => new NormalizedPoint(point.X + deltaX, point.Y + deltaY))
            .ToArray();
        if (visual.Region.IsManual)
        {
            NormalizedRect text = visual.Region.TextBox;
            visual.Region.TextBox = new NormalizedRect(
                Math.Clamp(text.X + deltaX, 0, 1000 - text.Width),
                Math.Clamp(text.Y + deltaY, 0, 1000 - text.Height),
                text.Width,
                text.Height);
        }
        PositionLayer(visual.Layer, visual.Text, visual.Region);
    }

    private void RegionResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is Thumb { Tag: RegionVisual visual })
        {
            SelectRegionFromCanvas(visual.Region);
        }
    }

    private void RegionResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_originalBitmap is null || sender is not Thumb { Tag: RegionVisual visual })
        {
            return;
        }
        NormalizedRect old = visual.Region.RenderBox;
        double width = Math.Clamp(old.Width + e.HorizontalChange / _originalBitmap.PixelWidth * 1000, 12, 1000 - old.X);
        double height = Math.Clamp(old.Height + e.VerticalChange / _originalBitmap.PixelHeight * 1000, 12, 1000 - old.Y);
        visual.Region.RenderBox = new NormalizedRect(old.X, old.Y, width, height);
        visual.Region.SafePolygon = visual.Region.SafePolygon
            .Select(point => new NormalizedPoint(
                old.X + (point.X - old.X) * width / old.Width,
                old.Y + (point.Y - old.Y) * height / old.Height))
            .ToArray();
        if (visual.Region.IsManual)
        {
            visual.Region.TextBox = visual.Region.RenderBox;
        }
        PositionLayer(visual.Layer, visual.Text, visual.Region);
    }

    private void RegionThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Thumb { Tag: RegionVisual visual } && visual.Region.IsManual)
        {
            UpdateCleanedPreview();
        }
        else
        {
            RebuildOverlay();
        }
    }

    private void SelectRegionFromCanvas(ComicRegion region)
    {
        _selectedRegion = region;
        _suppressSelectionRebuild = true;
        RegionListBox.SelectedItem = region;
        _suppressSelectionRebuild = false;
        RegionListBox.ScrollIntoView(region);
        ShowRegionEditor(region);
    }

    private void RegionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRegion = RegionListBox.SelectedItem as ComicRegion;
        ShowRegionEditor(_selectedRegion);
        if (!_suppressSelectionRebuild)
        {
            RebuildOverlay();
        }
    }

    private void ShowRegionEditor(ComicRegion? region)
    {
        _syncingEditor = true;
        try
        {
            NoSelectionPanel.Visibility = region is null ? Visibility.Visible : Visibility.Collapsed;
            RegionEditorScroll.Visibility = region is null ? Visibility.Collapsed : Visibility.Visible;
            if (region is null)
            {
                return;
            }

            SelectedRegionTitle.Text = $"Zona {region.Order} · {TypeLabel(region.Type)}";
            RegionVisibleCheckBox.IsChecked = region.IsEnabled;
            OriginalTextBox.Text = region.Original;
            TranslationTextBox.Text = region.Translation;
            TypeComboBox.SelectedValue = region.Type;
            CleanupComboBox.SelectedValue = region.CleanupMode;
            FontCategoryComboBox.SelectedValue = region.Style.FontCategory;
            FontScaleSlider.Value = region.FontScale * 100;
            FontScaleText.Text = $"{Math.Round(region.FontScale * 100)} %";
            BoldCheckBox.IsChecked = region.Style.FontWeight >= 650;
            ItalicCheckBox.IsChecked = region.Style.Italic;
            UppercaseCheckBox.IsChecked = region.Style.Uppercase;
            TextColorTextBox.Text = region.Style.TextColor;
            BackgroundColorTextBox.Text = region.Style.BackgroundColor ?? string.Empty;
        }
        finally
        {
            _syncingEditor = false;
        }
    }

    private void TranslationTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }
        _selectedRegion.Translation = TranslationTextBox.Text;
        _selectedRegion.NotifyVisualChange();
        RebuildOverlay();
    }

    private void RegionVisualControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }
        _selectedRegion.IsEnabled = RegionVisibleCheckBox.IsChecked == true;
        _selectedRegion.Type = TypeComboBox.SelectedValue as string ?? _selectedRegion.Type;
        _selectedRegion.Style.FontCategory = FontCategoryComboBox.SelectedValue as string ?? _selectedRegion.Style.FontCategory;
        _selectedRegion.Style.FontWeight = BoldCheckBox.IsChecked == true ? 800 : 400;
        _selectedRegion.Style.Italic = ItalicCheckBox.IsChecked == true;
        _selectedRegion.Style.Uppercase = UppercaseCheckBox.IsChecked == true;
        _selectedRegion.Style.TextColor = string.IsNullOrWhiteSpace(TextColorTextBox.Text)
            ? "#111111"
            : TextColorTextBox.Text.Trim();
        SelectedRegionTitle.Text = $"Zona {_selectedRegion.Order} · {TypeLabel(_selectedRegion.Type)}";
        UpdateCleanedPreview();
    }

    private void CleanupComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }
        _selectedRegion.CleanupMode = CleanupComboBox.SelectedValue as string ?? "auto";
        UpdateCleanedPreview();
    }

    private void CleanupStyleTextBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }
        _selectedRegion.Style.BackgroundColor = string.IsNullOrWhiteSpace(BackgroundColorTextBox.Text)
            ? null
            : BackgroundColorTextBox.Text.Trim();
        UpdateCleanedPreview();
    }

    private void FontScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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
        _selectedRegion.FontScale = FontScaleSlider.Value / 100;
        RebuildOverlay();
    }

    private void AddRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_originalBitmap is null)
        {
            return;
        }
        var region = new ComicRegion
        {
            Order = _regions.Count + 1,
            Original = string.Empty,
            Translation = "Texto en español",
            Type = "dialogue",
            Confidence = 1,
            TextBox = new NormalizedRect(350, 420, 300, 110),
            RenderBox = new NormalizedRect(350, 420, 300, 110),
            CleanupMode = "auto",
            IsManual = true,
            Style = new ComicTextStyle()
        };
        region.PropertyChanged += Region_PropertyChanged;
        _regions.Add(region);
        UpdateRegionCount();
        RegionListBox.SelectedItem = region;
        UpdateCleanedPreview();
        TranslationTextBox.Focus();
        TranslationTextBox.SelectAll();
        SetFooterStatus("Zona manual añadida. Arrástrala sobre el texto omitido.", "#4CB2BB");
    }

    private void DeleteRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRegion is null)
        {
            return;
        }
        int index = _regions.IndexOf(_selectedRegion);
        _selectedRegion.PropertyChanged -= Region_PropertyChanged;
        _regions.Remove(_selectedRegion);
        _selectedRegion = null;
        UpdateRegionCount();
        UpdateCleanedPreview();
        RegionListBox.SelectedIndex = _regions.Count == 0 ? -1 : Math.Min(index, _regions.Count - 1);
    }

    private void Region_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComicRegion.Translation) or nameof(ComicRegion.Original))
        {
            RegionListBox.Items.Refresh();
        }

        if (sender is ComicRegion region && !region.IsManual)
        {
            InvalidateRegionVisual(region);
        }
    }

    private void UpdateRegionCount()
    {
        RegionCountText.Text = $"{_regions.Count} zona{(_regions.Count == 1 ? string.Empty : "s")}";
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanedBitmap is null)
        {
            return;
        }
        string suggested = Path.GetFileNameWithoutExtension(_sourcePath ?? "pagina") + "-es.png";
        var dialog = new SaveFileDialog
        {
            Title = "Exportar página traducida",
            FileName = suggested,
            DefaultExt = ".png",
            AddExtension = true,
            Filter =
                "PNG sin pérdida|*.png|" +
                "JPEG|*.jpg;*.jpeg|" +
                "WebP|*.webp|" +
                "TIFF|*.tif;*.tiff|" +
                "Bitmap de Windows|*.bmp|" +
                "Documento PDF|*.pdf"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ExportButton.IsEnabled = false;
            SetFooterStatus("Preparando la exportación…", "#4CB2BB");
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            BitmapSource result = await _exportService.RenderAsync(_cleanedBitmap, _regions);
            await Task.Run(() => _exportService.Save(result, dialog.FileName));
            SetFooterStatus($"Exportado: {Path.GetFileName(dialog.FileName)}", "#58A77D");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"No se pudo exportar la imagen.\n\n{exception.Message}", "Tinta ES", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExportButton.IsEnabled = _cleanedBitmap is not null;
        }
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageStage is null || ZoomText is null)
        {
            return;
        }
        double scale = ZoomSlider.Value / 100;
        ImageStage.LayoutTransform = new ScaleTransform(scale, scale);
        ZoomText.Text = $"{Math.Round(ZoomSlider.Value)} %";
        if (_regions.Count > 0)
        {
            RebuildOverlay();
        }
    }

    private double CurrentZoom => Math.Max(0.01, ZoomSlider.Value / 100);

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModelsAsync();
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            LoadImage(paths[0]);
        }
    }

    private static string TypeLabel(string type)
    {
        return type switch
        {
            "dialogue" => "Diálogo",
            "thought" => "Pensamiento",
            "narration" => "Narración",
            "caption" => "Cartucho",
            "sfx" => "Onomatopeya",
            "sign" => "Letrero",
            _ => "Otro"
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _ollama.Dispose();
        base.OnClosed(e);
    }

    private sealed record Choice(string Label, string Value);
    private sealed record RegionVisual(ComicRegion Region, Grid Layer, ComicTextElement Text);
}
