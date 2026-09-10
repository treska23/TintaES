using System.Windows;
using System.Windows.Controls;

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
            ToolTip = "Abrir el documento actual en el lector de TintaES"
        };
        _comicReaderButton.Click += OpenComicReaderButton_Click;

        int anchorIndex = _openFolderButton is not null
            ? openPanel.Children.IndexOf(_openFolderButton)
            : openPanel.Children.IndexOf(OpenImageButton);
        openPanel.Children.Insert(Math.Min(openPanel.Children.Count, anchorIndex + 1), _comicReaderButton);
        _comicReaderButton.IsEnabled = _comicPages.Count > 0 && !_comicBatchBusy;
    }

    private async void OpenComicReaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0 || _comicBatchBusy || _pageNavigationBusy)
        {
            return;
        }

        PersistVisibleComicPageRegions();

        // El ejecutable TintaES.Reader y el botón «Leer cómic» usan exactamente la misma ventana:
        // MainWindow en modo de solo lectura. El antiguo ComicReaderWindow duplicaba navegación,
        // hit-testing, tarjeta y gestos, y era la causa de que ambos lectores se comportasen distinto.
        var reader = new MainWindow(readerOnly: true)
        {
            Owner = this,
            ShowInTaskbar = false
        };

        try
        {
            reader.Show();
            await reader.OpenReaderSnapshotAsync(this);
        }
        catch (Exception exception)
        {
            if (reader.IsVisible)
            {
                reader.Close();
            }
            MessageBox.Show(
                this,
                $"No se pudo abrir el lector.\n\n{exception.Message}",
                "Tinta ES Reader",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
