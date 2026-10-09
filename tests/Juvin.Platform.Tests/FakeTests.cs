using Juvin.Platform.Tests.Fakes;

namespace Juvin.Platform.Tests;

public sealed class FakeTests
{
    [Fact]
    public async Task FakeLocalSocket_RoutesClientRequestsToTheHandler()
    {
        var socket = new FakeLocalSocket();
        Assert.Null(await socket.SendAsync("{}", TimeSpan.FromSeconds(1)));

        await socket.StartAsync((line, _) => Task.FromResult("re:" + line), CancellationToken.None);

        Assert.True(socket.IsLivePeer());
        Assert.Equal("re:{}", await socket.SendAsync("{}", TimeSpan.FromSeconds(1)));
        Assert.Equal(["{}"], socket.ReceivedLines);
    }

    [Fact]
    public async Task FakeSecretStore_RoundTrips()
    {
        var store = new FakeSecretStore();

        await store.SetAsync("token", "value");

        Assert.Equal("value", await store.GetAsync("token"));
    }
}
