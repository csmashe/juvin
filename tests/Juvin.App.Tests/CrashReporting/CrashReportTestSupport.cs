using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;

namespace Juvin.App.Tests.CrashReporting;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SentrySdkCollection
{
    public const string Name = "SentrySdk";
}

/// <summary>Records each request body, gunzipped, in place of the network.</summary>
internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    private volatile bool _disposed;

    public ConcurrentQueue<string> Bodies { get; } = new();

    public bool IsDisposed => _disposed;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Like HttpClientHandler, a disposed handler refuses to send.
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.Content is not { } content)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        await using var body = content.Headers.ContentEncoding.Contains("gzip")
            ? new GZipStream(stream, CompressionMode.Decompress)
            : stream;
        using var reader = new StreamReader(body);
        Bodies.Enqueue(await reader.ReadToEndAsync(cancellationToken));
        return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return SendAsync(request, cancellationToken).GetAwaiter().GetResult();
    }
}
