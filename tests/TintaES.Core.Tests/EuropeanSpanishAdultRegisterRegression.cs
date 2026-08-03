using System.Runtime.CompilerServices;
using TintaES.Core;

internal static class EuropeanSpanishAdultRegisterRegression
{
    [ModuleInitializer]
    internal static void VerifyPeninsularAddressAndAdultRegister()
    {
        var informal = new ComicRegion
        {
            Original = "You need to leave now."
        };
        informal.Translation = "Ustedes tienen que irse ahora.";
        if (informal.HasRenderableTranslation)
        {
            throw new InvalidOperationException(
                "«Ustedes» no puede aceptarse como traducción general de YOU.");
        }

        informal.Translation = "Tenéis que iros ahora.";
        if (!informal.HasRenderableTranslation)
        {
            throw new InvalidOperationException(
                "El plural informal de España debe aceptar vosotros y su conjugación.");
        }

        var formal = new ComicRegion
        {
            Original = "Sir, you need to leave now."
        };
        formal.Translation = "Señor, usted tiene que marcharse ahora.";
        if (!formal.HasRenderableTranslation)
        {
            throw new InvalidOperationException(
                "El usted singular debe conservarse cuando el original marca cortesía real.");
        }

        var adult = new ComicRegion
        {
            Original = "Get the fuck out of here!"
        };
        adult.Translation = "Sal de aquí.";
        if (adult.HasRenderableTranslation)
        {
            throw new InvalidOperationException(
                "Una traducción que suaviza un taco fuerte debe repetirse.");
        }

        adult.Translation = "¡Lárgate de una puta vez!";
        if (!adult.HasRenderableTranslation)
        {
            throw new InvalidOperationException(
                "El registro adulto equivalente debe conservarse sin censura.");
        }
    }
}
