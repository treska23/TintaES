using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Vista ligera del documento que consume el lector. Conserva las mismas instancias de
/// ComicRegion que el proyecto para que una corrección hecha durante la lectura se guarde
/// después sin duplicar ni desincronizar los textos.
/// </summary>
internal sealed class ReaderComicDocument
{
    public ReaderComicDocument(
        string title,
        IReadOnlyList<ReaderComicPage> pages,
        int initialPageIndex = 0,
        Action<int, ComicRegion>? translationEdited = null)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "Cómic" : title;
        Pages = pages;
        InitialPageIndex = Math.Clamp(initialPageIndex, 0, Math.Max(0, pages.Count - 1));
        TranslationEdited = translationEdited;
    }

    public string Title { get; }

    public IReadOnlyList<ReaderComicPage> Pages { get; }

    public int InitialPageIndex { get; }

    public Action<int, ComicRegion>? TranslationEdited { get; }
}

internal sealed record ReaderComicPage(
    string SourcePath,
    string DisplayName,
    IReadOnlyList<ComicRegion> Regions);
