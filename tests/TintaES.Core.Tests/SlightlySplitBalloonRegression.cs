using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class SlightlySplitBalloonRegression
{
    [ModuleInitializer]
    internal static void VerifySlightContainerDriftStillFormsOneBalloon()
    {
        ComicRegion[] fragments =
        [
            new()
            {
                Original = "FIRST HALF OF THE SENTENCE",
                Type = "dialogue",
                TextBox = new NormalizedRect(130, 100, 120, 40),
                BubbleBox = new NormalizedRect(80, 70, 220, 150),
                BubbleConfidence = 0.92
            },
            new()
            {
                Original = "AND ITS SECOND HALF.",
                Type = "dialogue",
                TextBox = new NormalizedRect(145, 150, 135, 42),
                BubbleBox = new NormalizedRect(115, 88, 220, 150),
                BubbleConfidence = 0.91
            },
            new()
            {
                Original = "A DIFFERENT BALLOON.",
                Type = "dialogue",
                TextBox = new NormalizedRect(380, 110, 120, 45),
                BubbleBox = new NormalizedRect(340, 70, 220, 150),
                BubbleConfidence = 0.93
            }
        ];

        IReadOnlyList<ComicRegion> grouped = BalloonRegionGrouper.Group(fragments);
        if (grouped.Count != 2)
        {
            throw new InvalidOperationException(
                $"Dos lecturas ligeramente desplazadas del mismo globo deben unirse sin absorber el vecino. Resultado: {grouped.Count} zonas.");
        }

        const string expected =
            "FIRST HALF OF THE SENTENCE AND ITS SECOND HALF.";
        if (!string.Equals(grouped[0].Original, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"El bocadillo partido no se reconstruyó completo. Resultado: {grouped[0].Original}");
        }

        if (!string.Equals(
                grouped[1].Original,
                "A DIFFERENT BALLOON.",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "El pequeño aumento de tolerancia no puede fusionar el bocadillo contiguo.");
        }
    }
}
