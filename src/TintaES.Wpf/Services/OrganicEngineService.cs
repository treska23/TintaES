using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

public sealed record OrganicAnalysisResult(
    ComicAnalysis Analysis,
    BitmapSource CleanedBitmap,
    BitmapSource MaskBitmap,
    double ElapsedSeconds,
    bool FromCache);

public sealed class OrganicEngineService
{
    // Cambiar esta versión invalida únicamente la caché del análisis orgánico.
    // Es intencionado: la geometría de los bocadillos y la máscara de borrado
    // forman parte del resultado cacheado y no deben sobrevivir a cambios del algoritmo.
    private const string CacheVersion = "organic-layout-v4";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<OrganicAnalysisResult> AnalyzeAsync(
        string sourcePath,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("No se encuentra la página que se va a analizar.", sourcePath);
        }

        string projectRoot = FindProjectRoot();
        string workerPath = Path.Combine(projectRoot, "engine", "tinta_worker.py");
        string configPath = Path.Combine(projectRoot, "engine", "organic-engine-config.json");
        string pythonPath = Path.Combine(
            projectRoot,
            "engine",
            "manga-image-translator",
            ".venv",
            "Scripts",
            "python.exe");
        if (!File.Exists(pythonPath))
        {
            throw new InvalidOperationException(
                "Falta el entorno del motor orgánico. Ejecuta la preparación local del proyecto antes de analizar.");
        }

