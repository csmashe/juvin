using System.Runtime.Versioning;
using Juvin.Platform.Linux.Hotkeys.DesktopShortcut;
using Juvin.Platform.Tests.Fakes;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class HyprlandShortcutWriterTests : IDisposable
{
    private readonly FakeProcessRunner _runner = new();
    private readonly DesktopShortcutTestSession _session = new();
    private readonly HyprlandShortcutWriter _writer;

    public HyprlandShortcutWriterTests()
    {
        _writer = new HyprlandShortcutWriter(_runner, _session.Session);
    }

    private string MainConfig => Path.Join(_session.ConfigHome, "hypr", "hyprland.conf");

    private string JuvinConfig => Path.Join(_session.ConfigHome, "hypr", "juvin.conf");

    public void Dispose() => _session.Dispose();

    [LinuxFact]
    public async Task Write_AddsTheBindAndSourcesItOnce_ThenReloads()
    {
        _session.WriteConfig("hypr/hyprland.conf", "bind = SUPER, Q, exec, kitty");

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        Assert.Equal($"bind = SUPER, Q, exec, kitty\nsource = {JuvinConfig}\n", await File.ReadAllTextAsync(MainConfig));
        Assert.Equal(
            [SentinelBlock.Open, "bind = CTRL SHIFT, F, exec, /opt/juvin/juvin --trigger", SentinelBlock.Close],
            await File.ReadAllLinesAsync(JuvinConfig)
        );
        var (fileName, arguments) = Assert.Single(_runner.Calls);
        Assert.Equal("hyprctl", fileName);
        Assert.Equal(["reload"], arguments);
        Assert.True(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
    }

    [LinuxFact]
    public async Task Write_ReportsAFailedReload_AndReloadsAgainOnTheNextWriteEvenWhenUnchanged()
    {
        _session.WriteConfig("hypr/hyprland.conf", "bind = SUPER, Q, exec, kitty");
        _runner.ExitCode = 1;

        await Assert.ThrowsAsync<DesktopReloadException>(() => _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
        Assert.False(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));

        _runner.ExitCode = 0;
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        Assert.Equal(2, _runner.Calls.Count);
        Assert.True(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
        Assert.False(File.Exists(Path.Join(_session.ConfigHome, "hypr", "juvin.conf.reload-pending")));
    }

    [LinuxFact]
    public async Task Write_ReplacesTheBlockWhenTheShortcutChanges()
    {
        _session.WriteConfig("hypr/hyprland.conf", "");
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec("Super+Alt+J"), CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(JuvinConfig);
        Assert.Equal("bind = ALT SUPER, J, exec, /opt/juvin/juvin --trigger", Assert.Single(lines, line => line.StartsWith("bind", StringComparison.Ordinal)));
        Assert.Equal(2, _runner.Calls.Count);
    }

    [LinuxFact]
    public async Task Write_WithoutAHyprlandConfig_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));

        Assert.False(File.Exists(MainConfig));
    }

    [LinuxFact]
    public async Task Remove_EmptiesTheBlockButKeepsTheSourcedFile()
    {
        _session.WriteConfig("hypr/hyprland.conf", "");
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        await _writer.RemoveAsync("juvin.rewrite", CancellationToken.None);

        Assert.Equal("", await File.ReadAllTextAsync(JuvinConfig));
        Assert.False(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
        Assert.Equal(2, _runner.Calls.Count);
    }

    [LinuxFact]
    public void IsCurrentDesktop_NeedsTheSessionAndHyprctl()
    {
        _session.With("HYPRLAND_INSTANCE_SIGNATURE", "abc");
        Assert.False(_writer.IsCurrentDesktop());

        _session.WithTool("hyprctl");

        Assert.True(_writer.IsCurrentDesktop());
    }
}
