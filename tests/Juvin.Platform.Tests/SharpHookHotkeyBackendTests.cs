using Juvin.Core.Hotkeys;
using Juvin.Core.Localization;
using Juvin.Platform.Hotkeys;
using Juvin.Platform.Tests.Fakes;
using SharpHook.Data;

namespace Juvin.Platform.Tests;

// No native hook is started here: hooks run against a fake libuiohook.
public sealed class SharpHookHotkeyBackendTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    // ModifiersOf reads Command as the chord's Ctrl on macOS.
    private static readonly EventMask s_ctrl = OperatingSystem.IsMacOS() ? EventMask.LeftMeta : EventMask.LeftCtrl;

    [Fact]
    public async Task Start_AfterAStop_WaitsForTheOldHookToEnd_SoTheNewOneReportsEnabled()
    {
        var provider = new FakeGlobalHookProvider();
        var backend = new SharpHookHotkeyBackend(new Localizer(), provider);
        var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Pressed += () => pressed.TrySetResult();

        await backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None);
        await backend.StopAsync();
        await backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None);

        Assert.Equal(2, provider.Runs);
        provider.Press(KeyCode.VcF, s_ctrl | EventMask.LeftShift);
        await pressed.Task.WaitAsync(s_timeout);
        await backend.StopAsync();
    }

    [Fact]
    public async Task Start_WaitsForAnOldHookThatOutlivedTheStopTimeout()
    {
        var provider = new FakeGlobalHookProvider { TeardownDelay = TimeSpan.FromMilliseconds(1500) };
        var backend = new SharpHookHotkeyBackend(new Localizer(), provider);
        var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Pressed += () => pressed.TrySetResult();

        await backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None);
        await backend.StopAsync();
        await backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None);

        provider.Press(KeyCode.VcF, s_ctrl | EventMask.LeftShift);
        await pressed.Task.WaitAsync(s_timeout);
        await backend.StopAsync();
    }

    [Fact]
    public async Task Start_RefusesWhileAnOldHookIsStillEnding()
    {
        var provider = new FakeGlobalHookProvider { TeardownDelay = TimeSpan.FromMilliseconds(3500) };
        var backend = new SharpHookHotkeyBackend(new Localizer(), provider);

        await backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None);
        await backend.StopAsync();

        await Assert.ThrowsAsync<HotkeyBackendException>(() => backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None));
        Assert.Equal(1, provider.Runs);
    }

    [Fact]
    public async Task ChangeChord_SwitchesTheChordWithoutRestartingTheHook()
    {
        var provider = new FakeGlobalHookProvider();
        var backend = new SharpHookHotkeyBackend(new Localizer(), provider);
        var presses = 0;
        var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Pressed += () =>
        {
            // ReSharper disable once AccessToModifiedClosure -- the handler counts presses into the local on purpose
            Interlocked.Increment(ref presses);
            pressed.TrySetResult();
        };
        await backend.StartAsync(Chord("Ctrl+Shift+F"), CancellationToken.None);

        backend.ChangeChord(Chord("Ctrl+Alt+J"));
        provider.Press(KeyCode.VcF, s_ctrl | EventMask.LeftShift);
        provider.Release(KeyCode.VcF);
        provider.Press(KeyCode.VcJ, s_ctrl | EventMask.LeftAlt);

        await pressed.Task.WaitAsync(s_timeout);
        // The F press would have been dispatched before the J press; give a stray one time to land.
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref presses));
        Assert.Equal(1, provider.Runs);
        await backend.StopAsync();
    }

    [Fact]
    public void KeyCodes_MapEveryChordKeyToADistinctCode()
    {
        var codes = SharpHookHotkeyBackend.KeyCodes;

        Assert.Equal(KeyChord.KeyNames.Order(), codes.Keys.Order());
        Assert.Equal(codes.Count, codes.Values.Distinct().Count());
        Assert.DoesNotContain(KeyCode.VcUndefined, codes.Values);
    }

    [Theory]
    [InlineData("F", KeyCode.VcF)]
    [InlineData("7", KeyCode.Vc7)]
    [InlineData("F13", KeyCode.VcF13)]
    [InlineData("Equal", KeyCode.VcEquals)]
    [InlineData("BracketRight", KeyCode.VcCloseBracket)]
    [InlineData("Backquote", KeyCode.VcBackQuote)]
    public void KeyCodes_UseSharpHookNames(string key, KeyCode expected)
    {
        Assert.Equal(expected, SharpHookHotkeyBackend.KeyCodes[key]);
    }

    [Theory]
    [InlineData(EventMask.LeftCtrl | EventMask.RightShift, false, ChordModifiers.Ctrl | ChordModifiers.Shift)]
    [InlineData(EventMask.LeftMeta | EventMask.LeftAlt, false, ChordModifiers.Super | ChordModifiers.Alt)]
    [InlineData(EventMask.LeftMeta | EventMask.LeftShift, true, ChordModifiers.Ctrl | ChordModifiers.Shift)]
    [InlineData(EventMask.RightCtrl, true, ChordModifiers.Super)]
    [InlineData(EventMask.LeftCtrl | EventMask.NumLock | EventMask.CapsLock, false, ChordModifiers.Ctrl)]
    public void ModifiersOf_MapsCommandToTheChordsPrimaryModifierOnMac(EventMask mask, bool isMac, ChordModifiers expected)
    {
        Assert.Equal(expected, SharpHookHotkeyBackend.ModifiersOf(mask, isMac));
    }

    [Theory]
    [InlineData(UioHookResult.ErrorAxApiDisabled, true)]
    [InlineData(UioHookResult.ErrorAxApiRevoked, true)]
    [InlineData(UioHookResult.Failure, false)]
    public void IsAccessibilityFailure_CoversDeniedAndRevokedAccess(UioHookResult result, bool expected)
    {
        Assert.Equal(expected, SharpHookHotkeyBackend.IsAccessibilityFailure(result));
    }

    [Theory]
    [InlineData(false, "A")]
    [InlineData(true, "Q")]
    public void KeyToMatch_UsesThePhysicalKeyOnlyOnMac(bool isMac, string expected)
    {
        Assert.True(KeyChord.TryParse("Ctrl+Shift+A", out var chord));

        Assert.Equal(expected, SharpHookHotkeyBackend.KeyToMatch(chord.WithPhysicalKey("Q"), isMac));
    }

    [Theory]
    [InlineData("x11", null, true)]
    [InlineData(null, null, true)]
    [InlineData("wayland", null, false)]
    [InlineData("x11", "wayland-0", false)]
    public void IsX11Session_RejectsWayland(string? sessionType, string? waylandDisplay, bool expected)
    {
        var environment = new Dictionary<string, string?>
        {
            ["XDG_SESSION_TYPE"] = sessionType,
            ["WAYLAND_DISPLAY"] = waylandDisplay,
        };

        Assert.Equal(expected, SharpHookHotkeyBackend.IsX11Session(environment.GetValueOrDefault));
    }

    private static KeyChord Chord(string text)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        return chord;
    }
}
