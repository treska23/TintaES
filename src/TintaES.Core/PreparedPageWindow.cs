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
    private int _nextPosition;

    public PreparedPageWindow(int pageCount, int windowSize = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
        _pageCount = pageCount;
        _windowSize = windowSize;
    }

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
