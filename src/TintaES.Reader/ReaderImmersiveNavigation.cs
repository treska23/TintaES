using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Navegación inmersiva exclusiva del Reader. No reserva espacio fijo: la barra de páginas
/// aparece solo al buscarla en el borde inferior con ratón o al tocar esa franja con el dedo.
/// </summary>
public sealed partial class ComicReaderWindow
{
    private Border? _standaloneBottomNavigation;
    private Slider? _standalonePageSlider;
    private TextBlock? _standalonePageLabel;
    private Button? _standaloneFullscreenToggle;
    private DispatcherTimer? _standaloneNavigationHideTimer;
    private DispatcherTimer? _standalonePageScrubTimer;
    private bool _syncingStandalonePageSlider;
    private DateTime _standaloneSuppressPromotedClickUntilUtc;

    internal void EnsureStandaloneImmersiveNavigationInstalled()
    {
        if (_standaloneBottomNavigation is not null)
        {
            return;
        }

        _standaloneNavigationHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(850)
        };
        _standaloneNavigationHideTimer.Tick += (_, _) => HideStandaloneBottomNavigation();

        _standalonePageScrubTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _standalonePageScrubTimer.Tick += (_, _) =>
        {
            _standalonePageScrubTimer.Stop();
            ApplyStandalonePageScrubTarget();
        };

        _standaloneBottomNavigation = BuildStandaloneBottomNavigation();
        Panel.SetZIndex(_standaloneBottomNavigation, 4500);
        _viewerHost.Children.Add(_standaloneBottomNavigation);

        _viewerHost.PreviewMouseMove += StandaloneViewer_PreviewMouseMove;
        _viewerHost.MouseLeave += (_, _) => ScheduleStandaloneBottomNavigationHide(450);

