using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;
using TintaES.Wpf.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

internal static class WindowsOcrPerformanceRegression
{
    private static readonly MethodInfo ConvertMethod = typeof(WindowsOcrService).GetMethod(
        "CreateSoftwareBitmapAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Falta el conversor OCR que se está verificando.");

    // No necesita idiomas OCR instalados; también se ejecuta en CI.
    internal static async Task<int> RunPixelChecksAsync()
    {
        foreach (PixelFormat format in new[]
                 {
                     PixelFormats.Bgr24, PixelFormats.Bgr32, PixelFormats.Bgra32,
                     PixelFormats.Pbgra32, PixelFormats.Gray8, PixelFormats.Indexed8
                 })
        {
            await VerifyPixelsAsync(CreatePixelFixture(format), $"píxeles {format}");
        }
        await VerifyPixelsAsync(CreateAllAlphaFixture(), "todos los valores alfa Pbgra32");
        await VerifyPixelsAsync(ConvertFormat(CreatePixelFixture(PixelFormats.Bgr32), PixelFormats.Pbgra32), "Pbgra32 opaco");
        Console.WriteLine("WINDOWS_OCR_PIXEL_REGRESSION=OK");
        return 0;
    }

    // Ejecutar en el dispatcher STA del arnés. No carga modelos Python ni utiliza CUDA.
    internal static async Task<int> RunAsync(string[] imagePaths)
    {
        await RunPixelChecksAsync();

        BitmapSource synthetic = CreateTextFixture();
        var fixtures = new List<(string Name, BitmapSource Image)>
        {
            ("bocadillos adyacentes y dobles Bgr24", ConvertFormat(synthetic, PixelFormats.Bgr24)),
            ("bocadillos adyacentes y dobles Bgr32", ConvertFormat(synthetic, PixelFormats.Bgr32)),
            ("bocadillos adyacentes y dobles Pbgra32", synthetic)
        };
        fixtures.AddRange(imagePaths.Select(path => (Path.GetFileName(path), LoadBitmap(path))));

        foreach ((string name, BitmapSource image) in fixtures)
        {
            await VerifyPixelsAsync(image, name);
            await VerifyRawOcrAsync(image, name);
            BitmapSource legacySource = ConvertFormat(image, PixelFormats.Bgra32);
            await VerifyEquivalentSourcesAsync(image, legacySource);
            var actualTimer = Stopwatch.StartNew();
            ComicAnalysis actual = await new WindowsOcrService().RecognizeWithTilingAsync(image);
            actualTimer.Stop();
            var legacyTimer = Stopwatch.StartNew();
            ComicAnalysis expected = await RecognizeWithLegacyTilingAsync(legacySource);
            legacyTimer.Stop();
            AssertSameRegions(expected, actual, name);
            if (name.StartsWith("bocadillos", StringComparison.Ordinal) && actual.Regions.Count == 0)
            {
                throw new InvalidOperationException("El fixture de bocadillos no produjo ninguna región OCR.");
            }
            Console.WriteLine(
                $"WINDOWS_OCR_EQUIVALENTE={name}; zonas={actual.Regions.Count}; " +
                $"anterior_ms={legacyTimer.Elapsed.TotalMilliseconds:F1}; " +
                $"actual_ms={actualTimer.Elapsed.TotalMilliseconds:F1}");
        }

        await BenchmarkConversionAsync(ConvertFormat(synthetic, PixelFormats.Bgr24), "Bgr24");
        await BenchmarkConversionAsync(ConvertFormat(synthetic, PixelFormats.Bgr32), "Bgr32");
        await BenchmarkConversionAsync(synthetic, "Pbgra32 renderizado");
        Console.WriteLine("WINDOWS_OCR_PERFORMANCE_REGRESSION=OK");
        return 0;
    }

