using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class RegionMergeIsolationRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var caption = new ComicRegion
        {
            Original = "He isn't everywhere, but he seems to know where their biggest attacks will be.",
            Confidence = 0.86,
            TextBox = new NormalizedRect(300, 220, 300, 120),
            RenderBox = new NormalizedRect(290, 210, 320, 140)
        };
        var unrelatedShortReading = new ComicRegion
        {
            Original = "GURK.",
            Confidence = 0.94,
            TextBox = new NormalizedRect(390, 260, 70, 35),
            RenderBox = new NormalizedRect(382, 252, 86, 50)
        };

        IReadOnlyList<ComicRegion> merged = RegionMerger.Merge([caption, unrelatedShortReading]);
        if (merged.Count != 2)
        {
            throw new InvalidOperationException(
                "Regresión de regiones: una lectura corta no relacionada no puede desaparecer " +
                "solo por estar contenida geométricamente en una caja OCR mayor.");
        }
    }
}
