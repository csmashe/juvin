namespace Juvin.Platform.Windows;

/// <summary>
///     Single-instance guard backed by a session-local named mutex. Holding the handle open is
///     what claims the slot; the mutex is never waited on, so thread affinity does not matter.
/// </summary>
public sealed class WindowsSingleInstanceGuard : ISingleInstanceGuard, IDisposable
{
    // Also the AppMutex in scripts/windows/juvin.iss; change both together.
    private const string DefaultMutexName = @"Local\Juvin.SingleInstance";

    private readonly Lock _gate = new();
    private readonly string _mutexName;
    private Mutex? _mutex;

    public WindowsSingleInstanceGuard()
        : this(DefaultMutexName)
    {
    }

    internal WindowsSingleInstanceGuard(string mutexName)
    {
        _mutexName = mutexName;
    }

    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_mutex is not null)
            {
                return true;
            }

            var mutex = new Mutex(initiallyOwned: false, _mutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            _mutex = mutex;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _mutex?.Dispose();
            _mutex = null;
        }
    }
}
