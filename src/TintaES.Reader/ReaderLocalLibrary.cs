using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TintaES.Wpf;

/// <summary>
/// Biblioteca exclusiva del ejecutable independiente. Busca proyectos .tinta disponibles en
/// discos locales sin bloquear la interfaz y permite abrirlos directamente desde un panel lateral.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private static readonly bool StandaloneLibraryRegistered = RegisterStandaloneLibrary();

    private readonly ObservableCollection<ReaderLibraryItem> _libraryItems = [];
    private CancellationTokenSource? _libraryScanCancellation;
    private Border? _libraryPanel;
    private ListBox? _libraryList;
    private TextBlock? _libraryStatus;
    private Button? _libraryToggleButton;
    private bool _libraryInstalled;
    private bool _libraryVisible = true;

    private static bool RegisterStandaloneLibrary()
    {
        EventManager.RegisterClassHandler(
            typeof(ComicReaderWindow),
            LoadedEvent,
            new RoutedEventHandler(ComicReaderWindow_StandaloneLibraryLoaded),
            handledEventsToo: true);
        return true;
    }

    private static void ComicReaderWindow_StandaloneLibraryLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComicReaderWindow reader)
        {
            reader.Dispatcher.BeginInvoke(
                reader.InstallStandaloneLibrary,
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    private void InstallStandaloneLibrary()
    {
        if (_libraryInstalled || _readerRoot is null || _readerToolbar is null)
        {
            return;
        }

        _libraryInstalled = true;
        Grid root = _readerRoot;
        if (root.ColumnDefinitions.Count == 0)
        {
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(255) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        foreach (UIElement child in root.Children.Cast<UIElement>().ToArray())
        {
            Grid.SetColumn(child, 1);
        }

        _libraryPanel = BuildLibraryPanel();
        Grid.SetColumn(_libraryPanel, 0);
        Grid.SetRow(_libraryPanel, 0);
        Grid.SetRowSpan(_libraryPanel, Math.Max(1, root.RowDefinitions.Count));
        Panel.SetZIndex(_libraryPanel, 3000);
        root.Children.Add(_libraryPanel);

        if (_readerToolbar.Children.OfType<StackPanel>().FirstOrDefault() is { } toolbarItems)
        {
            _libraryToggleButton = CreateToolbarButton("Biblioteca", 88);
            _libraryToggleButton.Margin = new Thickness(6, 0, 0, 0);
            _libraryToggleButton.ToolTip = "Mostrar u ocultar los proyectos .tinta encontrados en el equipo";
            _libraryToggleButton.Click += (_, _) => ToggleLibraryPanel();
            toolbarItems.Children.Add(_libraryToggleButton);
        }

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
        var refresh = new Button
        {
            Content = "↻",
            Width = 30,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Volver a buscar proyectos .tinta"
        };
        DockPanel.SetDock(refresh, Dock.Right);
        refresh.Click += async (_, _) => await RefreshLocalLibraryAsync();
        header.Children.Add(refresh);
        header.Children.Add(new TextBlock
        {
            Text = "BIBLIOTECA",
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        panelRoot.Children.Add(header);

        _libraryStatus = new TextBlock
        {
            Text = "Buscando proyectos…",
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
            Text = "Doble clic o Intro para abrir",
            Foreground = new SolidColorBrush(Color.FromRgb(125, 133, 140)),
            FontSize = 10,
            Margin = new Thickness(12, 7, 10, 10)
        };
        Grid.SetRow(hint, 3);
        panelRoot.Children.Add(hint);

        return new Border
        {
            Child = panelRoot,
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
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ReaderLibraryItem.Name)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        root.AppendChild(name);

        var path = new FrameworkElementFactory(typeof(TextBlock));
        path.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ReaderLibraryItem.Directory)));
        path.SetValue(TextBlock.FontSizeProperty, 9.5d);
        path.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(143, 151, 158)));
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

        if (_libraryStatus is not null)
        {
            _libraryStatus.Text = "Buscando proyectos .tinta en los discos…";
        }

        try
        {
            ReaderLibraryItem[] found = await Task.Run(() => ScanLocalProjects(token), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _libraryItems.Clear();
            foreach (ReaderLibraryItem item in found)
            {
                _libraryItems.Add(item);
            }

            if (_libraryStatus is not null)
            {
                _libraryStatus.Text = found.Length == 0
                    ? "No se han encontrado proyectos .tinta."
                    : found.Length == 1
                        ? "1 proyecto encontrado"
                        : $"{found.Length} proyectos encontrados";
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

    private static ReaderLibraryItem[] ScanLocalProjects(CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, ReaderLibraryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            ScanDirectory(drive.RootDirectory.FullName, found, cancellationToken);
        }

        return found.Values
            .OrderByDescending(item => item.ModifiedUtc)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static void ScanDirectory(
        string root,
        IDictionary<string, ReaderLibraryItem> found,
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
                foreach (string file in Directory.EnumerateFiles(current, "*.tinta", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        found[info.FullName] = new ReaderLibraryItem(
                            Path.GetFileNameWithoutExtension(info.Name),
                            info.FullName,
                            info.DirectoryName ?? string.Empty,
                            info.LastWriteTimeUtc);
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
        return name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
               || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("$", StringComparison.Ordinal);
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
        if (_libraryPanel is null || _readerRoot is null || _readerRoot.ColumnDefinitions.Count < 2)
        {
            return;
        }

        _libraryVisible = !_libraryVisible;
        _libraryPanel.Visibility = _libraryVisible ? Visibility.Visible : Visibility.Collapsed;
        _readerRoot.ColumnDefinitions[0].Width = _libraryVisible
            ? new GridLength(255)
            : new GridLength(0);
        if (_libraryToggleButton is not null)
        {
            _libraryToggleButton.Content = _libraryVisible ? "Biblioteca" : "☰ Biblioteca";
        }
    }

    private sealed record ReaderLibraryItem(
        string Name,
        string FullPath,
        string Directory,
        DateTime ModifiedUtc);
}
