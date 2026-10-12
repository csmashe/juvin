using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Juvin.Platform.Linux.Hotkeys.Evdev;

/// <summary>
///     Finds keyboard <c>/dev/input/eventN</c> nodes by their EV_KEY capability bits rather than
///     udev's <c>-event-kbd</c> links, which virtual keyboards from remappers never get. Nodes this
///     process cannot open are left out, so an empty result also means "no keyboard access".
/// </summary>
internal static partial class KeyboardDeviceDiscovery
{
    private const string InputDirectory = "/dev/input";

    // A device declaring these typing keys is a keyboard; buttons, lid switches, and mice declare none.
    private const int KeyEnter = 28;
    private const int KeyA = 30;
    private const int KeyZ = 44;
    private const int KeySpace = 57;

    private const int KeyMax = 0x2ff;
    private const int KeyBitmapBytes = KeyMax / 8 + 1;
    private const int NameBufferBytes = 256;

    // _IOC(dir, type, nr, size) with _IOC_READ = 2 and the evdev type 'E'.
    private const uint IocRead = 2u;
    private const uint EvdevIocType = 0x45u;

    public static IReadOnlyList<string> EnumerateKeyboards()
    {
        try
        {
            if (!Directory.Exists(InputDirectory))
            {
                return [];
            }

            return [.. Directory.EnumerateFiles(InputDirectory, "event*").Where(IsKeyboardNode).Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[KeyboardDeviceDiscovery] Listing input devices failed ({ex.GetType().Name}).");
            return [];
        }
    }

    private static bool LooksLikeKeyboard(ReadOnlySpan<byte> keyBits) =>
        IsBitSet(keyBits, KeyEnter) && IsBitSet(keyBits, KeyA) && IsBitSet(keyBits, KeyZ) && IsBitSet(keyBits, KeySpace);

    /// <summary>Juvin's own virtual devices, should it ever inject input, must never trigger its shortcut.</summary>
    internal static bool IsExcludedByName(string deviceName) =>
        deviceName.Contains("Juvin", StringComparison.OrdinalIgnoreCase);

    private static bool IsKeyboardNode(string node)
    {
        try
        {
            using var handle = File.OpenHandle(node, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var keyBits = new byte[KeyBitmapBytes];
            if (ioctl(handle, Request(0x20 + InputEvent.EvKey, KeyBitmapBytes), keyBits) < 0 || !LooksLikeKeyboard(keyBits))
            {
                return false;
            }

            var name = new byte[NameBufferBytes];
            var length = ioctl(handle, Request(0x06, NameBufferBytes), name);
            return !IsExcludedByName(length > 0 ? DecodeCString(name, length) : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsBitSet(ReadOnlySpan<byte> bits, int bit)
    {
        var index = bit / 8;
        return index < bits.Length && (bits[index] & (1 << (bit % 8))) != 0;
    }

    private static string DecodeCString(byte[] buffer, int length)
    {
        var end = Array.IndexOf(buffer, (byte)0, 0, Math.Min(length, buffer.Length));
        return Encoding.UTF8.GetString(buffer, 0, end >= 0 ? end : Math.Min(length, buffer.Length));
    }

    private static nuint Request(int number, int size) =>
        (IocRead << 30) | ((uint)size << 16) | (EvdevIocType << 8) | (uint)number;

    // ReSharper disable once InconsistentNaming -- the libc function name
    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(SafeFileHandle fd, nuint request, [Out] byte[] buffer);
}
