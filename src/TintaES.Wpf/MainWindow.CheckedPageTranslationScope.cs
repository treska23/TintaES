using System.Windows.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Fija el alcance de una traducción a partir del estado real que el usuario ve en los checkbox.
/// La instantánea se toma una sola vez al pulsar el botón y no cambia aunque la interfaz se
/// refresque, se navegue a otra página o un módulo antiguo modifique después la selección global.
/// </summary>
public partial class MainWindow
{
    private int[] CaptureCheckedComicPageIndices()
    {
        bool completeVisualSet = _comicPages.Count > 0
                                 && _pageSelectionCheckBoxes.Count == _comicPages.Count
                                 && Enumerable.Range(0, _comicPages.Count)
                                     .All(_pageSelectionCheckBoxes.ContainsKey);

        int[] checkedIndices = completeVisualSet
            ? _pageSelectionCheckBoxes
                .Where(pair => pair.Key >= 0
                               && pair.Key < _comicPages.Count
                               && pair.Value.IsChecked == true)
                .Select(pair => pair.Key)
                .Distinct()
                .OrderBy(index => index)
                .ToArray()
            : _selectedComicPageIndices
                .Where(index => index >= 0 && index < _comicPages.Count)
                .Distinct()
                .OrderBy(index => index)
                .ToArray();

        // El estado visual es la fuente de verdad. Se resincroniza el conjunto compartido para
        // que revisión, exportación y guardado vean exactamente la misma selección del usuario.
        _selectedComicPageIndices.Clear();
        _selectedComicPageIndices.UnionWith(checkedIndices);
        return checkedIndices;
    }

    private static string FormatCheckedPageScope(IReadOnlyList<int> indices)
    {
        if (indices.Count == 0)
        {
            return "ninguna";
        }

        string[] pageNumbers = indices
            .Take(12)
            .Select(index => (index + 1).ToString())
            .ToArray();
        string suffix = indices.Count > pageNumbers.Length
            ? $", … (+{indices.Count - pageNumbers.Length})"
            : string.Empty;
        return string.Join(", ", pageNumbers) + suffix;
    }
}
