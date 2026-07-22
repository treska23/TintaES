using System.IO;
using System.Windows.Media.Imaging;
using TintaES.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TintaES.Wpf.Services;

public sealed class WindowsOcrService
{
    private const double CoordinateScale = 1000d;

    public async Task<ComicAnalysis> RecognizeAsync(
        BitmapSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] png = EncodePng(source);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        Windows.Graphics.Imaging.BitmapDecoder decoder =
            await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        OcrEngine engine = CreateEngine();
        OcrResult result = await engine.RecognizeAsync(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<TextLine> lines = result.Lines
            .Select(line => CreateLine(line, source.PixelWidth, source.PixelHeight))
            .Where(line => !string.IsNullOrWhiteSpace(line.Text) && line.Box.Width >= 3 && line.Box.Height >= 3)
            .OrderBy(line => line.Box.Y)
            .ThenBy(line => line.Box.X)
            .ToArray();

        IReadOnlyList<ComicRegion> regions = GroupLines(lines, source.PixelWidth, source.PixelHeight);
        string language = engine.RecognizerLanguage?.LanguageTag ?? "desconocido";
        return new ComicAnalysis(language, regions);
    }

    private static OcrEngine CreateEngine()
    {
        foreach (string languageTag in new[] { "en-US", "en-GB", "en" })
        {
            var language = new Language(languageTag);
            if (OcrEngine.IsLanguageSupported(language))
            {
                return OcrEngine.TryCreateFromLanguage(language);
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "Windows no tiene instalado ningún idioma compatible con OCR. Añade Inglés en Configuración > Hora e idioma > Idioma y región.");
    }

    private static TextLine CreateLine(OcrLine line, int pageWidth, int pageHeight)
    {
        Windows.Foundation.Rect[] boxes = line.Words.Select(word => word.BoundingRect).ToArray();
        if (boxes.Length == 0)
        {
            return new TextLine(line.Text.Trim(), new PixelRect(0, 0, 0, 0));
        }

        double left = boxes.Min(box => box.X);
        double top = boxes.Min(box => box.Y);
        double right = boxes.Max(box => box.X + box.Width);
        double bottom = boxes.Max(box => box.Y + box.Height);
        return new TextLine(
            line.Text.Trim(),
            new PixelRect(
                Math.Clamp(left, 0, pageWidth),
                Math.Clamp(top, 0, pageHeight),
                Math.Clamp(right - left, 0, pageWidth),
                Math.Clamp(bottom - top, 0, pageHeight)));
    }

    private static IReadOnlyList<ComicRegion> GroupLines(
        IReadOnlyList<TextLine> lines,
        int pageWidth,
        int pageHeight)
    {
        var blocks = new List<TextBlock>();
        foreach (TextLine line in lines)
        {
            TextBlock? best = blocks
                .Where(block => block.Lines.Count < 8 && CanJoin(block.Lines[^1].Box, line.Box))
                .OrderBy(block => VerticalGap(block.Lines[^1].Box, line.Box))
                .FirstOrDefault();

            if (best is null)
            {
                blocks.Add(new TextBlock(line));
            }
            else
            {
                best.Add(line);
            }
        }

        return blocks
            .Select((block, index) => CreateRegion(block, index, pageWidth, pageHeight))
            .OrderBy(region => region.RenderBox.Y)
            .ThenBy(region => region.RenderBox.X)
            .ToArray();
    }

    private static bool CanJoin(PixelRect block, PixelRect line)
    {
        double gap = VerticalGap(block, line);
        double allowedGap = Math.Max(block.Height, line.Height) * 0.85;
        if (gap < -Math.Min(block.Height, line.Height) * 0.35 || gap > allowedGap)
        {
            return false;
        }

        double overlap = Math.Max(0, Math.Min(block.Right, line.Right) - Math.Max(block.X, line.X));
        double overlapRatio = overlap / Math.Max(1, Math.Min(block.Width, line.Width));
        double centreDistance = Math.Abs(block.CenterX - line.CenterX);
        return overlapRatio >= 0.38 || centreDistance <= Math.Min(block.Width, line.Width) * 0.42;
    }

    private static double VerticalGap(PixelRect upper, PixelRect lower)
    {
        return lower.Y >= upper.Y ? lower.Y - upper.Bottom : upper.Y - lower.Bottom;
    }

    private static ComicRegion CreateRegion(TextBlock block, int order, int pageWidth, int pageHeight)
    {
        string original = string.Join("\n", block.Lines.Select(line => line.Text));
        bool uppercase = original.Any(char.IsLetter)
            && original.Where(char.IsLetter).All(char.IsUpper);
        bool looksLikeSfx = uppercase
            && block.Lines.Count <= 2
            && original.Count(char.IsLetter) <= 18
            && block.Box.Height >= 26;

        NormalizedRect textBox = Normalize(block.Box, pageWidth, pageHeight);
        NormalizedRect renderBox = textBox.Expand(0.18, 0.30);
        return RegionMerger.Sanitize(new ComicRegion
        {
            Order = order,
            Original = original,
            Translation = string.Empty,
            Type = looksLikeSfx ? "sfx" : "dialogue",
            Confidence = 0.82,
            TextBox = textBox,
            RenderBox = renderBox,
            CleanupMode = "none",
            IsEnabled = false,
            Style = new ComicTextStyle
            {
                FontCategory = looksLikeSfx ? "display" : "comic",
                FontWeight = uppercase ? 700 : 600,
                Uppercase = uppercase,
                TextColor = "#111111",
                Alignment = "center"
            }
        });
    }

    private static NormalizedRect Normalize(PixelRect box, int pageWidth, int pageHeight)
    {
        return new NormalizedRect(
            box.X / pageWidth * CoordinateScale,
            box.Y / pageHeight * CoordinateScale,
            box.Width / pageWidth * CoordinateScale,
            box.Height / pageHeight * CoordinateScale).Clamp();
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private sealed record TextLine(string Text, PixelRect Box);

    private sealed class TextBlock(TextLine first)
    {
        public List<TextLine> Lines { get; } = [first];
        public PixelRect Box { get; private set; } = first.Box;

        public void Add(TextLine line)
        {
            Lines.Add(line);
            Lines.Sort((left, right) => left.Box.Y.CompareTo(right.Box.Y));
            Box = PixelRect.Union(Box, line.Box);
        }
    }

    private sealed record PixelRect(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;
        public double CenterX => X + Width / 2;

        public static PixelRect Union(PixelRect left, PixelRect right)
        {
            double x = Math.Min(left.X, right.X);
            double y = Math.Min(left.Y, right.Y);
            double farRight = Math.Max(left.Right, right.Right);
            double bottom = Math.Max(left.Bottom, right.Bottom);
            return new PixelRect(x, y, farRight - x, bottom - y);
        }
    }
}
