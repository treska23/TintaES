from pathlib import Path

project_path = Path('src/TintaES.Wpf/MainWindow.ProjectPersistence.cs')
text = project_path.read_text(encoding='utf-8')

old = '''    private async void SaveProjectButton_Click(object sender, RoutedEventArgs e)\n    {\n        if (_comicPages.Count == 0)\n        {\n            return;\n        }\n\n        PersistVisibleComicPageRegions();\n        string? targetPath = _currentProjectPath;\n        if (string.IsNullOrWhiteSpace(targetPath))\n        {\n            var dialog = new SaveFileDialog\n            {\n                Title = "Guardar proyecto de TintaES",\n                FileName = MakeSafeFileName(_comicTitle ?? "comic") + ".tinta",\n                DefaultExt = ".tinta",\n                Filter = "Proyecto TintaES|*.tinta"\n            };\n            if (dialog.ShowDialog(this) != true)\n            {\n                return;\n            }\n            targetPath = dialog.FileName;\n        }\n\n        BusyOverlay.Visibility = Visibility.Visible;\n        BusyTitleText.Text = "Guardando proyecto…";\n        BusyProgressBar.IsIndeterminate = true;\n        FooterProgressBar.Visibility = Visibility.Visible;\n        FooterProgressBar.IsIndeterminate = true;\n        UpdateProjectCommandAvailability();\n        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);\n\n        try\n        {\n            string finalPath = targetPath;\n            await Task.Run(() => WriteTintaProject(finalPath));\n            _currentProjectPath = finalPath;\n            MarkActiveDocumentSaved();\n            SetFooterStatus($"Proyecto guardado · {Path.GetFileName(finalPath)}", "#58A77D");\n        }\n        catch (Exception exception)\n        {\n            MessageBox.Show(this, $"No se pudo guardar el proyecto.\\n\\n{exception.Message}", "Tinta ES",\n                MessageBoxButton.OK, MessageBoxImage.Error);\n            SetFooterStatus("No se pudo guardar el proyecto.", "#EE594B");\n        }\n        finally\n        {\n            BusyOverlay.Visibility = Visibility.Collapsed;\n            BusyProgressBar.IsIndeterminate = false;\n            FooterProgressBar.Visibility = Visibility.Collapsed;\n            FooterProgressBar.IsIndeterminate = false;\n            UpdateProjectCommandAvailability();\n        }\n    }\n'''

new = '''    private async void SaveProjectButton_Click(object sender, RoutedEventArgs e)\n    {\n        await SaveActiveProjectAsync();\n    }\n\n    private async Task<bool> SaveActiveProjectAsync()\n    {\n        if (_comicPages.Count == 0)\n        {\n            return true;\n        }\n\n        PersistVisibleComicPageRegions();\n        string? targetPath = _currentProjectPath;\n        if (string.IsNullOrWhiteSpace(targetPath))\n        {\n            var dialog = new SaveFileDialog\n            {\n                Title = "Guardar proyecto de TintaES",\n                FileName = MakeSafeFileName(_comicTitle ?? "comic") + ".tinta",\n                DefaultExt = ".tinta",\n                Filter = "Proyecto TintaES|*.tinta"\n            };\n            if (dialog.ShowDialog(this) != true)\n            {\n                return false;\n            }\n            targetPath = dialog.FileName;\n        }\n\n        BusyOverlay.Visibility = Visibility.Visible;\n        BusyTitleText.Text = "Guardando proyecto…";\n        BusyProgressBar.IsIndeterminate = true;\n        FooterProgressBar.Visibility = Visibility.Visible;\n        FooterProgressBar.IsIndeterminate = true;\n        UpdateProjectCommandAvailability();\n        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);\n\n        try\n        {\n            string finalPath = targetPath;\n            await Task.Run(() => WriteTintaProject(finalPath));\n            _currentProjectPath = finalPath;\n            MarkActiveDocumentSaved();\n            SetFooterStatus($"Proyecto guardado · {Path.GetFileName(finalPath)}", "#58A77D");\n            return true;\n        }\n        catch (Exception exception)\n        {\n            MessageBox.Show(this, $"No se pudo guardar el proyecto.\\n\\n{exception.Message}", "Tinta ES",\n                MessageBoxButton.OK, MessageBoxImage.Error);\n            SetFooterStatus("No se pudo guardar el proyecto.", "#EE594B");\n            return false;\n        }\n        finally\n        {\n            BusyOverlay.Visibility = Visibility.Collapsed;\n            BusyProgressBar.IsIndeterminate = false;\n            FooterProgressBar.Visibility = Visibility.Collapsed;\n            FooterProgressBar.IsIndeterminate = false;\n            UpdateProjectCommandAvailability();\n        }\n    }\n'''

if text.count(old) != 1:
    raise SystemExit('No se encontró exactamente una versión esperada de SaveProjectButton_Click.')
if 'SaveActiveProjectAsync' in text:
    raise SystemExit('SaveActiveProjectAsync ya existe; se aborta para no duplicar lógica.')
project_path.write_text(text.replace(old, new), encoding='utf-8')

close_guard = Path('src/TintaES.Wpf/MainWindow.CloseGuard.cs')
if close_guard.exists():
    raise SystemExit('MainWindow.CloseGuard.cs ya existe; se aborta para no sobrescribirlo.')

