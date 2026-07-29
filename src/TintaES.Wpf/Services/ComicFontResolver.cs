using System.Windows;
using System.Windows.Media;

namespace TintaES.Wpf.Services;

public static class ComicFontResolver
{
    private static readonly Lazy<FontFamily> ComicFallback = new(ResolveComicFallback);
    private static readonly char[] RequiredSpanishGlyphs = ['O', 'Y', 'Ó', '¡', '¿'];

    public static FontFamily Resolve(string? requestedFamily, string category)
    {
        if (!string.IsNullOrWhiteSpace(requestedFamily))
        {
            FontFamily? installed = Fonts.SystemFontFamilies.FirstOrDefault(font =>
                string.Equals(
                    font.Source,
                    requestedFamily.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            if (installed is not null)
            {
                return installed;
            }
        }

        return category switch
        {
            "comic" => ComicFallback.Value,
            "handwritten" => new FontFamily("Segoe Print"),
            "sans" => new FontFamily("Arial"),
            "condensed" => new FontFamily("Arial Narrow"),
            "serif" => new FontFamily("Georgia"),
            "display" => new FontFamily("Impact"),
            "monospace" => new FontFamily("Consolas"),
            _ => ComicFallback.Value
        };
    }

    private static FontFamily ResolveComicFallback()
    {
        FontFamily? packagedComic = TryResolvePackagedFont(
            "comic_shanns_2.ttf",
            "Comic Shanns");
        if (packagedComic is not null)
        {
            return packagedComic;
        }

        FontFamily? installedComic = Fonts.SystemFontFamilies.FirstOrDefault(font =>
            (string.Equals(font.Source, "Comic Shanns", StringComparison.OrdinalIgnoreCase)
             || string.Equals(font.Source, "Comic Sans MS", StringComparison.OrdinalIgnoreCase))
            && SupportsSpanish(font));
        if (installedComic is not null)
        {
            return installedComic;
        }

        // Anime Ace conserva el aspecto de cómic, pero algunas versiones dibujan «Ó»
        // como «Y» y carecen de los signos de apertura españoles. Solo se utiliza
        // cuando la copia instalada sí contiene todos los glifos necesarios.
        FontFamily? animeAce = Fonts.SystemFontFamilies.FirstOrDefault(font =>
            string.Equals(font.Source, "Anime Ace v3", StringComparison.OrdinalIgnoreCase)
            && SupportsSpanish(font));
        return animeAce ?? new FontFamily("Arial");
    }

    private static FontFamily? TryResolvePackagedFont(string fileName, string familyName)
    {
        try
        {
            var resourceUri = new Uri(
                $"pack://application:,,,/TintaES;component/Resources/Fonts/{fileName}",
                UriKind.Absolute);
            if (Application.GetResourceStream(resourceUri) is not null)
            {
                return new FontFamily(
                    new Uri(
                        "pack://application:,,,/TintaES;component/Resources/Fonts/",
                        UriKind.Absolute),
                    $"./#{familyName}");
            }
        }
        catch
        {
            // La fuente empaquetada es opcional. Se usa una instalada si el recurso no existe.
        }

        return null;
    }

    private static bool SupportsSpanish(FontFamily family)
    {
        var typeface = new Typeface(
            family,
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        return typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphs)
               && RequiredSpanishGlyphs.All(character =>
                   glyphs.CharacterToGlyphMap.ContainsKey(character))
               && glyphs.CharacterToGlyphMap['Ó'] != glyphs.CharacterToGlyphMap['Y'];
    }
}
