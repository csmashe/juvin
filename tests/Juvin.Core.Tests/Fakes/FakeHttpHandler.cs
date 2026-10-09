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
