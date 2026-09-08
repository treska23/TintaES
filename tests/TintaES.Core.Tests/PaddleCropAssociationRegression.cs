using System.Runtime.CompilerServices;
using System.Text.Json;
using TintaES.Core;

internal static class PaddleCropAssociationRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        VerifyAdjacentBalloonsKeepTheirOwnReading();
        VerifyBothReadingsCompleteWithoutMixing();
        VerifyUnknownAndAmbiguousCropsDoNotChangeRegions();
        VerifyCropGeometryAndFloatingPointRoundTrip();
        VerifyShorterAndUnchangedReadingsKeepTranslation();
    }

    private static void VerifyAdjacentBalloonsKeepTheirOwnReading()
    {
        ComicRegion[] regions = CreateAdjacentBalloons();
        string leftOriginal = regions[0].Original;
        string leftTranslation = regions[0].Translation;
        const string rightReading = "I HIT THAT DUDE WITH A GUITAR! TWICE!";
        var spot = new HunyuanTextSpot(rightReading, PaddleCropAssociation.GetCropBox(regions[1]));

        Require(ReferenceEquals(PaddleCropAssociation.FindOwner(regions, spot.Box), regions[1]),
            "El recorte de la derecha debe conservar su dueño aunque comparta BubbleBox con la izquierda.");
        Require(PaddleCropAssociation.ApplyToRegions(regions, [spot]) == 1,
            "Solo el bocadillo que originó el recorte debe corregirse.");
        Require(regions[1].Original == rightReading, "La lectura debe llegar al bocadillo de la derecha.");
        Require(regions[0].Original == leftOriginal && regions[0].Translation == leftTranslation,
            "El bocadillo de la izquierda debe mantener su lectura y su traducción.");
        Require(regions[0].StoredOcrAlternatives.Count == 0,
            "La lectura del vecino tampoco debe contaminar las alternativas OCR.");
    }

    private static void VerifyBothReadingsCompleteWithoutMixing()
    {
        ComicRegion[] regions = CreateAdjacentBalloons();
        const string leftReading = "EASY TO SAY WHEN I'M THE ONE DOING MOST OF THE FIGHTING.";
        const string rightReading = "I HIT THAT DUDE WITH A GUITAR! TWICE!";
        string leftPrevious = regions[0].Original;
        string rightPrevious = regions[1].Original;
        HunyuanTextSpot[] spots =
        [
            new(rightReading, PaddleCropAssociation.GetCropBox(regions[1])),
            new(leftReading, PaddleCropAssociation.GetCropBox(regions[0]))
        ];

        Require(PaddleCropAssociation.ApplyToRegions(regions, spots) == 2,
            "Deben recuperarse las dos lecturas completas aunque el worker las entregue en orden inverso.");
        Require(regions[0].Original == leftReading && regions[1].Original == rightReading,
            "Cada mitad del bocadillo doble debe contener exclusivamente su propia lectura.");
        Require(regions.All(region => region.Translation == string.Empty),
            "Las dos traducciones parciales deben invalidarse al completar su propio texto.");
        Require(regions[0].StoredOcrAlternatives.SequenceEqual([leftPrevious])
                && regions[1].StoredOcrAlternatives.SequenceEqual([rightPrevious]),
            "Cada región debe guardar su lectura anterior como alternativa, sin intercambios.");
        Require(regions[0].Style.OriginalLineCount == 3 && regions[1].Style.OriginalLineCount == 2,
            "La relectura de un recorte no debe reducir el número de líneas ya detectado.");
    }

    private static void VerifyUnknownAndAmbiguousCropsDoNotChangeRegions()
    {
        ComicRegion[] regions = CreateAdjacentBalloons();
        NormalizedRect crop = PaddleCropAssociation.GetCropBox(regions[1]);
        var staleCrop = crop with { X = crop.X + 0.001 };
        var orphan = new HunyuanTextSpot("A MUCH LONGER READING FROM AN OLD CROP.", staleCrop);
        Require(PaddleCropAssociation.FindOwner(regions, staleCrop) is null
                && PaddleCropAssociation.ApplyToRegions(regions, [orphan]) == 0,
            "Un recorte desconocido de una caché anterior debe rechazarse aunque esté casi en el mismo lugar.");

        regions[1].IsEnabled = false;
        var spot = new HunyuanTextSpot("I HIT THAT DUDE WITH A GUITAR! TWICE!", crop);
        Require(PaddleCropAssociation.FindOwner(regions, crop) is null
                && PaddleCropAssociation.ApplyToRegions(regions, [spot]) == 0,
            "El recorte de una región deshabilitada no debe transferirse al vecino habilitado.");

        var duplicate = new ComicRegion { TextBox = regions[1].TextBox, Original = "A DIFFERENT READING." };
        ComicRegion[] overlapping = [regions[1], duplicate];
        Require(ReferenceEquals(PaddleCropAssociation.FindOwner(overlapping, crop), duplicate),
            "Solo las regiones habilitadas participan en la identidad de los recortes enviados.");
        regions[1].IsEnabled = true;
        Require(PaddleCropAssociation.FindOwner(overlapping, crop) is null
                && PaddleCropAssociation.ApplyToRegions(overlapping, [spot]) == 0,
            "Dos regiones habilitadas con recortes idénticos deben dejar la lectura intacta por ambigüedad.");
        Require(PaddleCropAssociation.FindOwner([duplicate, regions[1]], crop) is null,
            "Cambiar el orden de regiones ambiguas no debe elegir un dueño arbitrario.");
        Require(PaddleCropAssociation.ApplyToRegions([], [spot]) == 0
                && PaddleCropAssociation.ApplyToRegions(regions, []) == 0,
            "Las entradas vacías deben conservar su comportamiento sin cambios.");
    }

    private static void VerifyCropGeometryAndFloatingPointRoundTrip()
    {
        var narrow = new ComicRegion { TextBox = new NormalizedRect(0, 0, 40, 20) };
        NormalizedRect narrowCrop = PaddleCropAssociation.GetCropBox(narrow);
        Require(narrowCrop.X == 0 && narrowCrop.Y == 0
                && Math.Abs(narrowCrop.Width - 49.6) < 0.000001
                && Math.Abs(narrowCrop.Height - 25.6) < 0.000001,
            "Los recortes pequeños deben conservar los márgenes tipográficos y el límite de página existentes.");

        var region = new ComicRegion { TextBox = new NormalizedRect(612.34567, 412.98765, 143.21098, 83.45678) };
        NormalizedRect crop = PaddleCropAssociation.GetCropBox(region);
        string response = JsonSerializer.Serialize(new[]
        {
            new { text = "THE COMPLETE READING.", bbox = new[] { crop.X, crop.Y, crop.Right, crop.Bottom } }
        });
        HunyuanTextSpot roundTripped = HunyuanTextSpotting.Parse(response, 1800, 2700).Single();
        Require(ReferenceEquals(PaddleCropAssociation.FindOwner([region], roundTripped.Box), region),
            "La serialización de los bordes y la reconstrucción de anchura/altura deben conservar la identidad.");
        Require(ReferenceEquals(PaddleCropAssociation.FindOwner([region], crop with { X = crop.X + 0.00000001 }), region),
            "El ruido mínimo de coma flotante no debe convertir un recorte válido en huérfano.");
    }

    private static void VerifyShorterAndUnchangedReadingsKeepTranslation()
    {
        ComicRegion[] regions = CreateAdjacentBalloons();
        ComicRegion owner = regions[1];
        owner.Original = "I HIT THAT DUDE WITH A GUITAR! TWICE!";
        owner.Translation = "¡Le di a ese tipo con una guitarra! ¡Dos veces!";
        string translation = owner.Translation;
        NormalizedRect crop = PaddleCropAssociation.GetCropBox(owner);
        Require(PaddleCropAssociation.ApplyToRegions(regions, [new HunyuanTextSpot("TWICE!", crop)]) == 0,
            "La asociación exacta debe mantener la protección contra sustituir una lectura larga por un fragmento.");
        Require(PaddleCropAssociation.ApplyToRegions(regions, [new HunyuanTextSpot(owner.Original, crop)]) == 0,
            "Una lectura idéntica no debe provocar otra sustitución.");
        Require(owner.Translation == translation && owner.StoredOcrAlternatives.Count == 0,
            "Lecturas rechazadas o idénticas deben conservar la traducción y las alternativas existentes.");
    }

    private static ComicRegion[] CreateAdjacentBalloons()
    {
        var sharedBubble = new NormalizedRect(600, 360, 300, 235);
        return
        [
            new()
            {
                Original = "EASY TO SAY.",
                Translation = "Es fácil decirlo.",
                TextBox = new NormalizedRect(620, 395, 145, 105),
                BubbleBox = sharedBubble,
                RenderBox = new NormalizedRect(620, 395, 145, 105),
                Style = new ComicTextStyle { OriginalLineCount = 3 }
            },
            new()
            {
                Original = "I HIT THAT DUDE.",
                Translation = "Le di a ese tipo.",
                TextBox = new NormalizedRect(755, 450, 120, 110),
                BubbleBox = sharedBubble,
                RenderBox = new NormalizedRect(755, 450, 120, 110),
                Style = new ComicTextStyle { OriginalLineCount = 2 }
            }
        ];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Regresión de recortes PaddleOCR: {message}");
        }
    }
}
