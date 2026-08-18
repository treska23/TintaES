using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class ReaderTouchHitRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var speech = new ComicRegion
        {
            Order = 1,
            Original = "THE READER MUST REACT TO A FINGER",
            Translation = "EL LECTOR DEBE REACCIONAR AL DEDO",
            Type = "dialogue",
            IsEnabled = true,
            BubbleConfidence = 0.92,
            TextBox = new NormalizedRect(420, 420, 100, 40),
            BubbleBox = new NormalizedRect(360, 350, 220, 180),
            RenderBox = new NormalizedRect(370, 365, 200, 150)
        };

        ComicRegion? mouse = ComicRegionHitResolver.Resolve([speech], 380, 380);
        Require(
            mouse is null,
            "El ratón debe seguir usando la zona precisa alrededor del texto.");

        ComicRegion? touch = ComicRegionHitResolver.ResolveForTouch([speech], 380, 380);
        Require(
            ReferenceEquals(touch, speech),
            "Un dedo dentro de un bocadillo fiable debe resolver su traducción aunque no caiga sobre las letras.");

        var oldProjectRegion = new ComicRegion
        {
            Order = 2,
            Original = "OLD PROJECT WITHOUT A RELIABLE BUBBLE BOX",
            Translation = "PROYECTO ANTIGUO SIN BUBBLEBOX FIABLE",
            Type = "dialogue",
            IsEnabled = true,
            BubbleConfidence = 0,
            TextBox = new NormalizedRect(420, 420, 100, 40),
            BubbleBox = null,
            RenderBox = new NormalizedRect(360, 350, 220, 180)
        };

        ComicRegion? oldProjectTouch = ComicRegionHitResolver.ResolveForTouch(
            [oldProjectRegion],
            380,
            380);
        Require(
            ReferenceEquals(oldProjectTouch, oldProjectRegion),
            "Un .tinta antiguo debe seguir siendo táctil usando su RenderBox guardado.");

        var enormousFalseBubble = new ComicRegion
        {
            Order = 3,
            Original = "DO NOT CAPTURE THE WHOLE PANEL",
            Translation = "NO CAPTURES TODA LA VIÑETA",
            Type = "dialogue",
            IsEnabled = true,
            BubbleConfidence = 0.98,
            TextBox = new NormalizedRect(440, 440, 90, 35),
            BubbleBox = new NormalizedRect(80, 80, 840, 840),
            RenderBox = new NormalizedRect(430, 430, 110, 60)
        };

        ComicRegion? rejected = ComicRegionHitResolver.ResolveForTouch(
            [enormousFalseBubble],
            150,
            150);
        Require(
            rejected is null,
            "Una BubbleBox gigantesca no puede convertir media página en un objetivo táctil.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Regresión táctil del Reader: " + message);
        }
    }
}
