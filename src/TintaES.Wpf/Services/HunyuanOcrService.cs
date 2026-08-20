using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using TintaES.Core;

namespace TintaES.Wpf.Services;

public sealed record HunyuanOcrPassResult(
    bool Available,
    int Spots,
    int Replacements,
    string Detail);

/// <summary>
/// Fuente primaria de reconocimiento visual. Usa HunyuanOCR-1.5 a través de llama-server
/// (API compatible con OpenAI). Si no está preparado, TintaES conserva automáticamente
/// el reconocimiento clásico; la ausencia del modelo nunca rompe el análisis.
/// </summary>
public sealed class HunyuanOcrService
{
    private const string DefaultEndpoint = "http://127.0.0.1:8080";
    private const string DefaultAlias = "HYVL";
    private const string SpotCacheVersion = "hunyuan-spots-v1";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);
    private static readonly SemaphoreSlim ServerGate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(4) };
    private static readonly ConcurrentDictionary<string, string> ResolvedModels =
        new(StringComparer.OrdinalIgnoreCase);
    private static Process? _serverProcess;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private const string ComicSpottingPrompt = """
检测并识别这张漫画页面中的所有文字。把属于同一个对话气泡、思想气泡、旁白框、标题框、招牌或同一视觉文本容器的多行文字合并成一个完整文本块。不要翻译，不要改写，不要猜测看不清的字。
只返回严格 JSON 数组，不要 Markdown，不要解释。每个元素必须是：
{"text":"完整原文","bbox":[x1,y1,x2,y2]}
坐标使用整张图片归一化后的 0-1000 坐标。bbox 必须覆盖该完整文本块，而不是只覆盖最后一行。
忽略没有可读文字的图形。保持页面阅读顺序。
""";

    public async Task<bool> WarmUpAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        if (IsDisabled())
        {
            return false;
        }

        string endpoint = ResolveEndpoint();
        try
        {
            if (!await EnsureAvailableAsync(endpoint, projectRoot, cancellationToken))
            {
                return false;
            }

            _ = await ResolveModelAsync(endpoint, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or JsonException
                or InvalidOperationException
                or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<bool> HasCachedResultAsync(
        string sourcePath,
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        if (IsDisabled())
        {
            return true;
        }

        try
        {
            byte[] imageBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            (int width, int height) = ReadImageSize(imageBytes);
            string cachePath = CreateSpotCachePath(
                imageBytes,
                ResolveEndpoint(),
                projectRoot);
            return TryLoadSpots(cachePath, width, height, out _);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    public async Task<HunyuanOcrPassResult> TryImproveAsync(
        string sourcePath,
        IReadOnlyList<ComicRegion> regions,
        string projectRoot,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsDisabled())
        {
            return new HunyuanOcrPassResult(false, 0, 0, "desactivado por configuración");
        }

        string endpoint = ResolveEndpoint();
        try
        {
            byte[] imageBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            (int width, int height) = ReadImageSize(imageBytes);
            string cachePath = CreateSpotCachePath(imageBytes, endpoint, projectRoot);
            if (TryLoadSpots(cachePath, width, height, out IReadOnlyList<HunyuanTextSpot> cachedSpots))
            {
                int cachedReplacements = HunyuanTextSpotting.ApplyToRegions(regions, cachedSpots);
                return new HunyuanOcrPassResult(
                    true,
                    cachedSpots.Count,
                    cachedReplacements,
                    $"{cachedReplacements} zona(s) corregidas desde la caché visual");
            }

            bool available = await EnsureAvailableAsync(endpoint, projectRoot, cancellationToken);
            if (!available)
            {
                return new HunyuanOcrPassResult(false, 0, 0, "modelo local no preparado");
            }

            progress?.Report(new AnalysisProgress(93, 100, "HunyuanOCR está leyendo la página completa…"));
            string model = await ResolveModelAsync(endpoint, cancellationToken);
            string response = await RequestSpottingAsync(
                endpoint,
                model,
                sourcePath,
                imageBytes,
                cancellationToken);
            IReadOnlyList<HunyuanTextSpot> spots = HunyuanTextSpotting.Parse(response, width, height);
            if (spots.Count == 0)
            {
                return new HunyuanOcrPassResult(true, 0, 0, "HunyuanOCR no devolvió bloques utilizables");
            }

            SaveSpots(cachePath, width, height, spots);
            int replacements = HunyuanTextSpotting.ApplyToRegions(regions, spots);
            return new HunyuanOcrPassResult(
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
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or JsonException
                or InvalidOperationException
                or TaskCanceledException)
        {
            return new HunyuanOcrPassResult(false, 0, 0, $"HunyuanOCR no disponible: {exception.Message}");
        }
        finally
        {
            // Con 8 GB, mantener Hunyuan, CTD y TranslateGemma a la vez llena la
            // VRAM. El servidor CUDA tarda unos dos segundos en cargar y una página
            // cacheada no lo arranca, así que liberarlo entre etapas mejora el flujo
            // completo sin cambiar ninguna operación del modelo.
            StopOwnedServer();
        }
    }

    private static bool IsDisabled()
    {
        string? value = Environment.GetEnvironmentVariable("TINTAES_HUNYUAN_OCR");
        return value is not null
               && (value.Equals("0", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveEndpoint()
    {
        string? configured = Environment.GetEnvironmentVariable("TINTAES_HUNYUAN_OCR_URL");
        return string.IsNullOrWhiteSpace(configured)
            ? DefaultEndpoint
            : configured.Trim().TrimEnd('/');
    }

    private static async Task<bool> EnsureAvailableAsync(
        string endpoint,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        if (await ProbeAsync(endpoint, cancellationToken))
        {
            return true;
        }

        // Un endpoint remoto o personalizado debe ser gestionado por el usuario; solo
        // autoarrancamos la instalación local estándar de TintaES.
        if (!endpoint.StartsWith("http://127.0.0.1:8080", StringComparison.OrdinalIgnoreCase)
            && !endpoint.StartsWith("http://localhost:8080", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await ServerGate.WaitAsync(cancellationToken);
        try
        {
            if (await ProbeAsync(endpoint, cancellationToken))
            {
                return true;
            }

            if (_serverProcess is { HasExited: false })
            {
                return await WaitUntilReadyAsync(endpoint, cancellationToken);
            }

            string? executable = FindServerExecutable(projectRoot);
            (string? model, string? mmproj) = FindModels(projectRoot);
            if (executable is null || model is null || mmproj is null)
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
            startInfo.ArgumentList.Add("--mmproj");
            startInfo.ArgumentList.Add(mmproj);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add("8080");
            startInfo.ArgumentList.Add("--alias");
            startInfo.ArgumentList.Add(DefaultAlias);
            startInfo.ArgumentList.Add("--ctx-size");
            startInfo.ArgumentList.Add("10240");
            startInfo.ArgumentList.Add("--n-predict");
            startInfo.ArgumentList.Add("4096");
            // TintaES envía una sola página cada vez. Un único slot evita reservar
            // memoria para peticiones paralelas que la aplicación nunca realiza.
            startInfo.ArgumentList.Add("--parallel");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--gpu-layers");
            startInfo.ArgumentList.Add("all");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                process.Dispose();
                return false;
            }

            _serverProcess = process;
            _ = DrainAsync(process.StandardOutput);
            _ = DrainAsync(process.StandardError);
            return await WaitUntilReadyAsync(endpoint, cancellationToken);
        }
        finally
        {
            ServerGate.Release();
        }
    }

    private static async Task<bool> ProbeAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
            using HttpResponseMessage response = await Http.GetAsync($"{endpoint}/v1/models", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitUntilReadyAsync(string endpoint, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ProbeAsync(endpoint, cancellationToken))
            {
                return true;
            }
            await Task.Delay(500, cancellationToken);
        }
        return false;
    }

    private static async Task<string> ResolveModelAsync(string endpoint, CancellationToken cancellationToken)
    {
        string? configured = Environment.GetEnvironmentVariable("TINTAES_HUNYUAN_OCR_MODEL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        if (ResolvedModels.TryGetValue(endpoint, out string? cachedModel))
        {
            return cachedModel;
        }

        using HttpResponseMessage response = await Http.GetAsync($"{endpoint}/v1/models", cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Array
            && data.GetArrayLength() > 0
            && data[0].TryGetProperty("id", out JsonElement id)
            && !string.IsNullOrWhiteSpace(id.GetString()))
        {
            string resolved = id.GetString()!;
            ResolvedModels[endpoint] = resolved;
            return resolved;
        }
        ResolvedModels[endpoint] = DefaultAlias;
        return DefaultAlias;
    }

    private static async Task<string> RequestSpottingAsync(
        string endpoint,
        string model,
        string sourcePath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        string mime = Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "image/png"
        };
        string dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

        var request = new
        {
            model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = string.Empty
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = dataUrl } },
                        new { type = "text", text = ComicSpottingPrompt }
                    }
                }
            },
            max_tokens = 4096,
            temperature = 0.0,
            top_p = 1.0
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(request, JsonOptions),
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await Http.PostAsync(
            $"{endpoint}/v1/chat/completions",
            content,
            cancellationToken);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {payload}");
        }

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("content", out JsonElement answer))
        {
            throw new InvalidOperationException("La respuesta de HunyuanOCR no contiene texto.");
        }

        if (answer.ValueKind == JsonValueKind.String)
        {
            return answer.GetString() ?? string.Empty;
        }

        if (answer.ValueKind == JsonValueKind.Array)
        {
            return string.Join(
                "\n",
                answer.EnumerateArray()
                    .Where(item => item.TryGetProperty("text", out _))
                    .Select(item => item.GetProperty("text").GetString())
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
        }

        return answer.ToString();
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

    private static string? FindServerExecutable(string projectRoot)
    {
        string root = LocalEnginePaths.GetHunyuanRoot(projectRoot);
        string? configured = Environment.GetEnvironmentVariable("TINTAES_HUNYUAN_SERVER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
        {
            return Path.GetFullPath(configured.Trim());
        }

        return new[]
            {
                Path.Combine(root, "runtime", "cuda", "llama-server.exe"),
                Path.Combine(root, "runtime", "cpu", "llama-server.exe"),
                Path.Combine(root, "llama.cpp", "build", "bin", "Release", "llama-server.exe"),
                Path.Combine(root, "llama.cpp", "build", "bin", "llama-server.exe"),
                Path.Combine(root, "llama-server.exe")
            }
            .FirstOrDefault(File.Exists);
    }

    private static (string? Model, string? Mmproj) FindModels(string projectRoot)
    {
        string root = LocalEnginePaths.GetHunyuanRoot(projectRoot);
        string[] folders =
        [
            Path.Combine(root, "model"),
            Path.Combine(root, "HunyuanOCR"),
            root
        ];
        foreach (string folder in folders)
        {
            string model = Path.Combine(folder, "hyocr-f16.gguf");
            string mmproj = Path.Combine(folder, "mmproj-hyocr-f16.gguf");
            if (File.Exists(model) && File.Exists(mmproj))
            {
                return (model, mmproj);
            }
        }
        return (null, null);
    }

    private static string CreateSpotCachePath(
        byte[] imageBytes,
        string endpoint,
        string projectRoot)
    {
        var identity = new StringBuilder();
        identity.Append(SpotCacheVersion)
            .Append('|').Append(endpoint)
            .Append('|').Append(Environment.GetEnvironmentVariable("TINTAES_HUNYUAN_OCR_MODEL"))
            .Append('|').Append(ComicSpottingPrompt)
            .Append('|').Append(Convert.ToHexString(SHA256.HashData(imageBytes)));

        (string? model, string? mmproj) = FindModels(projectRoot);
        AppendFileIdentity(identity, model);
        AppendFileIdentity(identity, mmproj);
        AppendFileIdentity(identity, FindServerExecutable(projectRoot));

        string key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))[..32]
            .ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TintaES",
            "Cache",
            "Hunyuan",
            $"{key}.json");
    }

    private static void AppendFileIdentity(StringBuilder identity, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            identity.Append("|missing");
            return;
        }

        var file = new FileInfo(path);
        identity.Append('|').Append(file.FullName)
            .Append('|').Append(file.Length)
            .Append('|').Append(file.LastWriteTimeUtc.Ticks);
    }

    private static bool TryLoadSpots(
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
            HunyuanSpotCache? cache = JsonSerializer.Deserialize<HunyuanSpotCache>(
                File.ReadAllText(cachePath, Encoding.UTF8),
                JsonOptions);
            if (cache is null
                || !string.Equals(cache.Version, SpotCacheVersion, StringComparison.Ordinal)
                || cache.Width != width
                || cache.Height != height
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

    private static void SaveSpots(
        string cachePath,
        int width,
        int height,
        IReadOnlyList<HunyuanTextSpot> spots)
    {
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(cachePath)
                               ?? throw new InvalidOperationException("La caché de Hunyuan no tiene carpeta.");
            Directory.CreateDirectory(directory);
            temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            var cache = new HunyuanSpotCache(SpotCacheVersion, width, height, spots);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(cache, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, cachePath, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // La caché acelera ejecuciones posteriores, pero nunca decide si el OCR funciona.
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
                    // Otro proceso puede estar terminando de mover el mismo archivo temporal.
                }
            }
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is not null)
            {
                // Mantiene vacíos los pipes del servidor residente.
            }
        }
        catch (IOException)
        {
            // El servidor terminó.
        }
        catch (ObjectDisposedException)
        {
            // El servidor terminó.
        }
    }

    private static void StopOwnedServer()
    {
        Process? process = Interlocked.Exchange(ref _serverProcess, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            // El proceso ya terminó o Windows lo cerró al finalizar la petición.
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed record HunyuanSpotCache(
        string Version,
        int Width,
        int Height,
        IReadOnlyList<HunyuanTextSpot> Spots);
}
