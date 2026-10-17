namespace Juvin.Platform.Tests;

public sealed class LocalSocketClientExtensionsTests
{
    [Fact]
    public async Task SendWhenReady_NeverPassesANegativeTimeoutAndStopsAtTheBudget()
    {
        var client = new RefusingClient();

        var reply = await client.SendWhenReadyAsync("show", TimeSpan.FromMilliseconds(400));

        Assert.Null(reply);
        Assert.NotEmpty(client.Timeouts);
        Assert.All(client.Timeouts, timeout => Assert.True(timeout > TimeSpan.Zero));
    }

    [Fact]
    public async Task SendWhenReady_WithNoBudgetLeft_ReturnsNullWithoutSending()
    {
        var client = new RefusingClient();

        var reply = await client.SendWhenReadyAsync("show", TimeSpan.Zero);

        Assert.Null(reply);
        Assert.Empty(client.Timeouts);
    }

    private sealed class RefusingClient : ILocalSocketClient
    {
        public List<TimeSpan> Timeouts { get; } = [];

        public async Task<string?> SendAsync(string line, TimeSpan timeout)
        {
            Timeouts.Add(timeout);
            // Overrun the caller's retry delay so the next attempt starts past the deadline.
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            return null;
        }
    }
}
