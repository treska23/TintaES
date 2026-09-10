using System.Runtime.CompilerServices;
using System.Text.Json;
using TintaES.Core;

internal static class ComicResearchContextRegression
{
    [ModuleInitializer]
    internal static void VerifyResearchContextIsCompactAndSeparateFromOcr()
    {
        var context = new ComicResearchContext
        {
            ComicTitle = "Spider-Punk: Arms Race #2",
            Findings =
            [
                "Hobie Brown is Spider-Punk and speaks informally with his bandmates.",
                "Kraven and the Hunters are the opposing group in this issue."
            ],
            Sources =
            [
                new ComicResearchSource
                {
                    Title = "Publisher page",
                    Url = "https://example.test/comic",
                    Snippet = "Official synopsis."
                }
            ]
        };

        string prompt = context.ToTranslationPrompt(600);
        if (!prompt.Contains("CONTEXTO DOCUMENTADO", StringComparison.Ordinal)
            || !prompt.Contains("Hobie Brown", StringComparison.Ordinal)
            || prompt.Length > 600)
        {
            throw new InvalidOperationException(
                "La ficha de investigación debe producir un contexto compacto y reconocible.");
        }

        ComicResearchAmbient.CurrentPrompt = prompt;
        try
        {
            var first = new ComicRegion
            {
                Order = 1,
                Original = "WE'LL BE WAITING.",
                OcrAlternatives = ["WE WILL BE WAITING."]
            };
            var second = new ComicRegion
            {
                Order = 2,
                Original = "LET'S GO.",
                OcrAlternatives = ["LETS GO."]
            };

            if (first.OcrAlternatives.Any(value =>
                    value.StartsWith("CONTEXTO DOCUMENTADO", StringComparison.Ordinal))
                || second.OcrAlternatives.Any(value =>
                    value.StartsWith("CONTEXTO DOCUMENTADO", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "La investigación de la obra nunca puede aparecer como alternativa OCR de un bocadillo.");
            }

            if (!first.OcrAlternatives.SequenceEqual(["WE WILL BE WAITING."])
                || !first.StoredOcrAlternatives.SequenceEqual(["WE WILL BE WAITING."])
                || !second.StoredOcrAlternatives.SequenceEqual(["LETS GO."]))
            {
                throw new InvalidOperationException(
                    "Las alternativas OCR deben conservar exclusivamente las lecturas reales de cada zona.");
            }

            if (!string.Equals(ComicResearchAmbient.CurrentPrompt, prompt, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "El contexto documental puede seguir disponible por su canal propio sin contaminar el OCR.");
            }

            string json = JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (json.Contains("CONTEXTO DOCUMENTADO", StringComparison.Ordinal)
                || !json.Contains("WE WILL BE WAITING", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "La persistencia debe guardar el OCR real y nunca la investigación como evidencia del bocadillo.");
            }
        }
        finally
        {
            ComicResearchAmbient.CurrentPrompt = null;
        }
    }
}
