using Juvin.Core.Hotkeys;
using Juvin.Core.Models;

namespace Juvin.Core.Tests;

public sealed class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F", ChordModifiers.Ctrl | ChordModifiers.Shift, "F")]
    [InlineData("control+shift+f", ChordModifiers.Ctrl | ChordModifiers.Shift, "F")]
    [InlineData(" Shift + Ctrl + F ", ChordModifiers.Ctrl | ChordModifiers.Shift, "F")]
    [InlineData("Cmd+Shift+F", ChordModifiers.Ctrl | ChordModifiers.Shift, "F")]
    [InlineData("Command+F", ChordModifiers.Ctrl, "F")]
    [InlineData("Option+Space", ChordModifiers.Alt, "Space")]
    [InlineData("Super+Esc", ChordModifiers.Super, "Escape")]
    [InlineData("Win+Return", ChordModifiers.Super, "Enter")]
    [InlineData("Meta+Alt+F12", ChordModifiers.Super | ChordModifiers.Alt, "F12")]
    [InlineData("Ctrl+Alt+Shift+Super+7", ChordModifiers.Ctrl | ChordModifiers.Alt | ChordModifiers.Shift | ChordModifiers.Super, "7")]
    [InlineData("Ctrl+pageup", ChordModifiers.Ctrl, "PageUp")]
    [InlineData("Alt+F24", ChordModifiers.Alt, "F24")]
    [InlineData("Ctrl+BracketLeft", ChordModifiers.Ctrl, "BracketLeft")]
    public void TryParse_ReadsModifiersAndAliases(string text, ChordModifiers modifiers, string key)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(modifiers, chord.Modifiers);
        Assert.Equal(key, chord.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("F")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+F+G")]
    [InlineData("Ctrl+Ctrl+F")]
    [InlineData("Ctrl++F")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Hyper")]
    [InlineData("Ctrl+Shift+F+")]
    [InlineData("Shift+A")]
    [InlineData("Shift+F5")]
    public void TryParse_RejectsAnythingButModifiersAndOneKey(string? text)
    {
        Assert.False(KeyChord.TryParse(text, out var chord));
        Assert.Null(chord);
    }

    [Theory]
    [InlineData("shift+ctrl+f", "Ctrl+Shift+F")]
    [InlineData("super+shift+alt+ctrl+esc", "Ctrl+Alt+Shift+Super+Escape")]
    [InlineData("Cmd+Shift+F", "Ctrl+Shift+F")]
    public void ToString_IsCanonicalAndParsesBack(string text, string canonical)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));

        Assert.Equal(canonical, chord.ToString());
        Assert.True(KeyChord.TryParse(chord.ToString(), out var again));
        Assert.Equal(chord, again);
    }

    [Theory]
    [InlineData("Ctrl+Shift+F", false, "Ctrl+Shift+F")]
    [InlineData("Ctrl+Shift+F", true, "Cmd+Shift+F")]
    [InlineData("Ctrl+Alt+Super+K", true, "Ctrl+Cmd+Option+K")]
    [InlineData("Super+Shift+F", true, "Ctrl+Shift+F")]
    [InlineData("Super+Shift+F", false, "Shift+Super+F")]
    [InlineData("Ctrl+Alt+Super+K", false, "Ctrl+Alt+Super+K")]
    public void ToDisplayString_UsesTheMacKeyNamesOnlyOnMac(string text, bool isMac, string expected)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));

        Assert.Equal(expected, chord.ToDisplayString(isMac));
    }

    [Fact]
    public void WithPhysicalKey_SetsAKnownKeyPositionOutsideTheTextForm()
    {
        Assert.True(KeyChord.TryParse("Ctrl+Shift+A", out var chord));
        Assert.Equal("A", chord.PhysicalKey);

        var moved = chord.WithPhysicalKey("q");

        Assert.Equal("Q", moved.PhysicalKey);
        Assert.Equal("A", moved.Key);
        Assert.Equal("Ctrl+Shift+A", moved.ToString());
        Assert.NotEqual(chord, moved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Hyper")]
    public void WithPhysicalKey_IgnoresMissingOrUnknownNames(string? name)
    {
        Assert.True(KeyChord.TryParse("Ctrl+Shift+A", out var chord));

        var same = chord.WithPhysicalKey(name);

        Assert.Equal("A", same.PhysicalKey);
        Assert.Equal(chord, same);
    }

    [Fact]
    public void TryCreate_NeedsAModifierAndAKnownKey()
    {
        Assert.True(KeyChord.TryCreate(ChordModifiers.Ctrl | ChordModifiers.Shift, "f", out var chord));
        Assert.Equal("Ctrl+Shift+F", chord.ToString());
        Assert.False(KeyChord.TryCreate(ChordModifiers.None, "F", out _));
        Assert.False(KeyChord.TryCreate(ChordModifiers.Shift, "A", out _));
        Assert.False(KeyChord.TryCreate(ChordModifiers.Ctrl, "Hyper", out _));
        Assert.False(KeyChord.TryCreate((ChordModifiers)64, "F", out _));
    }

    [Theory]
    [InlineData(ChordModifiers.Ctrl | ChordModifiers.Shift)]
    [InlineData(ChordModifiers.Alt | ChordModifiers.Shift)]
    [InlineData(ChordModifiers.Super | ChordModifiers.Shift)]
    public void TryCreate_AcceptsShiftAlongsideAnotherModifier(ChordModifiers modifiers)
    {
        Assert.True(KeyChord.TryCreate(modifiers, "A", out var chord));
        Assert.Equal(modifiers, chord.Modifiers);
    }

    [Theory]
    [InlineData("Alt+Tab", false)]
    [InlineData("Alt+F4", false)]
    [InlineData("Ctrl+Alt+Delete", false)]
    [InlineData("Super+L", false)]
    [InlineData("Ctrl+Esc", false)]
    [InlineData("Alt+Escape", false)]
    [InlineData("Cmd+Tab", true)]
    [InlineData("Cmd+Q", true)]
    [InlineData("Cmd+Space", true)]
    [InlineData("Cmd+H", true)]
    [InlineData("Cmd+M", true)]
    public void TryCreate_RefusesTheOsReservedShortcuts_WhileTryParseStillReadsThem(string text, bool isMac)
    {
        Assert.True(KeyChord.TryParse(text, out var parsed));

        Assert.False(KeyChord.TryCreate(parsed.Modifiers, parsed.Key, isMac, out var chord));
        Assert.Null(chord);
        Assert.True(KeyChord.TryCreate(parsed.Modifiers, parsed.Key, !isMac, out _));
    }

    [Theory]
    [InlineData("Ctrl+Shift+F", false)]
    [InlineData("Super+Shift+F", true)]
    [InlineData("Alt+Shift+Tab", false)]
    public void TryCreate_WithTheOs_AcceptsOrdinaryShortcuts(string text, bool isMac)
    {
        Assert.True(KeyChord.TryParse(text, out var parsed));

        Assert.True(KeyChord.TryCreate(parsed.Modifiers, parsed.Key, isMac, out var chord));
        Assert.Equal(parsed, chord);
    }

    [Theory]
    [InlineData(false, "Ctrl+Shift+F")]
    [InlineData(true, "Ctrl+Shift+F")]
    public void DefaultRewriteShortcut_IsThePhysicalControlShiftFOnEveryOs(bool isMac, string expected)
    {
        Assert.True(KeyChord.TryParse(AppSettings.DefaultRewriteShortcutFor(isMac), out var parsed));
        Assert.True(KeyChord.TryCreate(parsed.Modifiers, parsed.Key, isMac, out var chord));

        Assert.Equal(expected, chord.ToDisplayString(isMac));
        Assert.Equal(isMac ? ChordModifiers.Super | ChordModifiers.Shift : ChordModifiers.Ctrl | ChordModifiers.Shift, chord.Modifiers);
    }

    [Fact]
    public void KeyNames_CoverLettersDigitsFunctionKeysAndNamedKeys()
    {
        Assert.Equal(26 + 10 + 24 + 26, KeyChord.KeyNames.Count);
        Assert.Contains("Z", KeyChord.KeyNames);
        Assert.Contains("0", KeyChord.KeyNames);
        Assert.Contains("F24", KeyChord.KeyNames);
        Assert.Contains("Backquote", KeyChord.KeyNames);
    }
}
