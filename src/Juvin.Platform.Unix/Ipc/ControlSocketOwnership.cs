using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Juvin.Platform.Unix.Ipc;

internal enum ControlSocketCleanupResult
{
    Missing,
    Removed,
    Live,
    Indeterminate,
    OwnershipContended,
}

/// <summary>
///     Owns the stable advisory lock that serializes every bind and unlink of one socket path. The
///     lockfile sits next to the socket with a <c>.lock</c> extension and is persistent; closing
///     its file descriptor releases ownership without replacing the inode that all contenders lock.
/// </summary>
internal sealed partial class ControlSocketOwnership : IDisposable
{
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const uint OwnerReadWriteMode = 0b110_000_000; // 0600
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(2);

    private readonly SafeFileHandle _lockHandle;
    private int _disposed;

    private ControlSocketOwnership(
        string socketPath,
        SafeFileHandle lockHandle
    )
    {
        SocketPath = socketPath;
        _lockHandle = lockHandle;
    }

    private string SocketPath { get; }

    /// <summary>
    ///     Opens the persistent lockfile and attempts an exclusive lock without blocking.
    ///     Returns false only for ordinary lock contention; other failures are reported.
    /// </summary>
    internal static bool TryAcquire(
        string socketPath,
        [NotNullWhen(true)] out ControlSocketOwnership? ownership
    )
    {
        ownership = null;
        var lockPath = Path.ChangeExtension(socketPath, ".lock");
        // ReSharper disable once SuggestVarOrType_SimpleTypes -- OpenLockFile returns a non-nullable handle; the explicit nullable type is required for the `handle = null` ownership transfer below.
        SafeFileHandle? handle = OpenLockFile(lockPath);
        try
        {
            SetOwnerOnlyMode(handle, lockPath);
            while (flock(handle, LockExclusive | LockNonBlocking) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == UnixNative.ErrorInterrupted)
                {
                    continue;
                }

                if (error == UnixNative.ErrorTryAgain)
                {
                    return false;
                }

                throw new IOException(
                    $"Could not acquire socket ownership lock {lockPath}.",
                    new Win32Exception(error)
                );
            }

            ownership = new ControlSocketOwnership(socketPath, handle);
            handle = null;
            return true;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>
    ///     Best-effort client cleanup. Contention or any acquisition/probe/delete failure
    ///     leaves the socket pathname untouched.
    /// </summary>
    // ReSharper disable once UnusedMethodReturnValue.Global -- the outcome documents what happened; clients treat cleanup as best-effort
    internal static ControlSocketCleanupResult TryCleanupStaleSocket(string socketPath)
    {
        try
        {
            if (!TryAcquire(socketPath, out var ownership))
            {
                return ControlSocketCleanupResult.OwnershipContended;
            }

            using (ownership)
            {
                return ownership.CleanupStaleSocket();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[ControlSocketOwnership] Stale-socket cleanup for {socketPath} failed: {ex.Message}"
            );
            return ControlSocketCleanupResult.Indeterminate;
        }
    }

    /// <summary>
    ///     Re-probes and, only on ECONNREFUSED, unlinks a stale socket while ownership is held.
    ///     A live peer answers or refuses immediately; only a wedged peer costs the full
    ///     <see cref="s_probeTimeout" />.
    /// </summary>
    internal ControlSocketCleanupResult CleanupStaleSocket()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (!File.Exists(SocketPath))
        {
            return ControlSocketCleanupResult.Missing;
        }

        try
        {
            using var probe = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified
            );
            using var timeout = new CancellationTokenSource(s_probeTimeout);
            probe
                .ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), timeout.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return ControlSocketCleanupResult.Live;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            if (!File.Exists(SocketPath))
            {
                return ControlSocketCleanupResult.Missing;
            }

            try
            {
                File.Delete(SocketPath);
                if (!File.Exists(SocketPath))
                {
                    Trace.WriteLine(
                        $"[ControlSocketOwnership] Removed stale socket at {SocketPath}."
                    );
                    return ControlSocketCleanupResult.Removed;
                }
            }
            catch (Exception deleteException)
            {
                Trace.WriteLine(
                    $"[ControlSocketOwnership] Failed to remove stale socket {SocketPath}: {deleteException.Message}"
                );
                return ControlSocketCleanupResult.Indeterminate;
            }

            Trace.WriteLine(
                $"[ControlSocketOwnership] Stale socket {SocketPath} remained after deletion."
            );
            return ControlSocketCleanupResult.Indeterminate;
        }
        catch (Exception ex)
        {
            if (!File.Exists(SocketPath))
            {
                return ControlSocketCleanupResult.Missing;
            }

            Trace.WriteLine(
                $"[ControlSocketOwnership] Probe of {SocketPath} was indeterminate: {ex.Message}"
            );
            return ControlSocketCleanupResult.Indeterminate;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Closing releases flock ownership. Never unlink the stable lockfile.
        _lockHandle.Dispose();
    }

    private static SafeFileHandle OpenLockFile(string lockPath)
    {
        while (true)
        {
            // On Apple arm64 the variadic mode argument is not passed reliably through P/Invoke;
            // SetOwnerOnlyMode applies 0600 right after the open either way, inside a 0700 directory.
            var fd = open(
                lockPath,
                UnixNative.OpenReadWrite
                    | UnixNative.OpenCreate
                    | UnixNative.OpenNoFollow
                    | UnixNative.OpenCloseOnExec,
                OwnerReadWriteMode
            );
            if (fd >= 0)
            {
                // Native open has no managed sharing policy, so every contender reaches
                // the explicit nonblocking flock below.
                return new SafeFileHandle(fd, ownsHandle: true);
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == UnixNative.ErrorInterrupted)
            {
                continue;
            }

            throw new IOException(
                $"Could not open socket ownership lock {lockPath}.",
                new Win32Exception(error)
            );
        }
    }

    private static void SetOwnerOnlyMode(SafeFileHandle handle, string lockPath)
    {
        while (fchmod(handle, OwnerReadWriteMode) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == UnixNative.ErrorInterrupted)
            {
                continue;
            }

            throw new IOException(
                $"Could not secure socket ownership lock {lockPath} with mode 0600.",
                new Win32Exception(error)
            );
        }
    }

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int flock(SafeFileHandle fd, int operation);

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int fchmod(SafeFileHandle fd, uint mode);

    // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string pathname, int flags, uint mode);
}
