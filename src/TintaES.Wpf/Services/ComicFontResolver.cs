using System.Windows;
using System.Windows.Media;

namespace TintaES.Wpf.Services;

public static class ComicFontResolver
{
    private static readonly Lazy<FontFamily> MangaDialogueFont = new(ResolveMangaDialogueFont);
    private static readonly char[] RequiredSpanishGlyphs = ['O', 'Y', 'Ó', '¡', '¿', 'á', 'é', 'í', 'ó', 'ú', 'ñ'];

    /// <summary>
    /// Fuente única para toda la rotulación. No depende de la clasificación del OCR ni de
    /// datos guardados por versiones anteriores.
    /// </summary>
    public static FontFamily ResolveMangaDialogue() => MangaDialogueFont.Value;

    public static FontFamily Resolve(string? requestedFamily, string category) =>
        category == "comic"
            ? MangaDialogueFont.Value
            : category switch
            {
                "handwritten" => new FontFamily("Segoe Print"),
                "sans" => new FontFamily("Arial"),
                "condensed" => new FontFamily("Arial Narrow"),
                "serif" => new FontFamily("Georgia"),
                "display" => new FontFamily("Impact"),
                "monospace" => new FontFamily("Consolas"),
                _ => MangaDialogueFont.Value
            };

    private static FontFamily ResolveMangaDialogueFont()
    {
        FontFamily? packagedKlee = TryResolvePackagedFont(
            "klee_one_semibold_es.ttf",
            "Klee One");
        if (packagedKlee is not null)
        {
            return packagedKlee;
        }

        FontFamily? installedKlee = Fonts.SystemFontFamilies.FirstOrDefault(font =>
            string.Equals(font.Source, "Klee One", StringComparison.OrdinalIgnoreCase)
            && SupportsSpanish(font));
        if (installedKlee is not null)
        {
            return installedKlee;
        }

        // Respaldo únicamente para que una copia antigua siga arrancando mientras se actualiza
        // el recurso. No se usa Comic Sans ni una fuente monoespaciada.
        return new FontFamily("Segoe Print");
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
            // El recurso se valida también durante la compilación. El respaldo evita que una
            // instalación parcial impida abrir la aplicación.
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
