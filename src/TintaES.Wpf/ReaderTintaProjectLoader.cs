using System.IO;
using System.IO.Compression;
using System.Text.Json;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Carga un .tinta para lectura sin arrancar ninguna parte del traductor. Solo extrae las páginas
/// originales y lee las regiones/traducciones de project.json. Máscaras, fondos procesados,
/// Ollama, OCR y el motor Python quedan completamente fuera del lector.
/// </summary>
internal static class ReaderTintaProjectLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    internal static Task<ReaderComicDocument> LoadAsync(
        string projectPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(projectPath, cancellationToken), cancellationToken);

    private static ReaderComicDocument Load(
        string projectPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("No se encuentra el proyecto de TintaES.", projectPath);
        }

        string workspace = Path.Combine(
            Path.GetTempPath(),
            "TintaES",
            "Reader",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            using FileStream input = File.Open(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry manifestEntry = archive.GetEntry("project.json")
                ?? throw new InvalidOperationException(
                    "El archivo .tinta no contiene project.json y no parece un proyecto válido.");

            ReaderProjectManifest manifest;
            using (Stream manifestStream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<ReaderProjectManifest>(manifestStream, JsonOptions)
                    ?? throw new InvalidOperationException("No se pudo leer el proyecto de TintaES.");
            }

            if (manifest.Pages.Count == 0)
            {
                throw new InvalidOperationException("El proyecto no contiene páginas.");
            }

            var pages = new List<ReaderComicPage>(manifest.Pages.Count);
            for (int index = 0; index < manifest.Pages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReaderProjectPage storedPage = manifest.Pages[index];
                string sourceEntryName = NormalizeArchiveEntryName(storedPage.SourceFile);
                ZipArchiveEntry sourceEntry = archive.GetEntry(sourceEntryName)
                    ?? throw new InvalidOperationException(
                        $"Falta la imagen de la página {index + 1} dentro del proyecto.");

                string extension = Path.GetExtension(sourceEntry.Name);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".png";
                }

                // El nombre de salida lo genera el lector; nunca se usa una ruta procedente
                // directamente del ZIP como ruta de escritura.
                string targetPath = Path.Combine(workspace, $"{index + 1:D5}{extension.ToLowerInvariant()}");
                using (Stream source = sourceEntry.Open())
                using (FileStream output = File.Create(targetPath))
                {
                    source.CopyTo(output);
                }

                List<ComicRegion> storedRegions = storedPage.Regions ?? [];
                foreach (ComicRegion region in storedRegions)
                {
                    NormalizeReaderLoadedRegion(region);
                }

                IReadOnlyList<ComicRegion> regions = BalloonRegionGrouper.Group(storedRegions);
                pages.Add(new ReaderComicPage(
                    targetPath,
                    string.IsNullOrWhiteSpace(storedPage.DisplayName)
                        ? sourceEntry.Name
                        : storedPage.DisplayName,
                    regions));
            }

            int initialPage = Math.Clamp(
                manifest.CurrentPageIndex,
                0,
                Math.Max(0, pages.Count - 1));
            return new ReaderComicDocument(
                manifest.Title,
                pages,
                initialPage,
                disposeAction: () => DeleteWorkspace(workspace));
        }
        catch
        {
            DeleteWorkspace(workspace);
            throw;
        }
    }

    /// <summary>
    /// El editor normaliza los datos persistidos antes de usarlos. El Reader debe hacer lo mismo:
    /// de lo contrario TextBox/RenderBox pueden representar geometrías distintas aunque ambos
    /// estén leyendo exactamente el mismo .tinta.
    /// </summary>
    private static void NormalizeReaderLoadedRegion(ComicRegion region)
    {
        region.Style ??= new ComicTextStyle();
        RegionMerger.Sanitize(region);
        region.SafePolygon ??= [];
        region.RenderBox = ResolveReaderConservativeTextFrame(region);

        if (!double.IsFinite(region.FontScale) || region.FontScale <= 0)
        {
            region.FontScale = 1;
        }
        if (!double.IsFinite(region.ManualFontScale) || region.ManualFontScale <= 0)
        {
            region.ManualFontScale = 1;
        }
        if (!double.IsFinite(region.ManualBaseFontSize) || region.ManualBaseFontSize < 0)
        {
            region.ManualBaseFontSize = 0;
        }
        if (!double.IsFinite(region.TextOffsetX))
        {
            region.TextOffsetX = 0;
        }
        if (!double.IsFinite(region.TextOffsetY))
        {
            region.TextOffsetY = 0;
        }
    }

    private static NormalizedRect ResolveReaderConservativeTextFrame(ComicRegion region)
    {
        if (region.SafePolygon.Count >= 3)
        {
            double left = region.SafePolygon.Min(point => point.X);
            double top = region.SafePolygon.Min(point => point.Y);
            double right = region.SafePolygon.Max(point => point.X);
            double bottom = region.SafePolygon.Max(point => point.Y);
            if (double.IsFinite(left)
                && double.IsFinite(top)
                && double.IsFinite(right)
                && double.IsFinite(bottom)
                && right - left >= 5
                && bottom - top >= 5)
            {
                return new NormalizedRect(left, top, right - left, bottom - top)
                    .Expand(0.035, 0.045)
                    .Clamp();
            }
        }

        bool rectangular = region.Type is "narration" or "caption";
        return region.TextBox
            .Expand(rectangular ? 0.18 : 0.24, rectangular ? 0.30 : 0.40)
            .Clamp();
    }

    private static string NormalizeArchiveEntryName(string? value)
    {
        string normalized = (value ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Split('/').Any(part => part is ".." or "."))
        {
            throw new InvalidOperationException("El proyecto contiene una ruta de página no válida.");
        }
        return normalized;
    }

    private static void DeleteWorkspace(string workspace)
    {
        try
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
        catch (IOException)
        {
            // Windows puede conservar brevemente un handle de imagen. El sistema limpiará
            // la carpeta temporal más adelante; nunca se compromete el documento original.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ReaderProjectManifest
    {
        public int Version { get; set; }
        public string Title { get; set; } = "Cómic";
        public int CurrentPageIndex { get; set; }
        public List<ReaderProjectPage> Pages { get; set; } = [];
    }

    private sealed class ReaderProjectPage
    {
        public string DisplayName { get; set; } = string.Empty;
        public string SourceFile { get; set; } = string.Empty;
        public List<ComicRegion>? Regions { get; set; }
    }
}
