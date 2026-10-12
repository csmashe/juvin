using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Juvin.Core.Hotkeys;

[Flags]
public enum ChordModifiers
{
    None = 0,

    /// <summary>The primary modifier: Control on Linux and Windows, Command on macOS, as in browser shortcuts.</summary>
    Ctrl = 1,

    /// <summary>Alt, or Option on macOS.</summary>
    Alt = 2,

    Shift = 4,

    /// <summary>Super on Linux, Windows on Windows, and the Control key on macOS.</summary>
    Super = 8,
}

/// <summary>
///     A global shortcut: at least one modifier and exactly one key, such as <c>Ctrl+Shift+F</c>.
///     <see cref="ChordModifiers.Ctrl" /> is Command on macOS, so one stored shortcut means the
///     same keys as the browser extension's command on every OS.
/// </summary>
public sealed record KeyChord
{
    private const ChordModifiers AllModifiers =
        ChordModifiers.Ctrl | ChordModifiers.Alt | ChordModifiers.Shift | ChordModifiers.Super;

    private static readonly Dictionary<string, ChordModifiers> s_modifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = ChordModifiers.Ctrl,
        ["Control"] = ChordModifiers.Ctrl,
        ["Cmd"] = ChordModifiers.Ctrl,
        ["Command"] = ChordModifiers.Ctrl,
        ["Alt"] = ChordModifiers.Alt,
        ["Option"] = ChordModifiers.Alt,
        ["Shift"] = ChordModifiers.Shift,
        ["Super"] = ChordModifiers.Super,
        ["Win"] = ChordModifiers.Super,
        ["Meta"] = ChordModifiers.Super,
    };

    private static readonly Dictionary<string, string> s_keyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = "Escape",
        ["Return"] = "Enter",
    };

    private static readonly Dictionary<string, string> s_keys = BuildKeys();

    private KeyChord(ChordModifiers modifiers, string key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public ChordModifiers Modifiers { get; }

    /// <summary>The key's canonical name, one of <see cref="KeyNames" />.</summary>
    public string Key { get; }

    /// <summary>Every key a chord can use, by canonical name.</summary>
    public static IReadOnlyCollection<string> KeyNames => s_keys.Values;

    /// <summary>Parses text such as <c>Ctrl+Shift+F</c>; names are case-insensitive and accept common aliases.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out KeyChord? chord)
    {
        chord = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = ChordModifiers.None;
        string? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (s_modifierAliases.TryGetValue(part, out var modifier))
            {
                if ((modifiers & modifier) != 0)
                {
                    return false;
                }

                modifiers |= modifier;
            }
            else if (key is null && TryNormalizeKey(part, out var name))
            {
                key = name;
            }
            else
            {
                return false;
            }
        }

        return key is not null && TryCreate(modifiers, key, out chord);
    }

    /// <summary>Builds a chord from modifiers and a key name or alias; fails without a modifier or for an unknown key.</summary>
    public static bool TryCreate(ChordModifiers modifiers, string key, [NotNullWhen(true)] out KeyChord? chord)
    {
        chord = null;
        if (modifiers == ChordModifiers.None || (modifiers & ~AllModifiers) != 0 || !TryNormalizeKey(key, out var name))
        {
            return false;
        }

        chord = new KeyChord(modifiers, name);
        return true;
    }

    /// <summary>The canonical form stored in settings, for example <c>Ctrl+Shift+F</c>.</summary>
    public override string ToString() => Format("Ctrl", "Alt", "Shift", "Super");

    /// <summary>The chord as the user's keyboard labels it: <c>Cmd+Shift+F</c> on macOS for the canonical <c>Ctrl+Shift+F</c>.</summary>
    public string ToDisplayString(bool isMac) =>
        isMac ? Format("Cmd", "Option", "Shift", "Ctrl") : ToString();

    private string Format(string ctrl, string alt, string shift, string super)
    {
        var text = new StringBuilder();
        Append(ChordModifiers.Ctrl, ctrl);
        Append(ChordModifiers.Alt, alt);
        Append(ChordModifiers.Shift, shift);
        Append(ChordModifiers.Super, super);
        return text.Append(Key).ToString();

        void Append(ChordModifiers modifier, string name)
        {
            if ((Modifiers & modifier) != 0)
            {
                text.Append(name).Append('+');
            }
        }
    }

    private static bool TryNormalizeKey(string text, [NotNullWhen(true)] out string? name)
    {
        if (s_keyAliases.TryGetValue(text, out var alias))
        {
            text = alias;
        }

        return s_keys.TryGetValue(text, out name);
    }

    private static Dictionary<string, string> BuildKeys()
    {
        IEnumerable<string> names =
        [
            .. Enumerable.Range('A', 26).Select(letter => ((char)letter).ToString()),
            .. Enumerable.Range(0, 10).Select(digit => digit.ToString(CultureInfo.InvariantCulture)),
            .. Enumerable.Range(1, 24).Select(number => "F" + number.ToString(CultureInfo.InvariantCulture)),
            "Space", "Escape", "Enter", "Tab", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown",
            "Up", "Down", "Left", "Right",
            "Minus", "Equal", "BracketLeft", "BracketRight", "Semicolon", "Quote", "Backquote", "Backslash",
            "Comma", "Period", "Slash",
        ];
        return names.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
    }
}
