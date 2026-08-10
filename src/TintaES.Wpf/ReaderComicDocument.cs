using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Vista ligera del documento que consume el lector. Puede apuntar al documento vivo del editor
/// o a páginas extraídas temporalmente desde un .tinta por el ejecutable independiente.
/// </summary>
internal sealed class ReaderComicDocument : IDisposable
{
    private Action? _disposeAction;

    public ReaderComicDocument(
        string title,
        IReadOnlyList<ReaderComicPage> pages,
        int initialPageIndex = 0,
        Action<int, ComicRegion>? translationEdited = null,
        Action? disposeAction = null)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "Cómic" : title;
        Pages = pages;
        InitialPageIndex = Math.Clamp(initialPageIndex, 0, Math.Max(0, pages.Count - 1));
        TranslationEdited = translationEdited;
        _disposeAction = disposeAction;
    }

    public string Title { get; }

    public IReadOnlyList<ReaderComicPage> Pages { get; }

    public int InitialPageIndex { get; }

    public Action<int, ComicRegion>? TranslationEdited { get; }

    public void Dispose()
    {
        Action? cleanup = Interlocked.Exchange(ref _disposeAction, null);
        cleanup?.Invoke();
    }
}

internal sealed record ReaderComicPage(
    string SourcePath,
    string DisplayName,
    IReadOnlyList<ComicRegion> Regions);
