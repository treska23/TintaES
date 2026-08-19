using System.Windows.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Permite corregir manualmente el texto fuente cuando el OCR lo ha leído mal.
/// La corrección modifica ComicRegion.Original, por lo que participa en el mismo
/// seguimiento de cambios/guardado que el resto de la edición de la zona.
/// </summary>
public partial class MainWindow
{
    private bool _editableOriginalTextInstalled;

    private void InstallEditableOriginalText()
    {
        if (_editableOriginalTextInstalled)
        {
            return;
        }

        OriginalTextBox.IsReadOnly = false;
        OriginalTextBox.AcceptsReturn = true;
        OriginalTextBox.ToolTip = "Corrige aquí el texto original si el OCR lo ha leído mal";
        OriginalTextBox.TextChanged += OriginalTextBox_TextChanged;
        _editableOriginalTextInstalled = true;
    }

    private void OriginalTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        string corrected = OriginalTextBox.Text;
        if (string.Equals(_selectedRegion.Original, corrected, StringComparison.Ordinal))
        {
            return;
        }

        _selectedRegion.Original = corrected;
        RegionListBox.Items.Refresh();
    }
}
