namespace Juvin.Core.Connector;

/// <summary>A connector channel over a byte stream using native messaging framing.</summary>
public sealed class StreamConnectorChannel : IConnectorChannel
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Stream _stream;

    public StreamConnectorChannel(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
    }

    public async Task SendAsync(string json, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await NativeMessagingFraming.WriteFrameAsync(_stream, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ConnectorFrameException)
        {
            // Part of the frame may be on the wire, so nothing sent after it could be framed correctly.
            await _stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public Task<string?> ReceiveAsync(CancellationToken ct) => NativeMessagingFraming.ReadFrameAsync(_stream, ct);

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
