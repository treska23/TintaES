using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace TintaES.Wpf;

/// <summary>
/// Copias automáticas de recuperación. Nunca escriben sobre el proyecto del usuario y solo se
/// ejecutan cuando la aplicación lleva unos segundos inactiva y no hay una operación pesada.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan AutoBackupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AutoBackupIdleDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AutoBackupPollInterval = TimeSpan.FromSeconds(30);
    private const int AutoBackupCopiesPerDocument = 5;

    private readonly Dictionary<Guid, DateTime> _autoBackupLastCheckUtc = [];
    private readonly Dictionary<Guid, string> _autoBackupLastFingerprint = [];
    private DispatcherTimer? _autoBackupTimer;
    private DateTime _autoBackupLastUserActivityUtc = DateTime.UtcNow;
    private bool _autoBackupInProgress;
    private bool _autoBackupInstalled;
    private bool _autoBackupClosing;

    private static string AutoBackupRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TintaES",
        "Backups");

    private static string AutoBackupSessionMarkerPath => Path.Combine(
        AutoBackupRootPath,
        "session.active");

    private void InstallAutoBackupRecovery()
    {
        if (_autoBackupInstalled)
        {
            return;
        }

        _autoBackupInstalled = true;
        Directory.CreateDirectory(AutoBackupRootPath);

        bool previousSessionEndedUnexpectedly = File.Exists(AutoBackupSessionMarkerPath);
        try
        {
            File.WriteAllText(
                AutoBackupSessionMarkerPath,
                $"{Environment.ProcessId}|{DateTime.UtcNow:O}");
        }
        catch
        {
            // El autosave nunca debe impedir que arranque la aplicación.
        }

        PreviewMouseDown += AutoBackup_MouseActivity;
        PreviewMouseWheel += AutoBackup_MouseWheelActivity;
        PreviewKeyDown += AutoBackup_KeyActivity;
        PreviewTouchDown += AutoBackup_TouchActivity;
        Closing += AutoBackup_WindowClosing;
        Closed += AutoBackup_WindowClosed;

        _autoBackupTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = AutoBackupPollInterval
        };
        _autoBackupTimer.Tick += AutoBackupTimer_Tick;
        _autoBackupTimer.Start();

        if (previousSessionEndedUnexpectedly)
        {
            Dispatcher.BeginInvoke(
                async () => await OfferAutomaticBackupRecoveryAsync(),
                DispatcherPriority.ContextIdle);
        }
    }

    private void AutoBackup_MouseActivity(object sender, MouseButtonEventArgs e) =>
        _autoBackupLastUserActivityUtc = DateTime.UtcNow;

    private void AutoBackup_MouseWheelActivity(object sender, MouseWheelEventArgs e) =>
        _autoBackupLastUserActivityUtc = DateTime.UtcNow;

    private void AutoBackup_KeyActivity(object sender, KeyEventArgs e) =>
        _autoBackupLastUserActivityUtc = DateTime.UtcNow;

    private void AutoBackup_TouchActivity(object sender, TouchEventArgs e) =>
        _autoBackupLastUserActivityUtc = DateTime.UtcNow;

    private async void AutoBackupTimer_Tick(object? sender, EventArgs e)
    {
        if (_autoBackupClosing
            || _autoBackupInProgress
            || _switchingDocument
            || _documentOpenPending
            || HasDocumentOperationInProgress()
            || DateTime.UtcNow - _autoBackupLastUserActivityUtc < AutoBackupIdleDelay)
        {
            return;
        }

        await CreateDueAutomaticBackupsAsync();
    }

    private async Task CreateDueAutomaticBackupsAsync()
    {
        if (_autoBackupClosing || _autoBackupInProgress)
        {
            return;
        }

        _autoBackupInProgress = true;
        try
        {
            CaptureActiveDocumentState();
            DateTime now = DateTime.UtcNow;
            var due = new List<AutoBackupWorkItem>();

            foreach (ComicDocumentSession session in _documentSessions.Where(session => session.Pages.Count > 0))
            {
                if (_autoBackupClosing)
                {
                    break;
                }

                if (!_autoBackupLastCheckUtc.TryGetValue(session.Id, out DateTime lastCheck))
                {
                    _autoBackupLastCheckUtc[session.Id] = now;
                    continue;
                }

                if (now - lastCheck < AutoBackupInterval)
                {
                    continue;
                }

                _autoBackupLastCheckUtc[session.Id] = now;
                TintaProjectWriteSnapshot snapshot = CaptureTintaProjectWriteSnapshot(
                    session.Title,
                    session.PageIndex,
                    session.Pages);

                if (_autoBackupLastFingerprint.TryGetValue(session.Id, out string? previous)
                    && string.Equals(previous, snapshot.Fingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                string token = session.Id.ToString("N")[..8];
                string safeTitle = MakeSafeFileName(
                    string.IsNullOrWhiteSpace(session.Title) ? "comic" : session.Title);
                string fileName = $"{safeTitle}-autosave-{now:yyyyMMdd-HHmmss}-{token}.tinta";
                string path = Path.Combine(AutoBackupRootPath, fileName);
                due.Add(new AutoBackupWorkItem(session.Id, token, path, snapshot));
            }

            if (due.Count == 0 || _autoBackupClosing)
            {
                return;
            }

            IReadOnlyList<AutoBackupWorkItem> written = await Task.Run(() =>
            {
                var successful = new List<AutoBackupWorkItem>();
                foreach (AutoBackupWorkItem item in due)
                {
                    if (_autoBackupClosing)
                    {
                        break;
                    }

                    if (!TryWriteTintaProjectSnapshot(item.Path, item.Snapshot))
                    {
                        continue;
                    }

                    RotateAutomaticBackups(item.SessionToken);
                    successful.Add(item);
                }
                CleanupOldAutomaticBackupTemporaries();
                return (IReadOnlyList<AutoBackupWorkItem>)successful;
            });

            foreach (AutoBackupWorkItem item in written)
            {
                _autoBackupLastFingerprint[item.SessionId] = item.Snapshot.Fingerprint;
            }

            if (_autoBackupClosing || written.Count == 0)
            {
                return;
            }

            SetFooterStatus(
                written.Count == 1
                    ? $"Copia automática guardada · {DateTime.Now:HH:mm}"
                    : $"{written.Count} copias automáticas guardadas · {DateTime.Now:HH:mm}",
                "#58A77D");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            if (!_autoBackupClosing)
            {
                // No se interrumpe el trabajo del usuario por un fallo de autosave. Se informa de forma
                // discreta y el siguiente ciclo volverá a intentarlo.
                SetFooterStatus(
                    $"No se pudo crear la copia automática · {exception.Message}",
                    "#C99A35");
            }
        }
        finally
        {
            _autoBackupInProgress = false;
        }
    }

    private static void RotateAutomaticBackups(string sessionToken)
    {
        try
        {
            foreach (string stale in Directory
                         .EnumerateFiles(AutoBackupRootPath, $"*-{sessionToken}.tinta", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(AutoBackupCopiesPerDocument))
            {
                TryDeleteAutomaticBackupFile(stale);
            }
        }
        catch
        {
            // La rotación es secundaria; una copia válida nunca se elimina por un error de limpieza.
        }
    }

    private static void CleanupOldAutomaticBackupTemporaries()
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-2);
            foreach (string temporary in Directory.EnumerateFiles(
                         AutoBackupRootPath,
                         "*.tinta.tmp",
                         SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(temporary) < cutoff)
                {
                    TryDeleteAutomaticBackupFile(temporary);
                }
            }
        }
        catch
        {
            // Una limpieza fallida no afecta al backup ya escrito.
        }
    }

    private static void TryDeleteAutomaticBackupFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Se conserva el archivo si Windows lo mantiene bloqueado.
        }
    }

    private async Task OfferAutomaticBackupRecoveryAsync()
    {
        string? latest;
        try
        {
            latest = Directory
                .EnumerateFiles(AutoBackupRootPath, "*.tinta", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(latest) || !File.Exists(latest))
        {
            return;
        }

        DateTime backupTime = File.GetLastWriteTime(latest);
        MessageBoxResult answer = MessageBox.Show(
            this,
            "La sesión anterior no parece haberse cerrado correctamente.\n\n" +
            $"Hay una copia automática de recuperación de las {backupTime:HH:mm} del {backupTime:dd/MM/yyyy}.\n\n" +
            "¿Quieres abrirla? El proyecto original no se sobrescribirá.",
            "Recuperar copia automática",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await LoadTintaProjectAsync(latest);
        if (_comicPages.Count == 0)
        {
            return;
        }

        _currentProjectPath = null;
        _comicTitle = string.IsNullOrWhiteSpace(_comicTitle)
            ? "Recuperado"
            : $"{_comicTitle} (recuperado)";
        SynchronizeActiveDocumentState();
        MarkActiveDocumentDirty();
        SetFooterStatus(
            "Copia automática recuperada. Usa Guardar proyecto para elegir dónde conservarla.",
            "#C99A35");
    }

    private void AutoBackup_WindowClosing(object? sender, CancelEventArgs e)
    {
        _autoBackupClosing = true;
        _autoBackupTimer?.Stop();
    }

    private void AutoBackup_WindowClosed(object? sender, EventArgs e)
    {
        _autoBackupClosing = true;
        _autoBackupTimer?.Stop();
        _autoBackupTimer = null;
        TryDeleteAutomaticBackupFile(AutoBackupSessionMarkerPath);
    }

    private sealed record AutoBackupWorkItem(
        Guid SessionId,
        string SessionToken,
        string Path,
        TintaProjectWriteSnapshot Snapshot);
}
