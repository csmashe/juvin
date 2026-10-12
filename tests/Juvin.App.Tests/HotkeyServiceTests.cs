using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.Core.Connector;
using Juvin.Core.Localization;
using Juvin.Platform.Hotkeys;

namespace Juvin.App.Tests;

public sealed class HotkeyServiceTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly ConnectorService _connector;
    private readonly FakeSettingsService _settings = new();

    public HotkeyServiceTests()
    {
        _connector = new ConnectorService(new FakeConnectorSocket(), new FakeNativeHostRegistrar(), _settings, new Localizer());
    }

    [Fact]
    public async Task Start_UsesTheFirstAvailableBackendInRegistrationOrder()
    {
        var unavailable = new FakeHotkeyBackend("evdev", isAvailable: false);
        var first = new FakeHotkeyBackend("sharphook");
        var second = new FakeHotkeyBackend("desktop-shortcut");
        var service = Create(unavailable, first, second);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(new HotkeyStatus(HotkeyStatusKind.Active, "sharphook"), service.Status);
        Assert.Equal("Ctrl+Shift+F", first.Chord?.ToString());
        Assert.Equal(0, unavailable.StartCount);
        Assert.Equal(0, second.StartCount);
        Assert.Equal(["sharphook", "desktop-shortcut"], service.AvailableBackends.Select(backend => backend.Id));
    }

    [Fact]
    public async Task Start_RemovesTheDesktopShortcutWhenAnotherBackendListens_AndKeepsItWhenItIsTheActiveOne()
    {
        var evdev = new FakeHotkeyBackend("evdev");
        var desktop = new FakeDesktopShortcutBackend(requiresSessionRestart: false);
        var service = Create(evdev, desktop);

        await service.StartAsync(CancellationToken.None);
        Assert.Equal("evdev", service.Status.BackendId);
        Assert.Equal(1, desktop.RemoveCount);

        _settings.Update(settings => settings with { HotkeyBackend = "desktop-shortcut" });

        await WaitUntilAsync(() => service.Status.BackendId == "desktop-shortcut");
        Assert.Equal(1, desktop.RemoveCount);
    }

    [Fact]
    public async Task RemoveDesktopShortcut_StopsListeningWhenItWasTheActiveBackend()
    {
        var desktop = new FakeDesktopShortcutBackend(requiresSessionRestart: false);
        var service = Create(desktop);
        await service.StartAsync(CancellationToken.None);

        await service.RemoveDesktopShortcutAsync(CancellationToken.None);

        Assert.Equal(1, desktop.RemoveCount);
        Assert.Equal(HotkeyStatus.Stopped, service.Status);

        _settings.Update(settings => settings with { RewriteShortcut = "Ctrl+Alt+J" });
        await WaitUntilAsync(() => service.Status.Kind == HotkeyStatusKind.Active);
    }

    [Fact]
    public async Task Start_PrefersTheChosenBackendWhenItIsAvailable()
    {
        var first = new FakeHotkeyBackend("evdev");
        var chosen = new FakeHotkeyBackend("desktop-shortcut");
        _settings.Update(settings => settings with { HotkeyBackend = "desktop-shortcut" });
        var service = Create(first, chosen);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal("desktop-shortcut", service.Status.BackendId);
        Assert.Equal(0, first.StartCount);
    }

    [Fact]
    public async Task Start_FallsBackToAutomaticWhenTheChosenBackendIsUnavailable()
    {
        _settings.Update(settings => settings with { HotkeyBackend = "evdev" });
        var service = Create(new FakeHotkeyBackend("evdev", isAvailable: false), new FakeHotkeyBackend("sharphook"));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal("sharphook", service.Status.BackendId);
    }

    [Fact]
    public async Task Start_TriesTheNextBackendWhenOneFails_AndReportsTheFirstFailure()
    {
        var failing = new FakeHotkeyBackend("evdev") { StartFailure = "No keyboard access." };
        var working = new FakeHotkeyBackend("desktop-shortcut");
        var service = Create(failing, working);

        await service.StartAsync(CancellationToken.None);
        Assert.Equal("desktop-shortcut", service.Status.BackendId);

        working.StartFailure = "No desktop.";
        _settings.Update(settings => settings with { RewriteShortcut = "Ctrl+Alt+J" });

        await WaitUntilAsync(() => service.Status.Kind == HotkeyStatusKind.Failed);
        Assert.Equal(new HotkeyStatus(HotkeyStatusKind.Failed, "evdev", "No keyboard access."), service.Status);
    }

    [Fact]
    public async Task Start_ReportsAChosenBackendThatFailsWithoutTryingOthers()
    {
        _settings.Update(settings => settings with { HotkeyBackend = "desktop-shortcut" });
        var other = new FakeHotkeyBackend("evdev");
        var service = Create(other, new FakeHotkeyBackend("desktop-shortcut") { StartFailure = "No desktop." });

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(new HotkeyStatus(HotkeyStatusKind.Failed, "desktop-shortcut", "No desktop."), service.Status);
        Assert.Equal(0, other.StartCount);
    }

    [Fact]
    public async Task Start_ReportsNoBackendAndInvalidShortcut()
    {
        var none = Create(new FakeHotkeyBackend("evdev", isAvailable: false));
        await none.StartAsync(CancellationToken.None);
        Assert.Equal(HotkeyStatusKind.NoBackend, none.Status.Kind);

        _settings.Update(settings => settings with { RewriteShortcut = "F" });
        var backend = new FakeHotkeyBackend("sharphook");
        var invalid = Create(backend);
        await invalid.StartAsync(CancellationToken.None);
        Assert.Equal(HotkeyStatusKind.InvalidShortcut, invalid.Status.Kind);
        Assert.Equal(0, backend.StartCount);
    }

    [Fact]
    public async Task ShortcutChange_RestartsWithTheNewChord_OtherChangesDoNot()
    {
        var backend = new FakeHotkeyBackend("sharphook");
        var service = Create(backend);
        await service.StartAsync(CancellationToken.None);

        _settings.Update(settings => settings with { CloseToTray = false });
        _settings.Update(settings => settings with { RewriteShortcut = "Ctrl+Alt+J" });

        await WaitUntilAsync(() => backend.StartCount == 2);
        Assert.Equal("Ctrl+Alt+J", backend.Chord?.ToString());
        Assert.True(backend.IsRunning);
    }

    [Fact]
    public async Task PhysicalKeyChange_RestartsWithTheRecordedKeyPosition()
    {
        var backend = new FakeHotkeyBackend("evdev");
        var service = Create(backend);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal("F", backend.Chord?.PhysicalKey);

        _settings.Update(settings => settings with { RewriteShortcutPhysicalKey = "R" });

        await WaitUntilAsync(() => backend.StartCount == 2);
        Assert.Equal("Ctrl+Shift+F", backend.Chord?.ToString());
        Assert.Equal("R", backend.Chord?.PhysicalKey);
    }

    [Fact]
    public async Task Stop_StopsTheBackendAndIgnoresLaterSettingsChanges()
    {
        var backend = new FakeHotkeyBackend("sharphook");
        var service = Create(backend);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync();
        _settings.Update(settings => settings with { RewriteShortcut = "Ctrl+Alt+J" });

        Assert.False(backend.IsRunning);
        Assert.Equal(1, backend.StartCount);
        Assert.Equal(HotkeyStatus.Stopped, service.Status);
    }

    [Fact]
    public async Task Press_RaisesPressedAndTriggersACapture()
    {
        var backend = new FakeHotkeyBackend("sharphook");
        var service = Create(backend);
        await service.StartAsync(CancellationToken.None);
        var pressed = 0;
        service.Pressed += () => pressed++;
        var captured = new TaskCompletionSource<ContextResult>();
        _connector.TriggerFailed += (_, result) => captured.TrySetResult(result);

        backend.Press();

        Assert.Equal(1, pressed);
        Assert.Equal(ConnectorService.NoBrowserCode, (await captured.Task.WaitAsync(s_timeout)).Error?.Code);
    }

    [Fact]
    public async Task Trigger_CountsOnlyWhileTheDesktopShortcutOrNothingListens()
    {
        var evdev = new FakeHotkeyBackend("evdev");
        var desktop = new FakeDesktopShortcutBackend(requiresSessionRestart: true);
        var service = Create(evdev, desktop);
        var presses = 0;
        service.Pressed += () => presses++;

        service.HandleTrigger();
        Assert.Equal(1, presses);

        await service.StartAsync(CancellationToken.None);
        service.HandleTrigger();
        Assert.Equal(1, presses);

        _settings.Update(settings => settings with { HotkeyBackend = "desktop-shortcut" });
        await WaitUntilAsync(() => service.Status.BackendId == "desktop-shortcut");
        service.HandleTrigger();
        Assert.Equal(2, presses);
    }

    [Fact]
    public async Task Press_FromAStoppedBackendIsIgnored()
    {
        var backend = new FakeHotkeyBackend("sharphook");
        var service = Create(backend);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync();
        var pressed = 0;
        service.Pressed += () => pressed++;

        backend.Press();

        Assert.Equal(0, pressed);
    }

    [Fact]
    public async Task Press_WhileSuspendedIsIgnored_AndWorksAgainAfterResuming()
    {
        var backend = new FakeHotkeyBackend("sharphook");
        var service = Create(backend);
        await service.StartAsync(CancellationToken.None);
        var pressed = 0;
        service.Pressed += () => pressed++;
        var captures = 0;
        var captured = new TaskCompletionSource();
        _connector.TriggerFailed += (_, _) =>
        {
            // ReSharper disable once AccessToModifiedClosure -- the outer scope only reads it with Volatile.Read after the handler ran
            Interlocked.Increment(ref captures);
            captured.TrySetResult();
        };

        service.Suspended = true;
        backend.Press();
        service.HandlePress();
        Assert.Equal(0, pressed);

        service.Suspended = false;
        backend.Press();

        await captured.Task.WaitAsync(s_timeout);
        Assert.Equal(1, pressed);
        Assert.Equal(1, Volatile.Read(ref captures));
    }

    private HotkeyService Create(params IHotkeyBackend[] backends) => new(backends, _settings, _connector);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }
}
