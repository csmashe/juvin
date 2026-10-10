using System.IO.Pipelines;
using Juvin.Platform;

namespace Juvin.App.Tests.Fakes;

/// <summary>In-memory connector server and client in one: each connect hands the server side to the started handler.</summary>
public sealed class FakeConnectorSocket : IConnectorSocketServer, IConnectorSocketClient
{
    private readonly List<Task> _connections = [];
    private CancellationTokenSource? _cts;
    private Func<Stream, CancellationToken, Task>? _handler;

    public string Address => "fake";

    public List<string> AddedAddresses { get; } = [];

    /// <summary>When set, <see cref="AddAddressAsync" /> throws it instead of recording the address.</summary>
    public Exception? AddAddressFailure { get; set; }

    /// <summary>When set, only this address throws <see cref="AddAddressFailure" />; others are recorded.</summary>
    public string? FailOnlyAddress { get; set; }

    public Task StartAsync(Func<Stream, CancellationToken, Task> handleConnection, CancellationToken ct)
    {
        _handler = handleConnection;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return Task.CompletedTask;
    }

    public Task AddAddressAsync(string address, CancellationToken ct)
    {
        if (AddAddressFailure is not null && (FailOnlyAddress is null || FailOnlyAddress == address))
        {
            throw AddAddressFailure;
        }

        AddedAddresses.Add(address);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _handler = null;
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        await Task.WhenAll(_connections);
    }

    public Task<Stream?> ConnectAsync(TimeSpan timeout)
    {
        var handler = _handler;
        if (handler is null || _cts is null)
        {
            return Task.FromResult<Stream?>(null);
        }

        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = new PipeDuplexStream(toServer.Reader, toClient.Writer);
        var token = _cts.Token;
        _connections.Add(Task.Run(async () =>
        {
            await using (server)
            {
                try
                {
                    await handler(server, token);
                }
                catch (OperationCanceledException)
                {
                    // Stopped.
                }
            }
        }, CancellationToken.None));

        return Task.FromResult<Stream?>(new PipeDuplexStream(toClient.Reader, toServer.Writer));
    }

    private sealed class PipeDuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _input = reader.AsStream();
        private readonly Stream _output = writer.AsStream();

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
            _input.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);

        public override void Flush() => _output.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _input.Dispose();
                _output.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
