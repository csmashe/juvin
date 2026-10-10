using System.IO.Pipes;

namespace Juvin.Platform.Windows.Ipc;

/// <summary>Client side of <see cref="NamedPipeConnectorServer" />, used by the native host.</summary>
public sealed class NamedPipeConnectorClient : IConnectorSocketClient
{
    private readonly string _pipeName;

    public NamedPipeConnectorClient()
        : this(PipeNames.Connector)
    {
    }

    public NamedPipeConnectorClient(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        _pipeName = pipeName;
    }

    public async Task<Stream?> ConnectAsync(TimeSpan timeout)
    {
        // CurrentUserOnly also verifies the server end is owned by this user.
        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception ex)
            when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }
}
