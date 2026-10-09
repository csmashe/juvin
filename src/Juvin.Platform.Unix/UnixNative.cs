using System.Runtime.InteropServices;

namespace Juvin.Platform.Unix;

/// <summary>
///     libc open(2) flags and errno values. They differ between Linux and macOS, and
///     <c>O_NOFOLLOW</c> also differs between Linux architectures.
/// </summary>
internal static class UnixNative
{
    public const int OpenReadOnly = 0;
    public const int OpenReadWrite = 2;
    public const int ErrorNoEntry = 2;
    public const int ErrorInterrupted = 4;

    private static readonly bool s_isMacOs = OperatingSystem.IsMacOS();

    private static readonly bool s_isLinuxArm =
        OperatingSystem.IsLinux()
        && RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

    public static int OpenCreate => s_isMacOs ? 0x200 : 0x40;

    public static int OpenNoFollow => s_isMacOs ? 0x100 : s_isLinuxArm ? 0x8000 : 0x20000;

    public static int OpenCloseOnExec => s_isMacOs ? 0x1000000 : 0x80000;

    public static int OpenNonBlock => s_isMacOs ? 0x4 : 0x800;

    public static int ErrorTryAgain => s_isMacOs ? 35 : 11;

    public static int ErrorSymbolicLink => s_isMacOs ? 62 : 40;
}
