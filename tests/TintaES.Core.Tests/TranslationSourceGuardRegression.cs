using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class TranslationSourceGuardRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        RejectsHalftoneAndRepeatedLetterNoise();
        RejectsBrokenVocalisationNoiseFromRealPage();
        RejectsPhantomShortRegionsFromRealPage();
        RejectsPreviouslySavedHallucinationAtRenderTime();
        AcceptsActualComicDialogueAndSfx();
        AcceptsRealVocalisations();
        AcceptsShortProperNameWhenBalloonExists();
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

    private static void RejectsPhantomShortRegionsFromRealPage()
    {
        string[] repeatedNoise = ["OOOC", "OOOC,", "OOOCC", "CCCO"];
        foreach (string source in repeatedNoise)
        {
            var region = new ComicRegion
            {
                Original = source,
                Type = "dialogue",
                Confidence = 0.99,
                BubbleConfidence = 0.95
            };
            Require(!TranslationSourceGuard.IsReliable(region),
                $"El pseudo-token corto «{source}» debe rechazarse incluso con confianza geométrica alta.");
        }

        var noBalloon = new ComicRegion
        {
            Original = "LIO",
            Type = "dialogue",
            Confidence = 0.90,
            BubbleConfidence = 0
        };
        Require(!TranslationSourceGuard.IsReliable(noBalloon),
            "Una palabra corta desconocida detectada sobre el dibujo no es diálogo si no existe contenedor de bocadillo.");
    }

    private static void RejectsPreviouslySavedHallucinationAtRenderTime()
    {
        var automatic = new ComicRegion
        {
            Original = "nooo «jooo ooo",
            Translation = "¡Nooo!",
            Type = "dialogue",
            Confidence = 0.95
        };
        Require(!automatic.HasRenderableTranslation && automatic.DisplayText.Length == 0,
            "Una alucinación ya guardada en un proyecto antiguo tampoco puede volver a mostrarse.");

        var phantom = new ComicRegion
        {
            Original = "OOOC",
            Translation = "OOOC",
            Type = "dialogue",
            Confidence = 0.99,
            BubbleConfidence = 0.95
        };
        Require(!phantom.HasRenderableTranslation && phantom.DisplayText.Length == 0,
            "Un falso OOOC ya guardado tampoco puede volver a rotularse en la página.");

        var manual = new ComicRegion
        {
            Original = "texto libre",
            Translation = "Texto manual",
            Type = "dialogue",
            Confidence = 0,
            IsManual = true
        };
        Require(manual.HasRenderableTranslation,
            "Una zona creada manualmente por el usuario no depende de la confianza del OCR.");
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
            "HA HA HA!",
            "YEEES!"
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

    private static void AcceptsShortProperNameWhenBalloonExists()
    {
        var region = new ComicRegion
        {
            Original = "VICK",
            Type = "dialogue",
            Confidence = 0.70,
            BubbleConfidence = 0.72
        };
        Require(TranslationSourceGuard.IsReliable(region),
            "Un nombre corto dentro de un bocadillo real no debe confundirse con una detección fantasma.");
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
