namespace Juvin.Core.Connector;

/// <summary>
///     The native host's whole job: copy bytes verbatim between the browser's stdin/stdout and the
///     app's connector endpoint, in both directions at once, without parsing them.
/// </summary>
public static class ConnectorRelay
{
    private const int BufferBytes = 64 * 1024;

    private static readonly TimeSpan s_drainTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Returns when either direction ends. The other direction is cancelled and given a moment to
    ///     stop; a read that ignores cancellation, such as a console read, is abandoned, so the caller
    ///     should exit the process right after.
    /// </summary>
    public static async Task RunAsync(Stream browserInput, Stream browserOutput, Stream app, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(browserInput);
        ArgumentNullException.ThrowIfNull(browserOutput);
        ArgumentNullException.ThrowIfNull(app);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toApp = CopyAsync(browserInput, app, flush: false, stop.Token);
        var toBrowser = CopyAsync(app, browserOutput, flush: true, stop.Token);

        var first = await Task.WhenAny(toApp, toBrowser).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        var other = first == toApp ? toBrowser : toApp;
        try
        {
            await other.WaitAsync(s_drainTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Abandoned; the process exits next.
        }
    }

    /// <param name="source">Read until it ends.</param>
    /// <param name="destination">Receives every byte in order.</param>
    /// <param name="flush">
    ///     Flush after each write. Needed for stdout; avoided on the app side because a named pipe
    ///     flush blocks until the app has read.
    /// </param>
    /// <param name="ct">Stops the copy between reads.</param>
    private static async Task CopyAsync(Stream source, Stream destination, bool flush, CancellationToken ct)
    {
        var buffer = new byte[BufferBytes];
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (flush)
                {
                    await destination.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Either end going away ends this direction.
        }
    }
}
