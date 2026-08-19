using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);
    private static readonly SemaphoreSlim ServerGate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(4) };
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
        bool available = await EnsureAvailableAsync(endpoint, projectRoot, cancellationToken);
        if (!available)
        {
            return new HunyuanOcrPassResult(false, 0, 0, "modelo local no preparado");
        }

        progress?.Report(new AnalysisProgress(93, 100, "HunyuanOCR está leyendo la página completa…"));
        try
        {
            (int width, int height) = ReadImageSize(sourcePath);
            string model = await ResolveModelAsync(endpoint, cancellationToken);
            string response = await RequestSpottingAsync(endpoint, model, sourcePath, cancellationToken);
            IReadOnlyList<HunyuanTextSpot> spots = HunyuanTextSpotting.Parse(response, width, height);
            if (spots.Count == 0)
            {
                return new HunyuanOcrPassResult(true, 0, 0, "HunyuanOCR no devolvió bloques utilizables");
            }

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
            return new HunyuanOcrPassResult(true, 0, 0, $"falló HunyuanOCR: {exception.Message}");
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartTimeout);
        while (!timeout.IsCancellationRequested)
        {
            if (await ProbeAsync(endpoint, timeout.Token))
            {
                return true;
            }

            try
            {
                await Task.Delay(500, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
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

        using HttpResponseMessage response = await Http.GetAsync($"{endpoint}/v1/models", cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Array
            && data.GetArrayLength() > 0
            && data[0].TryGetProperty("id", out JsonElement id)
            && !string.IsNullOrWhiteSpace(id.GetString()))
        {
            return id.GetString()!;
        }
        return DefaultAlias;
    }

    private static async Task<string> RequestSpottingAsync(
        string endpoint,
        string model,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
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

    private static (int Width, int Height) ReadImageSize(string sourcePath)
    {
        using FileStream stream = File.OpenRead(sourcePath);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static string? FindServerExecutable(string projectRoot)
    {
        string root = Path.Combine(projectRoot, "engine", "hunyuanocr");
        return new[]
            {
                Path.Combine(root, "llama.cpp", "build", "bin", "Release", "llama-server.exe"),
                Path.Combine(root, "llama.cpp", "build", "bin", "llama-server.exe"),
                Path.Combine(root, "llama-server.exe")
            }
            .FirstOrDefault(File.Exists);
    }

    private static (string? Model, string? Mmproj) FindModels(string projectRoot)
    {
        string root = Path.Combine(projectRoot, "engine", "hunyuanocr");
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
}
