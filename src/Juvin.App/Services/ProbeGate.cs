namespace Juvin.App.Services;

/// <summary>
///     Runs one probe at a time. Starting a probe cancels the one in flight and waits for it to stop,
///     so two never overlap; the superseded call returns null.
/// </summary>
public sealed class ProbeGate
{
    private CancellationTokenSource? _current;
    private Task _running = Task.CompletedTask;

    public async Task<T?> RunAsync<T>(Func<CancellationToken, Task<T>> probe, CancellationToken lifetime)
        where T : class
    {
        // ReSharper disable once MethodHasAsyncOverload -- the previous probe is awaited below, so a synchronous cancel is enough
        _current?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _current = cts;
        var task = RunAfterAsync(_running, probe, cts.Token);
        _running = task;
        try
        {
            var result = await task;
            // A probe can finish just as the next one supersedes it; its result is stale.
            return cts.IsCancellationRequested ? null : result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (_current == cts)
            {
                _current = null;
            }
        }
    }

    private static async Task<T> RunAfterAsync<T>(
        Task previous,
        Func<CancellationToken, Task<T>> probe,
        CancellationToken ct
    )
    {
        try
        {
            await previous;
        }
        catch (Exception)
        {
            // The previous probe's outcome belongs to its own caller.
        }

        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => probe(ct), ct);
    }
}
