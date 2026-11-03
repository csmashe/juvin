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

    private string ReloadMarker => Path.Join(_session.ConfigHome, "sway", ".juvin-reload-pending");

    private static string Block(string newline) =>
        $"{SentinelBlock.Open}{newline}bindsym --no-repeat Ctrl+Shift+f exec /opt/juvin/juvin --trigger{newline}{SentinelBlock.Close}";

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
    public async Task Write_PointsABindNamingAnOldBinaryAtTheRunningApp()
    {
        _session.WriteConfig("sway/config", UserConfig);
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(command: "/home/user/old-build/juvin --trigger"), CancellationToken.None);
        Assert.False(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        Assert.Equal(UserConfig + "\n" + Block("\n") + "\n", await File.ReadAllTextAsync(Config));
        Assert.Equal(2, _runner.Calls.Count);
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
        Assert.False(File.Exists(ReloadMarker));
    }

    [LinuxFact]
    public async Task WriteAndRemove_KeepABlankLastLine()
    {
        const string trailingBlank = "set $mod Mod4\nbindsym $mod+Return exec foot\n\n";
        _session.WriteConfig("sway/config", trailingBlank);

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);
        await _writer.RemoveAsync("juvin.rewrite", CancellationToken.None);

        Assert.Equal(trailingBlank, await File.ReadAllTextAsync(Config));
    }

    [LinuxFact]
    public async Task WriteAndRemove_KeepTheFilesCrlfLineEndings()
    {
        const string crlfConfig = "set $mod Mod4\r\nbindsym $mod+Return exec foot\r\n";
        _session.WriteConfig("sway/config", crlfConfig);

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);
        Assert.Equal(crlfConfig + "\r\n" + Block("\r\n") + "\r\n", await File.ReadAllTextAsync(Config));
        Assert.True(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));

        await _writer.RemoveAsync("juvin.rewrite", CancellationToken.None);
        Assert.Equal(crlfConfig, await File.ReadAllTextAsync(Config));
    }

    [LinuxFact]
    public async Task WriteAndRemove_KeepAMissingFinalNewlineMissing()
    {
        const string unterminated = "set $mod Mod4\nbindsym $mod+Return exec foot";
        _session.WriteConfig("sway/config", unterminated);

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);
        Assert.Equal(unterminated + "\n\n" + Block("\n"), await File.ReadAllTextAsync(Config));

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec("Ctrl+Alt+J"), CancellationToken.None);
        Assert.EndsWith(SentinelBlock.Close, await File.ReadAllTextAsync(Config), StringComparison.Ordinal);

        await _writer.RemoveAsync("juvin.rewrite", CancellationToken.None);
        Assert.Equal(unterminated, await File.ReadAllTextAsync(Config));
    }

    [LinuxFact]
    public async Task Write_KeepsTheConfigsPermissions()
    {
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead;
        _session.WriteConfig("sway/config", UserConfig);
        File.SetUnixFileMode(Config, mode);

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);
        Assert.Equal(mode, File.GetUnixFileMode(Config));

        await _writer.RemoveAsync("juvin.rewrite", CancellationToken.None);
        Assert.Equal(mode, File.GetUnixFileMode(Config));
    }

    [LinuxFact]
    public async Task Write_ACancelledReloadLeavesTheMarker_SoTheNextWriteReloads()
    {
        _session.WriteConfig("sway/config", UserConfig);
        using var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        _runner.Handler = (_, _) =>
        {
            // ReSharper disable once AccessToDisposedClosure -- the handler runs inside the awaited write
            cancel.Cancel();
            throw new OperationCanceledException(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _writer.WriteAsync(DesktopShortcutTestSession.Spec(), cancel.Token));
        Assert.True(File.Exists(ReloadMarker));
        Assert.False(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));

        _runner.Handler = null;
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        Assert.Equal(2, _runner.Calls.Count);
        Assert.False(File.Exists(ReloadMarker));
        Assert.True(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
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