    private static async Task VerifyPixelsAsync(BitmapSource source, string name)
    {
        using SoftwareBitmap expected = await LegacyPngConversionAsync(source);
        using SoftwareBitmap actual = await OptimizedConversionAsync(source);
        if (expected.PixelWidth != actual.PixelWidth || expected.PixelHeight != actual.PixelHeight
            || expected.BitmapPixelFormat != actual.BitmapPixelFormat
            || expected.BitmapAlphaMode != actual.BitmapAlphaMode
            || !ReadPixels(expected).AsSpan().SequenceEqual(ReadPixels(actual)))
        {
            throw new InvalidOperationException($"La conversión OCR cambió los píxeles: {name}.");
        }
        Console.WriteLine($"WINDOWS_OCR_PIXELES_IGUALES={name}");
    }

    private static async Task VerifyRawOcrAsync(BitmapSource source, string name)
    {
        using SoftwareBitmap legacy = await LegacyPngConversionAsync(source);
        using SoftwareBitmap optimized = await OptimizedConversionAsync(source);
        OcrResult expected = await CreateEngine().RecognizeAsync(legacy);
        OcrResult actual = await CreateEngine().RecognizeAsync(optimized);
        string Snapshot(OcrResult result) => JsonSerializer.Serialize(new
        {
            result.Text,
            result.TextAngle,
            Lines = result.Lines.Select(line => new
            {
                line.Text,
                Words = line.Words.Select(word => new
                {
                    word.Text,
                    word.BoundingRect.X,
                    word.BoundingRect.Y,
                    word.BoundingRect.Width,
                    word.BoundingRect.Height
                })
            })
        });
        if (Snapshot(expected) != Snapshot(actual))
        {
            throw new InvalidOperationException($"El texto o sus coordenadas OCR cambiaron: {name}.");
        }
    }

    private static async Task<ComicAnalysis> RecognizeWithLegacyTilingAsync(BitmapSource source)
    {
        // Referencia anterior: motor nuevo por mosaico y conversión PNG. La fuente
        // Bgra32 y su igualdad se preparan fuera del intervalo cronometrado.
        int tileWidth = Math.Min(source.PixelWidth, 640);
        int tileHeight = Math.Min(source.PixelHeight, 640);
        var regions = new List<ComicRegion>();
        foreach (int y in LegacyOrigins(source.PixelHeight, tileHeight))
        foreach (int x in LegacyOrigins(source.PixelWidth, tileWidth))
        {
            var crop = new CroppedBitmap(source, new Int32Rect(x, y, tileWidth, tileHeight));
            crop.Freeze();
            ComicAnalysis tile = await new WindowsOcrService().RecognizeAsync(crop);
            foreach (ComicRegion region in tile.Regions)
            {
                NormalizedRect Map(NormalizedRect box) => new(
                    (x + box.X / 1000 * tileWidth) / source.PixelWidth * 1000,
                    (y + box.Y / 1000 * tileHeight) / source.PixelHeight * 1000,
                    box.Width / 1000 * tileWidth / source.PixelWidth * 1000,
                    box.Height / 1000 * tileHeight / source.PixelHeight * 1000);
                regions.Add(RegionMerger.Sanitize(new ComicRegion
                {
                    Original = region.Original,
                    Translation = string.Empty,
                    Type = region.Type,
                    Confidence = region.Confidence,
                    TextBox = Map(region.TextBox),
                    RenderBox = Map(region.RenderBox),
                    // Sanitize ya creó el polígono en coordenadas del mosaico;
                    // la referencia anterior lo trasladaba, no lo regeneraba.
                    CleanupPolygon = region.CleanupPolygon.Select(point => new NormalizedPoint(
                        (x + point.X / 1000 * tileWidth) / source.PixelWidth * 1000,
                        (y + point.Y / 1000 * tileHeight) / source.PixelHeight * 1000)).ToArray(),
                    SafePolygon = region.SafePolygon.Select(point => new NormalizedPoint(
                        (x + point.X / 1000 * tileWidth) / source.PixelWidth * 1000,
                        (y + point.Y / 1000 * tileHeight) / source.PixelHeight * 1000)).ToArray(),
                    Rotation = region.Rotation,
                    CleanupMode = "none",
                    IsEnabled = false,
                    Style = region.Style
                }));
            }
        }
        return new ComicAnalysis("en", RegionMerger.Merge(regions));
    }

