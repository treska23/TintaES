using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private Button? _comicReaderButton;

    private void InstallComicReaderCommand()
    {
        if (_comicReaderButton is not null || OpenImageButton.Parent is not StackPanel openPanel)
        {
            return;
        }

        _comicReaderButton = new Button
        {
            Content = "Visualizar cómic",
            Style = FindResource("ToolbarButton") as Style,
            Margin = new Thickness(7, 0, 0, 0),
            ToolTip = "Abrir un CBZ en el lector independiente de TintaES"
        };
        _comicReaderButton.Click += OpenComicReaderButton_Click;

        int anchorIndex = _openFolderButton is not null
            ? openPanel.Children.IndexOf(_openFolderButton)
            : openPanel.Children.IndexOf(OpenImageButton);
        openPanel.Children.Insert(Math.Min(openPanel.Children.Count, anchorIndex + 1), _comicReaderButton);
    }

    private void OpenComicReaderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Visualizar cómic CBZ",
            Filter = "Comic Book ZIP|*.cbz|Todos los archivos|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var reader = new ComicReaderWindow(dialog.FileName)
        {
            Owner = this
        };
        reader.Show();
    }
}
