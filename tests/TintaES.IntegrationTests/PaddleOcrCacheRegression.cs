using System.Globalization;
using System.Diagnostics;
using System.IO;
using TintaES.Core;
using TintaES.Wpf.Services;

internal static class PaddleOcrCacheRegression
{
    internal static async Task<int> RunCancellationAsync()
    {
        foreach (bool userCancellation in new[] { true, false })
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("No arrancó el worker de prueba.");
            using var cancellation = new CancellationTokenSource();
            if (userCancellation) cancellation.CancelAfter(200);
            bool canceled = false;
            bool exitedWhenCanceled = false;
            try
            {
                await PaddleOcrService.WaitForWorkerExitAsync(process,
                    userCancellation ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(200), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                exitedWhenCanceled = process.HasExited;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            Assert(canceled && exitedWhenCanceled, "Cancelar o agotar tiempo debe terminar el worker antes de devolver el control.");
            Assert(cancellation.IsCancellationRequested == userCancellation, "El timeout no debe cancelar la operación del usuario.");
        }
        Console.WriteLine("PADDLE_CANCEL_TIMEOUT_NO_ORPHAN=OK");
        return 0;
    }

    internal static int Run()
    {
        byte[] image = [1, 2, 3, 4];
        string root = Environment.CurrentDirectory;
        var first = new ComicRegion { Original = "HELLO", TextBox = new(100, 120, 90, 40) };
        var second = new ComicRegion { Original = "BOOM!", Type = "sfx", TextBox = new(210, 130, 80, 35) };
        ComicRegion[] regions = [first, second];
        string key = PaddleOcrService.CreateCachePath(image, root, regions);
        first.Id = Guid.NewGuid();
        first.Original = "HELLO FRIEND!";
        first.Translation = "¡Hola, amigo!";
        Assert(key == PaddleOcrService.CreateCachePath(image, root, regions), "IDs y correcciones no deben perder lecturas crudas reutilizables.");

        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
            string spanish = PaddleOcrService.CreateCachePath(image, root, regions);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert(spanish == PaddleOcrService.CreateCachePath(image, root, regions), "La caché no depende de la cultura del equipo.");
        }
        finally { CultureInfo.CurrentCulture = previous; }

        first.TextBox = new(101, 120, 90, 40);
        Assert(key != PaddleOcrService.CreateCachePath(image, root, regions), "Cambiar un recorte debe invalidar la caché.");
        first.TextBox = new(100, 120, 90, 40);
        second.IsEnabled = false;
        Assert(key != PaddleOcrService.CreateCachePath(image, root, regions), "Deshabilitar un vecino debe invalidar el manifiesto de recortes.");
        second.IsEnabled = true;
        Assert(key != PaddleOcrService.CreateCachePath(image, root, [second, first]), "El orden del lote forma parte de la entrada del modelo.");
        Assert(key != PaddleOcrService.CreateCachePath([1, 2, 3, 5], root, regions), "Una imagen distinta no puede reutilizar el OCR.");

        string directory = Path.Combine(Path.GetTempPath(), $"tintaes-cache-test-{Guid.NewGuid():N}");
        string cachePath = Path.Combine(directory, "spots.json");
        try
        {
            HunyuanTextSpot[] raw = [new("HELLO FRIEND! BOOM!", PaddleCropAssociation.GetCropBox(first))];
            PaddleOcrService.SaveSpots(cachePath, 900, 1200, raw);
            Assert(PaddleOcrService.TryLoadSpots(cachePath, 900, 1200, out var cached), "La lectura cruda debe recuperarse.");
            Assert(raw.SequenceEqual(cached), "La caché debe preservar texto y coordenadas exactos.");
            Assert(PaddleOcrService.CleanVisualSpots(regions, cached).SequenceEqual(
                PaddleOcrService.CleanVisualSpots(regions, raw)), "La caché debe producir la misma limpieza que una lectura nueva.");
            second.Original = "BANG!";
            Assert(PaddleOcrService.CleanVisualSpots(regions, cached).Single().Text.Contains("BOOM"),
                "Corregir un vecino debe permitir limpiar desde la lectura cruda, sin pérdidas acumuladas.");
            Assert(!PaddleOcrService.TryLoadSpots(cachePath, 901, 1200, out _), "Dimensiones incompatibles deben rechazar caché.");
            File.WriteAllText(cachePath, "{invalid");
            Assert(!PaddleOcrService.TryLoadSpots(cachePath, 900, 1200, out _), "Una caché dañada debe solicitar otra lectura.");
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
        Console.WriteLine("PADDLE_CACHE_GEOMETRY_RAW_ROUNDTRIP=OK");
        return 0;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