    private static async Task VerifyEquivalentSourcesAsync(BitmapSource source, BitmapSource legacySource)
    {
        using SoftwareBitmap original = await LegacyPngConversionAsync(source);
        using SoftwareBitmap converted = await LegacyPngConversionAsync(legacySource);
        if (!ReadPixels(original).AsSpan().SequenceEqual(ReadPixels(converted)))
        {
            throw new InvalidOperationException("La imagen de referencia del mosaico no preserva los píxeles.");
        }
    }

    private static IEnumerable<int> LegacyOrigins(int total, int size)
    {
        int previous = -1;
        for (int origin = 0; origin < total; origin += Math.Max(1, size - 180))
        {
            int clamped = Math.Min(origin, total - size);
            if (clamped != previous)
            {
                yield return clamped;
                previous = clamped;
            }
            if (clamped + size >= total)
            {
                yield break;
            }
        }
    }

    private static void AssertSameRegions(ComicAnalysis expected, ComicAnalysis actual, string name)
    {
        string Snapshot(ComicAnalysis analysis) => JsonSerializer.Serialize(new
        {
            analysis.SourceLanguage,
            Regions = analysis.Regions.Select(region => new
            {
                region.Order, region.Original, region.Translation, region.Type,
                region.Confidence, region.TextBox, region.RenderBox, region.Rotation,
                region.CleanupPolygon, region.SafePolygon, region.CleanupMode,
                region.IsEnabled, region.StoredOcrAlternatives, region.Style
            })
        });
        string expectedJson = Snapshot(expected);
        string actualJson = Snapshot(actual);
        if (expectedJson != actualJson)
        {
            string directory = Path.GetFullPath(".artifacts");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "windows-ocr-expected.json"), expectedJson);
            File.WriteAllText(Path.Combine(directory, "windows-ocr-actual.json"), actualJson);
            throw new InvalidOperationException($"Las regiones del mosaico cambiaron: {name}. Snapshots: {directory}.");
        }
    }

    private static async Task BenchmarkConversionAsync(BitmapSource source, string name)
    {
        using (await LegacyPngConversionAsync(source)) { }
        using (await OptimizedConversionAsync(source)) { }
        var legacySamples = new List<double>();
        var optimizedSamples = new List<double>();
        for (int iteration = 0; iteration < 7; iteration++)
        {
            // Alternar el orden reduce el sesgo de calentamiento y cachés.
            if (iteration % 2 == 0)
            {
                legacySamples.Add(await MeasureAsync(() => LegacyPngConversionAsync(source)));
                optimizedSamples.Add(await MeasureAsync(() => OptimizedConversionAsync(source)));
            }
            else
            {
                optimizedSamples.Add(await MeasureAsync(() => OptimizedConversionAsync(source)));
                legacySamples.Add(await MeasureAsync(() => LegacyPngConversionAsync(source)));
            }
        }
        double legacyMs = legacySamples.Order().ElementAt(3);
        double optimizedMs = optimizedSamples.Order().ElementAt(3);
        Console.WriteLine(
            $"WINDOWS_OCR_CONVERSION={name}; mediana_anterior_ms={legacyMs:F2}; " +
            $"mediana_actual_ms={optimizedMs:F2}; aceleracion={legacyMs / optimizedMs:F2}x");
    }

    private static async Task<double> MeasureAsync(Func<Task<SoftwareBitmap>> convert)
    {
        var elapsed = Stopwatch.StartNew();
        using SoftwareBitmap bitmap = await convert();
        return elapsed.Elapsed.TotalMilliseconds;
    }

    private static Task<SoftwareBitmap> OptimizedConversionAsync(BitmapSource source) =>
        (Task<SoftwareBitmap>)(ConvertMethod.Invoke(null, [source])
                              ?? throw new InvalidOperationException("El conversor no devolvió un bitmap."));

    private static async Task<SoftwareBitmap> LegacyPngConversionAsync(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(output.ToArray());
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        Windows.Graphics.Imaging.BitmapDecoder decoder =
            await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    private static byte[] ReadPixels(SoftwareBitmap bitmap)
    {
        var buffer = new Windows.Storage.Streams.Buffer(checked((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4)));
        bitmap.CopyToBuffer(buffer);
        CryptographicBuffer.CopyToByteArray(buffer, out byte[] pixels);
        return pixels;
    }

    private static OcrEngine CreateEngine()
    {
        foreach (string tag in new[] { "en-US", "en-GB", "en" })
        {
            var language = new Windows.Globalization.Language(tag);
            if (OcrEngine.IsLanguageSupported(language))
            {
                return OcrEngine.TryCreateFromLanguage(language);
            }
        }
        return OcrEngine.TryCreateFromUserProfileLanguages()
               ?? throw new InvalidOperationException("No hay un idioma OCR instalado para la regresión.");
    }

    private static BitmapSource CreatePixelFixture(PixelFormat format)
    {
        const int width = 37;
        const int height = 19;
        int stride = (width * format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * height];
        new Random(8392).NextBytes(pixels);
        if (format == PixelFormats.Pbgra32)
        {
            for (int index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = (byte)(pixels[index] * pixels[index + 3] / 255);
                pixels[index + 1] = (byte)(pixels[index + 1] * pixels[index + 3] / 255);
                pixels[index + 2] = (byte)(pixels[index + 2] * pixels[index + 3] / 255);
            }
        }
        BitmapSource source = BitmapSource.Create(
            width, height, 144, 120, format,
            format == PixelFormats.Indexed8 ? BitmapPalettes.Halftone256 : null,
            pixels, stride);
        source.Freeze();
        return source;
    }

    private static BitmapSource CreateAllAlphaFixture()
    {
        const int size = 256;
        var pixels = new byte[size * size * 4];
        for (int alpha = 0; alpha < size; alpha++)
        for (int value = 0; value < size; value++)
        {
            int offset = (alpha * size + value) * 4;
            pixels[offset] = (byte)Math.Min(value, alpha);
            pixels[offset + 1] = (byte)(alpha - Math.Min(value, alpha));
            pixels[offset + 2] = (byte)(value * alpha / 255);
            pixels[offset + 3] = (byte)alpha;
        }
        BitmapSource source = BitmapSource.Create(size, size, 96, 96,
            PixelFormats.Pbgra32, null, pixels, size * 4);
        source.Freeze();
        return source;
    }

    private static BitmapSource CreateTextFixture()
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1200, 1400));
            var outline = new Pen(Brushes.Black, 3);
            foreach (Rect balloon in new[]
                     {
                         new Rect(40, 40, 500, 350), new Rect(555, 40, 600, 350),
                         new Rect(200, 430, 650, 330), new Rect(270, 730, 620, 330),
                         new Rect(380, 1110, 750, 220)
                     })
            {
                context.DrawRoundedRectangle(Brushes.White, outline, balloon, 90, 90);
            }
            void Text(string text, double x, double y, double size = 32) => context.DrawText(
                new FormattedText(text, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                    new Typeface("Arial"), size, Brushes.Black, 1), new Point(x, y));
            Text("THIS IS THE LEFT\nSPEECH BALLOON.\nKEEP EVERY WORD.", 105, 120);
            Text("THIS IS THE RIGHT\nSPEECH BALLOON.\nDO NOT MIX OUR LINES.", 615, 120);
            Text("ONE CONNECTED BALLOON\nCAN CONTINUE INTO\nANOTHER PART.", 275, 510);
            Text("THIS CONTINUATION\nMUST ALSO REMAIN\nFULLY READABLE.", 340, 805);
            Text("TEXT CROSSING TILE BOUNDARIES\nSHOULD KEEP ALL ITS LETTERS.", 430, 1170, 28);
        }
        var bitmap = new RenderTargetBitmap(1200, 1400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource ConvertFormat(BitmapSource source, PixelFormat format)
    {
        var converted = new FormatConvertedBitmap(source, format, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
