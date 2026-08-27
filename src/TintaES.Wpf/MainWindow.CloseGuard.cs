using System.ComponentModel;
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
