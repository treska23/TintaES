using System.IO;

namespace TintaES.Wpf;

internal static class LocalEnginePaths
{
    internal static string GetMangaPython(string projectRoot) =>
        Path.Combine(
            projectRoot,
            "engine",
            "manga-image-translator",
            ".venv",
            "Scripts",
            "python.exe");
}
