using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class OcrReadingCompletionRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var incomplete = new ComicRegion
        {
            Original = "All right.",
            Translation = "Vale.",
            StoredOcrAlternatives =
            [
                "All right. Bye, guys!",
                "Unrelated nearby balloon"
            ]
        };

        int promoted = OcrReadingCompletion.PromoteCompleteAlternatives([incomplete]);
        Require(promoted == 1, "Debe detectar que el OCR principal dejó una frase incompleta.");
        Require(
            incomplete.Original == "All right. Bye, guys!",
            "Debe promocionar la lectura completa antes de traducir.");
        Require(
            string.IsNullOrWhiteSpace(incomplete.Translation),
            "Debe invalidar la traducción parcial cuando amplía el texto original.");
        Require(
            incomplete.StoredOcrAlternatives.Contains("All right."),
            "Debe conservar la lectura corta como evidencia OCR secundaria.");

        var neighbouring = new ComicRegion
        {
            Original = "All right.",
            Translation = "De acuerdo.",
            StoredOcrAlternatives =
            [
                "Dad said all right yesterday.",
                "Bye, Dad!"
            ]
        };

        int rejected = OcrReadingCompletion.PromoteCompleteAlternatives([neighbouring]);
        Require(rejected == 0, "No debe mezclar un bocadillo vecino por una coincidencia intermedia.");
        Require(
            neighbouring.Original == "All right.",
            "La lectura principal debe mantenerse cuando ninguna alternativa la completa de forma segura.");
        Require(
            neighbouring.Translation == "De acuerdo.",
            "Una alternativa ajena no debe invalidar una traducción correcta.");

        var damagedEnding = new ComicRegion
        {
            Original = "WE CAN DO TH",
            Translation = "Podemos hacerlo.",
            StoredOcrAlternatives = ["WE CAN DO THIS TOGETHER!"]
        };
        Require(
            OcrReadingCompletion.PromoteCompleteAlternatives([damagedEnding]) == 1
            && damagedEnding.Original == "WE CAN DO THIS TOGETHER!",
            "Debe completar una palabra cortada y conservar las palabras que siguen.");

        var missingMiddle = new ComicRegion
        {
            Original = "I CAN'T BELIEVE THIS",
            Translation = "No puedo creerlo.",
            StoredOcrAlternatives = ["I CAN'T BELIEVE YOU DID THIS!"]
        };
        Require(
            OcrReadingCompletion.PromoteCompleteAlternatives([missingMiddle]) == 1
            && missingMiddle.Original == "I CAN'T BELIEVE YOU DID THIS!",
            "Debe recuperar palabras omitidas en medio de un bocadillo.");

        var shortInteriorCoincidence = new ComicRegion
        {
            Original = "ALL RIGHT",
            Translation = "Vale.",
            StoredOcrAlternatives = ["DAD SAID ALL RIGHT YESTERDAY."]
        };
        Require(
            OcrReadingCompletion.PromoteCompleteAlternatives([shortInteriorCoincidence]) == 0,
            "No debe promocionar una coincidencia corta situada dentro de otro bocadillo.");

        var longCaptionEnding = new ComicRegion
        {
            Original = "at the aquarium.",
            Translation = "en el acuario.",
            Type = "dialogue",
            BubbleConfidence = 0.92,
            StoredOcrAlternatives =
            [
                "He isn't everywhere, but he seems to know where their biggest attacks will be, and he meets them head-on. Like tonight at the aquarium."
            ]
        };
        Require(
            OcrReadingCompletion.PromoteCompleteAlternatives([longCaptionEnding]) == 1,
            "Debe recuperar una didascalia larga aunque el OCR principal conserve solo su frase final.");
        Require(
            longCaptionEnding.Original.StartsWith("He isn't everywhere", StringComparison.OrdinalIgnoreCase)
            && longCaptionEnding.Original.EndsWith("at the aquarium.", StringComparison.OrdinalIgnoreCase),
            "La didascalia completa debe sustituir al fragmento final antes de traducir.");
        Require(
            string.IsNullOrWhiteSpace(longCaptionEnding.Translation),
            "La traducción del fragmento final debe invalidarse al recuperar la didascalia completa.");

        var longSfxLookalike = new ComicRegion
        {
            Original = "AT THE AQUARIUM",
            Translation = "EN EL ACUARIO",
            Type = "sfx",
            BubbleConfidence = 0.05,
            StoredOcrAlternatives =
            [
                "He isn't everywhere, but he seems to know where their biggest attacks will be, and he meets them head-on. Like tonight at the aquarium."
            ]
        };
        Require(
            OcrReadingCompletion.PromoteCompleteAlternatives([longSfxLookalike]) == 0,
            "Un SFX no debe absorber una didascalia larga aunque coincida con su frase final.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Regresión OCR: " + message);
        }
    }
}
