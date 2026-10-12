using System.IO.Pipes;

namespace Juvin.Platform.Windows.Ipc;

/// <summary>Named pipe server for the control endpoint: one request line and one reply line per connection.</summary>
public sealed class NamedPipeServer : ILocalSocketServer, IDisposable
{
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(30);

    private readonly NamedPipeListener _listener;

    public NamedPipeServer()
        : this(PipeNames.Control)
    {
    }

    public NamedPipeServer(string pipeName)
    {
        // 4x the line cap holds an oversized request and the error reply on either side.
        _listener = new NamedPipeListener(pipeName, "control pipe", 4 * LocalSocketProtocol.MaxLineBytes);
    }

    public string Address => _listener.Address;

    public Task StartAsync(Func<string, CancellationToken, Task<string>> handleLine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handleLine);

        return _listener.StartAsync((pipe, token) => ServeAsync(pipe, handleLine, token), ct);
    }

    public Task StopAsync() => _listener.StopAsync();

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private static async Task ServeAsync(
        NamedPipeServerStream pipe,
        Func<string, CancellationToken, Task<string>> handleLine,
        CancellationToken ct
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_requestTimeout);
        var reply = await LocalSocketProtocol
            .ReadAndHandleAsync(pipe, handleLine, timeout.Token)
            .ConfigureAwait(false);
        if (reply is null)
        {
            return;
        }

        await LocalSocketProtocol.WriteLineAsync(pipe, reply, timeout.Token).ConfigureAwait(false);
        // Closing the server end discards anything the client has not read yet, so wait
        // for the client to hang up. WaitForPipeDrain cannot be cancelled, this can.
        var drain = new byte[256];
        while (await pipe.ReadAsync(drain, timeout.Token).ConfigureAwait(false) > 0)
        {
            // Discard until the client hangs up.
        }
    }
}
