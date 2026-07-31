using System.Diagnostics;
using System.Threading;
using TintaES.Core;
using TintaES.Wpf.Services;

namespace TintaES.Wpf;

/// <summary>
/// Mantiene informada la interfaz durante operaciones largas. Ninguna fase se cancela por
/// alcanzar un tiempo fijo: cada dos minutos el usuario decide si continúa o cancela.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan LongOperationReviewInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OperationCancellationGrace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AnalysisHeartbeatInterval = TimeSpan.FromSeconds(12);

    private async Task<OrganicAnalysisResult> AnalyzePageWithWatchdogAsync(
        string sourcePath,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        long startedAt = Stopwatch.GetTimestamp();
        long lastHeartbeatAt = startedAt;
        long nextReviewAt = startedAt + ToStopwatchTicks(LongOperationReviewInterval);
        double lastPercentage = 0;
        string lastMessage = "Iniciando el motor local";
        object stateLock = new();

        var monitoredProgress = new ImmediateProgress<AnalysisProgress>(value =>
        {
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
            Task pulse = Task.Delay(1000, cancellationToken);
            Task completed = await Task.WhenAny(analysisTask, pulse);
            if (ReferenceEquals(completed, analysisTask))
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            long now = Stopwatch.GetTimestamp();
            TimeSpan totalElapsed = Stopwatch.GetElapsedTime(startedAt, now);

            if (now >= nextReviewAt)
            {
                string stage;
                lock (stateLock)
                {
                    stage = lastMessage;
                }

                bool continueWaiting = ShowSlowOperationPrompt(
                    "El análisis de la página",
                    stage,
                    totalElapsed);

                // La fase puede haber terminado mientras el usuario leía la pregunta.
                if (analysisTask.IsCompleted)
                {
                    break;
                }

                if (!continueWaiting)
                {
                    linkedCancellation.Cancel();
                    await Task.WhenAny(
                        analysisTask,
                        Task.Delay(OperationCancellationGrace, CancellationToken.None));
                    throw new OperationCanceledException(linkedCancellation.Token);
                }

                nextReviewAt = Stopwatch.GetTimestamp()
                               + ToStopwatchTicks(LongOperationReviewInterval);
            }

            if (Stopwatch.GetElapsedTime(lastHeartbeatAt, now) >= AnalysisHeartbeatInterval)
            {
                lastHeartbeatAt = now;
                string stage;
                double percentage;
                lock (stateLock)
                {
                    stage = lastMessage;
                    percentage = lastPercentage;
                }

                string elapsed = FormatDuration(totalElapsed.TotalSeconds);
                BusyTitleText.Text = $"{stage} · {elapsed}";
                FooterStatusText.Text = percentage > 0
                    ? $"{stage} · {percentage:0.#}% · {elapsed}"
                    : $"Esperando respuesta del motor local · {elapsed}";
            }
        }

        return await analysisTask;
    }

    private async Task RunLongOperationWithPromptAsync(
        Func<CancellationToken, Task> operationFactory,
        string operationName,
        Func<string> stageProvider,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task operationTask = operationFactory(linkedCancellation.Token);
        var elapsed = Stopwatch.StartNew();
        TimeSpan nextReview = LongOperationReviewInterval;

        while (!operationTask.IsCompleted)
        {
            Task pulse = Task.Delay(1000, cancellationToken);
            Task completed = await Task.WhenAny(operationTask, pulse);
            if (ReferenceEquals(completed, operationTask))
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed < nextReview)
            {
                continue;
            }

            bool continueWaiting = ShowSlowOperationPrompt(
                operationName,
                stageProvider(),
                elapsed.Elapsed);

            // La operación puede terminar mientras la pregunta está abierta.
            if (operationTask.IsCompleted)
            {
                break;
            }

            if (!continueWaiting)
            {
                linkedCancellation.Cancel();
                await Task.WhenAny(
                    operationTask,
                    Task.Delay(OperationCancellationGrace, CancellationToken.None));
                throw new OperationCanceledException(linkedCancellation.Token);
            }

            nextReview = elapsed.Elapsed + LongOperationReviewInterval;
        }

        await operationTask;
    }

    private bool ShowSlowOperationPrompt(
        string operationName,
        string stage,
        TimeSpan elapsed)
    {
        var prompt = new SlowOperationPromptWindow(operationName, stage, elapsed)
        {
            Owner = this
        };
        _ = prompt.ShowDialog();
        return prompt.ContinueWaiting;
    }

    private static long ToStopwatchTicks(TimeSpan duration) =>
        (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
