using System.Runtime.Versioning;
using Juvin.Platform.Linux.Hotkeys.DesktopShortcut;
using Juvin.Platform.Tests.Fakes;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class SwayShortcutWriterTests : IDisposable
{
    private const string UserConfig = "set $mod Mod4\nbindsym $mod+Return exec foot\n";

    private readonly FakeProcessRunner _runner = new();
    private readonly DesktopShortcutTestSession _session = new();
    private readonly SwayShortcutWriter _writer;

    public SwayShortcutWriterTests()
    {
        _writer = new SwayShortcutWriter(_runner, _session.Session);
    }

    private string Config => Path.Join(_session.ConfigHome, "sway", "config");

    public void Dispose() => _session.Dispose();

    [LinuxFact]
    public async Task Write_AppendsTheBlockOnce_AndReloadsSway()
    {
        _session.WriteConfig("sway/config", UserConfig);

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        Assert.Equal(
            UserConfig + $"\n{SentinelBlock.Open}\nbindsym --no-repeat Ctrl+Shift+f exec /opt/juvin/juvin --trigger\n{SentinelBlock.Close}\n",
            await File.ReadAllTextAsync(Config)
        );
        var (fileName, arguments) = Assert.Single(_runner.Calls);
        Assert.Equal("swaymsg", fileName);
        Assert.Equal(["reload"], arguments);
        Assert.True(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
    }

    [LinuxFact]
    public async Task Remove_RestoresTheUsersConfig()
    {
        _session.WriteConfig("sway/config", UserConfig);
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        await _writer.RemoveAsync("juvin.rewrite", CancellationToken.None);

        Assert.Equal(UserConfig, await File.ReadAllTextAsync(Config));
        Assert.Equal(2, _runner.Calls.Count);
    }

    [LinuxFact]
    public async Task Remove_ReloadsAgainWhenTheEarlierReloadFailed()
    {
        _session.WriteConfig("sway/config", UserConfig);
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);
        _runner.ExitCode = 1;

        await Assert.ThrowsAsync<DesktopReloadException>(() => _writer.RemoveAsync("juvin.rewrite", CancellationToken.None));
        _runner.ExitCode = 0;
        await _writer.RemoveAsync("juvin.rewrite", CancellationToken.None);

        Assert.Equal(UserConfig, await File.ReadAllTextAsync(Config));
        Assert.Equal(3, _runner.Calls.Count);
        Assert.False(File.Exists(Path.Join(_session.ConfigHome, "sway", ".juvin-reload-pending")));
    }

    [LinuxFact]
    public async Task Write_RefusesUnbalancedMarkers()
    {
        _session.WriteConfig("sway/config", UserConfig + SentinelBlock.Open + "\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));

        Assert.Empty(_runner.Calls);
    }

    [LinuxFact]
    public async Task Write_WithoutAUserConfig_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
    }

    [LinuxFact]
    public async Task Write_GoesThroughASymlinkedConfig()
    {
        var target = _session.WriteConfig("dotfiles/sway", UserConfig);
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        File.CreateSymbolicLink(Config, target);

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        Assert.NotNull(new FileInfo(Config).LinkTarget);
        Assert.Contains("bindsym --no-repeat Ctrl+Shift+f", await File.ReadAllTextAsync(target));
    }
}
