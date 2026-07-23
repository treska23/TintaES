using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene la edición interactiva ligera. Cambiar zoom, seleccionar una zona,
/// escribir una traducción o ajustar su escala no debe reconstruir las 17/50/100
/// capas de rotulación de la página.
///
/// El arrastre de las zonas sigue gestionándolo MainWindow.xaml.cs: el usuario puede
/// colocar manualmente cada texto en la posición deseada. Esta clase evita que las
/// operaciones de edición más frecuentes provoquen un RebuildOverlay completo.
/// </summary>
public partial class MainWindow
{
    private bool _fastEditingHandlersInstalled;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_fastEditingHandlersInstalled)
        {
            return;
        }

        _fastEditingHandlersInstalled = true;

        // Sustituimos únicamente los handlers que reconstruían todas las geometrías
        // en acciones de alta frecuencia. Los métodos originales permanecen para no
        // alterar el resto del flujo de la ventana.
        ZoomSlider.ValueChanged -= ZoomSlider_ValueChanged;
        ZoomSlider.ValueChanged += ZoomSlider_ValueChanged_Fast;

        RegionListBox.SelectionChanged -= RegionListBox_SelectionChanged;
        RegionListBox.SelectionChanged += RegionListBox_SelectionChanged_Fast;

        TranslationTextBox.TextChanged -= TranslationTextBox_TextChanged;
        TranslationTextBox.TextChanged += TranslationTextBox_TextChanged_Fast;

        FontScaleSlider.ValueChanged -= FontScaleSlider_ValueChanged;
        FontScaleSlider.ValueChanged += FontScaleSlider_ValueChanged_Fast;
    }

    private void ZoomSlider_ValueChanged_Fast(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageStage is null || ZoomText is null)
        {
            return;
        }

        // El ImageStage ya contiene imagen y overlay. Escalar el contenedor es suficiente;
        // recrear todos los ComicTextElement en cada paso del slider era la principal
        // fuente de tirones durante el zoom.
        double scale = ZoomSlider.Value / 100;
        ImageStage.LayoutTransform = new ScaleTransform(scale, scale);
        ZoomText.Text = $"{Math.Round(ZoomSlider.Value)} %";
    }

    private void RegionListBox_SelectionChanged_Fast(object sender, SelectionChangedEventArgs e)
    {
        _selectedRegion = RegionListBox.SelectedItem as ComicRegion;
        ShowRegionEditor(_selectedRegion);

        // Seleccionar una tarjeta no cambia ningún texto de la página. No hay motivo
        // para destruir y recrear todas las geometrías del overlay.
    }

    private void TranslationTextBox_TextChanged_Fast(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        _selectedRegion.Translation = TranslationTextBox.Text;
        _selectedRegion.NotifyVisualChange();
        InvalidateRegionVisual(_selectedRegion);
    }

    private void FontScaleSlider_ValueChanged_Fast(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontScaleText is null)
        {
            return;
        }

        FontScaleText.Text = $"{Math.Round(FontScaleSlider.Value)} %";
        if (_syncingEditor || _selectedRegion is null)
        {
            return;
        }

        _selectedRegion.FontScale = FontScaleSlider.Value / 100;
        InvalidateRegionVisual(_selectedRegion);
    }

    private void InvalidateRegionVisual(ComicRegion region)
    {
        foreach (Grid layer in OverlayCanvas.Children.OfType<Grid>())
        {
            if (!ReferenceEquals(layer.Tag, region))
            {
                continue;
            }

            ComicTextElement? text = layer.Children.OfType<ComicTextElement>().FirstOrDefault();
            text?.InvalidateVisual();
            break;
        }
    }
}
