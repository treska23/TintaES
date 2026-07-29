using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;
using TintaES.Wpf.Services;

try
{
    return await RunAsync(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"ERROR_INTEGRACION={exception.GetType().Name}: {exception.Message}");
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
string imagePath = args.ElementAtOrDefault(0) ?? string.Empty;
string requestedModel = args.ElementAtOrDefault(1) ?? "translategemma:4b";
if (args.Length is < 1 or > 2 || !File.Exists(imagePath))
{
    Console.Error.WriteLine("Uso: TintaES.IntegrationTests <imagen> [modelo]");
    return 2;
}

BitmapSource originalBitmap = LoadBitmap(Path.GetFullPath(imagePath));

var engine = new OrganicEngineService();
var warmup = Stopwatch.StartNew();
bool skipWarmup = string.Equals(
    Environment.GetEnvironmentVariable("TINTAES_SKIP_WARMUP"),
    "1",
    StringComparison.Ordinal);
try
{
    if (!skipWarmup)
    {
        await engine.WarmUpAsync();
    }
}
catch (InvalidOperationException exception)
{
    // Una aplicación TintaES abierta puede tener ya el motor residente. El análisis
    // cacheado y el render siguen siendo verificables sin arrancar una segunda copia.
    Console.WriteLine($"PRECARGA=omitida ({exception.Message})");
}
warmup.Stop();
Console.WriteLine(skipWarmup
    ? "PRECARGA=omitida por prueba concurrente"
    : $"PRECARGA={warmup.Elapsed.TotalSeconds:F2}s");
var stopwatch = Stopwatch.StartNew();
var progress = new Progress<AnalysisProgress>(value =>
    Console.WriteLine($"MOTOR={value.Percentage:F0}% {value.Message}"));
OrganicAnalysisResult organic = await engine.AnalyzeAsync(Path.GetFullPath(imagePath), progress);
var filter = new DialogueOnlyResultService();
DialogueOnlyResult filtered = filter.Build(
    originalBitmap,
    organic.CleanedBitmap,
    organic.MaskBitmap,
    organic.Analysis.Regions,
    includeAllDetectedText: true);
ComicAnalysis analysis = new(organic.Analysis.SourceLanguage, filtered.Regions);
TimeSpan engineTime = stopwatch.Elapsed;

using var ollama = new OllamaClient();
IReadOnlyList<OllamaModel> models = await ollama.GetModelsAsync();
string model = models.FirstOrDefault(item =>
                   item.Name.Equals(requestedModel, StringComparison.OrdinalIgnoreCase))?.Name
               ?? throw new InvalidOperationException($"Falta el modelo local {requestedModel}.");
Console.WriteLine($"MODELO={model}");
var translationProgress = new Progress<AnalysisProgress>(value =>
    Console.WriteLine($"TRADUCCION={value.Percentage:F0}% {value.Message}"));
await ollama.TranslateRegionsAsync(
    analysis.Regions,
    model,
    CancellationToken.None,
    translationProgress);
TimeSpan translationTime = stopwatch.Elapsed - engineTime;

ComicRegion[] threeBalloonRegions = CreateThreeBalloonRegressionRegions();
ApplyDetectedThreeBalloonStyles(threeBalloonRegions, analysis.Regions);
await ollama.TranslateRegionsAsync(
    threeBalloonRegions,
    model,
    CancellationToken.None);
bool threeBalloonTranslationVerified =
    VerifyThreeBalloonTranslationSemantics(threeBalloonRegions);

string artifactsDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts");
Directory.CreateDirectory(artifactsDirectory);
string renderedPath = Path.Combine(artifactsDirectory, "wpf-integration-result.png");
string[] exportedPaths =
[
    renderedPath,
    Path.Combine(artifactsDirectory, "wpf-integration-result.jpg"),
    Path.Combine(artifactsDirectory, "wpf-integration-result.webp"),
    Path.Combine(artifactsDirectory, "wpf-integration-result.tiff"),
    Path.Combine(artifactsDirectory, "wpf-integration-result.bmp"),
    Path.Combine(artifactsDirectory, "wpf-integration-result.pdf")
];
Exception? renderError = null;
bool manualFitVerified = false;
bool threeBalloonFitVerified = false;
bool spanishComicGlyphsVerified = false;
var renderThread = new Thread(() =>
{
    try
    {
        spanishComicGlyphsVerified = VerifySpanishComicGlyphs();
        var export = new ImageExportService();
        BitmapSource rendered = export
            .RenderAsync(filtered.CleanedBitmap, analysis.Regions)
            .GetAwaiter()
            .GetResult();
        foreach (string path in exportedPaths)
        {
            export.Save(rendered, path);
        }
        manualFitVerified = VerifyManualTextSafety(export);
        threeBalloonFitVerified = VerifyThreeBalloonAutomaticSafety(
            export,
            threeBalloonRegions,
            Path.Combine(artifactsDirectory, "three-balloon-regression.png"));
    }
    catch (Exception exception)
    {
        renderError = exception;
    }
});
renderThread.SetApartmentState(ApartmentState.STA);
renderThread.Start();
renderThread.Join();
if (renderError is not null)
{
    throw renderError;
}
stopwatch.Stop();

Console.WriteLine(
    $"MOTOR={engineTime.TotalSeconds:F2}s " +
    $"TRADUCCION={translationTime.TotalSeconds:F2}s " +
    $"TOTAL={stopwatch.Elapsed.TotalSeconds:F2}s " +
    $"ZONAS={analysis.Regions.Count} CACHE={organic.FromCache}");
for (int index = 0; index < analysis.Regions.Count; index++)
{
    ComicRegion region = analysis.Regions[index];
    Console.WriteLine($"[{index:00}] {region.Original.Replace('\n', ' ')}");
    Console.WriteLine($"     => {region.Translation.Replace('\n', ' ')}");
}

int translated = analysis.Regions.Count(region =>
    !string.IsNullOrWhiteSpace(region.Translation)
    && !string.Equals(region.Translation, "Traducción pendiente", StringComparison.Ordinal)
    && !string.Equals(region.Original, region.Translation, StringComparison.Ordinal));
int validExports = exportedPaths.Count(path =>
    File.Exists(path) && new FileInfo(path).Length > 1_000);
int layoutReferences = analysis.Regions.Count(region =>
    region.Style.FontSize > 0 && region.Style.OriginalLineCount > 0);
Console.WriteLine($"TRADUCIDAS={translated}/{analysis.Regions.Count}");
Console.WriteLine($"EXPORTACIONES={validExports}/{exportedPaths.Length}");
Console.WriteLine($"REFERENCIAS_TIPOGRAFICAS={layoutReferences}/{analysis.Regions.Count}");
Console.WriteLine($"AJUSTE_MANUAL_SEGURO={manualFitVerified}");
Console.WriteLine($"TRES_BOCADILLOS_VISIBLES_Y_DENTRO={threeBalloonFitVerified}");
Console.WriteLine($"TRES_BOCADILLOS_TRADUCIDOS_CON_SENTIDO={threeBalloonTranslationVerified}");
Console.WriteLine($"FUENTE_COMIC_ES_COMPATIBLE={spanishComicGlyphsVerified}");
Console.WriteLine(
    "ESTILO_3B=" +
    string.Join(
        ", ",
        threeBalloonRegions.Select(region =>
            $"{region.Style.FontWeight}/{(region.Style.Italic ? "cursiva" : "recta")}")));
foreach (ComicRegion region in threeBalloonRegions)
{
    Console.WriteLine($"REGRESION_3B: {region.Original} => {region.Translation}");
}
Console.WriteLine($"RESULTADO={renderedPath}");
return translated == analysis.Regions.Count
       && validExports == exportedPaths.Length
       && layoutReferences >= Math.Max(1, analysis.Regions.Count / 2)
       && manualFitVerified
       && threeBalloonFitVerified
       && threeBalloonTranslationVerified
       && spanishComicGlyphsVerified
    ? 0
    : 1;
}

