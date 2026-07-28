using System.Windows.Media;

namespace TintaES.Wpf.Services;

public static class ComicFontResolver
{
    private static readonly Lazy<FontFamily> BundledComicFont = new(() =>
        new FontFamily(
            new Uri(
                "pack://application:,,,/TintaES;component/Resources/Fonts/",
                UriKind.Absolute),
            "./#Anime Ace v3"));

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
            "comic" => BundledComicFont.Value,
            "handwritten" => new FontFamily("Segoe Print"),
            "sans" => new FontFamily("Arial"),
            "condensed" => new FontFamily("Arial Narrow"),
            "serif" => new FontFamily("Georgia"),
            "display" => new FontFamily("Impact"),
            "monospace" => new FontFamily("Consolas"),
            _ => BundledComicFont.Value
        };
    }
}
