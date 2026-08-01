using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TintaES.Core;

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
            Content = "Leer cómic",
            Style = FindResource("ToolbarButton") as Style,
            Margin = new Thickness(7, 0, 0, 0),
            ToolTip = "Abrir el documento actual en el lector traducido de TintaES"
        };
        _comicReaderButton.Click += OpenComicReaderButton_Click;

        int anchorIndex = _openFolderButton is not null
            ? openPanel.Children.IndexOf(_openFolderButton)
            : openPanel.Children.IndexOf(OpenImageButton);
        openPanel.Children.Insert(Math.Min(openPanel.Children.Count, anchorIndex + 1), _comicReaderButton);
    }

    private void OpenComicReaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count > 0)
        {
            PersistVisibleComicPageRegions();
            var document = new ReaderComicDocument(
                _comicTitle ?? "Cómic",
                _comicPages
                    .Select(page => new ReaderComicPage(
                        page.SourcePath,
                        page.DisplayName,
                        page.Regions))
                    .ToArray(),
                Math.Clamp(_comicPageIndex, 0, _comicPages.Count - 1),
                ReaderTranslationEdited);

            var currentReader = new ComicReaderWindow(document)
            {
                Owner = this
            };
            currentReader.Show();
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Leer cómic CBZ",
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

    private void ReaderTranslationEdited(int pageIndex, ComicRegion region)
    {
        MarkActiveDocumentDirty(pageIndex);
        if (pageIndex != _visibleComicPageIndex)
        {
            return;
        }

        RegionListBox.Items.Refresh();
        if (ReferenceEquals(_selectedRegion, region))
        {
            ShowRegionEditor(region);
        }
        SetFooterStatus($"Traducción corregida en la página {pageIndex + 1}.", "#58A77D");
    }
}
