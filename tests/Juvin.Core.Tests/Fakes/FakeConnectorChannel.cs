using System.Text.Json;
using System.Threading.Channels;
using Juvin.Core.Connector;

namespace Juvin.Core.Tests.Fakes;

/// <summary>An in-memory connector channel: the test plays the extension.</summary>
internal sealed class FakeConnectorChannel : IConnectorChannel
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();

    public bool IsDisposed { get; private set; }

    /// <summary>When set, sends wait until cancelled, like a peer that stopped reading.</summary>
    public bool BlockSends { get; set; }

    public void Deliver(string json) => _incoming.Writer.TryWrite(json);

    public void Disconnect() => _incoming.Writer.TryComplete();

    /// <summary>The next message the session sent, parsed.</summary>
    public async Task<JsonElement> NextSentAsync()
    {
        var json = await _sent.Reader.ReadAsync().AsTask().WaitAsync(s_timeout);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public bool HasSent => _sent.Reader.TryPeek(out _);

    public async Task SendAsync(string json, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (BlockSends)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        _sent.Writer.TryWrite(json);
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
