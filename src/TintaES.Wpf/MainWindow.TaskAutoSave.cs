using System.IO;
using System.Text.Json;
using System.Windows;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Persiste cada unidad de trabajo terminada directamente en el .tinta principal. No genera
/// versiones, snapshots ni copias permanentes: solo usa un temporal efímero para que sustituir
/// el contenido sea atómico y el archivo principal nunca quede a medio escribir.
/// </summary>
public partial class MainWindow
{
    private readonly SemaphoreSlim _taskAutoSaveGate = new(1, 1);

    /// <summary>
    /// Antes de una operación larga establece una base persistente. Si el proyecto todavía no
    /// tiene fichero pide la ruta una sola vez; si había cambios manuales pendientes los guarda
    /// primero para que los posteriores autoguardados incrementales no puedan perderlos.
    /// </summary>
    private async Task<bool> EnsureMainProjectForTaskAutoSaveAsync()
    {
        if (_comicPages.Count == 0)
        {
            return true;
        }

        bool missingMainFile = string.IsNullOrWhiteSpace(_currentProjectPath)
                               || !File.Exists(_currentProjectPath);
        if (!missingMainFile && !ProjectNeedsUserSave())
        {
            return true;
        }

        bool saved = await SaveActiveProjectAsync();
        if (!saved)
        {
            SetFooterStatus(
                "La tarea no se ha iniciado porque el proyecto principal no pudo guardarse.",
                "#EE594B");
        }
        return saved;
    }

    /// <summary>
    /// Se llama después de que una página haya terminado una tarea y su estado ya sea definitivo.
    /// Ignora el token del lote deliberadamente: si el usuario pulsa Cancelar justo después de
    /// terminar una página, primero se confirma su escritura y después se detiene el resto.
    /// </summary>
    private async Task AutoSaveCompletedProjectTaskAsync(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _comicPages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }
        if (string.IsNullOrWhiteSpace(_currentProjectPath)
            || !File.Exists(_currentProjectPath))
        {
            ShowTaskAutoSaveFailure(
                "El proyecto principal ya no existe en disco. La tarea se detendrá para no " +
                "seguir acumulando trabajo sin guardar.");
            throw new OperationCanceledException("No existe el proyecto principal para autoguardar.");
        }

        // El análisis ya generó el fondo sin las letras inglesas y su máscara. El modo lector
        // anterior los descartaba y por eso el español acababa dibujado encima del inglés.
        CommitPreparedComicPageArtifacts(pageIndex);

        string projectPath = _currentProjectPath;
        ComicBookPageState page = _comicPages[pageIndex];
        TintaProjectManifest manifest = BuildIncrementalProjectManifest(pageIndex);
        byte[] manifestJson = JsonSerializer.SerializeToUtf8Bytes(manifest, ProjectJsonOptions);

        await _taskAutoSaveGate.WaitAsync(CancellationToken.None);
        try
        {
            await Task.Run(() =>
                TintaProjectTaskAutosaveService.ReplacePageTransactionally(
                    projectPath,
                    pageIndex,
                    page.CleanedPath,
                    page.MaskPath,
                    manifestJson));

            MarkActiveDocumentPageSaved(pageIndex);
            RefreshDirtyAwareSaveCommands();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ShowTaskAutoSaveFailure(
                "No se pudo escribir el último trabajo terminado en el proyecto principal. " +
                "La traducción se detendrá aquí; las páginas autoguardadas anteriormente siguen " +
                "intactas.\n\n" + exception.Message);
            throw new OperationCanceledException(
                "Se detuvo el lote porque falló el autoguardado del proyecto principal.",
                exception);
        }
        finally
        {
            _taskAutoSaveGate.Release();
        }
    }

    private void ShowTaskAutoSaveFailure(string message)
    {
        SetFooterStatus(
            "Autoguardado fallido · se detiene la tarea para proteger el progreso.",
            "#EE594B");
        MessageBox.Show(
            this,
            message,
            "Autoguardado de Tinta ES",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
