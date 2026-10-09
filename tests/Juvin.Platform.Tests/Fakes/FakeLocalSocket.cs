namespace Juvin.Platform.Tests.Fakes;

/// <summary>In-memory server and client in one: requests sent by the client go straight to the started handler.</summary>
public sealed class FakeLocalSocket : ILocalSocketServer, ILocalSocketClient
{
    private Func<string, CancellationToken, Task<string>>? _handler;

    public string Address => "fake";

    public List<string> ReceivedLines { get; } = [];

    public Task StartAsync(Func<string, CancellationToken, Task<string>> handleLine, CancellationToken ct)
    {
        _handler = handleLine;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _handler = null;
        return Task.CompletedTask;
    }

    public async Task<string?> SendAsync(string line, TimeSpan timeout)
    {
        LocalSocketProtocol.ThrowIfInvalidRequest(line);
        var handler = _handler;
        if (handler is null)
        {
            return null;
        }

        ReceivedLines.Add(line);
        return await handler(line, CancellationToken.None);
    }

    public bool IsLivePeer() => _handler is not null;
}
