using System.Net;
using System.Text;
using System.Text.Json;
using TintaES.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Sanea cajas gigantes", TestSanitizeAsync),
    ("Combina lecturas solapadas", TestMergeAsync),
    ("Separa detección y traducción", TestOllamaPipelineAsync)
};

int failures = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"OK  {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} pruebas superadas");
return failures == 0 ? 0 : 1;

static Task TestSanitizeAsync()
{
    var region = new ComicRegion
    {
        Original = "\"SMASH\"",
        Type = "sfx",
        TextBox = new NormalizedRect(420, 300, 80, 45),
        RenderBox = new NormalizedRect(180, 120, 600, 720),
        Confidence = 2,
        Style = new ComicTextStyle
        {
            FontFamily = "  Impact  ",
            FontWeight = 947,
            FontSize = 500,
            FontWidthRatio = 3,
            LineHeightRatio = 0.2,
            OriginalLineCount = 99
        }
    };
    RegionMerger.Sanitize(region);
    Assert(region.Original == "SMASH", "Debe retirar comillas artificiales.");
    Assert(region.RenderBox.Area < 20_000, "Debe sustituir una caja de panel completo.");
    Assert(region.Confidence == 1, "Debe limitar la confianza.");
    Assert(region.Style.FontFamily == "Impact", "Debe limpiar el nombre de la fuente detectada.");
    Assert(region.Style.FontSize == 250, "Debe limitar tamaños tipográficos imposibles.");
    Assert(region.Style.FontWidthRatio == 1.5, "Debe limitar la anchura tipográfica.");
    Assert(region.Style.LineHeightRatio == 0.8, "Debe limitar el interlineado.");
    Assert(region.Style.OriginalLineCount == 20, "Debe limitar el número de líneas detectado.");
    return Task.CompletedTask;
}

static Task TestMergeAsync()
{
    var first = new ComicRegion
    {
        Original = "HELLO!",
        Confidence = 0.8,
        TextBox = new NormalizedRect(100, 100, 120, 50),
        RenderBox = new NormalizedRect(90, 90, 150, 80)
    };
    var duplicate = new ComicRegion
    {
        Original = "Hello!",
        Confidence = 0.95,
        TextBox = new NormalizedRect(105, 103, 118, 48),
        RenderBox = new NormalizedRect(92, 92, 152, 78)
    };
    var second = new ComicRegion
    {
        Original = "BOOM!",
        Confidence = 0.9,
        TextBox = new NormalizedRect(400, 600, 150, 90),
        RenderBox = new NormalizedRect(390, 590, 170, 110)
    };
    IReadOnlyList<ComicRegion> merged = RegionMerger.Merge([first, duplicate, second]);
    Assert(merged.Count == 2, "Las zonas solapadas del mismo texto deben combinarse.");
    Assert(merged.Any(region => region.Confidence == 0.95), "Debe conservar la lectura más fiable.");
    return Task.CompletedTask;
}

static async Task TestOllamaPipelineAsync()
{
    var handler = new FakeOllamaHandler();
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
    using var client = new OllamaClient(httpClient: http);
    var tile = new ComicImageTile(1, 1, 0, 0, 1000, 1000, 1000, 1000, [1, 2, 3, 4]);
    ComicAnalysis result = await client.AnalyzePageAsync([tile], "qwen3.5:9b");
    Assert(result.Regions.Count == 1, "Debe devolver la región detectada.");
    Assert(result.Regions[0].Translation == "¡CRASH!", "La traducción separada no puede quedar vacía.");
    Assert(result.Regions[0].RenderBox.Area < 20_000, "Debe corregir la caja gigante del detector.");
    Assert(result.Regions[0].Style.FontFamily == "Impact", "Debe conservar la familia tipográfica detectada.");
    Assert(Math.Abs(result.Regions[0].Style.FontSize - 52) < 0.01, "Debe convertir el tamaño detectado a coordenadas de página.");
    Assert(result.Regions[0].Style.OriginalLineCount == 1, "Debe conservar el número de líneas original.");
    Assert(handler.VisionCalls == 1 && handler.TranslationCalls == 1, "Debe realizar detección y traducción por separado.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeOllamaHandler : HttpMessageHandler
{
    public int VisionCalls { get; private set; }
    public int TranslationCalls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
        {
            return Json(new { models = new[] { new { name = "qwen3.5:9b", size = 123L } } });
        }

        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using JsonDocument requestDocument = JsonDocument.Parse(body);
        string prompt = requestDocument.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        bool hasImages = requestDocument.RootElement.GetProperty("messages")[0].TryGetProperty("images", out _);
        if (hasImages)
        {
            VisionCalls++;
            string detection = JsonSerializer.Serialize(new
            {
                source_language = "en",
                regions = new[]
                {
                    new
                    {
                        original = "\"SMASH\"",
                        type = "sfx",
                        confidence = 0.98,
                        text_box = new { x = 420, y = 300, width = 80, height = 45 },
                        render_box = new { x = 180, y = 120, width = 600, height = 720 },
                        rotation = 0,
                        vertical = false,
                        style = new
                        {
                            font_category = "display",
                            font_family = "Impact",
                            font_weight = 900,
                            font_size = 52,
                            font_width_ratio = 0.88,
                            line_height_ratio = 1.02,
                            line_count = 1,
                            italic = false,
                            uppercase = true,
                            text_color = "#111111",
                            outline_color = (string?)null,
                            outline_width = 0,
                            alignment = "center",
                            background_color = "#FFFFFF",
                            shadow = false
                        }
                    }
                }
            });
            return Json(new { message = new { content = detection } });
        }

        TranslationCalls++;
        int marker = prompt.IndexOf("ELEMENTOS:", StringComparison.Ordinal);
        using JsonDocument items = JsonDocument.Parse(prompt[(marker + "ELEMENTOS:".Length)..].Trim());
        string id = items.RootElement[0].GetProperty("id").GetString()!;
        string translation = JsonSerializer.Serialize(new
        {
            translations = new[] { new { id, translation = "¡CRASH!" } }
        });
        return Json(new { message = new { content = translation } });
    }

    private static HttpResponseMessage Json(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}