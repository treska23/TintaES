using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class ReaderClusterHitRegression
{
    [ModuleInitializer]
    internal static void VerifyEachClusteredBalloonKeepsItsOwnClickArea()
    {
        var left = new ComicRegion
        {
            Order = 1,
            Original = "EASY TO SAY WHEN I'M THE ONE DOING MOST OF THE FIGHTING.",
            Translation = "Es fácil decirlo cuando soy yo quien se encarga de casi toda la pelea.",
            Type = "dialogue",
            Confidence = 0.95,
            BubbleConfidence = 0.88,
            TextBox = new NormalizedRect(610, 390, 145, 90),
            BubbleBox = new NormalizedRect(565, 330, 415, 445)
        };
        var middle = new ComicRegion
        {
            Order = 2,
            Original = "I HIT THAT DUDE WITH A GUITAR! TWICE!",
            Translation = "¡Le aticé a ese tipo con una guitarra! ¡Dos veces!",
            Type = "dialogue",
            Confidence = 0.94,
            BubbleConfidence = 0,
            TextBox = new NormalizedRect(745, 455, 110, 95),
            BubbleBox = null
        };
        var lower = new ComicRegion
        {
            Order = 3,
            Original = "TRUE. GUESS I'M JUST THINKIN' BOUT THE LONG OF IT.",
            Translation = "Es verdad. Supongo que solo estaba pensando a largo plazo.",
            Type = "dialogue",
            Confidence = 0.94,
            BubbleConfidence = 0,
            TextBox = new NormalizedRect(790, 610, 120, 100),
            BubbleBox = null
        };

        ComicRegion[] regions = [left, middle, lower];

        ComicRegion? firstHit = ComicRegionHitResolver.Resolve(regions, 600, 405);
        ComicRegion? middleHit = ComicRegionHitResolver.Resolve(regions, 865, 505);
        ComicRegion? lowerHit = ComicRegionHitResolver.Resolve(regions, 922, 660);
        ComicRegion? emptyHit = ComicRegionHitResolver.Resolve(regions, 970, 360);

        AssertReference(firstHit, left,
            "La zona próxima al primer texto debe mostrar su propia traducción.");
        AssertReference(middleHit, middle,
            "La zona próxima al segundo texto debe mostrar su propia traducción.");
        AssertReference(lowerHit, lower,
            "La zona próxima al tercer texto debe mostrar su propia traducción.");
        if (emptyHit is not null)
        {
            throw new InvalidOperationException(
                "Pulsar fuera de la zona cercana al texto no debe mostrar ninguna traducción.");
        }

        NormalizedRect leftHitBox = ComicRegionHitResolver.ResolveHitBox(left);
        if (leftHitBox.Width >= left.BubbleBox!.Width * 0.75
            || leftHitBox.Height >= left.BubbleBox.Height * 0.75)
        {
            throw new InvalidOperationException(
                "La zona pulsable no puede crecer hasta ocupar el bocadillo completo.");
        }
    }

    private static void AssertReference(ComicRegion? actual, ComicRegion expected, string message)
    {
        if (!ReferenceEquals(actual, expected))
        {
            throw new InvalidOperationException(message);
        }
    }
}
