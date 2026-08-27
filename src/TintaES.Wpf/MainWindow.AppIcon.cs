using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace TintaES.Wpf;

public partial class MainWindow
{
    static MainWindow()
    {
        IconProperty.OverrideMetadata(typeof(MainWindow), new FrameworkPropertyMetadata(CreateApplicationIcon()));
    }

    private static BitmapFrame CreateApplicationIcon()
    {
        BitmapFrame icon = BitmapFrame.Create(
            new Uri("pack://application:,,,/Resources/TintaES.ico", UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        icon.Freeze();
        return icon;
    }
}
