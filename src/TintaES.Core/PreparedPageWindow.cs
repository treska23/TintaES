using System.Runtime.ExceptionServices;

namespace TintaES.Core;

/// <summary>
/// Groups page preparation before consuming its results, without delaying the first page.
/// Call sequentially in page order; a repeated position is prepared again for a retry.
/// </summary>
public sealed class PreparedPageWindow<T> where T : class
{
    private readonly int _pageCount;
    private readonly int _windowSize;
    private readonly Dictionary<int, PreparedPage> _prepared = [];
    private readonly Dictionary<int, PreparedPageWindowItem<T>> _scheduledPrepared = [];
    private int _nextPosition;
    private int _nextWindowPosition;

    public PreparedPageWindow(int pageCount, int windowSize = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
        _pageCount = pageCount;
        _windowSize = windowSize;
    }

    /// <summary>
    /// Number of pages that have not yet been returned by <see cref="TakeNextWindowAsync"/>.
    /// A partially prepared window remains pending when preparation is cancelled.
    /// </summary>
    public int RemainingCount => _pageCount - _nextWindowPosition;

    public bool HasRemaining => RemainingCount > 0;

    public async Task<T> TakeAsync(
        int position,
        Func<int, CancellationToken, Task<T>> prepare,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(position, _pageCount);
        ArgumentNullException.ThrowIfNull(prepare);
        cancellationToken.ThrowIfCancellationRequested();

        if (position < _nextPosition)
        {
            // A failed consumer may already have mutated its result. Never reuse that instance.
            return await PrepareAsync(position, prepare, cancellationToken);
        }

        if (position != _nextPosition)
        {
            throw new InvalidOperationException("Las páginas deben consumirse en orden, sin saltar posiciones.");
        }

        if (!_prepared.ContainsKey(position))
        {
            int count = position == 0 ? 1 : Math.Min(_windowSize, _pageCount - position);
            for (int offset = 0; offset < count; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pagePosition = position + offset;
                try
                {
                    T result = await PrepareAsync(pagePosition, prepare, cancellationToken);
                    _prepared.Add(pagePosition, new PreparedPage(result, null));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // One unreadable page must not prevent preparing or consuming its neighbours.
                    _prepared.Add(pagePosition, new PreparedPage(null, ExceptionDispatchInfo.Capture(exception)));
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _prepared.Remove(position, out PreparedPage? prepared);
        _nextPosition++;
        prepared!.Error?.Throw();
        return prepared.Result!;
    }

    /// <summary>
    /// Prepares and returns the next translation window. The first call contains only the first
    /// page so it can be translated immediately; later calls contain at most the configured
    /// window size and are ordered by ascending cost, then by original position.
    /// </summary>
    /// <remarks>
    /// Non-cancellation errors from preparation or cost estimation are returned as failed items
    /// at the end of their window. Cancellation is propagated and already completed preparations
    /// are retained for the next call.
    /// </remarks>
    public async Task<IReadOnlyList<PreparedPageWindowItem<T>>> TakeNextWindowAsync(
        Func<int, CancellationToken, Task<T>> prepare,
        Func<T, long> costSelector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(costSelector);
        cancellationToken.ThrowIfCancellationRequested();

        if (!HasRemaining)
        {
            return [];
        }

        int count = _nextWindowPosition == 0
            ? 1
            : Math.Min(_windowSize, RemainingCount);
        int windowEnd = _nextWindowPosition + count;
        for (int position = _nextWindowPosition; position < windowEnd; position++)
        {
            if (_scheduledPrepared.ContainsKey(position))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            PreparedPageWindowItem<T> item;
            try
            {
                T result = await PrepareAsync(position, prepare, cancellationToken);
                long cost = costSelector(result);
                cancellationToken.ThrowIfCancellationRequested();
                item = new PreparedPageWindowItem<T>(position, result, null, cost);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A bad page or cost estimate must not block the rest of the prepared window.
                item = new PreparedPageWindowItem<T>(position, null, exception, long.MaxValue);
            }

            _scheduledPrepared.Add(position, item);
        }

        cancellationToken.ThrowIfCancellationRequested();
        PreparedPageWindowItem<T>[] ordered = Enumerable
            .Range(_nextWindowPosition, count)
            .Select(position => _scheduledPrepared[position])
            .OrderBy(item => item.Error is null ? 0 : 1)
            .ThenBy(item => item.Cost)
            .ThenBy(item => item.Position)
            .ToArray();

        for (int position = _nextWindowPosition; position < windowEnd; position++)
        {
            _scheduledPrepared.Remove(position);
        }
        _nextWindowPosition = windowEnd;
        return ordered;
    }

    private static async Task<T> PrepareAsync(
        int position,
        Func<int, CancellationToken, Task<T>> prepare,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        T result = await prepare(position, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result ?? throw new InvalidOperationException("La preparación de una página no devolvió un resultado.");
    }

    private sealed record PreparedPage(T? Result, ExceptionDispatchInfo? Error);
}

/// <summary>
/// A prepared page ready for scheduled translation, or its isolated preparation error.
/// </summary>
public sealed record PreparedPageWindowItem<T>(
    int Position,
    T? Result,
    Exception? Error,
    long Cost)
    where T : class;
