using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class AdjacentBalloonSeparationRegression
{
    [ModuleInitializer]
    internal static void VerifyThreeAdjacentBalloonsStaySeparate()
    {
        ComicRegion[] regions =
        [
            new()
            {
                Original = "EASY TO SAY WHEN I'M THE ONE DOING MOST OF THE FIGHTING.",
                Translation = "Es fácil decirlo cuando soy yo quien hace casi toda la pelea.",
                Type = "dialogue",
                TextBox = new NormalizedRect(620, 395, 145, 105),
                BubbleBox = new NormalizedRect(600, 360, 190, 165),
                BubbleConfidence = 0.91
            },
            new()
            {
                Original = "I HIT THAT DUDE WITH A GUITAR! TWICE!",
                Translation = "¡Le di a ese tipo con una guitarra! ¡Dos veces!",
                Type = "dialogue",
                TextBox = new NormalizedRect(755, 450, 120, 110),
                BubbleBox = new NormalizedRect(735, 410, 165, 175),
                BubbleConfidence = 0.92
            },
            new()
            {
                Original = "TRUE. GUESS I'M JUST THINKIN' BOUT THE LONG OF IT.",
                Translation = "Cierto. Supongo que solo pensaba a largo plazo.",
                Type = "dialogue",
                TextBox = new NormalizedRect(790, 585, 145, 105),
                BubbleBox = new NormalizedRect(770, 550, 185, 165),
                BubbleConfidence = 0.90
            }
        ];

        IReadOnlyList<ComicRegion> grouped = BalloonRegionGrouper.Group(regions);
        if (grouped.Count != 3)
        {
            throw new InvalidOperationException(
                $"Tres bocadillos contiguos deben seguir siendo tres zonas pulsables. Resultado: {grouped.Count}.");
        }

        foreach (ComicRegion expected in regions)
        {
            ComicRegion? actual = grouped.SingleOrDefault(region => region.Original == expected.Original);
            if (actual is null || actual.Translation != expected.Translation)
            {
                throw new InvalidOperationException(
                    "Cada bocadillo debe conservar su propia traducción y su propia zona pulsable.");
            }
        }
    }
}
