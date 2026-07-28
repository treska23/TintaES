using System.Diagnostics;
using System.Threading;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Vigila el motor local durante el análisis de una página. La interfaz nunca debe quedar
/// bloqueada indefinidamente si Python, CTD, LaMa u OCR dejan de responder.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan InitialEngineResponseTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan EngineStageTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan EngineAbsoluteTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan EngineCancellationGrace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AnalysisHeartbeatInterval = TimeSpan.FromSeconds(12);

    private async Task<OrganicAnalysisResult> AnalyzePageWithWatchdogAsync(
        string sourcePath,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        long startedAt = Stopwatch.GetTimestamp();
        long lastProgressAt = startedAt;
        long lastHeartbeatAt = startedAt;
        int receivedProgress = 0;
        int lastPercentage = 0;
        string lastMessage = "Iniciando el motor local";
        object stateLock = new();

        var monitoredProgress = new ImmediateProgress<AnalysisProgress>(value =>
        {
            Interlocked.Exchange(ref lastProgressAt, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref receivedProgress);
            lock (stateLock)
            {
                lastPercentage = value.Percentage;
                if (!string.IsNullOrWhiteSpace(value.Message))
                {
                    lastMessage = value.Message;
                }
            }
            progress?.Report(value);
        });

        Task<OrganicAnalysisResult> analysisTask = _organicEngine.AnalyzeAsync(
            sourcePath,
            monitoredProgress,
            linkedCancellation.Token);

        while (!analysisTask.IsCompleted)
        {
            await Task.Delay(1000, cancellationToken);

            long now = Stopwatch.GetTimestamp();
            TimeSpan totalElapsed = Stopwatch.GetElapsedTime(startedAt, now);
            TimeSpan stalledFor = Stopwatch.GetElapsedTime(
                Interlocked.Read(ref lastProgressAt),
                now);
            bool hasProgress = Volatile.Read(ref receivedProgress) > 0;
            TimeSpan allowedStall = hasProgress
                ? EngineStageTimeout
                : InitialEngineResponseTimeout;

            if (totalElapsed >= EngineAbsoluteTimeout || stalledFor >= allowedStall)
            {
                string stage;
                lock (stateLock)
                {
                    stage = lastMessage;
                }

                linkedCancellation.Cancel();
                await Task.WhenAny(
                    analysisTask,
                    Task.Delay(EngineCancellationGrace, CancellationToken.None));

                string reason = totalElapsed >= EngineAbsoluteTimeout
                    ? $"El análisis superó el límite total de {EngineAbsoluteTimeout.TotalMinutes:0} minutos"
                    : hasProgress
                        ? $"El motor no avanzó durante {allowedStall.TotalMinutes:0} minutos"
                        : $"El motor no respondió durante {allowedStall.TotalMinutes:0} minutos";

                throw new TimeoutException(
                    $"{reason} en «{stage}». Se ha detenido para que Tinta ES no quede bloqueado.");
            }

            if (Stopwatch.GetElapsedTime(lastHeartbeatAt, now) >= AnalysisHeartbeatInterval)
            {
                lastHeartbeatAt = now;
                string stage;
                int percentage;
                lock (stateLock)
                {
                    stage = lastMessage;
                    percentage = lastPercentage;
                }

                string elapsed = FormatDuration(totalElapsed.TotalSeconds);
                BusyTitleText.Text = $"{stage} · {elapsed}";
                FooterStatusText.Text = percentage > 0
                    ? $"{stage} · {percentage}% · {elapsed}"
                    : $"Esperando respuesta del motor local · {elapsed}";
            }
        }

        return await analysisTask;
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
