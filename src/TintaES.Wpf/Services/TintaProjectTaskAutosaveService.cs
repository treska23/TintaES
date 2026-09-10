using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace TintaES.Wpf.Services;

/// <summary>
/// Actualiza únicamente project.json dentro del .tinta principal mediante una copia temporal
/// en el mismo directorio y una sustitución atómica. El temporal existe solo durante la escritura:
/// no se conserva ninguna copia de seguridad ni archivo alternativo del proyecto.
/// </summary>
internal static class TintaProjectTaskAutosaveService
{
    internal static void ReplaceManifestTransactionally(
        string projectPath,
        ReadOnlyMemory<byte> manifestJson)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            throw new FileNotFoundException(
                "No existe el archivo principal del proyecto que se debe autoguardar.",
                projectPath);
        }
        if (manifestJson.IsEmpty)
        {
            throw new InvalidDataException("El manifiesto que se iba a autoguardar está vacío.");
        }

        string temporaryPath = projectPath + ".task-autosave.tmp";
        TryDelete(temporaryPath);

        try
        {
            // Copiar el ZIP ya comprimido es mucho más barato que volver a empaquetar todas las
            // imágenes fuente en cada página terminada. El .tinta real no se abre para escritura.
            File.Copy(projectPath, temporaryPath, overwrite: true);

            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       bufferSize: 1024 * 1024,
                       FileOptions.RandomAccess))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
                {
                    archive.GetEntry("project.json")?.Delete();
                    ZipArchiveEntry manifestEntry = archive.CreateEntry(
                        "project.json",
                        CompressionLevel.Fastest);
                    using Stream output = manifestEntry.Open();
                    output.Write(manifestJson.Span);
                }

                // La tarea ya ha terminado: forzamos el temporal a disco antes de sustituir el
                // proyecto principal para que una pérdida de corriente no deje una escritura a medias.
                stream.Flush(flushToDisk: true);
            }

            ValidateArchive(temporaryPath);

            // Sin .bak: el usuario ha pedido que el progreso se guarde directamente en el fichero
            // principal. File.Replace mantiene la sustitución atómica y el temporal desaparece.
            File.Replace(
                temporaryPath,
                projectPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void ValidateArchive(string projectPath)
    {
        using FileStream input = new(
            projectPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 256 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry manifestEntry = archive.GetEntry("project.json")
            ?? throw new InvalidDataException("El proyecto temporal no contiene project.json.");

        using Stream manifestStream = manifestEntry.Open();
        using JsonDocument document = JsonDocument.Parse(manifestStream);
        if (!TryGetPropertyIgnoreCase(document.RootElement, "pages", out JsonElement pages)
            || pages.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("El project.json temporal no contiene la lista de páginas.");
        }

        foreach (JsonElement page in pages.EnumerateArray())
        {
            RequireReferencedEntry(archive, page, "sourceFile", required: true);
            RequireReferencedEntry(archive, page, "cleanedFile", required: false);
            RequireReferencedEntry(archive, page, "maskFile", required: false);
        }
    }

    private static void RequireReferencedEntry(
        ZipArchive archive,
        JsonElement page,
        string propertyName,
        bool required)
    {
        if (!TryGetPropertyIgnoreCase(page, propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                throw new InvalidDataException(
                    $"Una página del manifiesto no contiene {propertyName}.");
            }
            return;
        }

        string? entryName = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(entryName))
        {
            if (required)
            {
                throw new InvalidDataException(
                    $"Una página del manifiesto tiene {propertyName} vacío.");
            }
            return;
        }

        if (archive.GetEntry(entryName.Replace('\\', '/')) is null)
        {
            throw new InvalidDataException(
                $"El autoguardado temporal no conserva la entrada {entryName}.");
        }
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Una limpieza posterior nunca debe ocultar la excepción original.
        }
    }
}
