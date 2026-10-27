using System.Runtime.Versioning;
using Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class KdeShortcutWriterTests : IDisposable
{
    private readonly DesktopShortcutTestSession _session = new();
    private readonly KdeShortcutWriter _writer;

    public KdeShortcutWriterTests()
    {
        _writer = new KdeShortcutWriter(_session.Session);
    }

    private string EntryPath => Path.Join(_session.DataHome, "kglobalaccel", "juvin-rewrite.desktop");

    public void Dispose() => _session.Dispose();

    [LinuxFact]
    public async Task Write_CreatesAServiceEntryWithTheShortcut()
    {
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(command: "\"/home/a b/100%juvin\" --trigger"), CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(EntryPath);
        Assert.Contains("Type=Service", lines);
        Assert.Contains("Name=Juvin: Rewrite draft", lines);
        Assert.Contains("Exec=\"/home/a b/100%%juvin\" --trigger", lines);
        Assert.Contains("X-KDE-Shortcuts=Ctrl+Shift+F", lines);
        Assert.True(_writer.RequiresSessionRestartToApply);
    }

    [LinuxFact]
    public async Task Write_IsIdempotent_AndIsInstalledTracksTheSpec()
    {
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);
        var first = await File.ReadAllTextAsync(EntryPath);
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        Assert.Equal(first, await File.ReadAllTextAsync(EntryPath));
        Assert.True(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
        Assert.False(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec("Ctrl+Alt+F"), CancellationToken.None));
    }

    [LinuxFact]
    public async Task Write_PointsAnEntryNamingAnOldBinaryAtTheRunningApp()
    {
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(command: "/home/user/old-build/juvin --trigger"), CancellationToken.None);
        Assert.False(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));

        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(EntryPath);
        Assert.Contains("Exec=/opt/juvin/juvin --trigger", lines);
        Assert.DoesNotContain(lines, line => line.Contains("old-build", StringComparison.Ordinal));
        Assert.True(await _writer.IsInstalledAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
    }

    [LinuxFact]
    public async Task Write_LeavesAFileJuvinDidNotWrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        await File.WriteAllTextAsync(EntryPath, "[Desktop Entry]\nType=Service\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None));
        await _writer.RemoveAsync("juvin.rewrite", CancellationToken.None);

        Assert.True(File.Exists(EntryPath));
    }

    [LinuxFact]
    public async Task Remove_DeletesTheEntry()
    {
        await _writer.WriteAsync(DesktopShortcutTestSession.Spec(), CancellationToken.None);

        await _writer.RemoveAsync("juvin.rewrite", CancellationToken.None);

        Assert.False(File.Exists(EntryPath));
    }

    [LinuxFact]
    public void IsCurrentDesktop_RecognisesPlasma()
    {
        Assert.False(_writer.IsCurrentDesktop());

        _session.With("XDG_CURRENT_DESKTOP", "KDE");

        Assert.True(_writer.IsCurrentDesktop());
    }
}
