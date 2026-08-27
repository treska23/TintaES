using System.IO;
using System.Text.Json;

namespace TintaES.Wpf.Services;

internal static class LocalEnginePaths
{
    public static string GetMangaPython(string projectRoot)
    {
        string? configured = GetConfiguredPath(projectRoot, "TINTAES_MANGA_PYTHON", "mangaPython");
        return configured is null
            ? Path.Combine(
                projectRoot,
                "engine",
                "manga-image-translator",
                ".venv",
                "Scripts",
                "python.exe")
            : configured;
    }

    public static string? GetMangaModelDirectory(string projectRoot) =>
        GetConfiguredPath(projectRoot, "TINTAES_MANGA_MODEL_DIR", "mangaModelDirectory");

    public static string GetHunyuanRoot(string projectRoot)
    {
        string? configured = GetConfiguredPath(projectRoot, "TINTAES_HUNYUAN_HOME", "hunyuanHome");
        return configured is null
            ? Path.Combine(projectRoot, "engine", "hunyuanocr")
            : configured;
    }

    public static string GetPaddleRoot(string projectRoot)
    {
        string? configured = GetConfiguredPath(projectRoot, "TINTAES_PADDLE_HOME", "paddleOcrHome");
        return configured ?? Path.Combine(projectRoot, "engine", "paddleocr");
    }

    public static string GetPaddlePython(string projectRoot)
    {
        string? configured = GetConfiguredPath(projectRoot, "TINTAES_PADDLE_PYTHON", "paddlePython");
        return configured ?? Path.Combine(
            GetPaddleRoot(projectRoot),
            ".venv",
            "Scripts",
            "python.exe");
    }

    private static string? GetConfiguredPath(
        string projectRoot,
        string environmentVariable,
        string jsonProperty)
    {
        string? configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        string configPath = Path.Combine(projectRoot, "engine", "local-engine-paths.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.TryGetProperty(jsonProperty, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return Path.GetFullPath(value.GetString()!.Trim());
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or ArgumentException)
        {
            return null;
        }

        return null;
    }
}
