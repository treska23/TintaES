using System.IO;

namespace TintaES.Wpf.Services;

internal static class LocalEnginePaths
{
    public static string GetMangaPython(string projectRoot)
    {
        string? configured = Environment.GetEnvironmentVariable("TINTAES_MANGA_PYTHON");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                projectRoot,
                "engine",
                "manga-image-translator",
                ".venv",
                "Scripts",
                "python.exe")
            : Path.GetFullPath(configured.Trim());
    }

    public static string GetHunyuanRoot(string projectRoot)
    {
        string? configured = Environment.GetEnvironmentVariable("TINTAES_HUNYUAN_HOME");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(projectRoot, "engine", "hunyuanocr")
            : Path.GetFullPath(configured.Trim());
    }
}
