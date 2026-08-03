using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf;

public sealed class ComicResearchSetupWindow : Window
{
    private readonly TextBox _titleBox;
    private readonly PasswordBox _apiKeyBox;
    private readonly CheckBox _refreshCheckBox;

    public string ComicTitle => _titleBox.Text.Trim();
    public string ApiKey => _apiKeyBox.Password.Trim();
    public bool ForceRefresh => _refreshCheckBox.IsChecked == true;

    public ComicResearchSetupWindow(
        Window owner,
        string suggestedTitle,
        string? sessionApiKey,
        ComicResearchContext? existing)
    {
        Owner = owner;
        Title = "Contexto web del cómic";
        Width = 660;
        Height = existing is null ? 410 : 610;
        MinWidth = 540;
        MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(28, 31, 34));

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var intro = new TextBlock
        {
            Text = "TintaES hará dos búsquedas controladas sobre la obra y guardará una ficha local. " +
                   "La investigación solo se usará para nombres, relaciones, continuidad, terminología y registro; " +
                   "el texto visible del bocadillo siempre tendrá prioridad.",
            Foreground = new SolidColorBrush(Color.FromRgb(220, 222, 224)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        root.Children.Add(intro);

        var fields = new Grid();
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddLabel(fields, "Obra / número", 0);
        _titleBox = new TextBox
        {
            Text = suggestedTitle,
            Height = 32,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 0, 9)
        };
        Grid.SetColumn(_titleBox, 1);
        Grid.SetRow(_titleBox, 0);
        fields.Children.Add(_titleBox);

        AddLabel(fields, "Clave Tavily", 1);
        _apiKeyBox = new PasswordBox
        {
            Password = sessionApiKey ?? string.Empty,
            Height = 32,
            Padding = new Thickness(8, 5, 8, 5)
        };
        Grid.SetColumn(_apiKeyBox, 1);
        Grid.SetRow(_apiKeyBox, 1);
        fields.Children.Add(_apiKeyBox);

        Grid.SetRow(fields, 1);
        root.Children.Add(fields);

        _refreshCheckBox = new CheckBox
        {
            Content = existing is null
                ? "Volver a investigar aunque exista una ficha en caché"
                : "Reemplazar la ficha existente con una investigación nueva",
            Foreground = Brushes.White,
            Margin = new Thickness(120, 10, 0, 12),
            IsChecked = existing is null ? false : true
        };
        Grid.SetRow(_refreshCheckBox, 2);
        root.Children.Add(_refreshCheckBox);

        UIElement content;
        if (existing is null)
        {
            content = new TextBlock
            {
                Text = "La clave no se guarda en el proyecto ni se escribe en disco. También puedes definirla " +
                       "como variable de entorno TAVILY_API_KEY para no introducirla cada vez que abras TintaES.",
                Foreground = new SolidColorBrush(Color.FromRgb(160, 167, 173)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(120, 4, 0, 0)
            };
        }
        else
        {
            content = new TextBox
            {
                Text = existing.ToDisplayText(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10),
                Background = new SolidColorBrush(Color.FromRgb(20, 23, 25)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 222, 224)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 76, 81))
            };
        }
        Grid.SetRow(content, 3);
        root.Children.Add(content);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        Button cancel = CreateButton("Cancelar", 96);
        cancel.Click += (_, _) => DialogResult = false;
        Button research = CreateButton(existing is null ? "Investigar" : "Usar / investigar", 142);
        research.Margin = new Thickness(9, 0, 0, 0);
        research.IsDefault = true;
        research.Click += (_, _) =>
        {
            if (ComicTitle.Length < 2)
            {
                MessageBox.Show(this, "Escribe el título o la colección del cómic.", "Tinta ES");
                return;
            }
            if (existing is null || ForceRefresh)
            {
                string key = ApiKey.Length > 0
                    ? ApiKey
                    : Environment.GetEnvironmentVariable("TAVILY_API_KEY")?.Trim() ?? string.Empty;
                if (key.Length == 0)
                {
                    MessageBox.Show(
                        this,
                        "Introduce una clave de Tavily o define la variable TAVILY_API_KEY.",
                        "Tinta ES",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
            }
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(research);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        Loaded += (_, _) =>
        {
            _titleBox.Focus();
            _titleBox.SelectAll();
        };
    }

    private static void AddLabel(Grid grid, string text, int row)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 187, 192)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, row == 0 ? 9 : 0)
        };
        Grid.SetRow(label, row);
        grid.Children.Add(label);
    }

    private static Button CreateButton(string text, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 34,
        Padding = new Thickness(9, 3, 9, 3)
    };
}
