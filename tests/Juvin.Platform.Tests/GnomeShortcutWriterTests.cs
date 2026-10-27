using System.Runtime.Versioning;
using Juvin.Platform.Linux.Hotkeys.DesktopShortcut;
using Juvin.Platform.Tests.Fakes;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class GnomeShortcutWriterTests : IDisposable
{
    private const string ListKey = "org.gnome.settings-daemon.plugins.media-keys custom-keybindings";
    private const string JuvinPath = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/juvin-rewrite/";
    private const string EntrySchema = "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:" + JuvinPath;

    private readonly FakeGsettings _gsettings = new();
    private readonly DesktopShortcutTestSession _session = new();
    private readonly GnomeShortcutWriter _writer;

    public GnomeShortcutWriterTests()
    {
        _writer = new GnomeShortcutWriter(_gsettings, _session.Session);
    }

    public void Dispose() => _session.Dispose();

    [LinuxFact]
    public async Task Write_KeepsTheUsersShortcuts_AndSetsTheEntry()
    {
        _gsettings.Values[ListKey] = "['/org/gnome/custom0/', '/org/gnome/it\\'s/']";

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        Assert.Equal(["/org/gnome/custom0/", "/org/gnome/it's/", JuvinPath], GnomeShortcutWriter.ParseList(_gsettings.Values[ListKey]));
        Assert.Equal("'<Control><Shift>f'", _gsettings.Values[EntrySchema + " binding"]);
        Assert.Equal("'/opt/juvin/juvin --trigger'", _gsettings.Values[EntrySchema + " command"]);
        Assert.Equal("'Juvin: Rewrite draft'", _gsettings.Values[EntrySchema + " name"]);
        Assert.True(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
    }

    [LinuxFact]
    public async Task Write_Twice_ListsTheEntryOnce()
    {
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        Assert.Equal([JuvinPath], GnomeShortcutWriter.ParseList(_gsettings.Values[ListKey]));
    }

    [LinuxFact]
    public async Task IsInstalled_IsFalseForAChangedShortcut()
    {
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        Assert.False(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec("Ctrl+Alt+F"), CancellationToken.None));
    }

    [LinuxFact]
    public async Task Write_PointsAnEntryNamingAnOldBinaryAtTheRunningApp()
    {
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(command: "/home/user/old-build/juvin --trigger"), CancellationToken.None);
        Assert.False(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        Assert.Equal("'/opt/juvin/juvin --trigger'", _gsettings.Values[EntrySchema + " command"]);
        Assert.Equal([JuvinPath], GnomeShortcutWriter.ParseList(_gsettings.Values[ListKey]));
        Assert.True(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
    }

    [LinuxFact]
    public async Task Write_RefusesAListItCannotParse()
    {
        _gsettings.Values[ListKey] = "['/org/gnome/custom0/' oops";

        await Assert.ThrowsAsync<FormatException>(() => _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));

        Assert.Equal(0, _gsettings.SetCount);
    }

    [LinuxFact]
    public async Task Remove_DropsOnlyJuvinsEntry()
    {
        _gsettings.Values[ListKey] = "['/org/gnome/custom0/']";
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        await _writer.RemoveAsync("juvin.rewrite", CancellationToken.None);

        Assert.Equal(["/org/gnome/custom0/"], GnomeShortcutWriter.ParseList(_gsettings.Values[ListKey]));
        Assert.False(_gsettings.Values.ContainsKey(EntrySchema + " command"));
    }

    [LinuxFact]
    public void IsCurrentDesktop_NeedsGnomeAndGsettings()
    {
        _session.With("XDG_CURRENT_DESKTOP", "ubuntu:GNOME");
        Assert.False(_writer.IsCurrentDesktop());

        _session.WithTool("gsettings");
        Assert.True(_writer.IsCurrentDesktop());
    }

    [Theory]
    [InlineData("@as []", new string[0])]
    [InlineData("['/a/', \"/b/\"]", new[] { "/a/", "/b/" })]
    public void ParseList_ReadsGsettingsOutput(string raw, string[] expected)
    {
        Assert.Equal(expected, GnomeShortcutWriter.ParseList(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("['/a/'")]
    [InlineData("[/a/]")]
    [InlineData("['/a\\n/']")]
    public void ParseList_RejectsAnythingItCannotRoundTrip(string raw)
    {
        Assert.Throws<FormatException>(() => GnomeShortcutWriter.ParseList(raw));
    }
}