static bool VerifySpanishComicGlyphs()
{
    FontFamily family = ComicFontResolver.Resolve(null, "comic");
    var typeface = new Typeface(
        family,
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);
    if (!typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphs))
    {
        return false;
    }

    char[] required = ['O', 'Y', 'Ó', '¡', '¿'];
    return required.All(character => glyphs.CharacterToGlyphMap.ContainsKey(character))
           && glyphs.CharacterToGlyphMap['O'] != glyphs.CharacterToGlyphMap['Y']
           && glyphs.CharacterToGlyphMap['Ó'] != glyphs.CharacterToGlyphMap['Y'];
}

static bool VerifyManualTextSafety(ImageExportService export)
{
    const int width = 600;
    const int height = 400;
    int stride = width * 4;
    byte[] whitePixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
    BitmapSource white = BitmapSource.Create(
        width,
        height,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        whitePixels,
        stride);
    white.Freeze();

    var region = new ComicRegion
    {
        Original = "MANUAL TEXT",
        Translation =
            "ESTA ES UNA PRUEBA DE SEGURIDAD CON UNA FRASE MUY LARGA QUE DEBE REDUCIRSE " +
            "AUTOMÁTICAMENTE Y PERMANECER COMPLETA DENTRO DE LA CAJA SIN RECORTARSE.",
        Type = "dialogue",
        IsEnabled = true,
        IsManual = true,
        RenderBox = new NormalizedRect(200, 150, 600, 700),
        TextBox = new NormalizedRect(250, 200, 500, 600),
        ManualBaseFontSize = 92,
        ManualFontScale = 2.5,
        Style = new ComicTextStyle
        {
            FontCategory = "comic",
            FontWeight = 700,
            Uppercase = true,
            TextColor = "#111111",
            Alignment = "center",
            LineHeightRatio = 1.05
        }
    };
    BitmapSource rendered = export.Render(white, [region]);
    int renderedStride = rendered.PixelWidth * 4;
    byte[] pixels = new byte[renderedStride * rendered.PixelHeight];
    rendered.CopyPixels(pixels, renderedStride, 0);

    int left = (int)Math.Round(region.RenderBox.X / 1000 * width);
    int top = (int)Math.Round(region.RenderBox.Y / 1000 * height);
    int right = (int)Math.Round(region.RenderBox.Right / 1000 * width);
    int bottom = (int)Math.Round(region.RenderBox.Bottom / 1000 * height);
    const int safeMargin = 5;
    bool foundInk = false;
    for (int y = top; y < bottom; y++)
    {
        for (int x = left; x < right; x++)
        {
            int offset = y * renderedStride + x * 4;
            bool ink = pixels[offset] < 205
                       || pixels[offset + 1] < 205
                       || pixels[offset + 2] < 205;
            if (!ink)
            {
                continue;
            }
            foundInk = true;
            if (x < left + safeMargin
                || x >= right - safeMargin
                || y < top + safeMargin
                || y >= bottom - safeMargin)
            {
                return false;
            }
        }
    }
    return foundInk;
}

