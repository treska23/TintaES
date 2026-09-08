using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TintaES.Wpf;

/// <summary>
/// Barra de menús clásica para las operaciones de documento. Mantiene los botones rápidos
/// existentes, pero ofrece el flujo convencional Archivo / Edición / Ver / Ayuda.
/// </summary>
public partial class MainWindow
{
    private Menu? _classicMenu;
    private MenuItem? _menuSaveProject;
    private MenuItem? _menuSaveProjectAs;
    private MenuItem? _menuExportTranslationScript;
    private MenuItem? _menuImportTranslationScript;
    private MenuItem? _menuExportCbz;
    private MenuItem? _menuExportPsd;
    private MenuItem? _menuExportPng;

    private void InstallClassicMenu()
    {
        if (_classicMenu is not null || Content is not Grid rootGrid)
        {
            return;
        }

        // El XAML original no reservaba una fila para menú. Insertamos una arriba y desplazamos
        // las cuatro filas existentes sin tocar el resto de la composición visual.
        UIElement[] existingChildren = rootGrid.Children.Cast<UIElement>().ToArray();
        rootGrid.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
        foreach (UIElement child in existingChildren)
        {
            Grid.SetRow(child, Grid.GetRow(child) + 1);
        }

        _classicMenu = new Menu
        {
            Height = 27,
            Background = new SolidColorBrush(Color.FromRgb(242, 238, 229)),
            Foreground = Brushes.Black,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(_classicMenu, 0);
        Panel.SetZIndex(_classicMenu, 20_000);
        rootGrid.Children.Add(_classicMenu);

        MenuItem file = new() { Header = "_Archivo" };
        file.Items.Add(CreateMenuItem("Abrir _cómic…", "Ctrl+O", (_, e) => OpenComicFilesButton_Click_Multi(this, e)));
        file.Items.Add(CreateMenuItem("Abrir _proyecto…", "Ctrl+Shift+O", OpenProjectMenu_Click));
        file.Items.Add(new Separator());

        _menuSaveProject = CreateMenuItem("_Guardar proyecto", "Ctrl+S", SaveProjectButton_Click);
        _menuSaveProjectAs = CreateMenuItem("Guardar proyecto _como…", "Ctrl+Shift+S", SaveProjectAsMenu_Click);
        file.Items.Add(_menuSaveProject);
        file.Items.Add(_menuSaveProjectAs);
        file.Items.Add(new Separator());

        _menuExportTranslationScript = CreateMenuItem(
            "Exportar _guion de traducción…",
            null,
            ExportTranslationScriptMenu_Click);
        _menuImportTranslationScript = CreateMenuItem(
            "_Importar guion de traducción…",
            null,
            ImportTranslationScriptMenu_Click);
        file.Items.Add(_menuExportTranslationScript);
        file.Items.Add(_menuImportTranslationScript);
        file.Items.Add(new Separator());

        _menuExportCbz = CreateMenuItem("Exportar páginas seleccionadas a _CBZ…", null, ExportComicButton_Click_Robust);
        _menuExportPsd = CreateMenuItem("Exportar página _PSD…", null, ExportPsdButton_Click);
        _menuExportPng = CreateMenuItem("Exportar _página…", null, ExportButton_Click);
        file.Items.Add(_menuExportCbz);
        file.Items.Add(_menuExportPsd);
        file.Items.Add(_menuExportPng);
        file.Items.Add(new Separator());
        file.Items.Add(CreateMenuItem("_Salir", "Alt+F4", (_, _) => Close()));
        file.SubmenuOpened += (_, _) => UpdateClassicMenuAvailability();

        MenuItem edit = new() { Header = "_Edición" };
        edit.Items.Add(CreateMenuItem("_Añadir zona", null, AddRegionButton_Click));
        edit.Items.Add(CreateMenuItem("Analizar y _traducir", null, AnalyzeComicButton_Click));

        MenuItem view = new() { Header = "_Ver" };
        view.Items.Add(CreateMenuItem("Página _anterior", null, (_, _) => NavigateFromMenu(-1)));
        view.Items.Add(CreateMenuItem("Página _siguiente", null, (_, _) => NavigateFromMenu(1)));
        view.Items.Add(new Separator());
        view.Items.Add(CreateMenuItem("Ver _original", null, (_, _) => ShowPreviewMode("original")));
        view.Items.Add(CreateMenuItem("Ver _resultado", null, (_, _) => ShowPreviewMode("result")));
        view.Items.Add(new Separator());
        view.Items.Add(CreateMenuItem("Selector de _páginas", null, (_, _) =>
            SetPageSelectionPanelVisible(_pageSelectionPanel?.Visibility != Visibility.Visible)));

        MenuItem help = new() { Header = "A_yuda" };
        help.Items.Add(CreateMenuItem("_Acerca de Tinta ES", null, (_, _) =>
            MessageBox.Show(
                this,
                "Tinta ES\nTraductor y rotulador local de cómics.",
                "Acerca de Tinta ES",
                MessageBoxButton.OK,
                MessageBoxImage.Information)));

        _classicMenu.Items.Add(file);
        _classicMenu.Items.Add(edit);
        _classicMenu.Items.Add(view);
        _classicMenu.Items.Add(help);
        UpdateClassicMenuAvailability();
    }

    private static MenuItem CreateMenuItem(string header, string? gesture, RoutedEventHandler handler)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture ?? string.Empty
        };
        item.Click += handler;
        return item;
    }

    private void UpdateClassicMenuAvailability()
    {
        bool hasComic = _comicPages.Count > 0;
        bool available = hasComic && !_comicBatchBusy && !_pageNavigationBusy;
        if (_menuSaveProject is not null) _menuSaveProject.IsEnabled = available;
        if (_menuSaveProjectAs is not null) _menuSaveProjectAs.IsEnabled = available;
        if (_menuExportTranslationScript is not null) _menuExportTranslationScript.IsEnabled = available;
        if (_menuImportTranslationScript is not null) _menuImportTranslationScript.IsEnabled = available;
        if (_menuExportCbz is not null) _menuExportCbz.IsEnabled = available;
        if (_menuExportPng is not null) _menuExportPng.IsEnabled = available && _originalBitmap is not null;
        if (_menuExportPsd is not null)
        {
            _menuExportPsd.IsEnabled = available
                && _comicPageIndex >= 0
                && _comicPageIndex < _comicPages.Count
                && _comicPages[_comicPageIndex].Processed
                && _comicPages[_comicPageIndex].Error is null;
        }
    }

    private void OpenProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir proyecto de TintaES",
            Filter = "Proyecto TintaES|*.tinta",
            FilterIndex = 1,
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            _ = LoadTintaProjectAsync(dialog.FileName);
        }
    }

    private async void SaveProjectAsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_comicPages.Count == 0)
        {
            return;
        }

        PersistVisibleComicPageRegions();
        var dialog = new SaveFileDialog
        {
            Title = "Guardar proyecto de TintaES como",
            FileName = MakeSafeFileName(_comicTitle ?? "comic") + ".tinta",
            DefaultExt = ".tinta",
            Filter = "Proyecto TintaES|*.tinta"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitleText.Text = "Guardando proyecto…";
        BusyProgressBar.IsIndeterminate = true;
        FooterProgressBar.Visibility = Visibility.Visible;
        FooterProgressBar.IsIndeterminate = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            string targetPath = dialog.FileName;
            await Task.Run(() => WriteTintaProject(targetPath));
            _currentProjectPath = targetPath;
            MarkActiveDocumentSaved();
            SetFooterStatus($"Proyecto guardado · {Path.GetFileName(targetPath)}", "#58A77D");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"No se pudo guardar el proyecto.\n\n{exception.Message}", "Tinta ES",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            BusyProgressBar.IsIndeterminate = false;
            FooterProgressBar.Visibility = Visibility.Collapsed;
            FooterProgressBar.IsIndeterminate = false;
            UpdateClassicMenuAvailability();
        }
    }

    private void NavigateFromMenu(int delta)
    {
        int target = _comicPageIndex + delta;
        if (target >= 0 && target < _comicPages.Count)
        {
            _ = ShowComicPageFastAsync(target);
        }
    }
}
