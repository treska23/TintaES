using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;
using TintaES.Wpf.Services;

try
{
    if (args is ["--cleanup-polygon-self-test"])
    {
        return RunCleanupPolygonSelfTest();
    }
    if (args is ["--cleanup-image", var cleanupImage])
    {
        return await RunCleanupImageAsync(cleanupImage);
    }
    if (args is ["--lettering-layout-self-test"])
    {
        return RunLetteringLayoutSelfTest();
    }
    if (args is ["--windows-ocr-image", var ocrImage])
    {
        return await RunWindowsOcrImageAsync(ocrImage);
    }
    return await RunAsync(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"ERROR_INTEGRACION={exception.GetType().Name}: {exception.Message}");
    return 1;
}

static int RunCleanupPolygonSelfTest()
{
    const int width = 32;
    const int height = 32;
    const int colorStride = width * 4;
    var originalPixels = new byte[colorStride * height];
    var cleanedPixels = new byte[colorStride * height];
    var maskPixels = Enumerable.Repeat((byte)255, width * height).ToArray();
    for (int pixel = 0; pixel < width * height; pixel++)
    {
        int offset = pixel * 4;
        originalPixels[offset] = 20;
        originalPixels[offset + 1] = 40;
        originalPixels[offset + 2] = 60;
        originalPixels[offset + 3] = 255;
        cleanedPixels[offset] = 250;
        cleanedPixels[offset + 1] = 250;
        cleanedPixels[offset + 2] = 250;
        cleanedPixels[offset + 3] = 255;
    }

    BitmapSource original = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Bgra32, null, originalPixels, colorStride);
    BitmapSource cleaned = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Bgra32, null, cleanedPixels, colorStride);
    BitmapSource mask = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Gray8, null, maskPixels, width);
    var region = new ComicRegion
    {
        Original = "TEST",
        Type = "dialogue",
        Confidence = 1,
        TextBox = new NormalizedRect(180, 180, 640, 640),
        RenderBox = new NormalizedRect(100, 100, 800, 800),
        CleanupPolygon =
        [
            new NormalizedPoint(500, 100),
            new NormalizedPoint(900, 500),
            new NormalizedPoint(500, 900),
            new NormalizedPoint(100, 500)
        ]
    };

    DialogueOnlyResult result = new DialogueOnlyResultService().Build(
        original,
        cleaned,
        mask,
        [region],
        includeAllDetectedText: true);
    var resultPixels = new byte[colorStride * height];
    var resultMask = new byte[width * height];
    result.CleanedBitmap.CopyPixels(resultPixels, colorStride, 0);
    result.MaskBitmap.CopyPixels(resultMask, width, 0);

    int corner = 7 * width + 7;
    int centre = 16 * width + 16;
    bool cornerPreserved = resultMask[corner] == 0
        && resultPixels[corner * 4] == 20
        && resultPixels[corner * 4 + 1] == 40
        && resultPixels[corner * 4 + 2] == 60;
    bool centreCleaned = resultMask[centre] == 255
        && resultPixels[centre * 4] == 250
        && resultPixels[centre * 4 + 1] == 250
        && resultPixels[centre * 4 + 2] == 250;

    bool flatBalloonRepair = VerifyFlatBalloonRepair();
    Console.WriteLine($"LIMPIEZA_ORGANICA_ESQUINA={(cornerPreserved ? "OK" : "ERROR")}");
    Console.WriteLine($"LIMPIEZA_ORGANICA_CENTRO={(centreCleaned ? "OK" : "ERROR")}");
    Console.WriteLine($"REPARA_MANCHA_EN_BOCADILLO={(flatBalloonRepair ? "OK" : "ERROR")}");
    return cornerPreserved && centreCleaned && flatBalloonRepair ? 0 : 1;
}

