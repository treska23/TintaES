using System.Diagnostics;
using System.IO;

namespace TintaES.Wpf.Services;

/// <summary>
/// Libera el worker residente de PaddleOCR cuando termina una ventana de preparación.
/// De este modo varias páginas comparten una sola carga de Paddle, pero TranslateGemma
/// nunca tiene que competir con el VLM de OCR por la VRAM durante la traducción.
/// </summary>
internal static class PaddleOcrResidentControl
{
    internal static async Task ReleaseAsync()
    {
        string? projectRoot = TryFindProjectRoot();
        if (projectRoot is null)
        {
            return;
        }

        string python = LocalEnginePaths.GetPaddlePython(projectRoot);
        string worker = Path.Combine(projectRoot, "engine", "paddleocr", "ocr_page.py");
        if (!File.Exists(python) || !File.Exists(worker))
        {
            return;
        }

        string home = LocalEnginePaths.GetPaddleRoot(projectRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = Path.GetDirectoryName(worker) ?? projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(worker);
        startInfo.ArgumentList.Add("--shutdown");
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["TINTAES_PADDLE_MODEL_HOME"] = Path.Combine(home, "models");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return;
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(output, error);
        }
        catch (Exception exception) when (exception is IOException
                                               or InvalidOperationException
                                               or System.ComponentModel.Win32Exception)
        {
            // Es una liberación de recursos de mejor esfuerzo. Si no hay worker vivo,
            // la traducción puede continuar normalmente.
        }
    }

    private static string? TryFindProjectRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(start);
            for (int depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "engine", "paddleocr", "ocr_page.py")))
                {
                    return directory.FullName;
                }
            }
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Ya había terminado.
        }
    }
}
