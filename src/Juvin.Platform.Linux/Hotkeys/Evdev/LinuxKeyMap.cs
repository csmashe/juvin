using Juvin.Core.Hotkeys;

namespace Juvin.Platform.Linux.Hotkeys.Evdev;

/// <summary>Kernel <c>KEY_*</c> codes from <c>linux/input-event-codes.h</c> for chord keys and modifiers.</summary>
internal static class LinuxKeyMap
{
    private const int KeyLeftCtrl = 29;
    private const int KeyLeftShift = 42;
    private const int KeyRightShift = 54;
    private const int KeyLeftAlt = 56;
    private const int KeyRightCtrl = 97;
    private const int KeyRightAlt = 100;
    private const int KeyLeftMeta = 125;
    private const int KeyRightMeta = 126;

    /// <summary>Every <see cref="KeyChord.KeyNames" /> entry and its kernel key code.</summary>
    public static IReadOnlyDictionary<string, int> KeyCodes { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["1"] = 2, ["2"] = 3, ["3"] = 4, ["4"] = 5, ["5"] = 6, ["6"] = 7, ["7"] = 8, ["8"] = 9, ["9"] = 10, ["0"] = 11,
        ["Q"] = 16, ["W"] = 17, ["E"] = 18, ["R"] = 19, ["T"] = 20, ["Y"] = 21, ["U"] = 22, ["I"] = 23, ["O"] = 24, ["P"] = 25,
        ["A"] = 30, ["S"] = 31, ["D"] = 32, ["F"] = 33, ["G"] = 34, ["H"] = 35, ["J"] = 36, ["K"] = 37, ["L"] = 38,
        ["Z"] = 44, ["X"] = 45, ["C"] = 46, ["V"] = 47, ["B"] = 48, ["N"] = 49, ["M"] = 50,
        ["Escape"] = 1, ["Minus"] = 12, ["Equal"] = 13, ["Backspace"] = 14, ["Tab"] = 15,
        ["BracketLeft"] = 26, ["BracketRight"] = 27, ["Enter"] = 28, ["Semicolon"] = 39, ["Quote"] = 40,
        ["Backquote"] = 41, ["Backslash"] = 43, ["Comma"] = 51, ["Period"] = 52, ["Slash"] = 53, ["Space"] = 57,
        ["F1"] = 59, ["F2"] = 60, ["F3"] = 61, ["F4"] = 62, ["F5"] = 63, ["F6"] = 64, ["F7"] = 65, ["F8"] = 66,
        ["F9"] = 67, ["F10"] = 68, ["F11"] = 87, ["F12"] = 88,
        ["F13"] = 183, ["F14"] = 184, ["F15"] = 185, ["F16"] = 186, ["F17"] = 187, ["F18"] = 188,
        ["F19"] = 189, ["F20"] = 190, ["F21"] = 191, ["F22"] = 192, ["F23"] = 193, ["F24"] = 194,
        ["Home"] = 102, ["Up"] = 103, ["PageUp"] = 104, ["Left"] = 105, ["Right"] = 106, ["End"] = 107,
        ["Down"] = 108, ["PageDown"] = 109, ["Insert"] = 110, ["Delete"] = 111,
    };

    /// <summary>The chord modifier a key code belongs to, either side; <see cref="ChordModifiers.None" /> for other keys.</summary>
    public static ChordModifiers ModifierOf(int code) =>
        code switch
        {
            KeyLeftCtrl or KeyRightCtrl => ChordModifiers.Ctrl,
            KeyLeftShift or KeyRightShift => ChordModifiers.Shift,
            KeyLeftAlt or KeyRightAlt => ChordModifiers.Alt,
            KeyLeftMeta or KeyRightMeta => ChordModifiers.Super,
            _ => ChordModifiers.None,
        };
}
