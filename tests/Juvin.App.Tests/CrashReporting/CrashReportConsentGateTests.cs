using System.Net;
using Juvin.App.Services.CrashReporting;

namespace Juvin.App.Tests.CrashReporting;

public sealed class CrashReportConsentGateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TheGate_ForwardsOnlyWhileOpen(bool closed, bool synchronous)
    {
        using var handler = new RecordingHttpHandler();
        using var gate = new CrashReportConsentGate(handler);
        using var client = new HttpMessageInvoker(gate);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/");
        request.Content = new StringContent("report");
        if (closed)
        {
            gate.Close();
        }

        using var response = synchronous
            // ReSharper disable once MethodHasAsyncOverload -- this case exercises the synchronous send.
            ? client.Send(request, CancellationToken.None)
            : await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(!closed, gate.IsOpen);
        Assert.Equal(closed ? [] : ["report"], handler.Bodies);
    }
}
