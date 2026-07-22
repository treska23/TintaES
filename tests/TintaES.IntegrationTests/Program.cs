using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using TintaES.Core;
using TintaES.Wpf.Services;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Uso: TintaES.IntegrationTests <imagen>");
    return 2;
}

var stopwatch = Stopwatch.StartNew();
var engine = new OrganicEngineService();
var progress = new Progress<AnalysisProgress>(value =>
    Console.WriteLine($"MOTOR={value.Percentage:F0}% {value.Message}"));
OrganicAnalysisResult organic = await engine.AnalyzeAsync(Path.GetFullPath(args[0]), progress);
ComicAnalysis analysis = organic.Analysis;
TimeSpan engineTime = stopwatch.Elapsed;

using var ollama = new OllamaClient();
IReadOnlyList<OllamaModel> models = await ollama.GetModelsAsync();
string model = models.FirstOrDefault(item => item.Name.Equals("translategemma:4b", StringComparison.OrdinalIgnoreCase))?.Name
    ?? throw new InvalidOperationException("Falta translategemma:4b.");
await ollama.TranslateRegionsAsync(analysis.Regions, model, CancellationToken.None);
stopwatch.Stop();

string artifactsDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts");
Directory.CreateDirectory(artifactsDirectory);
string renderedPath = Path.Combine(artifactsDirectory, "wpf-integration-result.png");
Exception? renderError = null;
var renderThread = new Thread(() =>
{
    try
    {
        var export = new ImageExportService();
        export.SavePng(export.Render(organic.CleanedBitmap, analysis.Regions), renderedPath);
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

Console.WriteLine($"MOTOR={engineTime.TotalSeconds:F2}s TOTAL={stopwatch.Elapsed.TotalSeconds:F2}s ZONAS={analysis.Regions.Count} CACHE={organic.FromCache}");
for (int index = 0; index < analysis.Regions.Count; index++)
{
    ComicRegion region = analysis.Regions[index];
    Console.WriteLine($"[{index:00}] {region.Original.Replace('\n', ' ')}");
    Console.WriteLine($"     => {region.Translation.Replace('\n', ' ')}");
}

int translated = analysis.Regions.Count(region =>
    !string.IsNullOrWhiteSpace(region.Translation)
    && !string.Equals(region.Original, region.Translation, StringComparison.Ordinal));
Console.WriteLine($"TRADUCIDAS={translated}/{analysis.Regions.Count}");
Console.WriteLine($"RESULTADO={renderedPath}");
return translated == analysis.Regions.Count ? 0 : 1;
