using System.IO.Pipes;

namespace Juvin.Platform.Windows.Ipc;

/// <summary>Client side of <see cref="NamedPipeServer" />: one request line, one reply line per connection.</summary>
public sealed class NamedPipeClient : ILocalSocketClient
{
    private const int ProbeTimeoutMillis = 500;

    private readonly string _pipeName;

    public NamedPipeClient()
        : this(PipeNames.Control)
    {
    }

    public NamedPipeClient(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        _pipeName = pipeName;
    }

    public async Task<string?> SendAsync(string line, TimeSpan timeout)
    {
        LocalSocketProtocol.ThrowIfInvalidRequest(line);

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            // CurrentUserOnly also verifies the server end is owned by this user.
            await using var pipe = CreateClient();
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            await LocalSocketProtocol.WriteLineAsync(pipe, line, cts.Token).ConfigureAwait(false);
            var (reply, _) = await LocalSocketProtocol
                .ReadLineAsync(pipe, LocalSocketProtocol.MaxLineBytes, cts.Token)
                .ConfigureAwait(false);
            return reply;
        }
        catch (Exception ex)
            when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool IsLivePeer()
    {
        try
        {
            using var pipe = CreateClient();
            pipe.Connect(ProbeTimeoutMillis);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private NamedPipeClientStream CreateClient()
    {
        return new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
    }
}
