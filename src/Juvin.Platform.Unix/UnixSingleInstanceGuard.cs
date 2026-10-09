using Juvin.Platform.Unix.Ipc;

namespace Juvin.Platform.Unix;

/// <summary>
///     Single-instance guard backed by the control socket's ownership lock, so "this instance
///     owns the app" and "this instance may bind the socket" are the same fact.
/// </summary>
public sealed class UnixSingleInstanceGuard : ISingleInstanceGuard, IDisposable
{
    private readonly Lock _gate = new();
    private readonly string _socketPath;
    private ControlSocketOwnership? _ownership;

    public UnixSingleInstanceGuard()
        : this(SocketPathResolver.ResolveControlSocketPath())
    {
    }

    public UnixSingleInstanceGuard(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);

        _socketPath = socketPath;
    }

    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_ownership is not null)
            {
                return true;
            }

            if (!ControlSocketOwnership.TryAcquire(_socketPath, out var ownership))
            {
                return false;
            }

            _ownership = ownership;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _ownership?.Dispose();
            _ownership = null;
        }
    }

    internal ControlSocketOwnership? HeldOwnershipFor(string socketPath)
    {
        lock (_gate)
        {
            return string.Equals(socketPath, _socketPath, StringComparison.Ordinal) ? _ownership : null;
        }
    }
}