        string cacheKey = CreateCacheKey(sourcePath, workerPath, configPath);
        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TintaES",
            "Cache",
            "Organic",
            cacheKey);
        string manifestPath = Path.Combine(cacheRoot, "analysis.json");
        if (IsCompleteCache(manifestPath))
        {
            progress?.Report(new AnalysisProgress(100, 100, "Cargando el análisis guardado…"));
            return LoadResult(manifestPath, true);
        }

        Directory.CreateDirectory(cacheRoot);
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
                     workerPath,
                     "analyze",
                     "--input",
                     sourcePath,
                     "--output",
                     cacheRoot,
                     "--config",
                     configPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("No se pudo iniciar el motor orgánico local.");
        }

        string? reportedManifest = null;
        string? engineError = null;
        var stderr = new StringBuilder();
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // El proceso terminó al mismo tiempo que se solicitó la cancelación.
            }
        });

        Task readError = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                if (stderr.Length < 12_000)
                {
                    stderr.AppendLine(line);
                }
            }
        }, cancellationToken);

        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!TryReadMessage(line, out EngineMessage? message) || message is null)
            {
                continue;
            }

            if (message.Type == "progress" && message.Percent > 0)
            {
                progress?.Report(new AnalysisProgress(
                    Math.Clamp(message.Percent, 0, 100),
                    100,
                    string.IsNullOrWhiteSpace(message.Message) ? "Procesando la página…" : message.Message));
            }
            else if (message.Type == "complete")
            {
                reportedManifest = message.Manifest;
            }
            else if (message.Type == "error")
            {
                engineError = message.Message;
            }
        }

        await process.WaitForExitAsync(cancellationToken);
        await readError;
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            string detail = !string.IsNullOrWhiteSpace(engineError)
                ? engineError
                : stderr.ToString().Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"El motor orgánico terminó con el código {process.ExitCode}."
                    : $"El motor orgánico no pudo terminar: {detail}");
        }

        string resultManifest = !string.IsNullOrWhiteSpace(reportedManifest)
            ? reportedManifest
            : manifestPath;
        progress?.Report(new AnalysisProgress(100, 100, "Fondo reconstruido. Preparando la traducción…"));
        return LoadResult(resultManifest, false);
    }

    private static OrganicAnalysisResult LoadResult(string manifestPath, bool fromCache)
    {
        EngineManifest manifest = JsonSerializer.Deserialize<EngineManifest>(
                                      File.ReadAllText(manifestPath, Encoding.UTF8),
                                      JsonOptions)
                                  ?? throw new InvalidOperationException("El manifiesto del análisis está vacío.");
        if (!File.Exists(manifest.CleanImage) || !File.Exists(manifest.MaskImage))
        {
            throw new InvalidOperationException("El análisis está incompleto: faltan la máscara o el fondo limpio.");
        }

        var regions = manifest.Regions
            .Where(region => !string.IsNullOrWhiteSpace(region.Original))
            .Select((region, index) => CreateRegion(region, index, manifest.Width, manifest.Height))
            .ToArray();
        return new OrganicAnalysisResult(
            new ComicAnalysis(manifest.SourceLanguage, regions),
            LoadBitmap(manifest.CleanImage),
            LoadBitmap(manifest.MaskImage),
            manifest.ElapsedSeconds,
            fromCache);
    }

    private static ComicRegion CreateRegion(EngineRegion source, int index, int pageWidth, int pageHeight)
    {
        string type = source.Type is "sfx" or "caption" or "narration" or "sign"
            ? source.Type
            : "dialogue";
        return RegionMerger.Sanitize(new ComicRegion
        {
            Order = index + 1,
            Original = source.Original.Trim(),
            Translation = string.Empty,
            Type = type,
            Confidence = source.Confidence,
            BubbleConfidence = Math.Clamp(source.BubbleConfidence, 0, 1),
            TextBox = Normalize(source.TextBox, pageWidth, pageHeight),
            RenderBox = Normalize(source.RenderBox, pageWidth, pageHeight),
            SafePolygon = NormalizePolygon(source.ShapePolygon, pageWidth, pageHeight),
            Rotation = source.Rotation,
            Vertical = false,
            CleanupMode = "none",
            IsEnabled = source.Confidence >= 0.30,
            Style = new ComicTextStyle
            {
                FontCategory = type == "sfx" ? "display" : "comic",
                FontWeight = 700,
                Uppercase = source.Uppercase,
                TextColor = "#171515",
                Alignment = "center"
            }
        });
    }

    private static NormalizedRect Normalize(EngineRect box, int width, int height)
    {
        return new NormalizedRect(
            box.X / width * 1000d,
            box.Y / height * 1000d,
            box.Width / width * 1000d,
            box.Height / height * 1000d).Clamp();
    }

    private static IReadOnlyList<NormalizedPoint> NormalizePolygon(
        IReadOnlyList<double[]>? polygon,
        int width,
        int height)
    {
        if (polygon is null || polygon.Count < 3)
        {
            return [];
        }
        return polygon
            .Where(point => point.Length >= 2)
            .Select(point => new NormalizedPoint(
                Math.Clamp(point[0] / width * 1000d, 0, 1000),
                Math.Clamp(point[1] / height * 1000d, 0, 1000)))
            .ToArray();
    }

    private static BitmapSource LoadBitmap(string path)
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

    private static bool IsCompleteCache(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return false;
        }
        try
        {
            EngineManifest? manifest = JsonSerializer.Deserialize<EngineManifest>(
                File.ReadAllText(manifestPath, Encoding.UTF8),
                JsonOptions);
            return manifest is not null
                   && File.Exists(manifest.CleanImage)
                   && File.Exists(manifest.MaskImage);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CreateCacheKey(string sourcePath, params string[] dependencies)
    {
        var identity = new StringBuilder();
        identity.Append(CacheVersion).Append('|');
        var source = new FileInfo(sourcePath);
        identity.Append(source.FullName).Append('|').Append(source.Length).Append('|').Append(source.LastWriteTimeUtc.Ticks);
        foreach (string dependency in dependencies)
        {
            var file = new FileInfo(dependency);
            identity.Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks);
        }
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()));
        return Convert.ToHexString(digest)[..24].ToLowerInvariant();
    }

    private static string FindProjectRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(start);
            for (int depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "engine", "tinta_worker.py")))
                {
                    return directory.FullName;
                }
            }
        }
        throw new InvalidOperationException("No se encuentra la carpeta engine del proyecto Tinta ES.");
    }

    private static bool TryReadMessage(string line, out EngineMessage? message)
    {
        message = null;
        int start = line.IndexOf('{');
        if (start < 0)
        {
            return false;
        }
        try
        {
            message = JsonSerializer.Deserialize<EngineMessage>(line[start..], JsonOptions);
            return message is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record EngineMessage(
        string Type,
        int Percent,
        string? Message,
        string? Manifest);

    private sealed record EngineManifest(
        string SourceLanguage,
        int Width,
        int Height,
        string CleanImage,
        string MaskImage,
        IReadOnlyList<EngineRegion> Regions,
        double ElapsedSeconds);

    private sealed record EngineRegion(
        int Order,
        string Original,
        double Confidence,
        double BubbleConfidence,
        string Type,
        EngineRect TextBox,
        EngineRect RenderBox,
        IReadOnlyList<double[]>? ShapePolygon,
        double Rotation,
        bool Uppercase);

    private sealed record EngineRect(double X, double Y, double Width, double Height);
}
