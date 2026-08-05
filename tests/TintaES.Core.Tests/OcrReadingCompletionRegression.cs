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
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Regresión OCR: " + message);
        }
    }
}
