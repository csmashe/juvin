using System.Globalization;
using Avalonia.Input;
using Juvin.Core.Hotkeys;

namespace Juvin.App.ViewModels;

/// <summary>Translates Avalonia key events into <see cref="KeyChord" /> parts for the shortcut recorder.</summary>
public static class ShortcutKeyMap
{
    private static readonly Dictionary<Key, string> s_keys = BuildKeys();

    private static readonly Dictionary<PhysicalKey, string> s_physicalAliases = new()
    {
        [PhysicalKey.ArrowUp] = "Up",
        [PhysicalKey.ArrowDown] = "Down",
        [PhysicalKey.ArrowLeft] = "Left",
        [PhysicalKey.ArrowRight] = "Right",
    };

    private static readonly HashSet<Key> s_modifierKeys =
    [
        Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin,
    ];

    public static bool IsModifierKey(Key key) => s_modifierKeys.Contains(key);

    /// <summary>The chord name of <paramref name="key" />, or null when a shortcut cannot use it.</summary>
    public static string? KeyName(Key key) => s_keys.GetValueOrDefault(key);

    /// <summary>The chord name of the US key at position <paramref name="key" />, or null when a shortcut cannot use it.</summary>
    public static string? PhysicalKeyName(PhysicalKey key)
    {
        if (s_physicalAliases.TryGetValue(key, out var alias))
        {
            return alias;
        }

        if (key is >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9)
        {
            return (key - PhysicalKey.Digit0).ToString(CultureInfo.InvariantCulture);
        }

        return Enum.GetName(key) is { } name && KeyChord.KeyNames.Contains(name) ? name : null;
    }

    /// <summary>
    ///     On macOS the Command key (<see cref="KeyModifiers.Meta" />) is the chord's primary modifier and the
    ///     Control key is its Super, matching how <see cref="KeyChord" /> mirrors browser shortcuts.
    /// </summary>
    public static ChordModifiers Modifiers(KeyModifiers modifiers, bool isMac)
    {
        var result = ChordModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            result |= isMac ? ChordModifiers.Super : ChordModifiers.Ctrl;
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            result |= isMac ? ChordModifiers.Ctrl : ChordModifiers.Super;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            result |= ChordModifiers.Alt;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            result |= ChordModifiers.Shift;
        }

        return result;
    }

    private static Dictionary<Key, string> BuildKeys()
    {
        var keys = new Dictionary<Key, string>
        {
            [Key.Space] = "Space",
            [Key.Enter] = "Enter",
            [Key.Tab] = "Tab",
            [Key.Back] = "Backspace",
            [Key.Delete] = "Delete",
            [Key.Insert] = "Insert",
            [Key.Home] = "Home",
            [Key.End] = "End",
            [Key.PageUp] = "PageUp",
            [Key.PageDown] = "PageDown",
            [Key.Up] = "Up",
            [Key.Down] = "Down",
            [Key.Left] = "Left",
            [Key.Right] = "Right",
            [Key.OemMinus] = "Minus",
            [Key.OemPlus] = "Equal",
            [Key.OemOpenBrackets] = "BracketLeft",
            [Key.OemCloseBrackets] = "BracketRight",
            [Key.OemSemicolon] = "Semicolon",
            [Key.OemQuotes] = "Quote",
            [Key.OemTilde] = "Backquote",
            [Key.OemPipe] = "Backslash",
            [Key.OemBackslash] = "Backslash",
            [Key.OemComma] = "Comma",
            [Key.OemPeriod] = "Period",
            [Key.OemQuestion] = "Slash",
        };
        for (var i = 0; i < 26; i++)
        {
            keys[Key.A + i] = ((char)('A' + i)).ToString();
        }

        for (var i = 0; i < 10; i++)
        {
            keys[Key.D0 + i] = i.ToString(CultureInfo.InvariantCulture);
        }

        for (var i = 0; i < 24; i++)
        {
            keys[Key.F1 + i] = "F" + (i + 1).ToString(CultureInfo.InvariantCulture);
        }

        return keys;
    }
}
