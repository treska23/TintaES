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
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Regresión OCR: " + message);
        }
    }
}