static ComicRegion[] CreateThreeBalloonRegressionRegions()
{
    static NormalizedRect Box(double x, double y, double boxWidth, double boxHeight) =>
        new(x / 3599 * 1000, y / 2700 * 1000, boxWidth / 3599 * 1000, boxHeight / 2700 * 1000);

    static IReadOnlyList<NormalizedPoint> Polygon(params (double X, double Y)[] points) =>
        points.Select(point => new NormalizedPoint(
            point.X / 3599 * 1000,
            point.Y / 2700 * 1000)).ToArray();

    return
    [
        new ComicRegion
        {
            Original = "HOW CAN THIS BE?!",
            OcrAlternatives = ["HOVV CAN THIS BE?!"],
            Type = "dialogue",
            IsEnabled = true,
            TextBox = Box(2531, 538, 144, 221),
            RenderBox = Box(2481, 483, 244, 331),
            SafePolygon = Polygon((2481, 483), (2481, 813), (2724, 813), (2724, 483)),
            Style = new ComicTextStyle
            {
                FontCategory = "comic",
                FontWeight = 800,
                FontSize = 22.1,
                LineHeightRatio = 1.05,
                OriginalLineCount = 4,
                Italic = true,
                Uppercase = true,
                TextColor = "#111111",
                Alignment = "center"
            }
        },
        new ComicRegion
        {
            Original = "THIS IS IMPOS-SIBLE",
            OcrAlternatives = ["THIS IS IMPOS-SELE"],
            Type = "dialogue",
            IsEnabled = true,
            TextBox = Box(643, 1027, 169, 151),
            RenderBox = Box(584, 990, 287, 225),
            SafePolygon = Polygon((584, 990), (584, 1214), (870, 1214), (870, 990)),
            Style = new ComicTextStyle
            {
                FontCategory = "comic",
                FontWeight = 850,
                FontSize = 20.1,
                LineHeightRatio = 1.05,
                OriginalLineCount = 3,
                Italic = true,
                Uppercase = true,
                TextColor = "#111111",
                Alignment = "center"
            }
        },
        new ComicRegion
        {
            Original = "OPEN YOUR UP..",
            OcrAlternatives = ["OPEN YOUR EYES"],
            Type = "dialogue",
            IsEnabled = true,
            TextBox = Box(3093, 1498, 148, 200),
            RenderBox = Box(3042, 1448, 250, 300),
            SafePolygon = Polygon((3042, 1448), (3042, 1747), (3278, 1747), (3291, 1448)),
            Style = new ComicTextStyle
            {
                FontCategory = "comic",
                FontWeight = 650,
                FontSize = 26.7,
                LineHeightRatio = 1.05,
                OriginalLineCount = 3,
                Uppercase = true,
                TextColor = "#111111",
                Alignment = "center"
            }
        }
    ];
}

