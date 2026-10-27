using System.Net;
using Juvin.Core.Tests.Fakes;

namespace Juvin.Core.Tests;

public sealed class LoopbackHttpServerTests
{
    [Fact]
    public void Dispose_BeforeTheFirstAccept_DoesNotThrow()
    {
        for (var i = 0; i < 200; i++)
        {
            var server = new LoopbackHttpServer(() => new LoopbackResponse(HttpStatusCode.NoContent));
            server.Dispose();
            server.Dispose();
        }
    }
}
