using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TintaES.Wpf;

/// <summary>
/// Pregunta si debe seguir esperando una página lenta. No toma ninguna decisión por tiempo:
/// la ventana exige elegir expresamente entre continuar o cancelar esta página.
/// </summary>
internal sealed class CbzPageWaitPromptWindow : Window
{
    private bool _choiceMade;

    public bool ContinueWaiting { get; private set; } = true;

    public CbzPageWaitPromptWindow(int pageNumber)
    {
        Title = "Página lenta · Tinta ES";
        Width = 470;
        Height = 225;
        MinWidth = 440;
        MinHeight = 210;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(24, 27, 30));
        Foreground = Brushes.White;

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = $"La página {pageNumber} sigue procesándose",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(heading);

        var explanation = new TextBlock
        {
            Text = "Ya han pasado 2 minutos. La página continúa trabajando. ¿Quieres seguir esperando o cancelar únicamente esta página?",
            Margin = new Thickness(0, 14, 0, 0),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(205, 209, 214)),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(explanation, 1);
        root.Children.Add(explanation);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };

        var skipButton = new Button
        {
            Content = "Cancelar esta página",
            MinWidth = 145,
            Height = 34,
            Margin = new Thickness(0, 0, 10, 0)
        };
        skipButton.Click += (_, _) => Finish(continueWaiting: false);

        var continueButton = new Button
        {
            Content = "Seguir esperando",
            MinWidth = 145,
            Height = 34,
            IsDefault = true
        };
        continueButton.Click += (_, _) => Finish(continueWaiting: true);

        buttons.Children.Add(skipButton);
        buttons.Children.Add(continueButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => continueButton.Focus();
        Closing += (_, e) =>
        {
            if (!_choiceMade)
            {
                e.Cancel = true;
            }
        };
    }

    private void Finish(bool continueWaiting)
    {
        _choiceMade = true;
        ContinueWaiting = continueWaiting;
        DialogResult = continueWaiting;
    }
}