close_guard.write_text(r'''using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Protege el cierre de la aplicación cuando hay trabajo sin guardar.
/// El cierre se cancela mientras se resuelve la decisión del usuario y solo se reintenta
/// después de guardar correctamente o de confirmar expresamente el descarte.
/// </summary>
public partial class MainWindow
{
    private static readonly bool CloseGuardRegistered = RegisterCloseGuard();

    private bool _closeGuardInstalled;
    private bool _closeRequestInProgress;
    private bool _allowWindowClose;

    private static bool RegisterCloseGuard()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_CloseGuardLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void MainWindow_CloseGuardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.InstallCloseGuard();
        }
    }

    private void InstallCloseGuard()
    {
        if (_closeGuardInstalled)
        {
            return;
        }

        _closeGuardInstalled = true;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowWindowClose)
        {
            return;
        }

        CaptureActiveDocumentState();
        List<ComicDocumentSession> dirtyDocuments = _documentSessions
            .Where(session => session.DirtyPages.Count > 0)
            .ToList();
        if (dirtyDocuments.Count == 0)
        {
            return;
        }

        e.Cancel = true;
        if (_closeRequestInProgress)
        {
            return;
        }

        _closeRequestInProgress = true;
        _ = ResolveWindowCloseAsync(dirtyDocuments);
    }

    private async Task ResolveWindowCloseAsync(IReadOnlyList<ComicDocumentSession> dirtyDocuments)
    {
        try
        {
            CloseUnsavedChoice choice = ShowUnsavedChangesDialog(dirtyDocuments);
            if (choice == CloseUnsavedChoice.Cancel)
            {
                return;
            }

            if (choice == CloseUnsavedChoice.Discard)
            {
                _allowWindowClose = true;
                Close();
                return;
            }

            ComicDocumentSession? originalDocument = _activeDocumentSession;
            foreach (ComicDocumentSession document in dirtyDocuments)
            {
                if (!_documentSessions.Contains(document) || document.DirtyPages.Count == 0)
                {
                    continue;
                }

                if (!ReferenceEquals(document, _activeDocumentSession))
                {
                    await SwitchDocumentAsync(document);
                }

                if (!await SaveActiveProjectAsync())
                {
                    await RestoreDocumentAfterCancelledCloseAsync(originalDocument);
                    return;
                }
            }

            _allowWindowClose = true;
            Close();
        }
        finally
        {
            if (!_allowWindowClose)
            {
                _closeRequestInProgress = false;
            }
        }
    }

    private async Task RestoreDocumentAfterCancelledCloseAsync(ComicDocumentSession? originalDocument)
    {
        if (originalDocument is not null
            && _documentSessions.Contains(originalDocument)
            && !ReferenceEquals(originalDocument, _activeDocumentSession))
        {
            await SwitchDocumentAsync(originalDocument);
        }
    }

    private CloseUnsavedChoice ShowUnsavedChangesDialog(IReadOnlyList<ComicDocumentSession> dirtyDocuments)
    {
        bool multiple = dirtyDocuments.Count > 1;
        CloseUnsavedChoice choice = CloseUnsavedChoice.Cancel;

        var dialog = new Window
        {
            Title = "Cambios sin guardar",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Background = BrushFrom("#17191B"),
            Foreground = BrushFrom("#F2EEE5")
        };

        var content = new Border
        {
            Width = 500,
            Padding = new Thickness(24),
            Background = BrushFrom("#17191B"),
            BorderBrush = BrushFrom("#383E43"),
            BorderThickness = new Thickness(1)
        };
        var stack = new StackPanel();
        content.Child = stack;

        stack.Children.Add(new TextBlock
        {
            Text = multiple
                ? $"Hay {dirtyDocuments.Count} documentos con cambios sin guardar."
                : $"«{dirtyDocuments[0].Title}» tiene cambios sin guardar.",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = "¿Qué quieres hacer antes de cerrar TintaES?",
            Margin = new Thickness(0, 9, 0, 22),
            Foreground = BrushFrom("#B7BEC4"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var saveButton = new Button
        {
            Content = multiple ? "Guardar todo" : "Guardar",
            MinWidth = 110,
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
            Style = TryFindResource("AccentButton") as Style
        };
        var discardButton = new Button
        {
            Content = multiple ? "Descartar todo" : "Descartar",
            MinWidth = 110,
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            Style = TryFindResource("ToolbarButton") as Style
        };
        var cancelButton = new Button
        {
            Content = "Cancelar",
            MinWidth = 100,
            Height = 34,
            IsCancel = true,
            Style = TryFindResource("ToolbarButton") as Style
        };

        saveButton.Click += (_, _) =>
        {
            choice = CloseUnsavedChoice.Save;
            dialog.DialogResult = true;
        };
        discardButton.Click += (_, _) =>
        {
            choice = CloseUnsavedChoice.Discard;
            dialog.DialogResult = true;
        };

        buttons.Children.Add(saveButton);
        buttons.Children.Add(discardButton);
        buttons.Children.Add(cancelButton);
        stack.Children.Add(buttons);
        dialog.Content = content;
        dialog.ShowDialog();
        return choice;
    }

    private enum CloseUnsavedChoice
    {
        Save,
        Discard,
        Cancel
    }
}
''', encoding='utf-8')
