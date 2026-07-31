using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TintaES.Wpf;

/// <summary>
/// Informa de que una fase lleva más de dos minutos. Cerrar la ventana o pulsar
/// «Seguir esperando» conserva la tarea; solo «Cancelar tarea» la detiene.
/// </summary>
internal sealed class SlowOperationPromptWindow : Window
{
    private bool _choiceMade;

    public bool ContinueWaiting { get; private set; } = true;

    public SlowOperationPromptWindow(string operationName, string stage, TimeSpan elapsed)
    {
        Title = "La tarea sigue en curso · Tinta ES";
        Width = 500;
        Height = 255;
        MinWidth = 460;
        MinHeight = 235;
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
            Text = $"{operationName} sigue trabajando",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(heading);

        var explanation = new TextBlock
        {
            Text = $"Han pasado {FormatElapsed(elapsed)}. La tarea no se ha cancelado y puede necesitar más tiempo.",
            Margin = new Thickness(0, 14, 0, 0),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(205, 209, 214)),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(explanation, 1);
        root.Children.Add(explanation);

        var stageText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(stage)
                ? "Tinta ES está esperando a que termine la fase actual."
                : $"Fase actual: {stage}",
            Margin = new Thickness(0, 18, 0, 0),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 89, 75)),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(stageText, 2);
        root.Children.Add(stageText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };

        var cancelButton = new Button
        {
            Content = "Cancelar tarea",
            MinWidth = 125,
            Height = 34,
            Margin = new Thickness(0, 0, 10, 0)
        };
        cancelButton.Click += (_, _) => Finish(continueWaiting: false);

        var continueButton = new Button
        {
            Content = "Seguir esperando",
            MinWidth = 145,
            Height = 34,
            IsDefault = true
        };
        continueButton.Click += (_, _) => Finish(continueWaiting: true);

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(continueButton);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => continueButton.Focus();
        Closing += (_, _) =>
        {
            // El aspa nunca cancela una tarea por accidente.
            if (!_choiceMade)
            {
                ContinueWaiting = true;
            }
        };
    }

    private void Finish(bool continueWaiting)
    {
        _choiceMade = true;
        ContinueWaiting = continueWaiting;
        Close();
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours} h {elapsed.Minutes:00} min"
            : $"{Math.Max(2, (int)Math.Floor(elapsed.TotalMinutes))} minutos";
}
