using System.Diagnostics;
using System.Net.Sockets;

namespace Juvin.Platform.Unix.Ipc;

/// <summary>
///     Unix domain socket server for the control endpoint. The socket file is 0600 inside a 0700
///     directory, and connections from other users are dropped after a peer-credential check.
/// </summary>
/// <remarks>
///     Every bind and unlink happens while holding the <see cref="ControlSocketOwnership" />
///     lock. When the process already holds it through <see cref="UnixSingleInstanceGuard" />
///     the server borrows that ownership instead of taking its own.
/// </remarks>
public sealed class UnixSocketServer : ILocalSocketServer, IDisposable
{
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(30);

    private readonly UnixSingleInstanceGuard? _guard;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ActiveConnections _connections = new();

    private Task? _acceptLoop;
    private CancellationTokenSource? _cts;
    private Socket? _listener;
    private ControlSocketOwnership? _ownership;
    private bool _ownsOwnership;

    public UnixSocketServer(UnixSingleInstanceGuard? guard = null)
        : this(SocketPathResolver.ResolveControlSocketPath(), guard)
    {
    }

    public UnixSocketServer(string socketPath, UnixSingleInstanceGuard? guard = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);

        Address = socketPath;
        _guard = guard;
    }

    public string Address { get; }

    public async Task StartAsync(Func<string, CancellationToken, Task<string>> handleLine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handleLine);

        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_listener is not null)
            {
                return;
            }

            var ownership = AcquireOwnership(out var ownsOwnership);
            Socket? listener = null;
            CancellationTokenSource? cts = null;
            var bound = false;
            try
            {
                var cleanup = ownership.CleanupStaleSocket();
                if (
                    cleanup
                    is not (ControlSocketCleanupResult.Missing or ControlSocketCleanupResult.Removed)
                )
                {
                    throw InUse();
                }

                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(Address));
                bound = true;

                // Defense in depth: the parent directory is already 0700.
                File.SetUnixFileMode(Address, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                listener.Listen(8);

                cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var token = cts.Token;
                // ReSharper disable once AccessToDisposedClosure -- the catch disposes the listener only when start fails, and nothing after this line can throw
                _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, handleLine, token), token);
                _listener = listener;
                _cts = cts;
                _ownership = ownership;
                _ownsOwnership = ownsOwnership;

                Trace.WriteLine($"[UnixSocketServer] Listening on {Address}");
            }
            catch
            {
                cts?.Dispose();
                listener?.Dispose();
                if (bound)
                {
                    TryCleanup(ownership);
                }

                if (ownsOwnership)
                {
                    ownership.Dispose();
                }

                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var listener = _listener;
            var cts = _cts;
            var acceptLoop = _acceptLoop;
            var ownership = _ownership;
            var ownsOwnership = _ownsOwnership;

            _listener = null;
            _cts = null;
            _acceptLoop = null;
            _ownership = null;
            _ownsOwnership = false;

            if (listener is null || ownership is null)
            {
                return;
            }

            // Order matters: cancel and close before waiting so accept cannot block forever,
            // and wait before unlinking so the file is not removed under a running loop.
            await cts!.CancelAsync().ConfigureAwait(false);
            listener.Dispose();
            if (acceptLoop is not null)
            {
                try
                {
                    await acceptLoop.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                {
                    // The loop has been told to stop; it will exit on its own.
                }
            }

            await _connections.WaitForIdleAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            cts.Dispose();
            TryCleanup(ownership);
            if (ownsOwnership)
            {
                ownership.Dispose();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private ControlSocketOwnership AcquireOwnership(out bool ownsOwnership)
    {
        var borrowed = _guard?.HeldOwnershipFor(Address);
        if (borrowed is not null)
        {
            ownsOwnership = false;
            return borrowed;
        }

        bool acquired;
        ControlSocketOwnership? ownership;
        try
        {
            acquired = ControlSocketOwnership.TryAcquire(Address, out ownership);
        }
        catch (Exception ex)
        {
            // Lock uncertainty fails closed, the same as contention.
            Trace.WriteLine($"[UnixSocketServer] Could not acquire socket ownership: {ex.Message}");
            throw InUse(ex);
        }

        if (!acquired || ownership is null)
        {
            throw InUse();
        }

        ownsOwnership = true;
        return ownership;
    }

    private IOException InUse(Exception? inner = null)
    {
        return new IOException($"The control socket {Address} is owned by another instance.", inner);
    }

    private void TryCleanup(ControlSocketOwnership ownership)
    {
        try
        {
            if (ownership.CleanupStaleSocket() is ControlSocketCleanupResult.Live)
            {
                Trace.WriteLine(
                    $"[UnixSocketServer] Socket path {Address} is held by another listener; leaving it in place."
                );
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[UnixSocketServer] Could not remove socket file: {ex.Message}");
        }
    }

    private async Task AcceptLoopAsync(
        Socket listener,
        Func<string, CancellationToken, Task<string>> handleLine,
        CancellationToken ct
    )
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[UnixSocketServer] Accept failed: {ex.Message}");
                // Brief back-off so a persistent error doesn't pin a core.
                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            _connections.Track(Task.Run(() => HandleClientAsync(client, handleLine, ct), CancellationToken.None));
        }
    }

    private static async Task HandleClientAsync(
        Socket client,
        Func<string, CancellationToken, Task<string>> handleLine,
        CancellationToken ct
    )
    {
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: true);
            if (!UnixPeerCredentials.IsOwnedByEffectiveUser(client))
            {
                Trace.WriteLine("[UnixSocketServer] Rejected a connection from another user.");
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(s_requestTimeout);
            var reply = await LocalSocketProtocol
                .ReadAndHandleAsync(stream, handleLine, timeout.Token)
                .ConfigureAwait(false);
            if (reply is not null)
            {
                await LocalSocketProtocol.WriteLineAsync(stream, reply, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown or a client that never finished its request.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[UnixSocketServer] Client handler failed: {ex.GetType().Name}");
        }
    }
}
