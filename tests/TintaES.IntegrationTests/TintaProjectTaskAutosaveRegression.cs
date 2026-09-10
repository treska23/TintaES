using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using TintaES.Wpf.Services;

internal static class TintaProjectTaskAutosaveRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "TintaES-task-autosave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string project = Path.Combine(root, "comic.tinta");
            WriteInitialProject(project);

            byte[] updatedManifest = Encoding.UTF8.GetBytes(
                """
                {"version":1,"title":"actualizado","currentPageIndex":0,"pages":[{"displayName":"001.png","sourceFile":"source/0001.png","cleanedFile":null,"maskFile":null,"sourceLanguage":"en","processed":true,"error":null,"regions":[]}]}
                """);
            TintaProjectTaskAutosaveService.ReplaceManifestTransactionally(
                project,
                updatedManifest);

            Require(File.Exists(project), "El .tinta principal debe seguir existiendo.");
            Require(!File.Exists(project + ".task-autosave.tmp"),
                "El temporal no puede quedar después de un autoguardado correcto.");
            Require(!File.Exists(project + ".bak"),
                "El autoguardado de tareas no debe generar una copia de seguridad.");
            Require(ReadProjectJson(project).Contains("actualizado", StringComparison.Ordinal),
                "El project.json del archivo principal debe contener el trabajo terminado.");

            byte[] beforeFailure = SHA256.HashData(File.ReadAllBytes(project));
            byte[] invalidManifest = Encoding.UTF8.GetBytes(
                """
                {"version":1,"title":"no debe entrar","currentPageIndex":0,"pages":[{"displayName":"001.png","sourceFile":"source/9999.png","cleanedFile":null,"maskFile":null,"sourceLanguage":"en","processed":true,"error":null,"regions":[]}]}
                """);
            bool rejected = false;
            try
            {
                TintaProjectTaskAutosaveService.ReplaceManifestTransactionally(
                    project,
                    invalidManifest);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            Require(rejected, "Un manifiesto que pierde archivos del proyecto debe rechazarse.");
            Require(beforeFailure.SequenceEqual(SHA256.HashData(File.ReadAllBytes(project))),
                "Un autoguardado inválido no puede modificar el .tinta principal.");
            Require(!File.Exists(project + ".task-autosave.tmp"),
                "Un fallo tampoco puede dejar el temporal de autoguardado.");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // La limpieza del fixture no forma parte de la regresión funcional.
            }
        }
    }

    private static void WriteInitialProject(string path)
    {
        using FileStream stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        ZipArchiveEntry source = archive.CreateEntry("source/0001.png", CompressionLevel.NoCompression);
        using (Stream output = source.Open())
        {
            output.Write([1, 2, 3, 4]);
        }

        ZipArchiveEntry manifest = archive.CreateEntry("project.json", CompressionLevel.Fastest);
        using var writer = new StreamWriter(manifest.Open(), Encoding.UTF8, leaveOpen: false);
        writer.Write(
            "{\"version\":1,\"title\":\"original\",\"currentPageIndex\":0," +
            "\"pages\":[{\"displayName\":\"001.png\",\"sourceFile\":\"source/0001.png\"," +
            "\"cleanedFile\":null,\"maskFile\":null,\"sourceLanguage\":\"en\"," +
            "\"processed\":false,\"error\":null,\"regions\":[]}]}" );
    }

    private static string ReadProjectJson(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry entry = archive.GetEntry("project.json")
            ?? throw new InvalidDataException("Falta project.json en el fixture.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Regresión de autoguardado: " + message);
        }
    }
}
