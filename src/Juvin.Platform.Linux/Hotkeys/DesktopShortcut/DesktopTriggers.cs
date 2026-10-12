using System.Text;
using Juvin.Core.Hotkeys;

namespace Juvin.Platform.Linux.Hotkeys.DesktopShortcut;

/// <summary>A <see cref="KeyChord" /> in each desktop's shortcut syntax. Super is the Super (Windows) key.</summary>
internal static class DesktopTriggers
{
    // X keysym names, which GNOME, Hyprland, and Sway all accept.
    private static readonly Dictionary<string, string> s_keysyms = new(StringComparer.Ordinal)
    {
        ["Space"] = "space", ["Escape"] = "Escape", ["Enter"] = "Return", ["Tab"] = "Tab", ["Backspace"] = "BackSpace",
        ["Delete"] = "Delete", ["Insert"] = "Insert", ["Home"] = "Home", ["End"] = "End",
        ["PageUp"] = "Page_Up", ["PageDown"] = "Page_Down", ["Up"] = "Up", ["Down"] = "Down", ["Left"] = "Left", ["Right"] = "Right",
        ["Minus"] = "minus", ["Equal"] = "equal", ["BracketLeft"] = "bracketleft", ["BracketRight"] = "bracketright",
        ["Semicolon"] = "semicolon", ["Quote"] = "apostrophe", ["Backquote"] = "grave", ["Backslash"] = "backslash",
        ["Comma"] = "comma", ["Period"] = "period", ["Slash"] = "slash",
    };

    // Qt key names, which KDE's X-KDE-Shortcuts uses.
    private static readonly Dictionary<string, string> s_qtKeys = new(StringComparer.Ordinal)
    {
        ["Escape"] = "Esc", ["Delete"] = "Del", ["Insert"] = "Ins", ["PageUp"] = "PgUp", ["PageDown"] = "PgDown",
        ["Enter"] = "Return", ["Minus"] = "-", ["Equal"] = "=", ["BracketLeft"] = "[", ["BracketRight"] = "]",
        ["Semicolon"] = ";", ["Quote"] = "'", ["Backquote"] = "`", ["Backslash"] = "\\", ["Comma"] = ",", ["Period"] = ".", ["Slash"] = "/",
    };

    /// <summary>GNOME accelerator, such as <c>&lt;Control&gt;&lt;Shift&gt;f</c>.</summary>
    public static string Gnome(KeyChord chord) =>
        Modifiers(chord, "<Control>", "<Alt>", "<Shift>", "<Super>", "") + Keysym(chord.Key);

    /// <summary>Qt key sequence, such as <c>Ctrl+Shift+F</c>.</summary>
    public static string Kde(KeyChord chord) =>
        Modifiers(chord, "Ctrl+", "Alt+", "Shift+", "Meta+", "") + s_qtKeys.GetValueOrDefault(chord.Key, chord.Key);

    /// <summary>Hyprland bind modifiers and key, such as <c>CTRL SHIFT</c> and <c>F</c>.</summary>
    public static (string Modifiers, string Key) Hyprland(KeyChord chord)
    {
        var key = Keysym(chord.Key);
        return (Modifiers(chord, "CTRL", "ALT", "SHIFT", "SUPER", " "), key.Length == 1 ? key.ToUpperInvariant() : key);
    }

    /// <summary>Sway bindsym, such as <c>Ctrl+Shift+f</c>.</summary>
    public static string Sway(KeyChord chord) =>
        Modifiers(chord, "Ctrl+", "Alt+", "Shift+", "Mod4+", "") + Keysym(chord.Key);

    private static string Keysym(string key) =>
        s_keysyms.TryGetValue(key, out var keysym) ? keysym
        : key.Length == 1 ? key.ToLowerInvariant()
        : key;

    private static string Modifiers(KeyChord chord, string ctrl, string alt, string shift, string super, string separator)
    {
        var parts = new List<string>();
        Add(ChordModifiers.Ctrl, ctrl);
        Add(ChordModifiers.Alt, alt);
        Add(ChordModifiers.Shift, shift);
        Add(ChordModifiers.Super, super);
        return string.Join(separator, parts);

        void Add(ChordModifiers modifier, string name)
        {
            if ((chord.Modifiers & modifier) != 0)
            {
                parts.Add(name);
            }
        }
    }

    /// <summary>Double-quotes a path for a shell or a desktop entry when it holds anything but safe characters.</summary>
    public static string QuoteArgument(string value)
    {
        if (value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "/._-+:@%".Contains(c)))
        {
            return value;
        }

        var quoted = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '"' or '\\' or '$' or '`')
            {
                quoted.Append('\\');
            }

            quoted.Append(c);
        }

        return quoted.Append('"').ToString();
    }
}
