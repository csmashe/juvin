using System.IO.Pipes;

namespace Juvin.Platform.Windows.Ipc;

/// <summary>Named pipe server for the browser connector endpoint.</summary>
public sealed class NamedPipeConnectorServer : IConnectorSocketServer, IDisposable
{
    /// <summary>
    ///     A fraction of the 1 MiB frame cap. Larger frames still pass because both ends read
    ///     continuously, so a write that fills the buffer only waits for the reader.
    /// </summary>
    private const int BufferBytes = 64 * 1024;

    private readonly NamedPipeListener _listener;

    public NamedPipeConnectorServer()
        : this(PipeNames.Connector)
    {
    }

    public NamedPipeConnectorServer(string pipeName)
    {
        _listener = new NamedPipeListener(pipeName, "connector pipe", BufferBytes);
    }

    public string Address => _listener.Address;

    public Task StartAsync(Func<Stream, CancellationToken, Task> handleConnection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handleConnection);

        return _listener.StartAsync((pipe, token) => handleConnection(new DrainingPipeStream(pipe), token), ct);
    }

    /// <summary>Every browser on Windows reaches the one pipe, so there are no sandbox addresses to add.</summary>
    public Task AddAddressAsync(string address, CancellationToken ct) =>
        throw new NotSupportedException("The connector pipe has a single address on Windows.");

    public Task StopAsync() => _listener.StopAsync();

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Closing the server end discards anything the client has not read yet, which would lose a
    ///     final error frame. Disposal first gives the client a moment to read; it never waits longer.
    /// </summary>
    private sealed class DrainingPipeStream(NamedPipeServerStream pipe) : Stream
    {
        private static readonly TimeSpan s_drainTimeout = TimeSpan.FromSeconds(1);

        public override bool CanRead => pipe.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => pipe.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => pipe.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => pipe.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => pipe.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            pipe.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => pipe.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            pipe.WriteAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            await DrainAsync().ConfigureAwait(false);
            await pipe.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DrainAsync().GetAwaiter().GetResult();
                pipe.Dispose();
            }

            base.Dispose(disposing);
        }

        private async Task DrainAsync()
        {
            if (!pipe.IsConnected)
            {
                return;
            }

            try
            {
                // WaitForPipeDrain blocks until the client has read every byte and cannot be cancelled.
                await Task.Run(pipe.WaitForPipeDrain).WaitAsync(s_drainTimeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The client stopped reading or went away; closing now loses nothing it would have read.
            }
        }
    }
}
