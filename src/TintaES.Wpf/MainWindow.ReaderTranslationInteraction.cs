using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TintaES.Core;

namespace TintaES.Wpf;

/// <summary>
/// Permite consultar una traducción directamente sobre la página principal. El texto
/// detectado es una zona invisible: la página original nunca se modifica ni se tapa.
/// </summary>
public partial class MainWindow
{
    private Grid? _mainTranslationOverlay;
    private TextBlock? _mainTranslationSpanish;

    private void InstallMainTranslationInteraction()
    {
        if (_mainTranslationOverlay is not null || ImageScrollViewer.Parent is not Grid host)
        {
            return;
        }

        var overlay = new Grid
        {
            Visibility = Visibility.Collapsed,
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(overlay, 1900);

        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 780,
            Margin = new Thickness(28),
            Padding = new Thickness(20, 13, 20, 14),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1)
        };

        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        _mainTranslationSpanish = new TextBlock
        {
            Foreground = Brushes.Black,
            FontSize = Math.Max(18d, SystemFonts.MessageFontSize * 1.45d),
            FontWeight = SystemFonts.MessageFontWeight,
            FontFamily = SystemFonts.MessageFontFamily,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 680,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = Math.Max(24d, SystemFonts.MessageFontSize * 1.9d)
        };
        content.Children.Add(_mainTranslationSpanish);
        card.Child = content;
        overlay.Children.Add(card);

        host.Children.Add(overlay);
        _mainTranslationOverlay = overlay;
    }

    private bool TryShowMainTranslationAt(Point imagePoint)
    {
        if (_mainTranslationOverlay is null
            || _regions.Count == 0
            || ImageStage.ActualWidth <= 1
            || ImageStage.ActualHeight <= 1
            || imagePoint.X < 0
            || imagePoint.Y < 0
            || imagePoint.X > ImageStage.ActualWidth
            || imagePoint.Y > ImageStage.ActualHeight)
        {
            return false;
        }

        NormalizedPoint normalized = NormalizeImagePoint(
            imagePoint.X,
            imagePoint.Y,
            ImageStage.ActualWidth,
            ImageStage.ActualHeight);
        double x = normalized.X;
        double y = normalized.Y;
        ComicRegion? region = ResolveMainTranslationRegion(_regions, x, y);

        if (region is null)
        {
            return false;
        }

        ShowMainTranslation(region);
        return true;
    }

    internal static ComicRegion? ResolveMainTranslationRegion(
        IEnumerable<ComicRegion> regions,
        double x,
        double y) => ComicRegionHitResolver.Resolve(regions, x, y);

    internal static NormalizedPoint NormalizeImagePoint(
        double x,
        double y,
        double pageWidth,
        double pageHeight) => new(
        x / Math.Max(1, pageWidth) * 1000d,
        y / Math.Max(1, pageHeight) * 1000d);

    private void ShowMainTranslation(ComicRegion region)
    {
        if (_mainTranslationOverlay is null
            || _mainTranslationSpanish is null)
        {
            return;
        }

        _mainTranslationSpanish.Text = region.HasRenderableTranslation
            ? region.Translation.Trim()
            : "Traducción pendiente";
        _mainTranslationSpanish.Foreground = region.HasRenderableTranslation
            ? Brushes.Black
            : new SolidColorBrush(Color.FromRgb(120, 80, 20));
        _mainTranslationOverlay.Visibility = Visibility.Visible;
    }

    private void HideMainTranslation()
    {
        if (_mainTranslationOverlay is not null)
        {
            _mainTranslationOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void MainImage_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        if (!TryShowMainTranslationAt(e.GetTouchPoint(ImageStage).Position)
            || _mainTranslationOverlay is null)
        {
            return;
        }

        e.TouchDevice.Capture(ImageStage);
        e.Handled = true;
    }

    private void MainImage_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (!ImageStage.AreAnyTouchesCapturedWithin)
        {
            return;
        }

        e.TouchDevice.Capture(null);
        HideMainTranslation();
        e.Handled = true;
    }

}
