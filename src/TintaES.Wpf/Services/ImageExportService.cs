using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf.Services;

public sealed class ImageExportService
{
    public Task<BitmapSource> RenderAsync(
        BitmapSource background,
        IEnumerable<ComicRegion> regions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        BitmapSource frozenBackground = background;
        if (!frozenBackground.IsFrozen)
        {
            frozenBackground = background.CloneCurrentValue();
            frozenBackground.Freeze();
        }

        ComicRegion[] snapshot = regions.Select(CloneRegion).ToArray();
        var completion = new TaskCompletionSource<BitmapSource>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                BitmapSource rendered = Render(frozenBackground, snapshot);
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(rendered);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "TintaES image export"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public BitmapSource Render(BitmapSource background, IEnumerable<ComicRegion> regions)
    {
        int width = background.PixelWidth;
        int height = background.PixelHeight;
        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent
        };
        canvas.Children.Add(new Image
        {
            Source = background,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill
        });

        foreach (ComicRegion region in regions.Where(region =>
                     region.IsEnabled && region.HasRenderableTranslation))
        {
            NormalizedRect box = region.RenderBox;
            double left = (box.X + region.TextOffsetX) / 1000 * width;
            double top = (box.Y + region.TextOffsetY) / 1000 * height;
            double elementWidth = box.Width / 1000 * width;
            double elementHeight = box.Height / 1000 * height;
            var layer = new Grid
            {
                Width = elementWidth,
                Height = elementHeight,
                Background = null,
                ClipToBounds = true
            };

            // Vista y exportación comparten el mismo renderizador legible. No se vuelve a
            // calcular la composición con el número de líneas, fuente o rotación originales.
            var text = new InteractiveComicTextElement
            {
                Region = region,
                PageWidth = width,
                PageHeight = height,
                Width = elementWidth,
                Height = elementHeight
            };

            layer.Children.Add(text);
            Canvas.SetLeft(layer, left);
            Canvas.SetTop(layer, top);
            canvas.Children.Add(layer);
        }

        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(canvas);
        rendered.Freeze();
        return rendered;
    }

    public void SavePng(BitmapSource image, string path)
    {
        Save(image, path);
    }

    public void Save(BitmapSource image, string path, int quality = 95)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".webp" or ".pdf")
        {
            SaveWithPillow(
                image,
                path,
                extension == ".webp" ? "webp" : "pdf",
                quality);
            return;
        }

        BitmapEncoder encoder = extension switch
        {
            ".png" => new PngBitmapEncoder(),
            ".jpg" or ".jpeg" => new JpegBitmapEncoder
            {
                QualityLevel = Math.Clamp(quality, 1, 100)
            },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder
            {
                Compression = TiffCompressOption.Zip
            },
            _ => throw new NotSupportedException(
                $"El formato {extension} no está disponible. Usa PNG, JPEG, WebP, TIFF, BMP o PDF.")
        };
        SaveAtomically(image, path, encoder);
    }

    private static void SaveAtomically(BitmapSource image, string path, BitmapEncoder encoder)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
                           ?? throw new InvalidOperationException("La ruta de exportación no tiene carpeta.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = File.Create(temporary))
            {
                encoder.Save(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void SaveWithPillow(
        BitmapSource image,
        string path,
        string format,
        int quality)
    {
        string projectRoot = FindProjectRoot();
        string python = LocalEnginePaths.GetMangaPython(projectRoot);
        string script = Path.Combine(projectRoot, "engine", "export_image.py");
        if (!File.Exists(python) || !File.Exists(script))
        {
            throw new InvalidOperationException(
                $"No se encuentra el codificador local de {format.ToUpperInvariant()}. " +
                "Comprueba el motor Python de Tinta ES.");
        }

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
                           ?? throw new InvalidOperationException("La ruta de exportación no tiene carpeta.");
        Directory.CreateDirectory(directory);
        string temporaryPng = Path.Combine(Path.GetTempPath(), $"tinta-es-{Guid.NewGuid():N}.png");
        string temporaryOutput = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp.{format}");
        try
        {
            SaveAtomically(image, temporaryPng, new PngBitmapEncoder());
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string argument in new[]
                     {
                         script,
                         "--input",
                         temporaryPng,
                         "--output",
                         temporaryOutput,
                         "--format",
                         format,
                         "--quality",
                         Math.Clamp(quality, 1, 100).ToString()
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException(
                                    $"No se pudo iniciar el codificador {format.ToUpperInvariant()}.");
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(temporaryOutput))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"El codificador {format.ToUpperInvariant()} terminó sin crear el archivo."
                        : error.Trim());
            }
            File.Move(temporaryOutput, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPng))
            {
                File.Delete(temporaryPng);
            }
            if (File.Exists(temporaryOutput))
            {
                File.Delete(temporaryOutput);
            }
        }
    }

    private static ComicRegion CloneRegion(ComicRegion source) =>
        new()
        {
            Id = source.Id,
            Order = source.Order,
            Original = source.Original,
            OcrAlternatives = source.OcrAlternatives.ToArray(),
            Translation = source.Translation,
            Type = source.Type,
            Confidence = source.Confidence,
            BubbleConfidence = source.BubbleConfidence,
            TextBox = source.TextBox,
            BubbleBox = source.BubbleBox,
            RenderBox = source.RenderBox,
            CleanupPolygon = source.CleanupPolygon.ToArray(),
            SafePolygon = source.SafePolygon.ToArray(),
            Rotation = source.Rotation,
            Vertical = source.Vertical,
            Style = new ComicTextStyle
            {
                FontCategory = source.Style.FontCategory,
                FontFamily = source.Style.FontFamily,
                FontWeight = source.Style.FontWeight,
                FontSize = source.Style.FontSize,
                FontWidthRatio = source.Style.FontWidthRatio,
                LineHeightRatio = source.Style.LineHeightRatio,
                OriginalLineCount = source.Style.OriginalLineCount,
                Italic = source.Style.Italic,
                Uppercase = source.Style.Uppercase,
                TextColor = source.Style.TextColor,
                OutlineColor = source.Style.OutlineColor,
                OutlineWidth = source.Style.OutlineWidth,
                Alignment = source.Style.Alignment,
                BackgroundColor = source.Style.BackgroundColor,
                Shadow = source.Style.Shadow
            },
            IsEnabled = source.IsEnabled,
            CleanupMode = source.CleanupMode,
            FontScale = source.FontScale,
            ManualFontScale = source.ManualFontScale,
            TextOffsetX = source.TextOffsetX,
            TextOffsetY = source.TextOffsetY,
            IsManual = source.IsManual,
            ManualLayoutSeedText = source.ManualLayoutSeedText,
            ManualBaseFontSize = source.ManualBaseFontSize
        };

    private static string FindProjectRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(start);
            for (int depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "engine", "export_image.py")))
                {
                    return directory.FullName;
                }
            }
        }
        throw new InvalidOperationException("No se encuentra la carpeta engine de Tinta ES.");
    }
}
