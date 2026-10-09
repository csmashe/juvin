using System.Collections.Concurrent;

namespace Juvin.Platform;

/// <summary>Tracks in-flight connection handlers so a server can wait for them on shutdown.</summary>
public sealed class ActiveConnections
{
    private readonly ConcurrentDictionary<Task, byte> _tasks = new();

    public void Track(Task handler)
    {
        _tasks[handler] = 0;
        handler.ContinueWith(
            t => _tasks.TryRemove(t, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    /// <summary>Waits until every tracked handler has finished or <paramref name="timeout" /> passes.</summary>
    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(_tasks.Keys).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Handlers observe the server's cancellation token and exit on their own.
        }
    }
}
