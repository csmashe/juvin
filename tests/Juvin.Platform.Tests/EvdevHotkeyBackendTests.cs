using System.Runtime.Versioning;
using Juvin.Core.Hotkeys;
using Juvin.Core.Localization;
using Juvin.Platform.Hotkeys;
using Juvin.Platform.Linux.Hotkeys.Evdev;
using Juvin.Platform.Tests.Fakes;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class EvdevHotkeyBackendTests
{
    private const string Keyboard = "/dev/input/event3";
    private const string SecondKeyboard = "/dev/input/event7";
    private const int LeftCtrl = 29;
    private const int RightCtrl = 97;
    private const int LeftShift = 42;
    private const int LeftAlt = 56;
    private const int LeftMeta = 125;
    private const int KeyF = 33;
    private const int KeyG = 34;

    private readonly FakeEvdevDeviceSource _source = new();
    private readonly EvdevHotkeyBackend _backend;
    private int _presses;

    public EvdevHotkeyBackendTests()
    {
        _source.Keyboards.Add(Keyboard);
        _backend = new EvdevHotkeyBackend(new Localizer(), _source, TimeSpan.FromHours(1));
        _backend.Pressed += () => _presses++;
    }

    private Task StartAsync(string chord = "Ctrl+Shift+F")
    {
        Assert.True(KeyChord.TryParse(chord, out var parsed));
        return _backend.StartAsync(parsed, CancellationToken.None);
    }

    private void Press(params int[] codes)
    {
        foreach (var code in codes)
        {
            _source.Key(Keyboard, code, InputEvent.Pressed);
        }
    }

    private void Release(params int[] codes)
    {
        foreach (var code in codes)
        {
            _source.Key(Keyboard, code, InputEvent.Released);
        }
    }

    [LinuxFact]
    public async Task Press_WithTheChordsModifiersHeld_FiresOnce()
    {
        await StartAsync();

        Press(LeftCtrl, LeftShift, KeyF);

        Assert.Equal(1, _presses);
    }

    [LinuxFact]
    public async Task Press_AcceptsTheRightHandModifier()
    {
        await StartAsync();

        Press(RightCtrl, LeftShift, KeyF);

        Assert.Equal(1, _presses);
    }

    [LinuxFact]
    public async Task Press_WithAnExtraModifier_DoesNotFire()
    {
        await StartAsync();

        Press(LeftCtrl, LeftShift, LeftAlt, KeyF);

        Assert.Equal(0, _presses);
    }

    [LinuxFact]
    public async Task Press_WithAMissingModifierOrAnotherKey_DoesNotFire()
    {
        await StartAsync();

        Press(LeftCtrl, KeyF);
        Release(KeyF);
        Press(LeftShift, KeyG);

        Assert.Equal(0, _presses);
    }

    [LinuxFact]
    public async Task Autorepeat_DoesNotFireAgain()
    {
        await StartAsync();

        Press(LeftCtrl, LeftShift, KeyF);
        _source.Key(Keyboard, KeyF, InputEvent.Repeated);
        _source.Key(Keyboard, KeyF, InputEvent.Repeated);

        Assert.Equal(1, _presses);
    }

    [LinuxFact]
    public async Task ReleasedModifier_NoLongerCounts_AndPressingAgainFiresAgain()
    {
        await StartAsync();

        Press(LeftCtrl, LeftShift, KeyF);
        Release(KeyF, LeftShift);
        Press(KeyF);
        Release(KeyF);
        Press(LeftShift, KeyF);

        Assert.Equal(2, _presses);
    }

    [LinuxFact]
    public async Task SuperChord_MatchesTheMetaKey()
    {
        await StartAsync("Super+F");

        Press(LeftMeta, KeyF);

        Assert.Equal(1, _presses);
    }

    [LinuxFact]
    public async Task Modifiers_CombineAcrossKeyboards()
    {
        _source.Keyboards.Add(SecondKeyboard);
        await StartAsync();

        _source.Key(SecondKeyboard, LeftCtrl, InputEvent.Pressed);
        Press(LeftShift, KeyF);

        Assert.Equal(1, _presses);
    }

    [LinuxFact]
    public async Task DroppedEvents_ForgetTheDevicesHeldModifiers()
    {
        await StartAsync();

        Press(LeftCtrl, LeftShift);
        _source.Send(Keyboard, new InputEvent { Type = InputEvent.EvSyn, Code = InputEvent.SynDropped });
        Press(KeyF);

        Assert.Equal(0, _presses);
    }

    [LinuxFact]
    public async Task Start_WithNoOpenableKeyboard_Throws()
    {
        _source.Unopenable.Add(Keyboard);

        await Assert.ThrowsAsync<HotkeyBackendException>(() => StartAsync());
    }

    [LinuxFact]
    public async Task Stop_ClosesEveryReader()
    {
        await StartAsync();

        await _backend.StopAsync();

        Assert.Empty(_source.OpenDevices);
    }

    [LinuxFact]
    public async Task Rescan_AttachesNewKeyboards_AndReattachesAFailedOne()
    {
        await StartAsync();
        _source.Fail(Keyboard);
        _source.Keyboards.Add(SecondKeyboard);

        _backend.Rescan();

        Assert.Equal([Keyboard, SecondKeyboard], _source.OpenDevices.Order());
    }

    [LinuxFact]
    public async Task Restart_ListensForTheNewChord()
    {
        await StartAsync();
        await StartAsync("Ctrl+Shift+G");

        Press(LeftCtrl, LeftShift, KeyF);
        Release(KeyF);
        Press(KeyG);

        Assert.Equal(1, _presses);
    }
}