static bool VerifyFlatBalloonRepair()
{
    const int width = 64;
    const int height = 40;
    const int colorStride = width * 4;
    var originalPixels = new byte[colorStride * height];
    var cleanedPixels = new byte[colorStride * height];
    var maskPixels = new byte[width * height];
    for (int pixel = 0; pixel < width * height; pixel++)
    {
        int colorOffset = pixel * 4;
        originalPixels[colorOffset] = 248;
        originalPixels[colorOffset + 1] = 249;
        originalPixels[colorOffset + 2] = 250;
        originalPixels[colorOffset + 3] = 255;
        cleanedPixels[colorOffset] = 248;
        cleanedPixels[colorOffset + 1] = 249;
        cleanedPixels[colorOffset + 2] = 250;
        cleanedPixels[colorOffset + 3] = 255;
    }

    for (int y = 17; y <= 22; y++)
    {
        for (int x = 22; x <= 41; x++)
        {
            int pixel = y * width + x;
            int colorOffset = pixel * 4;
            originalPixels[colorOffset] = 24;
            originalPixels[colorOffset + 1] = 24;
            originalPixels[colorOffset + 2] = 24;
            cleanedPixels[colorOffset] = 0;
            cleanedPixels[colorOffset + 1] = 0;
            cleanedPixels[colorOffset + 2] = 0;
            maskPixels[pixel] = 255;
        }
    }

    BitmapSource original = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Bgra32, null, originalPixels, colorStride);
    BitmapSource cleaned = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Bgra32, null, cleanedPixels, colorStride);
    BitmapSource mask = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Gray8, null, maskPixels, width);
    var region = new ComicRegion
    {
        Original = "MAYBE",
        Type = "sfx",
        Confidence = 1,
        TextBox = new NormalizedRect(300, 350, 400, 300),
        RenderBox = new NormalizedRect(300, 350, 400, 300),
        CleanupPolygon =
        [
            new NormalizedPoint(300, 350),
            new NormalizedPoint(700, 350),
            new NormalizedPoint(700, 650),
            new NormalizedPoint(300, 650)
        ]
    };

    DialogueOnlyResult result = new DialogueOnlyResultService().Build(
        original,
        cleaned,
        mask,
        [region],
        includeAllDetectedText: true);
    var resultPixels = new byte[colorStride * height];
    result.CleanedBitmap.CopyPixels(resultPixels, colorStride, 0);
    int centre = 20 * width + 31;
    int offset = centre * 4;
    return resultPixels[offset] >= 245
        && resultPixels[offset + 1] >= 245
        && resultPixels[offset + 2] >= 245;
}

static async Task<int> RunCleanupImageAsync(string imagePath)
{
    if (!File.Exists(imagePath))
    {
        Console.Error.WriteLine($"No existe la imagen: {imagePath}");
        return 2;
    }

    BitmapSource original = LoadBitmap(Path.GetFullPath(imagePath));
    var engine = new OrganicEngineService();
    OrganicAnalysisResult organic = await engine.AnalyzeAsync(Path.GetFullPath(imagePath));
    DialogueOnlyResult filtered = new DialogueOnlyResultService().Build(
        original,
        organic.CleanedBitmap,
        organic.MaskBitmap,
        organic.Analysis.Regions,
        includeAllDetectedText: true);

    string artifactsDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts");
    Directory.CreateDirectory(artifactsDirectory);
    string cleanPath = Path.Combine(artifactsDirectory, "cleanup-organic-preview.png");
    string maskPath = Path.Combine(artifactsDirectory, "cleanup-organic-mask.png");
    SavePng(filtered.CleanedBitmap, cleanPath);
    SavePng(filtered.MaskBitmap, maskPath);

    int organicRegions = filtered.Regions.Count(region => region.CleanupPolygon.Count >= 3);
    Console.WriteLine($"ZONAS={filtered.Regions.Count}");
    Console.WriteLine($"CONTORNOS_ORGANICOS={organicRegions}/{filtered.Regions.Count}");
    Console.WriteLine($"FONDO_LIMPIO={cleanPath}");
    Console.WriteLine($"MASCARA={maskPath}");
    return organicRegions == filtered.Regions.Count ? 0 : 1;
}

static void SavePng(BitmapSource bitmap, string path)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using FileStream stream = File.Create(path);
    encoder.Save(stream);
}

static async Task<int> RunWindowsOcrImageAsync(string imagePath)
{
    if (!File.Exists(imagePath))
    {
        Console.Error.WriteLine($"No existe la imagen: {imagePath}");
        return 2;
    }

    BitmapSource original = LoadBitmap(Path.GetFullPath(imagePath));
    ComicAnalysis analysis = await new WindowsOcrService().RecognizeWithTilingAsync(original);
    foreach (ComicRegion region in analysis.Regions)
    {
        Console.WriteLine(
            $"OCR {region.Type} {region.TextBox.X:F0},{region.TextBox.Y:F0}," +
            $"{region.TextBox.Width:F0},{region.TextBox.Height:F0}: " +
            region.Original.Replace('\n', ' '));
    }
    Console.WriteLine($"OCR_ZONAS={analysis.Regions.Count}");
    return analysis.Regions.Count > 0 ? 0 : 1;
}

