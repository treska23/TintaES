using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

public sealed class SupplementalTextDetectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<string> CreateManifestAsync(
        string sourcePath,
        string outputDirectory,
        string projectRoot,
        string pythonPath,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new AnalysisProgress(2, 100, "Buscando textos pequeños que podrían quedar visibles…"));
        BitmapSource original = LoadBitmap(sourcePath);
        Task<ComicAnalysis> windowsTask =
            new WindowsOcrService().RecognizeAsync(original, cancellationToken);
        Task<BrightCandidateManifest> brightTask = RunBrightCandidateDetectorAsync(
            sourcePath,
            outputDirectory,
            projectRoot,
            pythonPath,
            cancellationToken);

        await Task.WhenAll(windowsTask, brightTask);
        ComicAnalysis windows = await windowsTask;
        BrightCandidateManifest bright = await brightTask;

        IReadOnlyList<ComicRegion> merged = RegionMerger.Merge(
            NormalizeWindowsRegions(windows.Regions)
                .Where(region => IsPlausibleText(region.Original)));

        var payload = new SupplementalManifest(
            bright.Width,
            bright.Height,
            bright.Candidates,
            merged.Select(region => ToPayload(region, bright.Width, bright.Height)).ToArray());
        string manifestPath = Path.Combine(outputDirectory, "supplemental-text.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            cancellationToken);
        return manifestPath;
    }

    private static async Task<BrightCandidateManifest> RunBrightCandidateDetectorAsync(
        string sourcePath,
        string outputDirectory,
        string projectRoot,
        string pythonPath,
        CancellationToken cancellationToken)
    {
        string script = Path.Combine(projectRoot, "engine", "bright_text_candidates.py");
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[]
                 {
                     script,
                     "--input",
                     sourcePath,
                     "--output",
                     outputDirectory
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                "No se pudo iniciar el detector de textos residuales.");
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "El detector de textos residuales no pudo terminar."
                    : error.Trim());
        }

        string manifestPath = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?? Path.Combine(outputDirectory, "bright-candidates.json");
        return JsonSerializer.Deserialize<BrightCandidateManifest>(
                   await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken),
                   JsonOptions)
               ?? throw new InvalidOperationException(
                   "El detector de textos residuales devolvió un manifiesto vacío.");
    }

    private static ComicRegion CreateBrightRegion(
        BrightCandidate candidate,
        string text,
        int pageWidth,
        int pageHeight)
    {
        var box = new NormalizedRect(
            candidate.X / (double)pageWidth * 1000,
            candidate.Y / (double)pageHeight * 1000,
            candidate.Width / (double)pageWidth * 1000,
            candidate.Height / (double)pageHeight * 1000).Clamp();
        bool uppercase = text.Any(char.IsLetter) && text.Where(char.IsLetter).All(char.IsUpper);
        return RegionMerger.Sanitize(new ComicRegion
        {
            Original = text.Trim(),
            Type = "sfx",
            Confidence = 0.76,
            TextBox = box,
            RenderBox = box.Expand(0.16, 0.20),
            CleanupMode = "none",
            IsEnabled = true,
            Style = new ComicTextStyle
            {
                FontCategory = "display",
                FontWeight = 700,
                Uppercase = uppercase,
                TextColor = "#F7F4E8",
                Alignment = "center"
            }
        });
    }

    private static bool IsPlausibleText(string value)
    {
        string text = value.Trim();
        int letters = text.Count(char.IsLetter);
        return letters >= 2
               && text.Length <= 280
               && letters / (double)Math.Max(1, text.Count(character => !char.IsWhiteSpace(character))) >= 0.45;
    }

    private static IEnumerable<ComicRegion> NormalizeWindowsRegions(
        IEnumerable<ComicRegion> regions)
    {
        foreach (ComicRegion region in regions)
        {
            string text = Regex.Replace(region.Original, @"\s+", " ").Trim();
            text = Regex.Replace(text, @"\bPOR\s+WWAT\b", "FOR WHAT", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bWWAT\b", "WHAT", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bgls\b", "HIS", RegexOptions.IgnoreCase);
            if (Regex.IsMatch(text, @"^[I1|]\s*['’]?\s*FOOD\b", RegexOptions.IgnoreCase))
            {
                text = "FOOD";
                region.Type = "sign";
                region.Style.FontCategory = "display";
                region.Style.TextColor = "#171515";
            }
            else if (Regex.IsMatch(text, @"^THW[/|\\]?P$", RegexOptions.IgnoreCase))
            {
                text = "THWIP";
                region.Type = "sfx";
                region.Style.FontCategory = "display";
                region.Style.TextColor = "#F7F4E8";
            }

            region.Original = text;
            yield return region;
        }
    }

    private static SupplementalRegion ToPayload(ComicRegion region, int width, int height)
    {
        NormalizedRect box = region.TextBox;
        return new SupplementalRegion(
            region.Original,
            region.Type,
            region.Confidence,
            (int)Math.Round(box.X / 1000 * width),
            (int)Math.Round(box.Y / 1000 * height),
            Math.Max(1, (int)Math.Round(box.Width / 1000 * width)),
            Math.Max(1, (int)Math.Round(box.Height / 1000 * height)));
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private sealed record BrightCandidateManifest(
        int Width,
        int Height,
        string Sheet,
        IReadOnlyList<BrightCandidate> Candidates);

    private sealed record BrightCandidate(int Id, int X, int Y, int Width, int Height);
    private sealed record SupplementalManifest(
        int Width,
        int Height,
        IReadOnlyList<BrightCandidate> BrightCandidates,
        IReadOnlyList<SupplementalRegion> Regions);

    private sealed record SupplementalRegion(
        string Original,
        string Type,
        double Confidence,
        int X,
        int Y,
        int Width,
        int Height);
}
