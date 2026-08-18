namespace TintaES.Wpf;

/// <summary>
/// Mantiene sincronizado el texto del botón de exportación con la selección real del panel.
/// La selección inicial de una sesión pertenece únicamente a SyncPageSelectionPanel; aquí no se
/// vuelve a seleccionar ninguna página ni se mantiene un segundo estado de sesión.
/// </summary>
public partial class MainWindow
{
    private void UpdateCbzExportSelectionCaption()
    {
        if (_exportComicButton is null)
        {
            return;
        }

        int total = _comicPages.Count;
        int selected = _selectedComicPageIndices.Count;
        string content = total == 0
            ? "Exportar CBZ"
            : selected == total
                ? $"Exportar CBZ ({total})"
                : $"Exportar CBZ ({selected}/{total})";
        string toolTip = total == 0
            ? "Exportar páginas a un archivo CBZ"
            : $"Se exportarán {selected} de {total} páginas. La selección se controla en el panel izquierdo.";

        if (!Equals(_exportComicButton.Content, content))
        {
            _exportComicButton.Content = content;
        }
        if (!Equals(_exportComicButton.ToolTip, toolTip))
        {
            _exportComicButton.ToolTip = toolTip;
        }
    }
}