static int RunLetteringLayoutSelfTest()
{
    const int width = 900;
    const int height = 600;
    int stride = width * 4;
    byte[] whitePixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
    BitmapSource white = BitmapSource.Create(
        width, height, 96, 96, PixelFormats.Bgra32, null, whitePixels, stride);
    white.Freeze();

    static NormalizedRect Box(double x, double y, double boxWidth, double boxHeight) =>
        new(x / width * 1000, y / height * 1000, boxWidth / width * 1000, boxHeight / height * 1000);

    static IReadOnlyList<NormalizedPoint> Ellipse(double x, double y, double boxWidth, double boxHeight) =>
        Enumerable.Range(0, 48)
            .Select(index =>
            {
                double angle = Math.PI * 2 * index / 48;
                return new NormalizedPoint(
                    (x + boxWidth / 2 + Math.Cos(angle) * boxWidth / 2) / width * 1000,
                    (y + boxHeight / 2 + Math.Sin(angle) * boxHeight / 2) / height * 1000);
            })
            .ToArray();

    ComicTextStyle Style(double originalFontPixels, int originalLines) => new()
    {
        FontCategory = "comic",
        FontWeight = 700,
        FontSize = originalFontPixels / height * 1000,
        LineHeightRatio = 1.02,
        OriginalLineCount = originalLines,
        Uppercase = true,
        TextColor = "#161616",
        Alignment = "center"
    };

    ComicRegion[] regions =
    [
        new ComicRegion
        {
            Original = "SHUT UP!",
            Translation = "¡Cállate!",
            Type = "dialogue",
            IsEnabled = true,
            TextBox = Box(105, 95, 110, 55),
            RenderBox = Box(45, 35, 230, 190),
            SafePolygon = Ellipse(45, 35, 230, 190),
            Style = Style(34, 2)
        },
        new ComicRegion
        {
            Original = "YOU THINK YOU ARE SO GREAT BUT YOU ARE MISSING THE POINT",
            Translation = "Crees que eres genial, pero se te escapa lo importante.",
            Type = "dialogue",
            IsEnabled = true,
            TextBox = Box(375, 75, 180, 130),
            RenderBox = Box(320, 25, 290, 265),
            SafePolygon = Ellipse(320, 25, 290, 265),
            Style = Style(28, 6)
        },
        new ComicRegion
        {
            Original = "THAT DOES NOT EVEN RHYME",
            Translation = "¡Eso ni siquiera rima!",
            Type = "thought",
            IsEnabled = true,
            TextBox = Box(665, 330, 135, 80),
            RenderBox = Box(625, 280, 225, 205),
            SafePolygon =
            [
                new NormalizedPoint(737.5 / width * 1000, 280d / height * 1000),
                new NormalizedPoint(850d / width * 1000, 382.5 / height * 1000),
                new NormalizedPoint(737.5 / width * 1000, 485d / height * 1000),
                new NormalizedPoint(625d / width * 1000, 382.5 / height * 1000)
            ],
            Style = Style(31, 4)
        }
    ];

    BitmapSource? rendered = null;
    Exception? renderError = null;
    var renderThread = new Thread(() =>
    {
        try
        {
            rendered = new ImageExportService().Render(white, regions);
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

    string output = Path.Combine(Environment.CurrentDirectory, "artifacts", "lettering-layout-self-test.png");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    SavePng(rendered!, output);

    byte[] pixels = new byte[stride * height];
    rendered!.CopyPixels(pixels, stride, 0);
    bool[] hasInk = new bool[regions.Length];
    int[] minX = Enumerable.Repeat(int.MaxValue, regions.Length).ToArray();
    int[] minY = Enumerable.Repeat(int.MaxValue, regions.Length).ToArray();
    int[] maxX = Enumerable.Repeat(int.MinValue, regions.Length).ToArray();
    int[] maxY = Enumerable.Repeat(int.MinValue, regions.Length).ToArray();
    bool inkStayedInside = true;

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int offset = y * stride + x * 4;
            bool ink = pixels[offset] < 210 || pixels[offset + 1] < 210 || pixels[offset + 2] < 210;
            if (!ink)
            {
                continue;
            }

            double normalizedX = (x + 0.5) / width * 1000;
            double normalizedY = (y + 0.5) / height * 1000;
            int owner = Array.FindIndex(regions, region =>
                normalizedX >= region.RenderBox.X
                && normalizedX <= region.RenderBox.Right
                && normalizedY >= region.RenderBox.Y
                && normalizedY <= region.RenderBox.Bottom);
            if (owner < 0
                || !ContainsPoint(regions[owner].SafePolygon, normalizedX, normalizedY))
            {
                inkStayedInside = false;
                continue;
            }

            hasInk[owner] = true;
            minX[owner] = Math.Min(minX[owner], x);
            minY[owner] = Math.Min(minY[owner], y);
            maxX[owner] = Math.Max(maxX[owner], x);
            maxY[owner] = Math.Max(maxY[owner], y);
        }
    }

    bool readableScale = regions.Select((region, index) =>
    {
        if (!hasInk[index])
        {
            return false;
        }
        double boxHeight = region.RenderBox.Height / 1000 * height;
        double inkHeight = maxY[index] - minY[index] + 1;
        return inkHeight / Math.Max(1, boxHeight) >= 0.20;
    }).All(value => value);

    Console.WriteLine($"ROTULOS_VISIBLES={(hasInk.All(value => value) ? "OK" : "ERROR")}");
    Console.WriteLine($"ROTULOS_DENTRO_DE_FORMAS={(inkStayedInside ? "OK" : "ERROR")}");
    Console.WriteLine($"ESCALA_LEGIBLE={(readableScale ? "OK" : "ERROR")}");
    Console.WriteLine($"MUESTRA_ROTULACION={output}");
    return hasInk.All(value => value) && inkStayedInside && readableScale ? 0 : 1;
}

static bool ContainsPoint(
    IReadOnlyList<NormalizedPoint> polygon,
    double x,
    double y)
{
    bool inside = false;
    for (int first = 0, second = polygon.Count - 1; first < polygon.Count; second = first++)
    {
        NormalizedPoint a = polygon[first];
        NormalizedPoint b = polygon[second];
        bool crosses = (a.Y > y) != (b.Y > y);
        if (crosses && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
        {
            inside = !inside;
        }
    }
    return inside;
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
double pageRenderSeconds = double.PositiveInfinity;
var renderStepTimings = new List<string>();
var renderThread = new Thread(() =>
{
    try
    {
        var renderStep = Stopwatch.StartNew();
        spanishComicGlyphsVerified = VerifySpanishComicGlyphs();
        renderStepTimings.Add($"fuente={renderStep.Elapsed.TotalSeconds:F2}s");
        var export = new ImageExportService();
        renderStep.Restart();
        BitmapSource rendered = export
            .RenderAsync(filtered.CleanedBitmap, analysis.Regions)
            .GetAwaiter()
            .GetResult();
        pageRenderSeconds = renderStep.Elapsed.TotalSeconds;
        renderStepTimings.Add($"render_pagina={renderStep.Elapsed.TotalSeconds:F2}s");
        foreach (string path in exportedPaths)
        {
            renderStep.Restart();
            export.Save(rendered, path);
            renderStepTimings.Add(
                $"guardar_{Path.GetExtension(path).TrimStart('.')}={renderStep.Elapsed.TotalSeconds:F2}s");
        }
        renderStep.Restart();
        manualFitVerified = VerifyManualTextSafety(export);
        renderStepTimings.Add($"ajuste_manual={renderStep.Elapsed.TotalSeconds:F2}s");
        renderStep.Restart();
        threeBalloonFitVerified = VerifyThreeBalloonAutomaticSafety(
            export,
            threeBalloonRegions,
            Path.Combine(artifactsDirectory, "three-balloon-regression.png"));
        renderStepTimings.Add($"ajuste_3_bocadillos={renderStep.Elapsed.TotalSeconds:F2}s");
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
Console.WriteLine($"PERFIL_RENDER={string.Join(" ", renderStepTimings)}");
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
Console.WriteLine($"RENDER_PAGINA_FLUIDO={pageRenderSeconds <= 15}");
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
       && pageRenderSeconds <= 15
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
