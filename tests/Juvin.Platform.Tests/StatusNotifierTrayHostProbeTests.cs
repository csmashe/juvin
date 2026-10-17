using System.ComponentModel;
using System.Runtime.Versioning;
using Juvin.Core.Providers;
using Juvin.Platform.Linux.Tray;
using Juvin.Platform.Tests.Fakes;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class StatusNotifierTrayHostProbeTests
{
    private readonly FakeProcessRunner _runner = new();

    private StatusNotifierTrayHostProbe Create() => new(_runner, TimeSpan.FromMilliseconds(50));

    [LinuxFact]
    public async Task ARegisteredHost_IsAvailable()
    {
        _runner.Handler = (_, _) => new ProcessResult(0, "(<true>,)\n", "");

        Assert.True(await Create().IsTrayHostAvailableAsync(CancellationToken.None));

        var call = Assert.Single(_runner.Calls);
        Assert.Equal("gdbus", call.FileName);
        Assert.Equal(["org.kde.StatusNotifierWatcher", "IsStatusNotifierHostRegistered"], call.Arguments.TakeLast(2));
    }

    [LinuxFact]
    public async Task NoRegisteredHost_IsUnavailable()
    {
        _runner.Handler = (_, _) => new ProcessResult(0, "(<false>,)\n", "");

        Assert.False(await Create().IsTrayHostAvailableAsync(CancellationToken.None));
    }

    [LinuxTheory]
    [InlineData("Error: GDBus.Error:org.freedesktop.DBus.Error.ServiceUnknown: The name org.kde.StatusNotifierWatcher was not provided by any .service files")]
    [InlineData("Error: GDBus.Error:org.freedesktop.DBus.Error.NameHasNoOwner: Could not get owner of name")]
    public async Task NoWatcherOnTheBus_IsUnavailable(string error)
    {
        _runner.Handler = (_, _) => new ProcessResult(1, "", error);

        Assert.False(await Create().IsTrayHostAvailableAsync(CancellationToken.None));
    }

    [LinuxFact]
    public async Task AnyOtherGdbusFailure_IsUnknown()
    {
        _runner.Handler = (_, _) => new ProcessResult(1, "", "Error: Timeout was reached");

        Assert.Null(await Create().IsTrayHostAvailableAsync(CancellationToken.None));
    }

    [LinuxFact]
    public async Task MissingGdbus_IsUnknown()
    {
        _runner.Handler = (_, _) => throw new Win32Exception(2);

        Assert.Null(await Create().IsTrayHostAvailableAsync(CancellationToken.None));
    }

    [LinuxFact]
    public async Task AHungBus_TimesOutAsUnknown()
    {
        _runner.NeverCompletes = true;

        Assert.Null(await Create().IsTrayHostAvailableAsync(CancellationToken.None));
    }

    [LinuxFact]
    public async Task CallerCancellation_IsNotAnAnswer()
    {
        _runner.NeverCompletes = true;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().IsTrayHostAvailableAsync(cancelled.Token));
    }
}
