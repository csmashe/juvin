using System.Runtime.InteropServices;

namespace Juvin.Platform.Unix;

/// <summary>access(2) for this user: what a file's mode, owner, and mount allow, not just whether it exists.</summary>
public static partial class UnixFileAccess
{
    private const int WriteOk = 2;
    private const int ExecuteOk = 1;

    /// <summary>False for a host copy that lost its execute bits, which a browser cannot start.</summary>
    public static bool CanExecute(string path) => access(path, ExecuteOk) == 0;

    /// <summary>False on a read-only mount, such as a disk image opened from a download.</summary>
    public static bool CanWrite(string path) => access(path, WriteOk) == 0;

    // ReSharper disable once InconsistentNaming -- the libc name, which LibraryImport uses as the entry point
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int access(string path, int mode);
}
