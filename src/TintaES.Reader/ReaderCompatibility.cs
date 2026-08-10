namespace TintaES.Wpf;

/// <summary>
/// El visor compartido nació dentro de TintaES.Wpf y conserva dos referencias al sello de
/// MainWindow. El ejecutable ligero satisface únicamente ese contrato de identidad; no incluye
/// ni enlaza la ventana principal del traductor.
/// </summary>
internal static class MainWindow
{
    internal const string CurrentUiBuildStamp = ReaderBuildInfo.CurrentBuildStamp;
}
