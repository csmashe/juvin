using System.Net;

namespace Juvin.App.Services.CrashReporting;

/// <remarks>
///     Starts open because <see cref="CrashReportingService" /> constructs a gate only once consent is present.
///     <paramref name="disposeInner" /> is false for an injected handler, so a later gate can wrap it again.
/// </remarks>
internal sealed class CrashReportConsentGate(HttpMessageHandler inner, bool disposeInner = true) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _inner = new(inner, disposeInner);
    private volatile bool _open = true;

    public bool IsOpen => _open;

    public void Close() => _open = false;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _open ? _inner.SendAsync(request, cancellationToken) : Task.FromResult(Discard(request));
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _open ? _inner.Send(request, cancellationToken) : Discard(request);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private static HttpResponseMessage Discard(HttpRequestMessage request)
    {
        // Acknowledge queued reports locally so opting out cannot flush them.
        request.Content?.Dispose();
        return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
    }
}
