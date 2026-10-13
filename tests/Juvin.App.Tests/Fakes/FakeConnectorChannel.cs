using System.Text.Json;
using System.Threading.Channels;
using Juvin.Core.Connector;

namespace Juvin.App.Tests.Fakes;

/// <summary>An in-memory connector channel: the test plays the extension.</summary>
internal sealed class FakeConnectorChannel : IConnectorChannel
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();

    private bool _disposed;

    public void Deliver(string json) => _incoming.Writer.TryWrite(json);

    /// <summary>The next message the session sent, parsed.</summary>
    public async Task<JsonElement> NextSentAsync()
    {
        var json = await _sent.Reader.ReadAsync().AsTask().WaitAsync(s_timeout);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public bool HasSent => _sent.Reader.TryPeek(out _);

    public Task SendAsync(string json, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sent.Writer.TryWrite(json);
        return Task.CompletedTask;
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
        _disposed = true;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
