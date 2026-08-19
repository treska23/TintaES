using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TintaES.Wpf;

/// <summary>
/// Biblioteca exclusiva del ejecutable independiente. Busca proyectos .tinta en discos fijos y
/// extraíbles sin bloquear la interfaz, pero no ocupa una columna permanente: se muestra solo
/// como overlay cuando el usuario la pide.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private readonly ObservableCollection<ReaderLibraryItem> _libraryItems = [];
    private readonly Dictionary<string, ReaderLibraryItem> _libraryItemsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _libraryScanCancellation;
    private Border? _libraryPanel;
    private ListBox? _libraryList;
    private TextBlock? _libraryStatus;
    private Button? _libraryToggleButton;
    private bool _libraryInstalled;
    private bool _libraryVisible;
    private int _libraryFoundCount;

    internal void EnsureStandaloneLibraryInstalled()
    {
        InstallStandaloneLibrary();
        if (!_libraryInstalled || _libraryPanel is null || _libraryList is null)
        {
            throw new InvalidOperationException(
                "No se pudo crear la biblioteca local de proyectos .tinta.");
        }
    }

    private void InstallStandaloneLibrary()
    {
        if (_libraryInstalled)
        {
            return;
        }

        if (_readerRoot is null || _readerToolbar is null)
        {
            throw new InvalidOperationException(
                "El visor todavía no ha creado la superficie necesaria para la biblioteca.");
        }

        Grid root = _readerRoot;
        _libraryPanel = BuildLibraryPanel();
        _libraryPanel.Width = 320;
        _libraryPanel.MaxWidth = 420;
        _libraryPanel.HorizontalAlignment = HorizontalAlignment.Left;
        _libraryPanel.VerticalAlignment = VerticalAlignment.Stretch;
        _libraryPanel.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_libraryPanel, 0);
        Grid.SetRow(_libraryPanel, 0);
        Grid.SetRowSpan(_libraryPanel, Math.Max(1, root.RowDefinitions.Count));
        Panel.SetZIndex(_libraryPanel, 4000);
        root.Children.Add(_libraryPanel);

        if (_readerToolbar.Children.OfType<StackPanel>().FirstOrDefault() is { } toolbarItems)
        {
            _libraryToggleButton = CreateToolbarButton("Biblioteca", 92);
            _libraryToggleButton.Margin = new Thickness(6, 0, 0, 0);
            _libraryToggleButton.ToolTip =
                "Mostrar u ocultar los proyectos .tinta encontrados en los discos del equipo";
            _libraryToggleButton.Click += (_, _) => ToggleLibraryPanel();
            toolbarItems.Children.Add(_libraryToggleButton);
        }

        _libraryInstalled = true;
        _libraryVisible = false;
        Closed += (_, _) =>
        {
            _libraryScanCancellation?.Cancel();
            _libraryScanCancellation?.Dispose();
            _libraryScanCancellation = null;
        };

        _ = RefreshLocalLibraryAsync();
    }

    private Border BuildLibraryPanel()
    {
        var panelRoot = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 23, 26))
        };
        panelRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panelRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panelRoot.RowDefinitions.Add(new RowDefinition());
        panelRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(12, 12, 10, 8) };
        var close = new Button
        {
            Content = "×",
            Width = 30,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Cerrar biblioteca"
        };
        DockPanel.SetDock(close, Dock.Right);
        close.Click += (_, _) => ToggleLibraryPanel();
        header.Children.Add(close);

        var refresh = new Button
        {
            Content = "↻",
            Width = 30,
            Height = 28,
            Margin = new Thickness(0, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Volver a buscar proyectos .tinta en los discos"
        };
        DockPanel.SetDock(refresh, Dock.Right);
        refresh.Click += async (_, _) => await RefreshLocalLibraryAsync();
        header.Children.Add(refresh);
        header.Children.Add(new TextBlock
        {
            Text = "BIBLIOTECA .TINTA",
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        panelRoot.Children.Add(header);

        _libraryStatus = new TextBlock
        {
            Text = "Buscando proyectos .tinta…",
            Foreground = new SolidColorBrush(Color.FromRgb(158, 166, 173)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 10, 9)
        };
        Grid.SetRow(_libraryStatus, 1);
        panelRoot.Children.Add(_libraryStatus);

        _libraryList = new ListBox
        {
            ItemsSource = _libraryItems,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            Margin = new Thickness(5, 0, 5, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        _libraryList.MouseDoubleClick += LibraryList_MouseDoubleClick;
        _libraryList.KeyDown += LibraryList_KeyDown;
        _libraryList.ItemTemplate = BuildLibraryItemTemplate();
        Grid.SetRow(_libraryList, 2);
        panelRoot.Children.Add(_libraryList);

        var hint = new TextBlock
        {
            Text = "Busca automáticamente en los discos · doble clic o Intro para abrir",
            Foreground = new SolidColorBrush(Color.FromRgb(125, 133, 140)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 7, 10, 10)
        };
        Grid.SetRow(hint, 3);
        panelRoot.Children.Add(hint);

        return new Border
        {
            Child = panelRoot,
            Background = new SolidColorBrush(Color.FromRgb(20, 23, 26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 53, 58)),
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
    }

    private static DataTemplate BuildLibraryItemTemplate()
    {
        var template = new DataTemplate(typeof(ReaderLibraryItem));
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 6, 7, 7));

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(ReaderLibraryItem.Name)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        root.AppendChild(name);

        var path = new FrameworkElementFactory(typeof(TextBlock));
        path.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(ReaderLibraryItem.Directory)));
        path.SetValue(TextBlock.FontSizeProperty, 9.5d);
        path.SetValue(TextBlock.ForegroundProperty,
            new SolidColorBrush(Color.FromRgb(143, 151, 158)));
        path.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        path.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        root.AppendChild(path);

        template.VisualTree = root;
        return template;
    }

    private async Task RefreshLocalLibraryAsync()
    {
        _libraryScanCancellation?.Cancel();
        _libraryScanCancellation?.Dispose();
        _libraryScanCancellation = new CancellationTokenSource();
        CancellationToken token = _libraryScanCancellation.Token;

        _libraryItems.Clear();
        _libraryItemsByPath.Clear();
        _libraryFoundCount = 0;
        if (_libraryStatus is not null)
        {
            _libraryStatus.Text = "Buscando proyectos .tinta en los discos…";
        }

        try
        {
            await Task.Run(
                () => ScanLocalProjects(token, item =>
                    Dispatcher.BeginInvoke(() => PublishLibraryItem(item))),
                token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (_libraryStatus is not null)
            {
                _libraryStatus.Text = _libraryFoundCount switch
                {
                    0 => "Búsqueda terminada · no se encontraron proyectos .tinta.",
                    1 => "Búsqueda terminada · 1 proyecto .tinta encontrado.",
                    _ => $"Búsqueda terminada · {_libraryFoundCount} proyectos .tinta encontrados."
                };
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (_libraryStatus is not null)
            {
                _libraryStatus.Text = "No se pudo completar la búsqueda: " + exception.Message;
            }
        }
    }

    private void PublishLibraryItem(ReaderLibraryItem item)
    {
        if (_libraryScanCancellation?.IsCancellationRequested != false
            || _libraryItemsByPath.ContainsKey(item.FullPath))
        {
            return;
        }

        _libraryItemsByPath[item.FullPath] = item;
        _libraryItems.Add(item);
        _libraryFoundCount++;
        if (_libraryStatus is not null)
        {
            _libraryStatus.Text = _libraryFoundCount == 1
                ? "Buscando… · 1 proyecto encontrado hasta ahora"
                : $"Buscando… · {_libraryFoundCount} proyectos encontrados hasta ahora";
        }
    }

    private static void ScanLocalProjects(
        CancellationToken cancellationToken,
        Action<ReaderLibraryItem> onFound)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            ScanDirectory(drive.RootDirectory.FullName, emitted, onFound, cancellationToken);
        }
    }

    private static void ScanDirectory(
        string root,
        ISet<string> emitted,
        Action<ReaderLibraryItem> onFound,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = pending.Pop();

            try
            {
                foreach (string file in Directory.EnumerateFiles(
                             current,
                             "*.tinta",
                             SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        if (!emitted.Add(info.FullName))
                        {
                            continue;
                        }

                        onFound(new ReaderLibraryItem(
                            Path.GetFileNameWithoutExtension(info.Name),
                            info.FullName,
                            info.DirectoryName ?? string.Empty,
                            info.LastWriteTimeUtc));
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                foreach (string directory in Directory.EnumerateDirectories(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ShouldSkipDirectory(directory))
                    {
                        pending.Push(directory);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool ShouldSkipDirectory(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("$", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private async void LibraryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_libraryList?.SelectedItem is ReaderLibraryItem item)
        {
            await OpenReaderPathAsync(item.FullPath);
        }
    }

    private async void LibraryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _libraryList?.SelectedItem is ReaderLibraryItem item)
        {
            e.Handled = true;
            await OpenReaderPathAsync(item.FullPath);
        }
    }

    private void ToggleLibraryPanel()
    {
        if (_libraryPanel is null)
        {
            return;
        }

        _libraryVisible = !_libraryVisible;
        _libraryPanel.Visibility = _libraryVisible ? Visibility.Visible : Visibility.Collapsed;
        Panel.SetZIndex(_libraryPanel, 4000);
        if (_libraryToggleButton is not null)
        {
            _libraryToggleButton.Content = _libraryVisible ? "Cerrar biblioteca" : "Biblioteca";
        }
    }

    private sealed record ReaderLibraryItem(
        string Name,
        string FullPath,
        string Directory,
        DateTime ModifiedUtc);
}
