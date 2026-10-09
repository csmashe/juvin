using Juvin.Core.Connector;

namespace Juvin.Core.Tests;

public sealed class StreamConnectorChannelTests
{
    [Fact]
    public async Task Send_ClosesTheChannelAfterAnInterruptedWrite()
    {
        var stream = new FailAfterFirstByteStream();
        var channel = new StreamConnectorChannel(stream);

        await Assert.ThrowsAsync<IOException>(() => channel.SendAsync("{\"v\":1}", CancellationToken.None));

        Assert.True(stream.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.SendAsync("{\"v\":1}", CancellationToken.None));
    }

    [Fact]
    public async Task Send_KeepsTheChannelOpenWhenTheFrameIsTooLarge()
    {
        var stream = new FailAfterFirstByteStream();
        var channel = new StreamConnectorChannel(stream);

        await Assert.ThrowsAsync<ConnectorFrameException>(() => channel.SendAsync(new string('x', NativeMessagingFraming.MaxFrameBytes + 1), CancellationToken.None));

        Assert.False(stream.Disposed);
    }

    private sealed class FailAfterFirstByteStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Write(buffer.Span[..1]);
            throw new IOException("The pipe broke mid-frame.");
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
