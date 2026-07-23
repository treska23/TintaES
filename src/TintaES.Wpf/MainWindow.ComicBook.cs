using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Modo cómic multipágina. Mantiene las páginas y sus regiones en una sesión ligera,
/// guarda fondos/máscaras procesados en disco temporal y solo carga en memoria la página
/// que se está viendo o procesando.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] ComicImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    private readonly List<ComicBookPageState> _comicPages = [];
    private int _comicPageIndex = -1;
    private string? _comicTitle;
    private string? _comicWorkspace;
    private bool _comicBookHandlersInstalled;
    private bool _comicBatchBusy;

    private Button? _openFolderButton;
    private Button? _previousPageButton;
    private Button? _nextPageButton;
    private Button? _exportComicButton;
    private TextBlock? _pageCounterText;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Dispatcher.BeginInvoke(InstallComicBookHandlers, DispatcherPriority.Loaded);
    }

    private void InstallComicBookHandlers()
    {
        if (_comicBookHandlersInstalled || OpenImageButton is null)
        {
            return;
        }

        _comicBookHandlersInstalled = true;

        OpenImageButton.Click -= OpenImageButton_Click;
        OpenImageButton.Click += OpenComicFilesButton_Click;
        OpenImageButton.Content = "Abrir cómic";

        AnalyzeButton.Click -= AnalyzeButton_Click;
        AnalyzeButton.Click -= AnalyzeButton_Click_Responsive;
        AnalyzeButton.Click += AnalyzeComicButton_Click;

        InstallComicToolbarButtons();
        InstallComicNavigationButtons();
        Closed += (_, _) => CleanupComicWorkspace();
        UpdateComicControls();
    }

    private void InstallComicToolbarButtons()
    {
        Style? toolbarStyle = FindResource("ToolbarButton") as Style;

        if (OpenImageButton.Parent is StackPanel openPanel)
        {
            _openFolderButton = new Button
            {
                Content = "Abrir carpeta",
                Style = toolbarStyle,
                Margin = new Thickness(7, 0, 0, 0)
            };
            _openFolderButton.Click += OpenComicFolderButton_Click;
            int openIndex = openPanel.Children.IndexOf(OpenImageButton);
            openPanel.Children.Insert(Math.Min(openPanel.Children.Count, openIndex + 1), _openFolderButton);
        }

        if (ExportButton.Parent is StackPanel exportPanel)
        {
            ExportButton.Content = "Exportar PNG";
            _exportComicButton = new Button
            {
                Content = "Exportar CBZ",
                Style = toolbarStyle,
                Margin = new Thickness(0, 0, 7, 0),
                IsEnabled = false
            };
            _exportComicButton.Click += ExportComicButton_Click;
            int exportIndex = exportPanel.Children.IndexOf(ExportButton);
            exportPanel.Children.Insert(Math.Max(0, exportIndex), _exportComicButton);
        }
    }

    private void InstallComicNavigationButtons()
    {
        if (OriginalPreviewButton.Parent is not StackPanel previewPanel)
        {
            return;
        }

        Style? toolbarStyle = FindResource("ToolbarButton") as Style;
        _previousPageButton = new Button
        {
            Content = "‹",
            Width = 32,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            Style = toolbarStyle,
            ToolTip = "Página anterior"
        };
        _previousPageButton.Click += (_, _) => ShowComicPage(_comicPageIndex - 1);

        _pageCounterText = new TextBlock
        {
            Text = "— / —",
            Width = 68,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 4, 0)
        };

        _nextPageButton = new Button
        {
            Content = "›",
            Width = 32,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 10, 0),
            Style = toolbarStyle,
            ToolTip = "Página siguiente"
        };
        _nextPageButton.Click += (_, _) => ShowComicPage(_comicPageIndex + 1);

        previewPanel.Children.Insert(0, _previousPageButton);
        previewPanel.Children.Insert(1, _pageCounterText);
        previewPanel.Children.Insert(2, _nextPageButton);
    }

    private void OpenComicFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir cómic o páginas",
            Filter = "Cómic CBZ|*.cbz|Imágenes|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Todos los archivos|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            if (dialog.FileNames.Length == 1
                && string.Equals(Path.GetExtension(dialog.FileName), ".cbz", StringComparison.OrdinalIgnoreCase))
            {
                LoadComicFromCbz(dialog.FileName);
                return;
            }

            string[] images = dialog.FileNames
                .Where(IsSupportedComicImage)
                .OrderBy(path => path, NaturalPageComparer.Instance)
                .ToArray();
            if (images.Length == 0)
            {
                MessageBox.Show(this, "Selecciona un archivo CBZ o una o varias imágenes.", "Tinta ES",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string title = images.Length == 1
                ? Path.GetFileNameWithoutExtension(images[0])
                : new DirectoryInfo(Path.GetDirectoryName(images[0]) ?? string.Empty).Name;
            LoadComicSession(images, title);
        }
        catch (Exception exception)
        {
            ShowComicOpenError(exception);
        }
    }

    private void OpenComicFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Selecciona la carpeta con las páginas del cómic",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string[] images = Directory.EnumerateFiles(dialog.FolderName, "*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedComicImage)
                .OrderBy(path => path, NaturalPageComparer.Instance)
                .ToArray();
            if (images.Length == 0)
            {
                MessageBox.Show(this, "La carpeta no contiene imágenes compatibles.", "Tinta ES",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LoadComicSession(images, new DirectoryInfo(dialog.FolderName).Name);
        }
        catch (Exception exception)
        {
            ShowComicOpenError(exception);
        }
    }

    private void LoadComicFromCbz(string cbzPath)
    {
        PrepareNewComicWorkspace();
        string sourceDirectory = Path.Combine(_comicWorkspace!, "source");
        Directory.CreateDirectory(sourceDirectory);

        using FileStream stream = File.OpenRead(cbzPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry[] entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name) && IsSupportedComicImage(entry.FullName))
            .OrderBy(entry => entry.FullName, NaturalPageComparer.Instance)
            .ToArray();
        if (entries.Length == 0)
        {
            throw new InvalidOperationException("El CBZ no contiene páginas de imagen compatibles.");
        }

        var paths = new List<string>(entries.Length);
        for (int index = 0; index < entries.Length; index++)
        {
            string extension = Path.GetExtension(entries[index].Name).ToLowerInvariant();
            string target = Path.Combine(sourceDirectory, $"{index + 1:D4}{extension}");
            using Stream input = entries[index].Open();
            using FileStream output = File.Create(target);
            input.CopyTo(output);
            paths.Add(target);
        }

        InitializeComicPages(paths, Path.GetFileNameWithoutExtension(cbzPath));
    }

    private void LoadComicSession(IReadOnlyList<string> imagePaths, string title)
    {
        PrepareNewComicWorkspace();
        InitializeComicPages(imagePaths, title);
    }

    private void PrepareNewComicWorkspace()
    {
        _analysisCancellation?.Cancel();
        CleanupComicWorkspace();
        _comicWorkspace = Path.Combine(Path.GetTempPath(), "TintaES", "comic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_comicWorkspace);
        Directory.CreateDirectory(Path.Combine(_comicWorkspace, "processed"));
    }

    private void InitializeComicPages(IReadOnlyList<string> imagePaths, string title)
    {
        _comicPages.Clear();
        foreach (string path in imagePaths.OrderBy(path => path, NaturalPageComparer.Instance))
        {
            _comicPages.Add(new ComicBookPageState(path, Path.GetFileName(path)));
        }

        _comicTitle = string.IsNullOrWhiteSpace(title) ? "comic" : title;
        _comicPageIndex = 0;
        ShowComicPage(0);
        UpdateComicControls();
        SetFooterStatus($"Cómic cargado · {_comicPages.Count} páginas. Pulsa Traducir cómic.", "#4CB2BB");
    }

    private void ShowComicPage(int index)
    {
        if (_comicBatchBusy || index < 0 || index >= _comicPages.Count)
        {
            return;
        }

        ComicBookPageState page = _comicPages[index];
        _comicPageIndex = index;
        LoadImage(page.SourcePath);

        if (page.Processed && !string.IsNullOrWhiteSpace(page.CleanedPath) && File.Exists(page.CleanedPath))
        {
            _cleanedBaseBitmap = LoadBitmapSource(page.CleanedPath);
            _cleanedBitmap = _cleanedBaseBitmap;
            _maskBitmap = !string.IsNullOrWhiteSpace(page.MaskPath) && File.Exists(page.MaskPath)
                ? LoadBitmapSource(page.MaskPath)
                : null;

            _regions.Clear();
            foreach (ComicRegion region in page.Regions)
            {
                _regions.Add(region);
            }

            LanguageText.Text = $"{page.SourceLanguage.ToUpperInvariant()} → ES";
            MaskPreviewButton.IsEnabled = _maskBitmap is not null;
            CleanPreviewButton.IsEnabled = true;
            ResultPreviewButton.IsEnabled = true;
            PageImage.Source = _cleanedBitmap;
            ShowPreviewMode("result");
            RebuildOverlay();
            UpdateRegionCount();
            if (_regions.Count > 0)
            {
                RegionListBox.SelectedIndex = 0;
            }
        }

        PageNameText.Text = page.DisplayName;
        if (_originalBitmap is not null)
        {
            PageInfoText.Text = $"{_originalBitmap.PixelWidth} × {_originalBitmap.PixelHeight} px · Página {index + 1} de {_comicPages.Count}";
        }

        UpdateComicControls();
        string state = page.Processed
            ? page.Error is null ? "traducida" : "con error"
            : "pendiente";
        SetFooterStatus($"Página {index + 1}/{_comicPages.Count} · {state}", page.Error is null ? "#58A77D" : "#C99A35");
    }

    private async void AnalyzeComicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0)
        {
            AnalyzeButton_Click_Responsive(sender, e);
            return;
        }
        if (ModelComboBox.SelectedValue is not string model || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        int[] pending = _comicPages
            .Select((page, index) => (page, index))
            .Where(item => !item.page.Processed)
            .Select(item => item.index)
            .ToArray();
        if (pending.Length == 0)
        {
            SetFooterStatus("Todas las páginas del cómic ya están procesadas.", "#58A77D");
            return;
        }

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _analysisCancellation.Token;
        var stopwatch = Stopwatch.StartNew();
        int failures = 0;
        bool cancelled = false;

        _comicBatchBusy = true;
        SetBusy(true);
        UpdateComicControls();
        BusyProgressBar.IsIndeterminate = false;
        FooterProgressBar.IsIndeterminate = false;
        BusyProgressBar.Value = 0;
        FooterProgressBar.Value = 0;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            for (int pendingPosition = 0; pendingPosition < pending.Length; pendingPosition++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pageIndex = pending[pendingPosition];
                ComicBookPageState page = _comicPages[pageIndex];
                int humanPage = pageIndex + 1;

                BusyTitleText.Text = $"Página {humanPage}/{_comicPages.Count} · localizando y limpiando texto…";
                FooterStatusText.Text = $"Procesando página {humanPage} de {_comicPages.Count}…";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                try
                {
                    BitmapSource original = LoadBitmapSource(page.SourcePath);
                    var progress = new Progress<AnalysisProgress>(value =>
                    {
                        double withinPending = Math.Clamp(value.Percentage / 100d, 0, 1);
                        double overall = (pendingPosition + withinPending * 0.9) / pending.Length * 100;
                        BusyProgressBar.Value = overall;
                        FooterProgressBar.Value = overall;
                        BusyTitleText.Text = $"Página {humanPage}/{_comicPages.Count} · {value.Message}";
                    });

                    OrganicAnalysisResult organic = await _organicEngine.AnalyzeAsync(
                        page.SourcePath,
                        progress,
                        cancellationToken);

                    DialogueOnlyResult filtered = await Task.Run(
                        () => _dialogueOnlyResultService.Build(
                            original,
                            organic.CleanedBitmap,
                            organic.MaskBitmap,
                            organic.Analysis.Regions),
                        cancellationToken);

                    var analysis = new ComicAnalysis(organic.Analysis.SourceLanguage, filtered.Regions);
                    if (analysis.Regions.Count > 0)
                    {
                        BusyTitleText.Text = $"Página {humanPage}/{_comicPages.Count} · traduciendo {analysis.Regions.Count} bocadillos…";
                        FooterStatusText.Text = $"Traduciendo página {humanPage} con {model}…";
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                        await _ollama.TranslateRegionsAsync(analysis.Regions, model, cancellationToken);
                    }

                    foreach (ComicRegion region in analysis.Regions)
                    {
                        region.FontScale = 1;
                        region.ManualFontScale = 1;
                        region.IsManual = false;
                        region.ManualBaseFontSize = 0;
                        region.ManualLayoutSeedText = string.Empty;
                    }

                    string processedDirectory = Path.Combine(_comicWorkspace!, "processed");
                    string cleanedPath = Path.Combine(processedDirectory, $"{pageIndex + 1:D4}-clean.png");
                    string maskPath = Path.Combine(processedDirectory, $"{pageIndex + 1:D4}-mask.png");
                    SaveBitmap(filtered.CleanedBitmap, cleanedPath);
                    SaveBitmap(filtered.MaskBitmap, maskPath);

                    page.Regions.Clear();
                    page.Regions.AddRange(analysis.Regions);
                    page.SourceLanguage = analysis.SourceLanguage;
                    page.CleanedPath = cleanedPath;
                    page.MaskPath = maskPath;
                    page.Processed = true;
                    page.Error = null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    page.Error = exception.Message;
                    failures++;
                }

                double completed = (pendingPosition + 1d) / pending.Length * 100;
                BusyProgressBar.Value = completed;
                FooterProgressBar.Value = completed;
                double secondsPerPage = stopwatch.Elapsed.TotalSeconds / Math.Max(1, pendingPosition + 1);
                double remainingSeconds = secondsPerPage * Math.Max(0, pending.Length - pendingPosition - 1);
                FooterStatusText.Text = remainingSeconds > 1
                    ? $"Página {humanPage} terminada · tiempo restante estimado {FormatDuration(remainingSeconds)}"
                    : $"Página {humanPage} terminada";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            stopwatch.Stop();
            _comicBatchBusy = false;
            SetBusy(false);
            UpdateComicControls();
        }

        ShowComicPage(Math.Clamp(_comicPageIndex, 0, _comicPages.Count - 1));
        if (cancelled)
        {
            SetFooterStatus("Traducción del cómic cancelada. Las páginas ya terminadas se conservan.", "#C99A35");
        }
        else if (failures > 0)
        {
            SetFooterStatus($"Proceso terminado en {FormatDuration(stopwatch.Elapsed.TotalSeconds)} · {failures} página(s) con error.", "#C99A35");
        }
        else
        {
            SetFooterStatus($"Cómic traducido · {_comicPages.Count} páginas · {FormatDuration(stopwatch.Elapsed.TotalSeconds)}", "#58A77D");
        }
    }

    private async void ExportComicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar cómic traducido",
            FileName = MakeSafeFileName(_comicTitle ?? "comic") + "-es.cbz",
            DefaultExt = ".cbz",
            Filter = "Comic Book ZIP|*.cbz"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _analysisCancellation.Token;
        _comicBatchBusy = true;
        SetBusy(true);
        UpdateComicControls();
        BusyTitleText.Text = "Preparando CBZ…";
        BusyProgressBar.IsIndeterminate = false;
        FooterProgressBar.IsIndeterminate = false;

        try
        {
            using FileStream output = File.Create(dialog.FileName);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
            for (int index = 0; index < _comicPages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ComicBookPageState page = _comicPages[index];
                BusyTitleText.Text = $"Exportando página {index + 1}/{_comicPages.Count}…";
                double progress = index / (double)_comicPages.Count * 100;
                BusyProgressBar.Value = progress;
                FooterProgressBar.Value = progress;

                BitmapSource image;
                if (page.Processed && page.Error is null && !string.IsNullOrWhiteSpace(page.CleanedPath) && File.Exists(page.CleanedPath))
                {
                    BitmapSource background = LoadBitmapSource(page.CleanedPath);
                    image = _exportService.Render(background, page.Regions);
                }
                else
                {
                    image = LoadBitmapSource(page.SourcePath);
                }

                ZipArchiveEntry entry = archive.CreateEntry($"{index + 1:D4}.png", CompressionLevel.Fastest);
                using Stream entryStream = entry.Open();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(entryStream);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            BusyProgressBar.Value = 100;
            FooterProgressBar.Value = 100;
            SetFooterStatus($"CBZ exportado · {Path.GetFileName(dialog.FileName)}", "#58A77D");
        }
        catch (OperationCanceledException)
        {
            SetFooterStatus("Exportación CBZ cancelada.", "#C99A35");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"No se pudo exportar el CBZ.\n\n{exception.Message}", "Tinta ES",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetFooterStatus("La exportación CBZ ha fallado.", "#EE594B");
        }
        finally
        {
            _comicBatchBusy = false;
            SetBusy(false);
            UpdateComicControls();
        }
    }

    private void UpdateComicControls()
    {
        bool hasComic = _comicPages.Count > 0;
        bool busy = _comicBatchBusy || BusyOverlay.Visibility == Visibility.Visible;

        if (_previousPageButton is not null)
        {
            _previousPageButton.IsEnabled = hasComic && !busy && _comicPageIndex > 0;
        }
        if (_nextPageButton is not null)
        {
            _nextPageButton.IsEnabled = hasComic && !busy && _comicPageIndex >= 0 && _comicPageIndex < _comicPages.Count - 1;
        }
        if (_pageCounterText is not null)
        {
            _pageCounterText.Text = hasComic ? $"{_comicPageIndex + 1} / {_comicPages.Count}" : "— / —";
        }
        if (_openFolderButton is not null)
        {
            _openFolderButton.IsEnabled = !busy;
        }
        if (_exportComicButton is not null)
        {
            _exportComicButton.IsEnabled = hasComic && !busy;
        }

        OpenImageButton.IsEnabled = !busy;
        AnalyzeButton.Content = hasComic && _comicPages.Count > 1 ? "✦  Traducir cómic" : "✦  Analizar y traducir";
        AnalyzeButton.IsEnabled = hasComic && !busy && ModelComboBox.SelectedItem is not null;
    }

    private static BitmapSource LoadBitmapSource(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Ruta de salida inválida."));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private void CleanupComicWorkspace()
    {
        if (string.IsNullOrWhiteSpace(_comicWorkspace) || !Directory.Exists(_comicWorkspace))
        {
            _comicWorkspace = null;
            return;
        }

        try
        {
            Directory.Delete(_comicWorkspace, recursive: true);
        }
        catch
        {
            // Los temporales se podrán limpiar en una ejecución posterior del sistema.
        }
        _comicWorkspace = null;
    }

    private static bool IsSupportedComicImage(string path) =>
        ComicImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string FormatDuration(double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    private static string MakeSafeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        return string.IsNullOrWhiteSpace(name) ? "comic" : name.Trim();
    }

    private void ShowComicOpenError(Exception exception)
    {
        MessageBox.Show(this, $"No se pudo abrir el cómic.\n\n{exception.Message}", "Tinta ES",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private sealed class ComicBookPageState(string sourcePath, string displayName)
    {
        public string SourcePath { get; } = sourcePath;
        public string DisplayName { get; } = displayName;
        public string SourceLanguage { get; set; } = "en";
        public string? CleanedPath { get; set; }
        public string? MaskPath { get; set; }
        public List<ComicRegion> Regions { get; } = [];
        public bool Processed { get; set; }
        public string? Error { get; set; }
    }

    private sealed class NaturalPageComparer : IComparer<string>
    {
        public static NaturalPageComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            string left = Path.GetFileName(x);
            string right = Path.GetFileName(y);
            int i = 0;
            int j = 0;
            while (i < left.Length && j < right.Length)
            {
                if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
                {
                    int startI = i;
                    int startJ = j;
                    while (i < left.Length && char.IsDigit(left[i])) i++;
                    while (j < right.Length && char.IsDigit(right[j])) j++;
                    string numberLeft = left[startI..i].TrimStart('0');
                    string numberRight = right[startJ..j].TrimStart('0');
                    if (numberLeft.Length != numberRight.Length)
                    {
                        return numberLeft.Length.CompareTo(numberRight.Length);
                    }
                    int numeric = string.Compare(numberLeft, numberRight, StringComparison.Ordinal);
                    if (numeric != 0) return numeric;
                    continue;
                }

                int character = char.ToUpperInvariant(left[i]).CompareTo(char.ToUpperInvariant(right[j]));
                if (character != 0) return character;
                i++;
                j++;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
