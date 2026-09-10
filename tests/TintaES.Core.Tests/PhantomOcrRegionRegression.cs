using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class PhantomOcrRegionRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        foreach (string type in new[] { "dialogue", "sfx" })
        {
            foreach (string source in new[] { "OOOC", "OOOC,", "OOOCC", "CCCO" })
            {
                var region = new ComicRegion
                {
                    Original = source,
                    Translation = "¡Nooo!",
                    Type = type,
                    Confidence = 0.99,
                    BubbleConfidence = 0.95
                };

                if (TranslationSourceGuard.IsReliable(region) || region.HasRenderableTranslation)
                {
                    throw new InvalidOperationException(
                        $"Regresión anti-alucinación: «{source}» no puede sobrevivir como {type} ni renderizarse.");
                }
            }
        }

        var legitimateSfx = new ComicRegion
        {
            Original = "BZZZZZZ",
            Type = "sfx",
            Confidence = 0.75
        };
        if (!TranslationSourceGuard.IsReliable(legitimateSfx))
        {
            throw new InvalidOperationException(
                "Regresión anti-alucinación: un BZZZZZZ real debe seguir siendo un SFX válido.");
        }

        Console.WriteLine("OK  Las detecciones fantasma cortas se descartan también si CTD las llama SFX");
    }
}
