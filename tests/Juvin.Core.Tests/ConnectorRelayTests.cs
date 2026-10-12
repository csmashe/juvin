using System.Text;
using Juvin.Core.Connector;

namespace Juvin.Core.Tests;

public sealed class ConnectorRelayTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task BrowserInput_IsCopiedToTheAppUntilTheBrowserCloses()
    {
        await using var app = new DuplexStream(new BlockingStream());
        await using var browserOutput = new FlushCountingStream();
        await using var browserInput = new MemoryStream([.. "frame-bytes"u8]);

        await ConnectorRelay
            .RunAsync(browserInput, browserOutput, app, CancellationToken.None)
            .WaitAsync(s_timeout);

        Assert.Equal("frame-bytes", Encoding.UTF8.GetString(app.Written.ToArray()));
        Assert.Equal(0, browserOutput.Length);
    }

    [Fact]
    public async Task AppOutput_IsCopiedAndFlushedToTheBrowserUntilTheAppCloses()
    {
        await using var appOutput = new MemoryStream([.. "reply-bytes"u8]);
        await using var app = new DuplexStream(appOutput);
        await using var browserOutput = new FlushCountingStream();
        await using var browserInput = new BlockingStream(ignoreCancellation: true);

        // The browser input never ends and ignores cancellation, like a console read.
        await ConnectorRelay
            .RunAsync(browserInput, browserOutput, app, CancellationToken.None)
            .WaitAsync(s_timeout);

        Assert.Equal("reply-bytes", Encoding.UTF8.GetString(browserOutput.ToArray()));
        Assert.True(browserOutput.Flushes > 0);
        Assert.Equal(0, app.Written.Length);
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int Flushes { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return base.FlushAsync(cancellationToken);
        }
    }

    /// <summary>A read side that never returns data.</summary>
    private sealed class BlockingStream(bool ignoreCancellation = false) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, ignoreCancellation ? CancellationToken.None : cancellationToken);
            return 0;
        }
    }

    /// <summary>Reads from <paramref name="source" /> and records what is written.</summary>
    private sealed class DuplexStream(Stream source) : Stream
    {
        public MemoryStream Written { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            source.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Written.WriteAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
