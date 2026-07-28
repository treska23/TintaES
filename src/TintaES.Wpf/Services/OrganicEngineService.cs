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
    private const string CacheVersion = "organic-layout-v7-lettering";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);
    private static readonly object WorkerStateLock = new();
    private static readonly StringBuilder WorkerErrors = new();
    private static Process? _residentWorker;
    private static Task? _residentErrorReader;
    private static string? _residentWorkerPath;
    private static string? _residentPythonPath;

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        string projectRoot = FindProjectRoot();
        string workerPath = Path.Combine(projectRoot, "engine", "tinta_worker.py");
        string pythonPath = Path.Combine(
            projectRoot,
            "engine",
            "manga-image-translator",
            ".venv",
            "Scripts",
            "python.exe");
        if (!File.Exists(workerPath) || !File.Exists(pythonPath))
        {
            return;
        }

        await WorkerGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureResidentWorkerAsync(
                projectRoot,
                workerPath,
                pythonPath,
                cancellationToken);
        }
        catch
        {
            ResetResidentWorker();
            throw;
        }
        finally
        {
            WorkerGate.Release();
        }
    }

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
        string brightDetectorPath = Path.Combine(projectRoot, "engine", "bright_text_candidates.py");
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

        string cacheKey = CreateCacheKey(sourcePath, workerPath, brightDetectorPath, configPath);
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
        var supplementalDetector = new SupplementalTextDetectionService();
        string supplementalManifest = await supplementalDetector.CreateManifestAsync(
            sourcePath,
            cacheRoot,
            projectRoot,
            pythonPath,
            progress,
            cancellationToken);
        string resultManifest = await RunResidentWorkerAsync(
            projectRoot,
            workerPath,
            pythonPath,
            sourcePath,
            cacheRoot,
            supplementalManifest,
            configPath,
            manifestPath,
            progress,
            cancellationToken);
        progress?.Report(new AnalysisProgress(100, 100, "Fondo reconstruido. Preparando la traducción…"));
        return LoadResult(resultManifest, false);
    }

    private static async Task<string> RunResidentWorkerAsync(
        string projectRoot,
        string workerPath,
        string pythonPath,
        string sourcePath,
        string cacheRoot,
        string supplementalManifest,
        string configPath,
        string fallbackManifest,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        await WorkerGate.WaitAsync(cancellationToken);
        try
        {
            Process worker = await EnsureResidentWorkerAsync(
                projectRoot,
                workerPath,
                pythonPath,
                cancellationToken);
            lock (WorkerStateLock)
            {
                WorkerErrors.Clear();
            }

            string request = JsonSerializer.Serialize(new
            {
                input = sourcePath,
                output = cacheRoot,
                supplemental = supplementalManifest,
                config = configPath,
                cpu = false
            }, JsonOptions);
            await worker.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
            await worker.StandardInput.FlushAsync(cancellationToken);

            while (await worker.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
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
                        string.IsNullOrWhiteSpace(message.Message)
                            ? "Procesando la página…"
                            : message.Message));
                }
                else if (message.Type == "complete")
                {
                    return !string.IsNullOrWhiteSpace(message.Manifest)
                        ? message.Manifest
                        : fallbackManifest;
                }
                else if (message.Type == "error")
                {
                    string detail = !string.IsNullOrWhiteSpace(message.Message)
                        ? message.Message
                        : GetWorkerErrors();
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(detail)
                            ? "El motor orgánico no pudo terminar."
                            : $"El motor orgánico no pudo terminar: {detail}");
                }
            }

            string errors = GetWorkerErrors();
            ResetResidentWorker();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(errors)
                    ? "El motor orgánico local se cerró de forma inesperada."
                    : $"El motor orgánico local se cerró: {errors}");
        }
        catch (OperationCanceledException)
        {
            ResetResidentWorker();
            throw;
        }
        finally
        {
            WorkerGate.Release();
        }
    }

    private static async Task<Process> EnsureResidentWorkerAsync(
        string projectRoot,
        string workerPath,
        string pythonPath,
        CancellationToken cancellationToken)
    {
        if (_residentWorker is { HasExited: false }
            && string.Equals(_residentWorkerPath, workerPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_residentPythonPath, pythonPath, StringComparison.OrdinalIgnoreCase))
        {
            return _residentWorker;
        }

        ResetResidentWorker();
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(workerPath);
        startInfo.ArgumentList.Add("serve");
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        var worker = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!worker.Start())
        {
            worker.Dispose();
            throw new InvalidOperationException("No se pudo iniciar el motor orgánico local.");
        }

        _residentWorker = worker;
        _residentWorkerPath = workerPath;
        _residentPythonPath = pythonPath;
        _residentErrorReader = ReadWorkerErrorsAsync(worker);

        while (await worker.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (TryReadMessage(line, out EngineMessage? message)
                && message?.Type == "ready")
            {
                return worker;
            }
        }

        string errors = GetWorkerErrors();
        ResetResidentWorker();
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(errors)
                ? "El motor orgánico no pudo prepararse."
                : $"El motor orgánico no pudo prepararse: {errors}");
    }

    private static async Task ReadWorkerErrorsAsync(Process worker)
    {
        try
        {
            while (await worker.StandardError.ReadLineAsync() is { } line)
            {
                lock (WorkerStateLock)
                {
                    if (WorkerErrors.Length < 12_000)
                    {
                        WorkerErrors.AppendLine(line);
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // El proceso se cerró mientras se vaciaba stderr.
        }
    }

    private static string GetWorkerErrors()
    {
        lock (WorkerStateLock)
        {
            return WorkerErrors.ToString().Trim();
        }
    }

    private static void ResetResidentWorker()
    {
        Process? worker = _residentWorker;
        _residentWorker = null;
        _residentWorkerPath = null;
        _residentPythonPath = null;
        _residentErrorReader = null;
        if (worker is null)
        {
            return;
        }
        try
        {
            if (!worker.HasExited)
            {
                worker.StandardInput.Close();
                if (!worker.WaitForExit(500))
                {
                    worker.Kill(entireProcessTree: true);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Ya había terminado.
        }
        finally
        {
            worker.Dispose();
        }
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

        IReadOnlyList<ComicRegion> regions = RegionMerger.Merge(
            manifest.Regions
                .Where(region => !string.IsNullOrWhiteSpace(region.Original))
                .Select((region, index) => CreateRegion(region, index, manifest.Width, manifest.Height)));
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
        int originalLineCount = source.Lines?.Count(line => line.Length >= 3) ?? 0;
        double originalFontPixels = EstimateOriginalFontPixels(source, originalLineCount);
        return RegionMerger.Sanitize(new ComicRegion
        {
            Order = index + 1,
            Original = source.Original.Trim(),
            OcrAlternatives = source.OcrAlternatives ?? [],
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
                FontWeight = source.FontWeight ?? (source.Uppercase ? 700 : 600),
                FontSize = originalFontPixels > 0 && pageHeight > 0
                    ? originalFontPixels / pageHeight * 1000
                    : 0,
                FontWidthRatio = source.FontWidthRatio ?? 1,
                LineHeightRatio = 1.05,
                OriginalLineCount = originalLineCount,
                Italic = source.Italic ?? false,
                Uppercase = source.Uppercase,
                TextColor = string.IsNullOrWhiteSpace(source.TextColor)
                    ? "#171515"
                    : source.TextColor,
                Alignment = "center"
            }
        });
    }

    private static double EstimateOriginalFontPixels(EngineRegion source, int lineCount)
    {
        if (source.Lines is null || lineCount <= 0 || source.TextBox.Height <= 0)
        {
            return 0;
        }

        double[] glyphHeights = source.Lines
            .Where(line => line.Length >= 3)
            .Select(line =>
            {
                double minimum = line.Min(point => point.Length >= 2 ? point[1] : double.PositiveInfinity);
                double maximum = line.Max(point => point.Length >= 2 ? point[1] : double.NegativeInfinity);
                return maximum - minimum;
            })
            .Where(height => double.IsFinite(height) && height >= 2)
            .Order()
            .ToArray();
        if (glyphHeights.Length == 0)
        {
            return 0;
        }

        double median = glyphHeights[glyphHeights.Length / 2];
        double lineSlot = source.TextBox.Height / lineCount;

        // El contorno OCR mide el glifo visible, no el cuadrado em completo de WPF.
        // Combinamos ambas observaciones para mantener el tamaño editorial original sin
        // permitir que una línea ruidosa infle todo el bocadillo.
        double estimated = Math.Max(median * 1.18, lineSlot * 0.88);
        return Math.Clamp(estimated, 5, Math.Max(5, lineSlot * 1.08));
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
        IReadOnlyList<string>? OcrAlternatives,
        double Confidence,
        double BubbleConfidence,
        string Type,
        EngineRect TextBox,
        EngineRect RenderBox,
        IReadOnlyList<double[]>? ShapePolygon,
        double Rotation,
        bool Uppercase,
        string? TextColor,
        int? FontWeight,
        double? FontWidthRatio,
        bool? Italic,
        IReadOnlyList<double[][]>? Lines);

    private sealed record EngineRect(double X, double Y, double Width, double Height);
}