        Closed += (_, _) =>
        {
            _standaloneNavigationHideTimer?.Stop();
            _standalonePageScrubTimer?.Stop();
        };
    }

    /// <summary>
    /// Se llama antes de intentar mostrar una traducción táctil. Devuelve true cuando el toque
    /// pertenece a la navegación del Reader y por tanto no debe interpretarse como bocadillo.
    /// </summary>
    private bool TryHandleStandaloneImmersiveNavigation(Point viewerPoint)
    {
        if (_pageIndex < 0 || PageCount <= 0 || _viewerHost.ActualWidth <= 1 || _viewerHost.ActualHeight <= 1)
        {
            return false;
        }

        const double bottomActivationHeight = 92;
        if (viewerPoint.Y >= _viewerHost.ActualHeight - bottomActivationHeight)
        {
            HideTranslationCard();
            ShowStandaloneBottomNavigation(fromTouch: true);
            return true;
        }

        const double edgeActivationWidth = 82;
        if (viewerPoint.X <= edgeActivationWidth && _leftEdgeNavigationAvailable)
        {
            HideTranslationCard();
            NavigateStandaloneEdge(rightArrow: false);
            return true;
        }

        if (viewerPoint.X >= _viewerHost.ActualWidth - edgeActivationWidth && _rightEdgeNavigationAvailable)
        {
            HideTranslationCard();
            NavigateStandaloneEdge(rightArrow: true);
            return true;
        }

        return false;
    }

    private Border BuildStandaloneBottomNavigation()
    {
        var grid = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _standaloneFullscreenToggle = CreateStandaloneNavigationButton("Salir", 64);
        _standaloneFullscreenToggle.ToolTip = "Salir de pantalla completa";
        BindStandaloneNavigationButton(_standaloneFullscreenToggle, () =>
        {
            if (_isFullscreen)
            {
                ToggleFullscreen();
            }
            else
            {
                ToggleFullscreen();
            }
            HideStandaloneBottomNavigation();
        });
        Grid.SetColumn(_standaloneFullscreenToggle, 0);
        grid.Children.Add(_standaloneFullscreenToggle);

        Button previous = CreateStandaloneNavigationButton("‹", 46);
        previous.FontSize = 28;
        previous.Margin = new Thickness(8, 0, 8, 0);
        BindStandaloneNavigationButton(previous, () =>
        {
            NavigateByArrow(rightArrow: false);
            SyncStandaloneBottomNavigation();
        });
        Grid.SetColumn(previous, 1);
        grid.Children.Add(previous);

        _standalonePageSlider = new Slider
        {
            Minimum = 1,
            Maximum = 1,
            Value = 1,
            Height = 38,
            MinWidth = 80,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Arrastra para saltar rápidamente por las páginas"
        };
        _standalonePageSlider.ValueChanged += StandalonePageSlider_ValueChanged;
        InstallExplicitTouchSliderHandling(_standalonePageSlider);
        Grid.SetColumn(_standalonePageSlider, 2);
        grid.Children.Add(_standalonePageSlider);

        _standalonePageLabel = new TextBlock
        {
            Text = "– / –",
            MinWidth = 76,
            Margin = new Thickness(10, 0, 8, 0),
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_standalonePageLabel, 3);
        grid.Children.Add(_standalonePageLabel);

        Button next = CreateStandaloneNavigationButton("›", 46);
        next.FontSize = 28;
        BindStandaloneNavigationButton(next, () =>
        {
            NavigateByArrow(rightArrow: true);
            SyncStandaloneBottomNavigation();
        });
        Grid.SetColumn(next, 4);
        grid.Children.Add(next);

        var border = new Border
        {
            Child = grid,
            Background = new SolidColorBrush(Color.FromArgb(220, 12, 14, 16)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            MaxWidth = 920,
            Margin = new Thickness(12, 0, 12, 12),
            Visibility = Visibility.Collapsed
        };
        border.MouseEnter += (_, _) => _standaloneNavigationHideTimer?.Stop();
        border.MouseLeave += (_, _) => ScheduleStandaloneBottomNavigationHide(550);
        border.PreviewTouchDown += (_, _) => KeepStandaloneBottomNavigationAlive();
        border.PreviewStylusDown += (_, e) =>
        {
            if (IsTouchStylus(e.StylusDevice))
            {
                KeepStandaloneBottomNavigationAlive();
            }
        };
        return border;
    }

    private static Button CreateStandaloneNavigationButton(string text, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 42,
        Padding = new Thickness(8, 2, 8, 2),
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromRgb(43, 48, 52)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(86, 93, 99)),
        BorderThickness = new Thickness(1),
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private void BindStandaloneNavigationButton(Button button, Action action)
    {
        button.Click += (_, _) =>
        {
            if (DateTime.UtcNow < _standaloneSuppressPromotedClickUntilUtc)
            {
                return;
            }
            action();
            KeepStandaloneBottomNavigationAlive();
        };

        button.AddHandler(
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>((_, e) =>
            {
                _standaloneSuppressPromotedClickUntilUtc = DateTime.UtcNow.AddMilliseconds(550);
                action();
                KeepStandaloneBottomNavigationAlive();
                e.Handled = true;
            }),
            handledEventsToo: true);

        button.AddHandler(
            UIElement.PreviewStylusDownEvent,
            new StylusDownEventHandler((_, e) =>
            {
                if (!IsTouchStylus(e.StylusDevice))
                {
                    return;
                }
                _standaloneSuppressPromotedClickUntilUtc = DateTime.UtcNow.AddMilliseconds(550);
                action();
                KeepStandaloneBottomNavigationAlive();
                e.Handled = true;
            }),
            handledEventsToo: true);
    }

    private void InstallExplicitTouchSliderHandling(Slider slider)
    {
        slider.AddHandler(
            UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>((_, e) =>
            {
                SetStandaloneSliderFromPoint(e.GetTouchPoint(slider).Position.X);
                e.TouchDevice.Capture(slider);
                KeepStandaloneBottomNavigationAlive();
                e.Handled = true;
            }),
            handledEventsToo: true);
        slider.AddHandler(
            UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>((_, e) =>
            {
                if (e.TouchDevice.Captured != slider)
                {
                    return;
                }
                SetStandaloneSliderFromPoint(e.GetTouchPoint(slider).Position.X);
                KeepStandaloneBottomNavigationAlive();
                e.Handled = true;
            }),
            handledEventsToo: true);
        slider.AddHandler(
            UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>((_, e) =>
            {
                if (e.TouchDevice.Captured == slider)
                {
                    e.TouchDevice.Capture(null);
                }
                ApplyStandalonePageScrubTarget();
                KeepStandaloneBottomNavigationAlive();
                e.Handled = true;
            }),
            handledEventsToo: true);

        slider.AddHandler(
            UIElement.PreviewStylusDownEvent,
            new StylusDownEventHandler((_, e) =>
            {
                if (!IsTouchStylus(e.StylusDevice))
                {
                    return;
                }
                SetStandaloneSliderFromPoint(e.GetPosition(slider).X);
                Stylus.Capture(slider, CaptureMode.Element);
                KeepStandaloneBottomNavigationAlive();
                e.Handled = true;
            }),
            handledEventsToo: true);
        slider.AddHandler(
            UIElement.PreviewStylusMoveEvent,
            new StylusEventHandler((_, e) =>
            {
                if (!IsTouchStylus(e.StylusDevice) || Stylus.Captured != slider)
                {
                    return;
                }
                SetStandaloneSliderFromPoint(e.GetPosition(slider).X);
                KeepStandaloneBottomNavigationAlive();
                e.Handled = true;
            }),
            handledEventsToo: true);
        slider.AddHandler(
            UIElement.PreviewStylusUpEvent,
            new StylusEventHandler((_, e) =>
            {
                if (!IsTouchStylus(e.StylusDevice))
                {
                    return;
                }
                if (Stylus.Captured == slider)
                {
                    Stylus.Capture(null);
                }
                ApplyStandalonePageScrubTarget();
                KeepStandaloneBottomNavigationAlive();
                e.Handled = true;
            }),
            handledEventsToo: true);
    }

    private void StandaloneViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice is not null || _pageIndex < 0)
        {
            return;
        }

        Point point = e.GetPosition(_viewerHost);
        bool nearBottom = point.Y >= _viewerHost.ActualHeight - 78;
        if (nearBottom || _standaloneBottomNavigation?.IsMouseOver == true)
        {
            ShowStandaloneBottomNavigation(fromTouch: false);
        }
        else if (_standaloneBottomNavigation?.Visibility == Visibility.Visible)
        {
            ScheduleStandaloneBottomNavigationHide(360);
        }
    }

    private void ShowStandaloneBottomNavigation(bool fromTouch)
    {
        if (_standaloneBottomNavigation is null || _pageIndex < 0 || PageCount <= 0)
        {
            return;
        }

        SyncStandaloneBottomNavigation();
        _standaloneBottomNavigation.Visibility = Visibility.Visible;
        Panel.SetZIndex(_standaloneBottomNavigation, 4500);
        _standaloneNavigationHideTimer?.Stop();
        if (fromTouch)
        {
            ScheduleStandaloneBottomNavigationHide(3600);
        }
    }

    private void HideStandaloneBottomNavigation()
    {
        _standaloneNavigationHideTimer?.Stop();
        if (_standaloneBottomNavigation is not null
            && _standaloneBottomNavigation.IsMouseOver == false)
        {
            _standaloneBottomNavigation.Visibility = Visibility.Collapsed;
        }
    }

    private void ScheduleStandaloneBottomNavigationHide(int milliseconds)
    {
        if (_standaloneNavigationHideTimer is null
            || _standaloneBottomNavigation?.Visibility != Visibility.Visible)
        {
            return;
        }

        _standaloneNavigationHideTimer.Stop();
        _standaloneNavigationHideTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _standaloneNavigationHideTimer.Start();
    }

    private void KeepStandaloneBottomNavigationAlive()
    {
        _standaloneNavigationHideTimer?.Stop();
        ScheduleStandaloneBottomNavigationHide(3600);
    }

    private void SyncStandaloneBottomNavigation()
    {
        if (_standalonePageSlider is null || _standalonePageLabel is null)
        {
            return;
        }

        _syncingStandalonePageSlider = true;
        try
        {
            int count = Math.Max(1, PageCount);
            int current = Math.Clamp(_pageIndex + 1, 1, count);
            _standalonePageSlider.Minimum = 1;
            _standalonePageSlider.Maximum = count;
            _standalonePageSlider.Value = current;
            _standalonePageLabel.Text = $"{current} / {count}";
            if (_standaloneFullscreenToggle is not null)
            {
                _standaloneFullscreenToggle.Content = _isFullscreen ? "Salir" : "⛶";
                _standaloneFullscreenToggle.ToolTip = _isFullscreen
                    ? "Salir de pantalla completa"
                    : "Entrar en pantalla completa";
            }
        }
        finally
        {
            _syncingStandalonePageSlider = false;
        }
    }

    private void StandalonePageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingStandalonePageSlider || _standalonePageLabel is null)
        {
            return;
        }

        int target = Math.Clamp((int)Math.Round(e.NewValue), 1, Math.Max(1, PageCount));
        _standalonePageLabel.Text = $"{target} / {Math.Max(1, PageCount)}";
        _standalonePageScrubTimer?.Stop();
        _standalonePageScrubTimer?.Start();
    }

    private void SetStandaloneSliderFromPoint(double x)
    {
        if (_standalonePageSlider is null || _standalonePageSlider.ActualWidth <= 1)
        {
            return;
        }

        double ratio = Math.Clamp(x / _standalonePageSlider.ActualWidth, 0, 1);
        _standalonePageSlider.Value = _standalonePageSlider.Minimum
            + ratio * (_standalonePageSlider.Maximum - _standalonePageSlider.Minimum);
    }

    private void ApplyStandalonePageScrubTarget()
    {
        _standalonePageScrubTimer?.Stop();
        if (_standalonePageSlider is null || PageCount <= 0)
        {
            return;
        }

        int target = Math.Clamp((int)Math.Round(_standalonePageSlider.Value) - 1, 0, PageCount - 1);
        if (target != _pageIndex)
        {
            _ = ShowPageAsync(target);
        }
    }

    private void NavigateStandaloneEdge(bool rightArrow)
    {
        NavigateByArrow(rightArrow);
        FlashStandaloneEdgeButton(rightArrow ? _edgeNextButton : _edgePreviousButton);
    }

    private async void FlashStandaloneEdgeButton(Button? button)
    {
        if (button is null || button.Visibility != Visibility.Visible)
        {
            return;
        }

        button.Opacity = 0.78;
        await Task.Delay(240);
        if (!button.IsMouseOver)
        {
            button.Opacity = 0;
            button.IsHitTestVisible = false;
        }
    }
}
