using System.Net;
using System.Text;

namespace Juvin.Core.Tests.Fakes;

/// <summary>Answers requests by path with canned responses and records what was sent.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<string?, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<(string Path, string? Body)> Requests { get; } = [];

    public FakeHttpHandler Route(string path, string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[path] = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8),
        };
        return this;
    }

    /// <summary>Answers from the request body; a null answer is a 404.</summary>
    public FakeHttpHandler RouteByBody(string path, Func<string, string?> respond)
    {
        _routes[path] = body =>
            respond(body ?? "") is { } content
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
        return this;
    }

    /// <summary>Sends the headers, then a body that never produces a byte.</summary>
    public FakeHttpHandler Stall(string path)
    {
        _routes[path] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        };
        return this;
    }

    /// <summary>Sends each chunk after a pause, so the body outlasts a timeout without ever stalling for it.</summary>
    public FakeHttpHandler Trickle(string path, TimeSpan pause, params string[] chunks)
    {
        _routes[path] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TricklingStream(pause, chunks)),
        };
        return this;
    }

    public FakeHttpHandler Fail(string path, Exception exception)
    {
        _routes[path] = _ => throw exception;
        return this;
    }

    public string BodyFor(string path) => Requests.Last(r => r.Path == path).Body ?? "";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((path, body));
        return _routes.TryGetValue(path, out var respond)
            ? respond(body)
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
    }

    private sealed class TricklingStream : Stream
    {
        private readonly Queue<byte[]> _chunks;
        private readonly TimeSpan _pause;
        private ReadOnlyMemory<byte> _remainder;

        public TricklingStream(TimeSpan pause, IEnumerable<string> chunks)
        {
            _pause = pause;
            _chunks = new Queue<byte[]>(chunks.Select(Encoding.UTF8.GetBytes));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remainder.IsEmpty)
            {
                if (_chunks.Count == 0)
                {
                    return 0;
                }

                await Task.Delay(_pause, cancellationToken);
                _remainder = _chunks.Dequeue();
            }

            var count = Math.Min(buffer.Length, _remainder.Length);
            _remainder[..count].CopyTo(buffer);
            _remainder = _remainder[count..];
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
