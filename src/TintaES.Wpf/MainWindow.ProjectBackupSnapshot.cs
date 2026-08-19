using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Crea una fotografía inmutable de un proyecto antes de escribirlo en segundo plano. Así el
/// guardado automático no enumera colecciones de la interfaz mientras el usuario vuelve a editar.
/// El formato resultante es exactamente un .tinta normal y puede abrirse con el cargador existente.
/// </summary>
public partial class MainWindow
{
    private TintaProjectWriteSnapshot CaptureTintaProjectWriteSnapshot(
        string title,
        int currentPageIndex,
        IReadOnlyList<ComicBookPageState> pages)
    {
        var manifest = new TintaProjectManifest
        {
            Version = 1,
            Title = string.IsNullOrWhiteSpace(title) ? "comic" : title,
            CurrentPageIndex = Math.Clamp(currentPageIndex, 0, Math.Max(0, pages.Count - 1))
        };
        var files = new List<TintaProjectFileSnapshot>();

        for (int index = 0; index < pages.Count; index++)
        {
            ComicBookPageState page = pages[index];
            string sourceExtension = Path.GetExtension(page.SourcePath);
            string sourceEntry = $"source/{index + 1:D4}{sourceExtension}";
            files.Add(new TintaProjectFileSnapshot(page.SourcePath, sourceEntry));

            string? cleanedEntry = null;
            if (!string.IsNullOrWhiteSpace(page.CleanedPath) && File.Exists(page.CleanedPath))
            {
                cleanedEntry = $"processed/{index + 1:D4}-clean.png";
                files.Add(new TintaProjectFileSnapshot(page.CleanedPath, cleanedEntry));
            }

            string? maskEntry = null;
            if (!string.IsNullOrWhiteSpace(page.MaskPath) && File.Exists(page.MaskPath))
            {
                maskEntry = $"processed/{index + 1:D4}-mask.png";
                files.Add(new TintaProjectFileSnapshot(page.MaskPath, maskEntry));
            }

            manifest.Pages.Add(new TintaProjectPage
            {
                DisplayName = page.DisplayName,
                SourceFile = sourceEntry,
                CleanedFile = cleanedEntry,
                MaskFile = maskEntry,
                SourceLanguage = page.SourceLanguage,
                Processed = page.Processed,
                Error = page.Error,
                Regions = page.Regions.ToList()
            });
        }

        // La serialización se realiza en el hilo de interfaz al capturar la instantánea. A partir
        // de aquí el worker de copia solo toca bytes y archivos, nunca objetos ComicRegion vivos.
        byte[] manifestJson = JsonSerializer.SerializeToUtf8Bytes(manifest, ProjectJsonOptions);
        string fingerprint = CreateTintaProjectSnapshotFingerprint(manifestJson, files);
        return new TintaProjectWriteSnapshot(manifestJson, files, fingerprint);
    }

    private static string CreateTintaProjectSnapshotFingerprint(
        byte[] manifestJson,
        IReadOnlyList<TintaProjectFileSnapshot> files)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(manifestJson);

        foreach (TintaProjectFileSnapshot file in files)
        {
            var info = new FileInfo(file.SourcePath);
            string metadata = string.Join(
                "|",
                file.EntryName,
                info.Exists ? info.Length : -1,
                info.Exists ? info.LastWriteTimeUtc.Ticks : -1);
            hash.AppendData(Encoding.UTF8.GetBytes(metadata));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// Intenta escribir una copia automática completa. Los SourcePath de un cómic abierto pueden
    /// apuntar al workspace temporal; al cerrar/cambiar de documento ese workspace puede desaparecer
    /// mientras el worker de autosave está escribiendo. En ese caso se descarta la copia entera en
    /// lugar de lanzar una excepción o dejar un .tinta cuyo project.json referencia entradas ausentes.
    /// </summary>
    private static bool TryWriteTintaProjectSnapshot(
        string targetPath,
        TintaProjectWriteSnapshot snapshot)
    {
        // Preflight barato: evita incluso intentar abrir el ZIP cuando el cierre ya ha retirado
        // alguna página temporal. La comprobación se repite al abrir cada fichero para cubrir carreras.
        if (snapshot.Files.Any(file => !File.Exists(file.SourcePath)))
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = targetPath + ".tmp";
        TryDeleteProjectSnapshotTemporary(temporaryPath);

        try
        {
            using (FileStream output = File.Create(temporaryPath))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (TintaProjectFileSnapshot file in snapshot.Files)
                {
                    if (!TryAddSnapshotFileToArchive(archive, file.SourcePath, file.EntryName))
                    {
                        return AbortProjectSnapshot(temporaryPath);
                    }
                }

                ZipArchiveEntry manifestEntry = archive.CreateEntry(
                    "project.json",
                    CompressionLevel.Optimal);
                using Stream stream = manifestEntry.Open();
                stream.Write(snapshot.ManifestJson, 0, snapshot.ManifestJson.Length);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            // Un archivo temporal puede desaparecer o quedar bloqueado durante el cierre.
            // El autosave es recuperable: se descarta esta copia y no se propaga la excepción.
            TryDeleteProjectSnapshotTemporary(temporaryPath);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteProjectSnapshotTemporary(temporaryPath);
            return false;
        }
    }

    private static bool TryAddSnapshotFileToArchive(
        ZipArchive archive,
        string filePath,
        string entryName)
    {
        try
        {
            // FileShare.Delete permite terminar de leer un fichero que el workspace empieza a
            // retirar en paralelo; si ya desapareció, se devuelve false sin excepción al caller.
            using var input = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            ZipArchiveEntry entry = archive.CreateEntry(
                entryName.Replace('\\', '/'),
                CompressionLevel.Optimal);
            using Stream output = entry.Open();
            input.CopyTo(output);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool AbortProjectSnapshot(string temporaryPath)
    {
        TryDeleteProjectSnapshotTemporary(temporaryPath);
        return false;
    }

    private static void TryDeleteProjectSnapshotTemporary(string path)
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
            // Una copia temporal bloqueada no debe impedir que TintaES siga funcionando.
        }
    }

    private sealed record TintaProjectWriteSnapshot(
        byte[] ManifestJson,
        IReadOnlyList<TintaProjectFileSnapshot> Files,
        string Fingerprint);

    private sealed record TintaProjectFileSnapshot(string SourcePath, string EntryName);
}
