using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Juvin.Core;

namespace Juvin.Platform.Unix.Ipc;

/// <summary>
///     Resolves the paths to the Juvin control and connector sockets and ensures the containing
///     directory exists with user-only permissions.
/// </summary>
/// <remarks>
///     Linux prefers <c>$XDG_RUNTIME_DIR/juvin/</c> (the runtime dir is already 0700 via
///     systemd-logind) and falls back to <c>&lt;data&gt;/Runtime/</c> when
///     <c>XDG_RUNTIME_DIR</c> is absent, unusable, or open to other users. macOS always uses the data directory.
/// </remarks>
internal static partial class SocketPathResolver
{
    private const string ControlSocketFileName = "control.sock";
    private const string ConnectorSocketFileName = "connector.sock";

    private const UnixFileMode OwnerOnlyDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // statx(2) ABI: kernel-defined struct, arch-independent. stx_uid is at offset 20 (after
    // stx_mask:4, stx_blksize:4, stx_attributes:8, stx_nlink:4), then stx_gid:4 and stx_mode:2.
    private const int StatxBufSize = 256;
    private const int StatxUidOffset = 20;
    private const int StatxModeOffset = 28;
    private const int AtFdcwd = -100;
    private const int AtSymlinkNofollow = 0x100;
    private const uint StatxType = 0x00000001;
    private const uint StatxMode = 0x00000002;
    private const uint StatxUid = 0x00000008;
    private const int FileTypeMask = 0xF000;
    private const int DirectoryType = 0x4000;
    private const int GroupOrOtherWrite = 0b000_010_010;

    // sun_path is 108 bytes on Linux and 104 on macOS, and one of them holds the terminating NUL.
    private static readonly int s_maxSocketPathBytes = OperatingSystem.IsMacOS() ? 103 : 107;

    public static string ResolveControlSocketPath()
    {
        return ResolveSocketPath(ControlSocketFileName, JuvinEnvironment.RuntimePath, RuntimeDirectory());
    }

    public static string ResolveConnectorSocketPath()
    {
        return ResolveSocketPath(ConnectorSocketFileName, JuvinEnvironment.RuntimePath, RuntimeDirectory());
    }

    /// <summary>
    ///     The connector path the native host connects to. Inside a Flatpak sandbox the host
    ///     cannot see the app's runtime directory, so it uses the per-app directory the app
    ///     exposes to that sandbox instead.
    /// </summary>
    public static string ResolveConnectorClientPath()
    {
        return FlatpakConnectorPath(
                Environment.GetEnvironmentVariable("FLATPAK_ID"),
                Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            )
            ?? ResolveConnectorSocketPath();
    }

    /// <summary>
    ///     <c>$XDG_RUNTIME_DIR/app/&lt;FLATPAK_ID&gt;/juvin/connector.sock</c>, or null outside a
    ///     sandbox or when the id is not a plain application id.
    /// </summary>
    internal static string? FlatpakConnectorPath(string? flatpakId, string? runtimeDirectory)
    {
        if (string.IsNullOrEmpty(flatpakId) || string.IsNullOrEmpty(runtimeDirectory))
        {
            return null;
        }

        // ReSharper disable once InvertIf -- a guard clause reads better than the success case nested in an if
        if (!flatpakId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') || flatpakId.Contains(".."))
        {
            Trace.WriteLine("[SocketPathResolver] Ignoring a FLATPAK_ID that is not an application id.");
            return null;
        }

        return Path.Join(runtimeDirectory, "app", flatpakId, "juvin", ConnectorSocketFileName);
    }

    /// <param name="fileName">The socket file name.</param>
    /// <param name="fallbackDirectory">The data directory used when the runtime directory is absent or unsafe.</param>
    /// <param name="runtimeDirectory">The value of <c>XDG_RUNTIME_DIR</c>, or null where it is not used.</param>
    internal static string ResolveSocketPath(string fileName, string fallbackDirectory, string? runtimeDirectory)
    {
        if (!string.IsNullOrEmpty(runtimeDirectory) && Directory.Exists(runtimeDirectory))
        {
            var dir = Path.Join(runtimeDirectory, "juvin");
            try
            {
                if (!IsOwnedAndNotWritableByOthers(runtimeDirectory))
                {
                    throw new IOException("XDG_RUNTIME_DIR is not a directory owned by this user and closed to other users.");
                }

                var path = Path.Join(dir, fileName);
                if (IsTooLong(path))
                {
                    throw new IOException("The socket path is over the length limit for Unix sockets.");
                }

                PreparePrivateDirectory(dir);
                return path;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[SocketPathResolver] XDG path {dir} unusable: {ex.Message}. Falling back to {fallbackDirectory}."
                );
            }
        }

        PreparePrivateDirectory(fallbackDirectory);
        return Path.Join(fallbackDirectory, fileName);
    }

