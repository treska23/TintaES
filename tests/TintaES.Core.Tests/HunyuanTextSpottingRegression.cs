using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class HunyuanTextSpottingRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        const string fullCaption = "He isn't everywhere, but he seems to know where their biggest attacks will be, and he meets them head-on. Like tonight at the aquarium.";
        string response = $$"""
        [
          {
            "text": "{{fullCaption}}",
            "bbox": [510, 115, 850, 270]
          },
          {
            "text": "GURK.",
            "bbox": [120, 680, 205, 725]
          }
        ]
        """;

        IReadOnlyList<HunyuanTextSpot> spots = HunyuanTextSpotting.Parse(response, 1200, 1800);
        Assert(spots.Count == 2, "HunyuanOCR debe parsear los dos bloques JSON.");

        var caption = new ComicRegion
        {
            Order = 1,
            Original = "at the aquarium.",
            Type = "caption",
            Confidence = 0.95,
            BubbleConfidence = 0.82,
            TextBox = new NormalizedRect(650, 228, 150, 32),
            BubbleBox = new NormalizedRect(500, 100, 365, 190),
            RenderBox = new NormalizedRect(510, 110, 350, 175),
            Style = new ComicTextStyle { OriginalLineCount = 1 }
        };
        var sfx = new ComicRegion
        {
            Order = 2,
            Original = "GURK.",
            Type = "sfx",
            TextBox = new NormalizedRect(115, 675, 95, 55),
            RenderBox = new NormalizedRect(110, 670, 105, 65)
        };

        int replacements = HunyuanTextSpotting.ApplyToRegions([caption, sfx], spots);
        Assert(replacements == 1, "Solo la didascalia incompleta debe necesitar sustitución.");
        Assert(caption.Original == fullCaption, "HunyuanOCR debe recuperar la didascalia completa.");
        Assert(
            caption.StoredOcrAlternatives.Contains("at the aquarium."),
            "La lectura OCR parcial anterior debe conservarse como alternativa.");
        Assert(sfx.Original == "GURK.", "Un SFX vecino no debe absorber el texto de la didascalia.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Regresión HunyuanOCR: {message}");
        }
    }
}
