using System.Windows;
using System.Windows.Media;

namespace TintaES.Wpf.Services;

public static class ComicFontResolver
{
    private static readonly Lazy<FontFamily> ComicFallback = new(ResolveComicFallback);

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
        try
        {
            var resourceUri = new Uri(
                "pack://application:,,,/TintaES;component/Resources/Fonts/anime_ace_3.ttf",
                UriKind.Absolute);
            if (Application.GetResourceStream(resourceUri) is not null)
            {
                return new FontFamily(
                    new Uri(
                        "pack://application:,,,/TintaES;component/Resources/Fonts/",
                        UriKind.Absolute),
                    "./#Anime Ace v3");
            }
        }
        catch
        {
            // La fuente empaquetada es opcional. Se usa una instalada si el recurso no existe.
        }

        FontFamily? installedComic = Fonts.SystemFontFamilies.FirstOrDefault(font =>
            string.Equals(font.Source, "Anime Ace v3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(font.Source, "Comic Sans MS", StringComparison.OrdinalIgnoreCase));
        return installedComic ?? new FontFamily("Arial");
    }
}
