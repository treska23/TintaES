using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class DominantOcrRecoveryRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var wrongPrimary = new ComicRegion
        {
            Original = "GURK.",
            Translation = "GURK.",
            Type = "dialogue",
            BubbleConfidence = 0.91,
            StoredOcrAlternatives =
            [
                "He isn't everywhere, but he seems to know where their biggest attacks will be, and he meets them head-on. Like tonight at the aquarium.",
                "He isn't everywhere, but he seems to know where their biggest attacks will be and he meets them head-on, like tonight at the aquarium."
            ]
        };

        int recovered = OcrReadingCompletion.PromoteCompleteAlternatives([wrongPrimary]);
        Require(recovered == 1, "Una lectura primaria corta y claramente errónea debe ceder ante dos OCR largos concordantes.");
        Require(
            wrongPrimary.Original.StartsWith("He isn't everywhere", StringComparison.OrdinalIgnoreCase),
            "La didascalia completa debe convertirse en el texto original de la región.");
        Require(
            string.IsNullOrWhiteSpace(wrongPrimary.Translation),
            "La traducción de la lectura errónea debe invalidarse para volver a traducir la frase completa.");

        var realSfx = new ComicRegion
        {
            Original = "GURK!",
            Translation = "¡GURK!",
            Type = "sfx",
            BubbleConfidence = 0.05,
            StoredOcrAlternatives =
            [
                "He isn't everywhere, but he seems to know where their biggest attacks will be.",
                "He isn't everywhere but he seems to know where their biggest attacks will be."
            ]
        };

        int preserved = OcrReadingCompletion.PromoteCompleteAlternatives([realSfx]);
        Require(preserved == 0, "Un SFX real no debe sustituirse por una didascalia vecina aunque existan OCR largos.");
        Require(realSfx.Original == "GURK!", "El SFX legítimo debe conservarse intacto.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Regresión OCR dominante: " + message);
        }
    }
}
