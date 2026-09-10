using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

public sealed record PaddleOcrPassResult(
    bool Available,
    int Spots,
    int Replacements,
    string Detail);

/// <summary>
/// Mejora la transcripción del OCR clásico con PaddleOCR-VL 1.6. El proceso es
/// local, aislado y de una sola ejecución para liberar la VRAM antes de traducir.
/// </summary>
public sealed class PaddleOcrService
{
    private const string CacheVersion = "paddleocr-vl-1.6-ctd-crops-v4-owned-raw";
    private const string ResultPrefix = "TINTAES_RESULT=";
    private const string FastServerRequiredEngine = "__tintaes_fast_server_required__";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Ollama = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:11434/"),
        Timeout = TimeSpan.FromSeconds(3)
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<bool> HasCachedResultAsync(
        string sourcePath,
        string projectRoot,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ComicRegion>? regions = null)
    {
        if (IsDisabled() || !IsPrepared(projectRoot))
        {
            return true;
        }
        // La imagen por sí sola no identifica los recortes que se han reconocido.
        if (regions is null)
        {
            return false;
        }
        if (!regions.Any(region => region.IsEnabled))
        {
            return true;
        }

        try
        {
            byte[] imageBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            (int width, int height) = ReadImageSize(imageBytes);
            if (TryLoadSpots(
                    CreateCachePath(imageBytes, projectRoot, regions),
                    width,
                    height,
                    out _))
            {
                return true;
            }

            // Si no hay resultado de Paddle guardado y tampoco está preparado el
            // servidor rápido, el análisis orgánico sigue siendo reutilizable: en
            // ese caso usaremos el OCR clásico sin lanzar el camino lento oculto.
            return !IsFastBackendPrepared(projectRoot, out _);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    public async Task<PaddleOcrPassResult> TryImproveAsync(
        string sourcePath,
        IReadOnlyList<ComicRegion> regions,
        string projectRoot,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsDisabled())
        {
            return new PaddleOcrPassResult(false, 0, 0, "desactivado por configuración");
        }
        if (!IsPrepared(projectRoot))
        {
            return new PaddleOcrPassResult(false, 0, 0, "entorno local no preparado");
        }
        cancellationToken.ThrowIfCancellationRequested();
        int enabledCount = regions.Count(region => region.IsEnabled);
        if (enabledCount == 0)
        {
            return new PaddleOcrPassResult(true, 0, 0, "sin zonas de texto habilitadas");
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            byte[] imageBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            (int width, int height) = ReadImageSize(imageBytes);
            string cachePath = CreateCachePath(imageBytes, projectRoot, regions);
            if (TryLoadSpots(cachePath, width, height, out IReadOnlyList<HunyuanTextSpot> cached))
            {
                IReadOnlyList<HunyuanTextSpot> cleanedCached = CleanVisualSpots(regions, cached);
                int cachedReplacements = PaddleCropAssociation.ApplyToRegions(regions, cleanedCached);
                return new PaddleOcrPassResult(
                    true,
                    cleanedCached.Count,
                    cachedReplacements,
                    $"{cachedReplacements} zona(s) corregidas desde la caché visual");
            }

            if (!IsFastBackendPrepared(projectRoot, out string fastBackendProblem))
            {
                return new PaddleOcrPassResult(
                    false,
                    0,
                    0,
                    $"aceleración Paddle no preparada: {fastBackendProblem}");
            }

            progress?.Report(new AnalysisProgress(
                93,
                100,
                $"PaddleOCR-VL: segunda pasada de precisión sobre {enabledCount} zona(s) de texto, en paralelo…"));
            await UnloadOllamaModelsAsync(cancellationToken);
            string response = await RunWorkerAsync(
                sourcePath,
                regions,
                projectRoot,
                cancellationToken);
            IReadOnlyList<HunyuanTextSpot> spots = HunyuanTextSpotting.Parse(
                response,
                width,
                height);
            if (spots.Count == 0)
            {
                return new PaddleOcrPassResult(true, 0, 0, "no devolvió bloques utilizables");
            }

            SaveSpots(cachePath, width, height, spots);
            // Guardar la lectura cruda permite aplicar la limpieza una sola vez,
            // también al recuperar la caché con textos corregidos por el usuario.
            spots = CleanVisualSpots(regions, spots);
            int replacements = PaddleCropAssociation.ApplyToRegions(regions, spots);
            return new PaddleOcrPassResult(
                true,
                spots.Count,
                replacements,
                replacements > 0
                    ? $"{replacements} zona(s) corregidas con {spots.Count} bloque(s) visuales"
                    : $"{spots.Count} bloque(s) visuales, sin cambios más fiables");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                               or JsonException
                                               or InvalidOperationException
                                               or Win32Exception
                                               or TaskCanceledException)
        {
            return new PaddleOcrPassResult(false, 0, 0, $"no disponible: {exception.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsDisabled()
    {
        string? value = Environment.GetEnvironmentVariable("TINTAES_PADDLE_OCR");
        return value is not null
               && (value.Equals("0", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPrepared(string projectRoot) =>
        File.Exists(LocalEnginePaths.GetPaddlePython(projectRoot))
        && File.Exists(GetWorkerPath(projectRoot));

    private static bool IsFastBackendPrepared(string projectRoot, out string problem)
    {
        string home = LocalEnginePaths.GetPaddleRoot(projectRoot);
        string? explicitServer = Environment.GetEnvironmentVariable("TINTAES_PADDLE_LLAMA_SERVER");
        string serverPath;
        if (!string.IsNullOrWhiteSpace(explicitServer))
        {
            serverPath = explicitServer.Trim().Trim('"');
        }
        else
        {
            string pathFile = Path.Combine(home, "llama-server.path");
            if (!File.Exists(pathFile))
            {
                problem = "falta llama-server.path; vuelve a ejecutar setup-paddleocr.ps1";
                return false;
            }
            try
            {
                serverPath = File.ReadAllText(pathFile, Encoding.UTF8).Trim().Trim('"');
            }
            catch (IOException exception)
            {
                problem = $"no se puede leer llama-server.path: {exception.Message}";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
        {
            problem = "no se encuentra llama-server.exe; vuelve a ejecutar setup-paddleocr.ps1";
            return false;
        }

        string modelRoot = Path.Combine(home, "models", "llama");
        string model = Environment.GetEnvironmentVariable("TINTAES_PADDLE_LLAMA_MODEL")
                       ?? Path.Combine(modelRoot, "PaddleOCR-VL-1.6-GGUF.gguf");
        string mmproj = Environment.GetEnvironmentVariable("TINTAES_PADDLE_LLAMA_MMPROJ")
                        ?? Path.Combine(modelRoot, "PaddleOCR-VL-1.6-GGUF-mmproj.gguf");
        if (!File.Exists(model) || !File.Exists(mmproj))
        {
            problem = "faltan los GGUF de PaddleOCR-VL; vuelve a ejecutar setup-paddleocr.ps1";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static string GetWorkerPath(string projectRoot) =>
        Path.Combine(projectRoot, "engine", "paddleocr", "ocr_page.py");

    private static async Task<string> RunWorkerAsync(
        string sourcePath,
        IReadOnlyList<ComicRegion> regions,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        string python = LocalEnginePaths.GetPaddlePython(projectRoot);
        string worker = GetWorkerPath(projectRoot);
        string home = LocalEnginePaths.GetPaddleRoot(projectRoot);
        Directory.CreateDirectory(home);

        string manifestPath = Path.Combine(
            Path.GetTempPath(),
            $"tintaes-paddle-regions-{Guid.NewGuid():N}.json");
        var manifest = regions
            .Where(region => region.IsEnabled)
            .Select(region =>
            {
                NormalizedRect box = PaddleCropAssociation.GetCropBox(region);
                return new
                {
                    id = region.Id,
                    bbox = new[] { box.X, box.Y, box.Right, box.Bottom }
                };
            })
            .ToArray();
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = Path.GetDirectoryName(worker) ?? projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(worker);
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add(manifestPath);
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["TINTAES_PADDLE_MODEL_HOME"] = Path.Combine(home, "models");

        // Esta invocación sólo tiene sentido si entra por el servidor llama.cpp.
        // El worker Python históricamente caía silenciosamente a Transformers y
        // podía dejar una página 10-15 minutos en "releyendo". Un engine inválido
        // actúa como fusible: no se usa cuando llama.cpp funciona y hace fallar de
        // inmediato cualquier fallback lento, para continuar con el OCR clásico.
        startInfo.Environment["TINTAES_PADDLE_ENGINE"] = FastServerRequiredEngine;
        if (!startInfo.Environment.ContainsKey("TINTAES_PADDLE_MAX_NEW_TOKENS"))
        {
            // 256 tokens son muchísimo más texto del que cabe en un bocadillo normal
            // y limitan los crops patológicos que no emiten EOS y se eternizan.
            startInfo.Environment["TINTAES_PADDLE_MAX_NEW_TOKENS"] = "256";
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("No se pudo iniciar PaddleOCR-VL.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await WaitForWorkerExitAsync(process, TimeSpan.FromMinutes(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await Task.WhenAll(outputTask, errorTask);
                throw;
            }

            string output = await outputTask;
            string error = await errorTask;
            if (process.ExitCode != 0)
            {
                string detail = string.Join(
                    " ",
                    error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(4));
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"PaddleOCR-VL terminó con el código {process.ExitCode}."
                        : detail);
            }

            string? result = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.StartsWith(ResultPrefix, StringComparison.Ordinal));
            if (result is null)
            {
                throw new InvalidOperationException("PaddleOCR-VL no devolvió un resultado JSON.");
            }
            return result[ResultPrefix.Length..];
        }
        finally
        {
            try
            {
                File.Delete(manifestPath);
            }
            catch (IOException)
            {
                // El sistema limpiará el temporal si el worker sigue cerrando el archivo.
            }
        }
    }

    internal static IReadOnlyList<HunyuanTextSpot> CleanVisualSpots(
        IReadOnlyList<ComicRegion> regions,
        IReadOnlyList<HunyuanTextSpot> spots)
    {
        var cleaned = new List<HunyuanTextSpot>(spots.Count);
        foreach (HunyuanTextSpot spot in spots)
        {
            ComicRegion? owner = PaddleCropAssociation.FindOwner(regions, spot.Box);
            if (owner is null)
            {
                continue;
            }
            string text = Regex.Replace(
                spot.Text,
                @"\b\d{1,3}:\d+(?:\.\d+)?/\d+/\d+(?:\.\d+)?\b",
                " ",
                RegexOptions.CultureInvariant);

            foreach (ComicRegion other in regions.Where(region => region.IsEnabled && region != owner))
            {
                string otherText = Regex.Replace(other.Original.Trim(), @"\s+", " ");
                string comparable = ComparableLetters(otherText);
                if (comparable.Length is < 4 or > 24
                    || (other.Type != "sfx" && other.Type != "sign"))
                {
                    continue;
                }

                string pattern = Regex.Escape(otherText).Replace("\\ ", @"\s+");
                text = Regex.Replace(
                    text,
                    pattern,
                    " ",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                Match trailing = Regex.Match(text, @"(?<word>[\p{L}\p{N}]+)[.!?]*\s*$");
                if (trailing.Success
                    && LevenshteinDistance(
                        ComparableLetters(trailing.Groups["word"].Value),
                        comparable) <= 1)
                {
                    text = text[..trailing.Index];
                }
            }

            text = Regex.Replace(text, @"\s+", " ").Trim(' ', ',', ';', ':');
            if (!string.IsNullOrWhiteSpace(text))
            {
                cleaned.Add(new HunyuanTextSpot(text, spot.Box));
            }
        }
        return cleaned;
    }

    private static string ComparableLetters(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return Math.Max(left.Length, right.Length);
        }
        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] current = new int[right.Length + 1];
        for (int row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (int column = 1; column <= right.Length; column++)
            {
                int substitution = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitution);
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    internal static async Task WaitForWorkerExitAsync(
        Process process,
        TimeSpan timeLimit,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeLimit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            // No dejar otro modelo ocupando GPU ni liberar el semáforo hasta que salga.
            await process.WaitForExitAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TaskCanceledException("PaddleOCR-VL superó el límite de tiempo.");
        }
    }

    private static void TryKill(Process process)
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
            // El proceso ya terminó.
        }
    }

    private static async Task UnloadOllamaModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage running = await Ollama.GetAsync("api/ps", cancellationToken);
            if (!running.IsSuccessStatusCode)
            {
                return;
            }
            using JsonDocument document = JsonDocument.Parse(
                await running.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("models", out JsonElement models)
                || models.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (JsonElement item in models.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out JsonElement nameElement)
                    || string.IsNullOrWhiteSpace(nameElement.GetString()))
                {
                    continue;
                }
                using var content = JsonContent.Create(new
                {
                    model = nameElement.GetString(),
                    keep_alive = 0
                });
                using HttpResponseMessage _ = await Ollama.PostAsync(
                    "api/generate",
                    content,
                    cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
                                               or JsonException
                                               or TaskCanceledException)
        {
            // Ollama puede no estar abierto todavía; el OCR sigue siendo utilizable.
        }
    }

    private static (int Width, int Height) ReadImageSize(byte[] imageBytes)
    {
        using var stream = new MemoryStream(imageBytes, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    internal static string CreateCachePath(
        byte[] imageBytes,
        string projectRoot,
        IReadOnlyList<ComicRegion> regions)
    {
        var identity = new StringBuilder(CacheVersion)
            .Append('|').Append(Environment.GetEnvironmentVariable("TINTAES_PADDLE_DEVICE"))
            .Append('|').Append(Environment.GetEnvironmentVariable("TINTAES_PADDLE_ENGINE"))
            .Append('|').Append(Convert.ToHexString(SHA256.HashData(imageBytes)));
        AppendFileIdentity(identity, GetWorkerPath(projectRoot));
        AppendFileIdentity(identity, LocalEnginePaths.GetPaddlePython(projectRoot));
        foreach (ComicRegion region in regions.Where(region => region.IsEnabled))
        {
            NormalizedRect box = PaddleCropAssociation.GetCropBox(region);
            // No incluir IDs efímeros: el manifiesto orgánico los regenera al abrir.
            foreach (double coordinate in new[] { box.X, box.Y, box.Right, box.Bottom })
            {
                identity.Append('|').Append(coordinate.ToString("R", CultureInfo.InvariantCulture));
            }
        }
        string key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))[..32]
            .ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TintaES",
            "Cache",
            "PaddleOCR",
            $"{key}.json");
    }

    private static void AppendFileIdentity(StringBuilder identity, string path)
    {
        if (!File.Exists(path))
        {
            identity.Append("|missing");
            return;
        }
        var file = new FileInfo(path);
        identity.Append('|').Append(file.FullName)
            .Append('|').Append(file.Length)
            .Append('|').Append(file.LastWriteTimeUtc.Ticks);
    }

    internal static bool TryLoadSpots(
        string cachePath,
        int width,
        int height,
        out IReadOnlyList<HunyuanTextSpot> spots)
    {
        spots = [];
        if (!File.Exists(cachePath))
        {
            return false;
        }
        try
        {
            PaddleSpotCache? cache = JsonSerializer.Deserialize<PaddleSpotCache>(
                File.ReadAllText(cachePath, Encoding.UTF8),
                JsonOptions);
            if (cache is null
                || !string.Equals(cache.Version, CacheVersion, StringComparison.Ordinal)
                || cache.Width != width
                || cache.Height != height
                || cache.Spots is null
                || cache.Spots.Count == 0)
            {
                return false;
            }
            spots = cache.Spots;
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    internal static void SaveSpots(
        string cachePath,
        int width,
        int height,
        IReadOnlyList<HunyuanTextSpot> spots)
    {
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(cachePath)
                               ?? throw new InvalidOperationException("La caché OCR no tiene carpeta.");
            Directory.CreateDirectory(directory);
            temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    new PaddleSpotCache(CacheVersion, width, height, spots),
                    JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, cachePath, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException)
        {
            // La caché solo acelera ejecuciones posteriores.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Otro proceso puede estar moviendo el mismo archivo.
                }
            }
        }
    }

    private sealed record PaddleSpotCache(
        string Version,
        int Width,
        int Height,
        IReadOnlyList<HunyuanTextSpot> Spots);
}
