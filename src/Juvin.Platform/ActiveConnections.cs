using System.Collections.Concurrent;

namespace Juvin.Platform;

/// <summary>
///     Runs connection handlers with a cap on how many run at once, and closes their transports on
///     shutdown whether or not the handlers cooperate.
/// </summary>
public sealed class ActiveConnections
{
    public const int DefaultLimit = 64;

    private readonly ConcurrentDictionary<Task, IDisposable> _handlers = new();
    private readonly int _limit;

    public ActiveConnections(int limit = DefaultLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        _limit = limit;
    }

    /// <summary>
    ///     Runs <paramref name="handle" /> for <paramref name="transport" />, or disposes the transport and
    ///     returns false when the cap is reached. Called from one accept loop, so the count only drops in between.
    /// </summary>
    public bool TryStart(IDisposable transport, Func<Task> handle)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(handle);

        if (_handlers.Count >= _limit)
        {
            transport.Dispose();
            return false;
        }

        var handler = Task.Run(handle, CancellationToken.None);
        _handlers[handler] = transport;
        handler.ContinueWith(
            t => _handlers.TryRemove(t, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        return true;
    }

    /// <summary>
    ///     Waits until every handler has finished or <paramref name="timeout" /> passes, then closes the
    ///     transports of any still running.
    /// </summary>
    public async Task CloseAllAsync(TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(_handlers.Keys).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The transports are closed below, which ends any read or write the handler is stuck in.
        }

        foreach (var transport in _handlers.Values)
        {
            try
            {
                transport.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Already closing.
            }
        }
    }
}
