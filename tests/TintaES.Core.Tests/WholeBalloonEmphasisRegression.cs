using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class WholeBalloonEmphasisRegression
{
    [ModuleInitializer]
    internal static void VerifyColoredWordStaysInsideWholeBalloon()
    {
        var container = new NormalizedRect(610, 95, 300, 155);
        ComicRegion[] fragments =
        [
            new()
            {
                Original = "KRAVEN BITES NOTHING! KRAVEN ONLY",
                Type = "dialogue",
                Confidence = 0.93,
                BubbleConfidence = 0.92,
                TextBox = new NormalizedRect(650, 112, 190, 58),
                BubbleBox = container
            },
            new()
            {
                Original = "TAKES!",
                Type = "sfx",
                Confidence = 0.86,
                TextBox = new NormalizedRect(832, 139, 48, 19),
                BubbleBox = null,
                Style = new ComicTextStyle { TextColor = "#D71920" }
            },
            new()
            {
                Original = "AND ONCE YOU AND THIS TRASH ARE GONE, HE WILL HAVE WHAT IS PROMISED!",
                Type = "dialogue",
                Confidence = 0.94,
                BubbleConfidence = 0.92,
                TextBox = new NormalizedRect(653, 164, 225, 70),
                BubbleBox = container
            }
        ];

        IReadOnlyList<ComicRegion> grouped = BalloonRegionGrouper.Group(fragments);
        if (grouped.Count != 1)
        {
            throw new InvalidOperationException(
                "La palabra roja TAKES! debe reunirse con las partes negras del mismo bocadillo.");
        }

        ComicRegion balloon = grouped[0];
        const string expected =
            "KRAVEN BITES NOTHING! KRAVEN ONLY TAKES! AND ONCE YOU AND THIS TRASH ARE GONE, HE WILL HAVE WHAT IS PROMISED!";
        if (!string.Equals(balloon.Original, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"El texto completo del bocadillo no se conservó. Resultado: {balloon.Original}");
        }

        if (!string.Equals(balloon.Type, "dialogue", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Una palabra roja incrustada no puede convertir el bocadillo en onomatopeya.");
        }

        if (balloon.HasRenderableTranslation)
        {
            throw new InvalidOperationException(
                "Al reunir fragmentos debe descartarse la traducción parcial y retraducirse el bocadillo entero.");
        }
    }
}
