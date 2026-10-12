using System.Runtime.InteropServices;

namespace Juvin.Platform.Linux.Hotkeys.Evdev;

/// <summary>The kernel's <c>input_event</c> on 64-bit targets: a 16-byte timestamp, type, code, and value.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InputEvent
{
    public const ushort EvSyn = 0;
    public const ushort EvKey = 1;
    public const ushort SynDropped = 3;
    public const int Released = 0;
    public const int Pressed = 1;
    public const int Repeated = 2;

    public static readonly int SizeBytes = Marshal.SizeOf<InputEvent>();

    public long TimeSec;
    public long TimeUsec;
    public ushort Type;
    public ushort Code;
    public int Value;

    public static InputEvent Key(int code, int value) => new() { Type = EvKey, Code = (ushort)code, Value = value };
}
