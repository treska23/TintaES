using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TintaES.Wpf;

public partial class MainWindow
{
    private readonly ConditionalWeakTable<Grid, OverlayGuideState> _overlayGuideStates = new();
    private bool _presentationHooksAttached;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ConstrainWindowToWorkingArea();
        AttachPresentationHooks();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ConstrainWindowToWorkingArea();
    }

    private void AttachPresentationHooks()
    {
        if (_presentationHooksAttached)
        {
            return;
        }

        _presentationHooksAttached = true;

        // Evita que el tema del sistema aplique texto oscuro a las tarjetas de la
        // lista lateral. DisplayText y Original deben ser legibles sobre el panel oscuro.
        if (FindResource("InkBrush") is Brush inkBrush)
        {
            RegionListBox.Foreground = inkBrush;
        }

        OverlayCanvas.LayoutUpdated += OverlayCanvas_PresentationLayoutUpdated;
    }

    private void ConstrainWindowToWorkingArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return;
        }

        // Las dimensiones de WPF están expresadas en DIP, por lo que este límite
        // también funciona cuando Windows usa 125 %, 150 %, 175 % o 200 % de escala.
        MinWidth = Math.Min(MinWidth, Math.Max(640, workArea.Width * 0.72));
        MinHeight = Math.Min(MinHeight, Math.Max(460, workArea.Height * 0.72));
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;

        if (Width > workArea.Width)
        {
            Width = workArea.Width;
        }
        if (Height > workArea.Height)
        {
            Height = workArea.Height;
        }
    }

    private void OverlayCanvas_PresentationLayoutUpdated(object? sender, EventArgs e)
    {
        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (_overlayGuideStates.TryGetValue(layer, out _))
            {
                continue;
            }

            Border? border = layer.Children.OfType<Border>().FirstOrDefault();
            Thumb[] thumbs = layer.Children.OfType<Thumb>().ToArray();
            if (border is null || thumbs.Length < 2)
            {
                continue;
            }

            Thumb resizeThumb = thumbs[^1];
            var state = new OverlayGuideState(border, resizeThumb);
            _overlayGuideStates.Add(layer, state);
            HideGuide(state);

            layer.MouseEnter += (_, _) => ShowGuide(state);
            layer.MouseLeave += (_, _) =>
            {
                if (!state.IsDragging)
                {
                    HideGuide(state);
                }
            };

            foreach (Thumb thumb in thumbs)
            {
                thumb.DragStarted += (_, _) =>
                {
                    state.IsDragging = true;
                    ShowGuide(state);
                };
                thumb.DragCompleted += (_, _) =>
                {
                    state.IsDragging = false;
                    if (!layer.IsMouseOver)
                    {
                        HideGuide(state);
                    }
                };
            }
        }
    }

    private static void ShowGuide(OverlayGuideState state)
    {
        state.Border.Visibility = Visibility.Visible;
        state.ResizeThumb.Visibility = Visibility.Visible;
    }

    private static void HideGuide(OverlayGuideState state)
    {
        state.Border.Visibility = Visibility.Collapsed;
        state.ResizeThumb.Visibility = Visibility.Collapsed;
    }

    private sealed class OverlayGuideState(Border border, Thumb resizeThumb)
    {
        public Border Border { get; } = border;
        public Thumb ResizeThumb { get; } = resizeThumb;
        public bool IsDragging { get; set; }
    }
}
