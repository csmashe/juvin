namespace Juvin.Core.Providers;

/// <summary>
///     The HTTP client for model servers. Redirects are not followed, so a server cannot forward
///     the prompt to another host; a 3xx reply fails like any other non-success status.
/// </summary>
public static class ModelHttpClient
{
    public static HttpClient Create(TimeSpan timeout) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = timeout };
}
