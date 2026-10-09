using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Juvin.Platform.Windows.Ipc;

/// <summary>
///     Named pipe server for the control endpoint. The pipe's ACL grants only the current user,
///     and the first instance is created with <see cref="PipeOptions.FirstPipeInstance" /> so no
///     other process can own the name before us.
/// </summary>
public sealed class NamedPipeServer : ILocalSocketServer, IDisposable
{
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ActiveConnections _connections = new();

    private Task? _acceptLoop;
    private CancellationTokenSource? _cts;

    public NamedPipeServer()
        : this(PipeNames.Control)
    {
    }

    public NamedPipeServer(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        Address = pipeName;
    }

    public string Address { get; }

    public async Task StartAsync(Func<string, CancellationToken, Task<string>> handleLine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handleLine);

        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_acceptLoop is not null)
            {
                return;
            }

            NamedPipeServerStream firstPipe;
            try
            {
                firstPipe = CreatePipe(firstInstance: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"The control pipe {Address} is owned by another instance.", ex);
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = cts.Token;
            _cts = cts;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(firstPipe, handleLine, token), CancellationToken.None);

            Trace.WriteLine($"[NamedPipeServer] Listening on {Address}");
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
            var cts = _cts;
            var acceptLoop = _acceptLoop;
            _cts = null;
            _acceptLoop = null;

            if (cts is null || acceptLoop is null)
            {
                return;
            }

            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await acceptLoop.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // The loop has been told to stop; it will exit on its own.
            }

            // A pipe instance still held by a handler would make the next FirstPipeInstance fail.
            await _connections.WaitForIdleAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            cts.Dispose();
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

    private async Task AcceptLoopAsync(
        NamedPipeServerStream pipe,
        Func<string, CancellationToken, Task<string>> handleLine,
        CancellationToken ct
    )
    {
        while (true)
        {
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException ex)
            {
                // A client that connected and vanished; recycle this instance.
                Trace.WriteLine($"[NamedPipeServer] Connection failed: {ex.Message}");
                await pipe.DisposeAsync().ConfigureAwait(false);
                pipe = await CreateNextPipeAsync(ct).ConfigureAwait(false);
                continue;
            }

            var connected = pipe;
            // The next listening instance is created before this one is released, so the name
            // never lapses and cannot be claimed by another process in between.
            try
            {
                pipe = await CreateNextPipeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await connected.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _connections.Track(Task.Run(() => HandleClientAsync(connected, handleLine, ct), CancellationToken.None));
        }
    }

    private async Task<NamedPipeServerStream> CreateNextPipeAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return CreatePipe(firstInstance: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[NamedPipeServer] Could not create a pipe instance: {ex.Message}");
                // Brief back-off so a persistent error doesn't pin a core.
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current user has no SID.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        // The client's CurrentUserOnly check compares the pipe owner with the token's default
        // owner, which is the Administrators group for an elevated token, not the user SID.
        security.SetOwner(identity.Owner ?? user);

        var options = PipeOptions.Asynchronous;
        if (firstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return NamedPipeServerStreamAcl.Create(
            Address,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            // Zero-size buffers make every write block until the peer reads, which deadlocks an
            // oversized request against the error reply; 4x the line cap holds either side.
            inBufferSize: 4 * LocalSocketProtocol.MaxLineBytes,
            outBufferSize: 4 * LocalSocketProtocol.MaxLineBytes,
            security
        );
    }

    private static async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        Func<string, CancellationToken, Task<string>> handleLine,
        CancellationToken ct
    )
    {
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(s_requestTimeout);
                var reply = await LocalSocketProtocol
                    .ReadAndHandleAsync(pipe, handleLine, timeout.Token)
                    .ConfigureAwait(false);
                if (reply is not null)
                {
                    await LocalSocketProtocol.WriteLineAsync(pipe, reply, timeout.Token).ConfigureAwait(false);
                    // Closing the server end discards anything the client has not read yet, so wait
                    // for the client to hang up. WaitForPipeDrain cannot be cancelled, this can.
                    var drain = new byte[256];
                    while (await pipe.ReadAsync(drain, timeout.Token).ConfigureAwait(false) > 0)
                    {
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown or a client that never finished its request.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[NamedPipeServer] Client handler failed: {ex.GetType().Name}");
        }
    }
}
