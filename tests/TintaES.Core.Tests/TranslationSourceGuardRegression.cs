using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class TranslationSourceGuardRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        RejectsHalftoneAndRepeatedLetterNoise();
        RejectsBrokenVocalisationNoiseFromRealPage();
        AcceptsActualComicDialogueAndSfx();
        AcceptsRealVocalisations();
        AcceptsARealStoredOcrAlternative();
        IgnoresDocumentResearchAsOcrEvidence();
        Console.WriteLine("OK  La barrera OCR impide traducir ruido como si fuera diálogo");
    }

    private static void RejectsHalftoneAndRepeatedLetterNoise()
    {
        string[] noise =
        [
            "OOO OOO OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO",
            "oooo acaoooc coooc ooooc aoooc ooooooooooooooooo",
            "CCCCCCCCCCCCCCCC",
            "OOOOOOOOOO CCOOOOOOOOO OOOOOOOOOO",
            "aaaaoaaaa aaaaoaaaa aaaaoaaaa"
        ];

        foreach (string source in noise)
        {
            var region = new ComicRegion
            {
                Original = source,
                Type = "dialogue",
                Confidence = 0.95
            };
            Require(!TranslationSourceGuard.IsReliable(region),
                $"El ruido OCR «{source}» no puede convertirse en objetivo de traducción.");
        }
    }

    private static void RejectsBrokenVocalisationNoiseFromRealPage()
    {
        string[] noise =
        [
            "nooo «jooo ooo",
            "NOOO JOOO OOO",
            "ooo jooo ooo",
            "cooo oooo cooo"
        ];

        foreach (string source in noise)
        {
            var region = new ComicRegion
            {
                Original = source,
                Type = "dialogue",
                Confidence = 0.95
            };
            Require(!TranslationSourceGuard.IsReliable(region),
                $"Una secuencia rota de pseudo-vocalizaciones no puede traducirse por contexto: «{source}».");
        }
    }

    private static void AcceptsActualComicDialogueAndSfx()
    {
        string[] dialogue =
        [
            "YES, SIR.",
            "BUT--",
            "OH!",
            "BETWEEN BATMAN AND ANYONE ELSE HAPPENING TONIGHT--",
            "BULLOCK. IT CAME STRAIGHT FROM ABOVE."
        ];

        foreach (string source in dialogue)
        {
            var region = new ComicRegion
            {
                Original = source,
                Type = "dialogue",
                Confidence = 0.50
            };
            Require(TranslationSourceGuard.IsReliable(region),
                $"Una frase real y legible no debe ser descartada: «{source}».");
        }

        var sfx = new ComicRegion
        {
            Original = "BZZZZZZ",
            Type = "sfx",
            Confidence = 0.70
        };
        Require(TranslationSourceGuard.IsReliable(sfx),
            "Un SFX repetitivo legítimo no debe confundirse con ruido de diálogo.");
    }

    private static void AcceptsRealVocalisations()
    {
        string[] vocalisations =
        [
            "NOOO!",
            "NOOO! NOOO!",
            "OOOH!",
            "HA HA HA!"
        ];

        foreach (string source in vocalisations)
        {
            var region = new ComicRegion
            {
                Original = source,
                Type = "dialogue",
                Confidence = 0.65
            };
            Require(TranslationSourceGuard.IsReliable(region),
                $"Una vocalización real no debe descartarse solo por repetir letras: «{source}».");
        }
    }

    private static void AcceptsARealStoredOcrAlternative()
    {
        var region = new ComicRegion
        {
            Original = "OOOOOOOOOOOOOOOO",
            StoredOcrAlternatives = ["YES, SIR."],
            Type = "dialogue",
            Confidence = 0.60
        };

        Require(TranslationSourceGuard.IsReliable(region),
            "Una lectura principal dañada puede salvarse si otra pasada OCR real sí contiene texto legible.");
    }

    private static void IgnoresDocumentResearchAsOcrEvidence()
    {
        ComicResearchAmbient.CurrentPrompt =
            "CONTEXTO DOCUMENTADO DE LA OBRA: Batman habla con Bullock sobre un crimen.";
        try
        {
            var region = new ComicRegion
            {
                Order = 1,
                Original = "OOOOOOOOOOOOOOOOOOOO",
                Type = "dialogue",
                Confidence = 0.90
            };

            Require(!TranslationSourceGuard.IsReliable(region),
                "El contexto documental no puede rescatar una lectura OCR basura ni convertirse en diálogo.");
            Require(region.OcrAlternatives.Count == 0,
                "El contexto documental no debe aparecer entre las alternativas OCR.");
        }
        finally
        {
            ComicResearchAmbient.CurrentPrompt = null;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Regresión anti-alucinación: {message}");
        }
    }
}
