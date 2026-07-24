using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Pregunta si debe seguir esperando una página lenta. Si el usuario no responde en treinta
/// segundos, devuelve false para que la exportación salte automáticamente a la siguiente página.
/// </summary>
internal sealed class CbzPageWaitPromptWindow : Window
{
    private const int ResponseTimeoutSeconds = 30;

    private readonly DispatcherTimer _countdownTimer;
    private readonly TextBlock _countdownText;
    private int _secondsRemaining = ResponseTimeoutSeconds;

    public CbzPageWaitPromptWindow(int pageNumber)
    {
        Title = "Página lenta · Tinta ES";
        Width = 470;
        Height = 235;
        MinWidth = 440;
        MinHeight = 220;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(24, 27, 30));
        Foreground = Brushes.White;

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
            Text = "Ya han pasado 2 minutos. ¿Quieres esperar otros 2 minutos o saltar esta página?",
            Margin = new Thickness(0, 14, 0, 0),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(205, 209, 214)),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(explanation, 1);
        root.Children.Add(explanation);

        _countdownText = new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 0),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 89, 75)),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_countdownText, 2);
        root.Children.Add(_countdownText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };

        var skipButton = new Button
        {
            Content = "Saltar página",
            MinWidth = 125,
            Height = 34,
            Margin = new Thickness(0, 0, 10, 0),
            IsCancel = true
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
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;

        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;

        Loaded += (_, _) =>
        {
            UpdateCountdownText();
            _countdownTimer.Start();
            continueButton.Focus();
        };
        Closed += (_, _) => _countdownTimer.Stop();
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        if (_secondsRemaining <= 0)
        {
            Finish(continueWaiting: false);
            return;
        }

        UpdateCountdownText();
    }

    private void UpdateCountdownText()
    {
        _countdownText.Text =
            $"Si no respondes en {_secondsRemaining} segundos, Tinta ES saltará esta página y la dejará marcada como pendiente.";
    }

    private void Finish(bool continueWaiting)
    {
        _countdownTimer.Stop();
        DialogResult = continueWaiting;
        Close();
    }
}
