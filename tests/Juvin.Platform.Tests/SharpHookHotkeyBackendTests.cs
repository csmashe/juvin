using Juvin.Core.Hotkeys;
using Juvin.Platform.Hotkeys;
using SharpHook.Native;

namespace Juvin.Platform.Tests;

// No hook is started here: these cover the key table and the pure checks only.
public sealed class SharpHookHotkeyBackendTests
{
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
    [InlineData(ModifierMask.LeftCtrl | ModifierMask.RightShift, false, ChordModifiers.Ctrl | ChordModifiers.Shift)]
    [InlineData(ModifierMask.LeftMeta | ModifierMask.LeftAlt, false, ChordModifiers.Super | ChordModifiers.Alt)]
    [InlineData(ModifierMask.LeftMeta | ModifierMask.LeftShift, true, ChordModifiers.Ctrl | ChordModifiers.Shift)]
    [InlineData(ModifierMask.RightCtrl, true, ChordModifiers.Super)]
    [InlineData(ModifierMask.LeftCtrl | ModifierMask.NumLock | ModifierMask.CapsLock, false, ChordModifiers.Ctrl)]
    public void ModifiersOf_MapsCommandToTheChordsPrimaryModifierOnMac(ModifierMask mask, bool isMac, ChordModifiers expected)
    {
        Assert.Equal(expected, SharpHookHotkeyBackend.ModifiersOf(mask, isMac));
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
}
