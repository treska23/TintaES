using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TintaES.Core;
using TintaES.Wpf.Controls;

namespace TintaES.Wpf.Services;

public sealed class ImageExportService
{
    public BitmapSource Render(BitmapSource background, IEnumerable<ComicRegion> regions)
    {
        int width = background.PixelWidth;
        int height = background.PixelHeight;
        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent
        };
        canvas.Children.Add(new Image
        {
            Source = background,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill
        });

        foreach (ComicRegion region in regions.Where(region => region.IsEnabled))
        {
            NormalizedRect box = region.RenderBox;
            double left = box.X / 1000 * width;
            double top = box.Y / 1000 * height;
            double elementWidth = box.Width / 1000 * width;
            double elementHeight = box.Height / 1000 * height;
            var layer = new Grid
            {
                Width = elementWidth,
                Height = elementHeight,
                ClipToBounds = true,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(region.Rotation)
            };
            var text = new ComicTextElement
            {
                Region = region,
                PageWidth = width,
                PageHeight = height,
                Width = elementWidth,
                Height = elementHeight
            };
            layer.Children.Add(text);
            Canvas.SetLeft(layer, left);
            Canvas.SetTop(layer, top);
            canvas.Children.Add(layer);
        }

        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(canvas);
        rendered.Freeze();
        return rendered;
    }

    public void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
