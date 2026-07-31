using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Servicios compartidos por el procesamiento individual y por lotes. No pertenecen a la
/// interfaz de edición ni registran manejadores visuales.
/// </summary>
public partial class MainWindow
{
    private readonly DialogueOnlyResultService _dialogueOnlyResultService = new();
    private readonly TranslationRecoveryService _translationRecoveryService = new();
}
