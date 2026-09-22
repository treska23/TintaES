using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TintaES.Core;
using TintaES.Wpf;

/// <summary>
/// Verifica que el ejecutable independiente conserve las páginas originales, incluso
/// las pendientes de traducir, y que no reagrupe ni borre las traducciones almacenadas.
/// </summary>
internal static class ReaderProjectLoaderRegression
{
    internal static async Task<int> RunAsync()
    {
        string projectPath = Path.Combine(
            Path.GetTempPath(), "tinta-reader-regression-" + Guid.NewGuid().ToString("N") + ".tinta");
        try
        {
            var first = new ComicRegion
            {
                Original = "HELLO",
                Translation = "HOLA",
                Type = "dialogue",
                TextBox = new NormalizedRect(100, 100, 80, 30),
                BubbleBox = new NormalizedRect(90, 85, 130, 90),
                BubbleConfidence = 0.9
            };
            var second = new ComicRegion
            {
                Original = "HEY",
                Translation = "EH",
                Type = "text",
                TextBox = new NormalizedRect(102, 133, 70, 20)
            };

            using (var stream = File.Create(projectPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                AddText(archive, "source/0001.png", "ORIGINAL_PAGE_1");
                AddText(archive, "source/0002.png", "ORIGINAL_PAGE_2");
                AddText(archive, "processed/0001-mask.png", "NOT_A_COMIC_PAGE");

                var manifest = new
                {
                    version = 1,
                    title = "Regresión Reader",
                    currentPageIndex = 0,
                    pages = new[]
                    {
                        new
                        {
                            displayName = "001.png",
                            sourceFile = "source/0001.png",
                            regions = new[] { first, second }
                        },
                        new
                        {
                            displayName = "002.png",
                            sourceFile = "source/0002.png",
                            regions = Array.Empty<ComicRegion>()
                        }
                    }
                };
                ZipArchiveEntry entry = archive.CreateEntry("project.json");
                using var json = entry.Open();
                JsonSerializer.Serialize(json, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            using ReaderComicDocument document = await ReaderTintaProjectLoader.LoadAsync(projectPath);
            Require(document.Pages.Count == 2, "Deben incluirse también las páginas no traducidas.");
            Require(
                File.ReadAllText(document.Pages[0].SourcePath) == "ORIGINAL_PAGE_1"
                && File.ReadAllText(document.Pages[1].SourcePath) == "ORIGINAL_PAGE_2",
                "Las imágenes fuente no pueden reemplazarse por máscaras procesadas.");
            Require(
                document.Pages[0].Regions.Count == 2
                && document.Pages[0].Regions[0].Translation == "HOLA"
                && document.Pages[0].Regions[1].Translation == "EH",
                "El lector no puede volver a agrupar las regiones ni borrar traducciones guardadas.");
            Require(
                document.Pages[1].Regions.Count == 0,
                "Una página sin OCR debe permanecer visible en el lector.");

            Console.WriteLine("Reader project regression: OK");
            return 0;
        }
        finally
        {
            if (File.Exists(projectPath))
            {
                File.Delete(projectPath);
            }
        }
    }

    private static void AddText(ZipArchive archive, string entryName, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Regresión de proyecto Reader: " + message);
        }
    }
}