static void ApplyDetectedThreeBalloonStyles(
    IReadOnlyList<ComicRegion> target,
    IReadOnlyList<ComicRegion> detected)
{
    if (target.Count != 3 || detected.Count != 3)
    {
        return;
    }

    string[] anchors = ["HOW", "IMPOS", "OPEN"];
    for (int index = 0; index < anchors.Length; index++)
    {
        ComicRegion? source = detected.FirstOrDefault(region =>
            region.Original.Contains(anchors[index], StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return;
        }

        target[index].Style.FontWeight = source.Style.FontWeight;
        target[index].Style.FontWidthRatio = source.Style.FontWidthRatio;
        target[index].Style.Italic = source.Style.Italic;
        target[index].Style.TextColor = source.Style.TextColor;
    }
}

static bool VerifyThreeBalloonTranslationSemantics(IReadOnlyList<ComicRegion> regions)
{
    if (regions.Count != 3
        || regions.Any(region =>
            string.IsNullOrWhiteSpace(region.Translation)
            || string.Equals(
                region.Translation,
                "Traducción pendiente",
                StringComparison.OrdinalIgnoreCase)))
    {
        return false;
    }

    static string Letters(string value) =>
        new(value.Normalize(System.Text.NormalizationForm.FormD)
            .Where(character =>
                char.GetUnicodeCategory(character)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(char.ToUpperInvariant)
            .Where(char.IsLetter)
            .ToArray());

    string first = Letters(regions[0].Translation);
    string second = Letters(regions[1].Translation);
    string third = Letters(regions[2].Translation);
    return first == "COMOPUEDESER"
           && second == "ESTOESIMPOSIBLE"
           && third == "ABRELOSOJOS";
}

static bool VerifyThreeBalloonAutomaticSafety(
    ImageExportService export,
    IReadOnlyList<ComicRegion> regions,
    string outputPath)
{
    const int width = 1800;
    const int height = 1350;
    int stride = width * 4;
    byte[] whitePixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
    BitmapSource white = BitmapSource.Create(
        width,
        height,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        whitePixels,
        stride);
    white.Freeze();

    BitmapSource rendered = export.Render(white, regions);
    export.Save(rendered, outputPath);

    string? pageBackgroundPath =
        Environment.GetEnvironmentVariable("TINTAES_THREE_BALLOON_BACKGROUND");
    if (!string.IsNullOrWhiteSpace(pageBackgroundPath)
        && File.Exists(pageBackgroundPath))
    {
        BitmapSource pageBackground = LoadBitmap(pageBackgroundPath);
        if (pageBackground.PixelWidth == 3599 && pageBackground.PixelHeight == 2700)
        {
            BitmapSource pagePreview = export.Render(pageBackground, regions);
            export.Save(
                pagePreview,
                Path.Combine(
                    Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory,
                    "three-balloon-page-preview.png"));
        }
    }

    int renderedStride = rendered.PixelWidth * 4;
    byte[] pixels = new byte[renderedStride * rendered.PixelHeight];
    rendered.CopyPixels(pixels, renderedStride, 0);
    bool[] regionHasInk = new bool[regions.Count];

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int offset = y * renderedStride + x * 4;
            bool ink = pixels[offset] < 235
                       || pixels[offset + 1] < 235
                       || pixels[offset + 2] < 235;
            if (!ink)
            {
                continue;
            }

            int owner = -1;
            for (int index = 0; index < regions.Count; index++)
            {
                NormalizedRect box = regions[index].RenderBox;
                int left = (int)Math.Floor(box.X / 1000 * width);
                int top = (int)Math.Floor(box.Y / 1000 * height);
                int right = (int)Math.Ceiling(box.Right / 1000 * width);
                int bottom = (int)Math.Ceiling(box.Bottom / 1000 * height);
                if (x >= left && x < right && y >= top && y < bottom)
                {
                    owner = index;
                    int safeMargin = Math.Max(3, (int)Math.Round(Math.Min(right - left, bottom - top) * 0.045));
                    if (x < left + safeMargin
                        || x >= right - safeMargin
                        || y < top + safeMargin
                        || y >= bottom - safeMargin)
                    {
                        return false;
                    }
                    break;
                }
            }

            if (owner < 0)
            {
                return false;
            }
            regionHasInk[owner] = true;
        }
    }

    return regionHasInk.All(value => value);
}

static BitmapSource LoadBitmap(string path)
{
    var bitmap = new BitmapImage();
    bitmap.BeginInit();
    bitmap.CacheOption = BitmapCacheOption.OnLoad;
    bitmap.UriSource = new Uri(path, UriKind.Absolute);
    bitmap.EndInit();
    bitmap.Freeze();
    return bitmap;
}
