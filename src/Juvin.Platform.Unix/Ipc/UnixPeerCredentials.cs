using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Juvin.Platform.Unix.Ipc;

/// <summary>Reads the connecting process's user id so a socket only serves its own user.</summary>
internal static partial class UnixPeerCredentials
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    internal static bool IsOwnedByEffectiveUser(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        return GetPeerUid(socket) == geteuid();
    }

    private static uint GetPeerUid(Socket socket)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (getpeereid((int)socket.Handle, out var uid, out _) != 0)
            {
                throw new IOException(
                    "Could not read Unix-socket peer credentials.",
                    new Win32Exception(Marshal.GetLastPInvokeError())
                );
            }

            return uid;
        }

        var credentials = new PeerCredentials();
        var length = (uint)Marshal.SizeOf<PeerCredentials>();
        if (getsockopt(socket.Handle, SolSocket, SoPeerCred, ref credentials, ref length) != 0)
        {
            throw new IOException(
                "Could not read Unix-socket peer credentials.",
                new Win32Exception(Marshal.GetLastPInvokeError())
            );
        }

        // ReSharper disable once ConvertIfStatementToReturnStatement -- a guard clause reads better than a throw inside a conditional
        if (length != Marshal.SizeOf<PeerCredentials>())
        {
            throw new IOException("Unix-socket peer credentials had an unexpected size.");
        }

        return credentials.Uid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials
    {
        internal int Pid;
        internal uint Uid;
        internal uint Gid;
    }

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int getsockopt(
        IntPtr socket,
        int level,
        int optionName,
        ref PeerCredentials optionValue,
        ref uint optionLength
    );

    // ReSharper disable once InconsistentNaming -- native libc function name; macOS only.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int getpeereid(int socket, out uint uid, out uint gid);

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc")]
    private static partial uint geteuid();
}
