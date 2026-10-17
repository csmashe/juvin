using Juvin.App.Services;
using Juvin.Platform;

namespace Juvin.App.Tests;

public sealed class TrayHostAvailabilityTests
{
    private readonly ScriptedProbe _probe = new();

    [Fact]
    public async Task AProbeThatCannotTellAtClose_KeepsTheTraySoTheWindowHides()
    {
        var availability = Started(true);
        var changes = 0;
        availability.Changed += (_, _) => changes++;
        _probe.Answer = null;

        Assert.True(await availability.RefreshAsync());
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task AnswersThatChange_UpdateAndRaiseOnce()
    {
        var availability = Started(false);
        var changes = 0;
        availability.Changed += (_, _) => changes++;

        _probe.Answer = true;
        Assert.True(await availability.RefreshAsync());
        Assert.True(await availability.RefreshAsync());
        Assert.Equal(1, changes);

        _probe.Answer = false;
        Assert.False(await availability.RefreshAsync());
        Assert.Equal(2, changes);
    }

    [Fact]
    public void AnUnknownAnswerAtStartup_CountsAsNoTray()
    {
        Assert.False(Started(null).IsAvailable);
    }

    private TrayHostAvailability Started(bool? answer)
    {
        _probe.Answer = answer;
        var availability = new TrayHostAvailability(_probe);
        availability.ProbeBlocking();
        return availability;
    }

    private sealed class ScriptedProbe : ITrayHostProbe
    {
        public bool? Answer { get; set; }

        public Task<bool?> IsTrayHostAvailableAsync(CancellationToken ct) => Task.FromResult(Answer);
    }
}
