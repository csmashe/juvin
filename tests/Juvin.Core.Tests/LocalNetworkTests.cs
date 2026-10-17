using System.Net;
using Juvin.Core.Providers;

namespace Juvin.Core.Tests;

public sealed class LocalNetworkTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.3.20", true)]
    [InlineData("100.100.1.2", true)]
    [InlineData("172.16.0.4", true)]
    [InlineData("192.168.1.5", true)]
    [InlineData("169.254.10.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("203.0.113.10", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:203.0.113.10", false)]
    public void IsLocalAddress_AllowsOnlyThisMachineAndPrivateRanges(string address, bool expected)
    {
        Assert.Equal(expected, LocalNetwork.IsLocalAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task ModelHttpClient_RefusesAPublicAddressBeforeConnecting()
    {
        using var http = ModelHttpClient.Create(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("http://203.0.113.10:1/"));

        var refusal = ex as OutsideLocalNetworkException ?? Assert.IsType<OutsideLocalNetworkException>(ex.InnerException);
        Assert.Equal("The local model server address resolves to an address outside this machine and your local network.", refusal.Message);
    }

    [Fact]
    public async Task ModelHttpClient_ConnectsToAnIPv4MappedAddress()
    {
        using var server = new Fakes.LoopbackHttpServer(() => new Fakes.LoopbackResponse(HttpStatusCode.NoContent));
        using var http = ModelHttpClient.Create(TimeSpan.FromSeconds(30));

        using var response = await http.GetAsync(server.Url.Replace("127.0.0.1", "[::ffff:127.0.0.1]"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ModelHttpClient_ConnectsToLocalhost()
    {
        using var server = new Fakes.LoopbackHttpServer(() => new Fakes.LoopbackResponse(HttpStatusCode.NoContent));
        using var http = ModelHttpClient.Create(TimeSpan.FromSeconds(30));

        using var response = await http.GetAsync(server.Url.Replace("127.0.0.1", "localhost"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
