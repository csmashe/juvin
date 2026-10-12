using System.Runtime.Versioning;
using Juvin.Core.Hotkeys;
using Juvin.Core.Localization;
using Juvin.Platform.Hotkeys;
using Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class DesktopShortcutHotkeyBackendTests
{
    private static KeyChord Chord(string text)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        return chord;
    }

    [Theory]
    [InlineData("Ctrl+Shift+F", "<Control><Shift>f", "Ctrl+Shift+F", "CTRL SHIFT", "F", "Ctrl+Shift+f")]
    [InlineData("Super+Alt+Space", "<Alt><Super>space", "Alt+Meta+Space", "ALT SUPER", "space", "Alt+Mod4+space")]
    [InlineData("Ctrl+F5", "<Control>F5", "Ctrl+F5", "CTRL", "F5", "Ctrl+F5")]
    [InlineData("Ctrl+PageDown", "<Control>Page_Down", "Ctrl+PgDown", "CTRL", "Page_Down", "Ctrl+Page_Down")]
    [InlineData("Ctrl+Slash", "<Control>slash", "Ctrl+/", "CTRL", "slash", "Ctrl+slash")]
    [InlineData("Ctrl+7", "<Control>7", "Ctrl+7", "CTRL", "7", "Ctrl+7")]
    public void Triggers_UseEachDesktopsSyntax(string chord, string gnome, string kde, string hyprMods, string hyprKey, string sway)
    {
        var parsed = Chord(chord);

        Assert.Equal(gnome, DesktopTriggers.Gnome(parsed));
        Assert.Equal(kde, DesktopTriggers.Kde(parsed));
        Assert.Equal((hyprMods, hyprKey), DesktopTriggers.Hyprland(parsed));
        Assert.Equal(sway, DesktopTriggers.Sway(parsed));
    }

    [Theory]
    [InlineData("/opt/juvin/juvin", "/opt/juvin/juvin --trigger")]
    [InlineData("/home/a b/Juvin/juvin", "\"/home/a b/Juvin/juvin\" --trigger")]
    [InlineData("/home/$x/juvin", "\"/home/\\$x/juvin\" --trigger")]
    [InlineData("/usr/lib/dotnet/dotnet", "juvin --trigger")]
    [InlineData(null, "juvin --trigger")]
    public void TriggerCommand_QuotesThePathAndFallsBackUnderTheDotnetHost(string? processPath, string expected)
    {
        Assert.Equal(expected, DesktopShortcutHotkeyBackend.TriggerCommand(processPath));
    }

    [LinuxFact]
    public async Task Start_WritesOnlyWhenNotInstalled_AndStopLeavesTheShortcut()
    {
        var writer = new RecordingWriter("hyprland", current: true);
        var backend = new DesktopShortcutHotkeyBackend([new RecordingWriter("gnome", current: false), writer], new Localizer(), "/opt/juvin/juvin");

        await backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None);
        await backend.StopAsync();
        await backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None);

        Assert.True(backend.IsAvailable);
        Assert.Equal("hyprland", backend.DesktopId);
        var spec = Assert.Single(writer.Written);
        Assert.Equal(new DesktopShortcutSpec("juvin.rewrite", "Juvin: Rewrite draft", Chord("Ctrl+Shift+F"), "/opt/juvin/juvin --trigger"), spec);
        Assert.Equal(0, writer.Removed);
    }

    [LinuxFact]
    public async Task Start_ReportsAFailedReload_AndWritesAgainNextTime()
    {
        var writer = new RecordingWriter("hyprland", current: true) { ReloadFails = true };
        var backend = new DesktopShortcutHotkeyBackend([writer], new Localizer(), "/opt/juvin/juvin");

        var failure = await Assert.ThrowsAsync<HotkeyBackendException>(() => backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None));
        writer.ReloadFails = false;
        await backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None);
        await backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None);

        Assert.Contains("hyprctl reload", failure.Message);
        Assert.Equal(2, writer.Written.Count);
    }

    [LinuxFact]
    public async Task Remove_RemovesThroughTheCurrentWriter()
    {
        var writer = new RecordingWriter("kde", current: true);
        var backend = new DesktopShortcutHotkeyBackend([writer], new Localizer(), "/opt/juvin/juvin");

        await backend.RemoveAsync(CancellationToken.None);

        Assert.Equal(1, writer.Removed);
        Assert.True(backend.RequiresSessionRestartToApply);
    }

    [LinuxFact]
    public async Task Start_ReportsWriterFailuresAndMissingDesktops()
    {
        var failing = new DesktopShortcutHotkeyBackend([new RecordingWriter("sway", current: true) { Failure = new IOException("disk full") }], new Localizer(), null);
        var none = new DesktopShortcutHotkeyBackend([new RecordingWriter("sway", current: false)], new Localizer(), null);

        var failure = await Assert.ThrowsAsync<HotkeyBackendException>(() => failing.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None));
        await Assert.ThrowsAsync<HotkeyBackendException>(() => none.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None));

        Assert.Contains("disk full", failure.Message);
        Assert.False(none.IsAvailable);
        Assert.Equal("", none.DesktopId);
    }

    private sealed class RecordingWriter(string desktopId, bool current) : IDesktopShortcutWriter
    {
        public List<DesktopShortcutSpec> Written { get; } = [];

        public int Removed { get; private set; }

        public Exception? Failure { get; init; }

        public string DesktopId => desktopId;

        public bool RequiresSessionRestartToApply => desktopId == "kde";

        public bool IsCurrentDesktop() => current;

        /// <summary>Like a compositor writer whose reload failed: the file is right but a reload is still owed.</summary>
        public bool ReloadFails { get; set; }

        private bool ReloadPending { get; set; }

        public Task<bool> IsInstalledAsync(DesktopShortcutSpec spec, CancellationToken ct) => Task.FromResult(Written.Contains(spec) && !ReloadPending);

        public Task WriteAsync(DesktopShortcutSpec spec, CancellationToken ct)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Written.Add(spec);
            ReloadPending = ReloadFails;
            return ReloadFails ? throw new DesktopReloadException("hyprctl") : Task.CompletedTask;
        }

        public Task RemoveAsync(string id, CancellationToken ct)
        {
            Removed++;
            return Task.CompletedTask;
        }
    }
}