    private static string? RuntimeDirectory() =>
        OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") : null;

    internal static bool IsTooLong(string socketPath) => Encoding.UTF8.GetByteCount(socketPath) > s_maxSocketPathBytes;

    /// <summary>Called where a socket is bound, so resolving a path never fails for its length.</summary>
    internal static void ThrowIfTooLong(string socketPath)
    {
        if (IsTooLong(socketPath))
        {
            throw new IOException(
                $"The socket path is {Encoding.UTF8.GetByteCount(socketPath)} bytes, over the {s_maxSocketPathBytes}-byte limit for Unix sockets."
            );
        }
    }

    /// <summary>Checks the directory itself, not a symlink's target, the same as the private subdirectory.</summary>
    private static bool IsOwnedAndNotWritableByOthers(string path)
    {
        return TryStat(path, out var ownerUid, out var mode)
            && (mode & FileTypeMask) == DirectoryType
            && (mode & GroupOrOtherWrite) == 0
            && ownerUid == geteuid();
    }

    internal static void PreparePrivateDirectory(string directory)
    {
        try
        {
            // Create owner-only in the mkdir itself so a new directory is never briefly
            // world-traversable; the explicit mode covers a directory that already existed.
            Directory.CreateDirectory(directory, OwnerOnlyDirectoryMode);
            File.SetUnixFileMode(directory, OwnerOnlyDirectoryMode);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Could not create private socket directory {directory}.",
                ex
            );
        }

        if (!IsDirectoryPrivateAndOwned(directory))
        {
            throw new IOException(
                $"Could not secure private socket directory {directory} with owner-only mode 0700."
            );
        }
    }

    private static bool IsDirectoryPrivateAndOwned(string path)
    {
        try
        {
            const UnixFileMode groupOrOther =
                UnixFileMode.GroupRead
                | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute;
            var mode = File.GetUnixFileMode(path);
            if ((mode & groupOrOther) != 0 || (mode & OwnerOnlyDirectoryMode) != OwnerOnlyDirectoryMode)
            {
                return false;
            }

            // statx is Linux-only. On macOS the directory sits inside the user's own Library
            // folder, which another user cannot create entries in.
            if (!OperatingSystem.IsLinux())
            {
                return true;
            }

            if (TryStat(path, out var ownerUid, out _) && ownerUid == geteuid())
            {
                return true;
            }

            Trace.WriteLine($"[SocketPathResolver] {path} is not owned by the current user.");
            return false;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SocketPathResolver] Could not validate {path}: {ex.Message}");
            return false;
        }
    }

    private static bool TryStat(string path, out uint ownerUid, out int mode)
    {
        ownerUid = uint.MaxValue;
        mode = 0;
        var buffer = Marshal.AllocHGlobal(StatxBufSize);
        try
        {
            for (var i = 0; i < StatxBufSize; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            if (statx(AtFdcwd, path, AtSymlinkNofollow, StatxType | StatxMode | StatxUid, buffer) != 0)
            {
                return false;
            }

            ownerUid = (uint)Marshal.ReadInt32(buffer, StatxUidOffset);
            mode = (ushort)Marshal.ReadInt16(buffer, StatxModeOffset);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc")]
    private static partial uint geteuid();

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(
        int dirfd,
        string pathname,
        int flags,
        uint mask,
        IntPtr statxbuf
    );
}
