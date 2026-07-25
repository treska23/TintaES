namespace TintaES.Wpf;

public partial class MainWindow
{
    // Compatibilidad con el nombre utilizado por la optimización de deshacer/rehacer.
    // El editor nativo conserva un único marco de selección y lo actualiza en el siguiente render.
    private void SyncSelectedTextFrameChrome() =>
        QueueNativeTextFrameRefresh();
}
